using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using PGPM.Lib.Helper;

namespace PGPM.Persons.DirectorySync;

/// <summary>The app registration the directory sync signs in with (docs/03 §5.5): Entra's tenant, client id and secret.</summary>
public sealed record DirectoryCredentials(Guid TenantId, string ClientId, string ClientSecret);

/// <summary>
/// One account of a delta page: its object id, whether Graph reports it removed (<c>@removed</c>), and the selected
/// properties the page carries – a key that is missing is unchanged, a key with null is empty.
/// </summary>
public sealed record GraphUser(Guid Id, bool Removed, JsonObject Fields);

/// <summary>Graph answered 410 Gone: the delta link expired, the next run starts from scratch (a full run).</summary>
public sealed class DeltaExpiredException() : Exception("The directory's delta link expired (410 Gone).");

/// <summary>Microsoft's login or Graph refused or failed: the message says what it answered (never a secret).</summary>
public sealed class GraphException(string message) : Exception(message);

/// <summary>
/// App-only tokens for Microsoft Graph (docs/03 §8.1): the client credentials grant with the same app registration as the
/// SSO (application permission <c>User.Read.All</c>), cached until 5 minutes before they expire.
/// </summary>
public sealed class GraphTokenCache(IHttpClientFactory clients)
{
    public const string Scope = "https://graph.microsoft.com/.default";

    public static readonly TimeSpan Margin = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<(Guid Tenant, string Client), (string Token, DateTime Until)> tokens = new();

    public async Task<string> GetAsync(DirectoryCredentials credentials, CancellationToken ct)
    {
        var key = (credentials.TenantId, credentials.ClientId);
        if (tokens.TryGetValue(key, out var cached) && cached.Until > Clock.UtcNow)
        {
            return cached.Token;
        }
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{GraphDirectoryClient.LoginAddress}{credentials.TenantId}/oauth2/v2.0/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = credentials.ClientId,
                ["client_secret"] = credentials.ClientSecret,
                ["scope"] = Scope,
                ["grant_type"] = "client_credentials",
            }),
        };
        using var response = await clients.CreateClient(GraphDirectoryClient.ClientName).SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        JsonNode? json;
        try
        {
            json = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            json = null;
        }
        if (!response.IsSuccessStatusCode || json?["access_token"]?.GetValue<string>() is not { Length: > 0 } token)
        {
            var error = json?["error"]?.ToString();
            var description = json?["error_description"]?.ToString()?.Split('\n')[0].Trim();
            throw new GraphException($"Microsoft's login refused the directory's token request ({(int)response.StatusCode}"
                + (error is null ? "" : $" {error}") + ")" + (description is { Length: > 0 } ? $": {Cut(description, 300)}" : "."));
        }
        var seconds = json["expires_in"] is JsonValue v && v.TryGetValue<int>(out var s) ? s
            : int.TryParse(json["expires_in"]?.ToString(), CultureInfo.InvariantCulture, out var parsed) ? parsed : 3600;
        tokens[key] = (token, Clock.UtcNow.AddSeconds(seconds) - Margin);
        return token;
    }

    /// <summary>A token Graph refused (401): the next request gets a new one.</summary>
    public void Forget(DirectoryCredentials credentials) => tokens.TryRemove((credentials.TenantId, credentials.ClientId), out _);

    internal static string Cut(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}

/// <summary>
/// Reads the whole tenant's accounts from Microsoft Graph (docs/03 §8.1): <c>GET /users/delta</c> with the
/// <see cref="Select"/>, following <c>@odata.nextLink</c> until the <c>@odata.deltaLink</c>, which the next incremental run
/// starts from. 429 and 503 are waited out as <c>Retry-After</c> says, from the same page; 410 Gone ends the delta link
/// (<see cref="DeltaExpiredException"/>). Raw <c>HttpClient</c>, like GGITSV's MicrosoftDirectoryReader – no Graph SDK.
/// </summary>
public sealed class GraphDirectoryClient(IHttpClientFactory clients, GraphTokenCache tokens, ILogger<GraphDirectoryClient> logger)
{
    /// <summary>The HTTP client for the login and Graph (absolute addresses); the tests give it a fake handler.</summary>
    public const string ClientName = "microsoft-directory";

    public const string LoginAddress = "https://login.microsoftonline.com/";

    public const string Select =
        "id,displayName,givenName,surname,mail,userPrincipalName,jobTitle,department,companyName,businessPhones,mobilePhone,accountEnabled,userType";

    public const string DeltaAddress = "https://graph.microsoft.com/v1.0/users/delta?$select=" + Select;

    /// <summary>Throttling answers (429, 503) waited out per page before the run gives up.</summary>
    public const int MaxRetries = 8;

    /// <summary>The longest wait for one <c>Retry-After</c>.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(5);

    /// <summary>The waits a run made (for its log line and the tests).</summary>
    public sealed record ReadResult(string DeltaLink, int Pages, int Retries);

    /// <summary>
    /// Every page from <paramref name="deltaLink"/> (null: from the start, a full run), handed to <paramref name="onPage"/> in
    /// order; the new delta link at the end.
    /// </summary>
    public async Task<ReadResult> ReadAsync(DirectoryCredentials credentials, string? deltaLink, Func<IReadOnlyList<GraphUser>, Task> onPage,
        CancellationToken ct)
    {
        var url = deltaLink ?? DeltaAddress;
        var pages = 0;
        var retries = 0;
        while (true)
        {
            var (page, waited) = await GetPageAsync(credentials, url, ct);
            retries += waited;
            pages++;
            var users = new List<GraphUser>();
            foreach (var value in page["value"] as JsonArray ?? [])
            {
                if (value is JsonObject account && Guid.TryParse(account["id"]?.ToString(), out var id))
                {
                    var removed = account.ContainsKey("@removed");
                    account.Remove("@removed");
                    account.Remove("id");
                    users.Add(new GraphUser(id, removed, account));
                }
            }
            await onPage(users);
            if (page["@odata.nextLink"]?.ToString() is { Length: > 0 } next)
            {
                url = next;
                continue;
            }
            if (page["@odata.deltaLink"]?.ToString() is { Length: > 0 } link)
            {
                return new ReadResult(link, pages, retries);
            }
            throw new GraphException("Microsoft Graph's page had neither a next link nor a delta link.");
        }
    }

    private async Task<(JsonObject Page, int Retries)> GetPageAsync(DirectoryCredentials credentials, string url, CancellationToken ct)
    {
        var retries = 0;
        var renewed = false;
        while (true)
        {
            var token = await tokens.GetAsync(credentials, ct);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await clients.CreateClient(ClientName).SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                try
                {
                    return (JsonNode.Parse(body) as JsonObject ?? throw new GraphException("Microsoft Graph's page is no JSON object."), retries);
                }
                catch (JsonException)
                {
                    throw new GraphException("Microsoft Graph's page is no JSON.");
                }
            }
            switch (response.StatusCode)
            {
                case HttpStatusCode.Gone:
                    throw new DeltaExpiredException();
                case HttpStatusCode.Unauthorized when !renewed:
                    tokens.Forget(credentials);
                    renewed = true;
                    continue;
                case HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable when retries < MaxRetries:
                    var wait = RetryAfter(response);
                    retries++;
                    logger.LogInformation("Microsoft Graph answered {Status}: waiting {Seconds} s before the page again", (int)response.StatusCode,
                        (int)wait.TotalSeconds);
                    await Task.Delay(wait, ct);
                    continue;
            }
            throw new GraphException($"Microsoft Graph answered {(int)response.StatusCode} to the directory's delta query"
                + (await ErrorOfAsync(response, ct) is { } error ? $": {error}" : "."));
        }
    }

    /// <summary>Retry-After in seconds or as a date; 10 seconds when missing; at most <see cref="MaxWait"/>.</summary>
    internal static TimeSpan RetryAfter(HttpResponseMessage response)
    {
        var after = response.Headers.RetryAfter;
        var wait = after?.Delta ?? (after?.Date is { } date ? date - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(10));
        return wait < TimeSpan.Zero ? TimeSpan.Zero : wait > MaxWait ? MaxWait : wait;
    }

    private static async Task<string?> ErrorOfAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            return json?["error"]?["message"]?.ToString() is { Length: > 0 } message ? GraphTokenCache.Cut(message, 300) : json?["error"]?["code"]?.ToString();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
