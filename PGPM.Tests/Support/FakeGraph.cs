using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Web;

namespace PGPM.Tests.Support;

/// <summary>
/// Microsoft's login and Graph's <c>/users/delta</c> for the directory sync (docs/03 §8.1), in memory: tenants of their own
/// per test, the client credentials grant, delta paging with <c>@odata.nextLink</c> and <c>@odata.deltaLink</c>, changes and
/// removals since a delta token, and on request an expired delta link (410), throttling (429/503 with <c>Retry-After</c>)
/// or a refused token request.
/// </summary>
public sealed class FakeGraph : HttpMessageHandler
{
    public const string ClientId = "4c1f0d2e-3b5a-4e6f-8a7b-9c0d1e2f3a4b";
    public const string ClientSecret = "fake-directory-secret";

    private readonly ConcurrentDictionary<Guid, Tenant> tenants = new();
    private readonly ConcurrentDictionary<string, Guid> tokens = new();

    /// <summary>One tenant's accounts and how its endpoints answer.</summary>
    public sealed class Tenant(Guid id)
    {
        private readonly Lock gate = new();
        private readonly Dictionary<Guid, JsonObject> users = [];
        private readonly List<(int Version, Guid Id)> changes = [];

        public Guid Id { get; } = id;

        public int Version { get; private set; }

        public int PageSize { get; set; } = 2;

        public int TokenRequests;

        /// <summary>The token endpoint answers this (400 invalid_client) instead of a token.</summary>
        public bool RefuseToken { get; set; }

        /// <summary>A delta link is answered with 410 Gone.</summary>
        public bool DeltaExpired { get; set; }

        /// <summary>The next Graph requests are answered with these statuses and Retry-After seconds.</summary>
        public ConcurrentQueue<(HttpStatusCode Status, int RetryAfter)> Throttle { get; } = new();

        /// <summary>The Graph requests (path and query) and their statuses, in order.</summary>
        public ConcurrentQueue<(string Url, int Status)> Requests { get; } = new();

        /// <summary>An account is created or changed (and appears in the next delta).</summary>
        public Guid Put(JsonObject account, Guid? id = null)
        {
            lock (gate)
            {
                var oid = id ?? (account["id"] is { } existing ? Guid.Parse(existing.GetValue<string>()) : Guid.NewGuid());
                account["id"] = oid.ToString();
                users[oid] = account;
                changes.Add((++Version, oid));
                return oid;
            }
        }

        /// <summary>A change of some properties of an account.</summary>
        public void Change(Guid id, Action<JsonObject> change)
        {
            lock (gate)
            {
                change(users[id]);
                changes.Add((++Version, id));
            }
        }

        /// <summary>Deleted in the directory: the next delta reports it <c>@removed</c>.</summary>
        public void Remove(Guid id)
        {
            lock (gate)
            {
                users.Remove(id);
                changes.Add((++Version, id));
            }
        }

        /// <summary>Gone without a trace in the deltas (a missed change): only a full run notices.</summary>
        public void Forget(Guid id)
        {
            lock (gate)
            {
                users.Remove(id);
            }
        }

        /// <summary>The accounts a query returns: all (no delta token) or those changed after the token's version.</summary>
        internal (List<JsonObject> Items, int Version) Items(int? since)
        {
            lock (gate)
            {
                if (since is not { } v)
                {
                    return ([.. users.OrderBy(u => u.Key).Select(u => (JsonObject)u.Value.DeepClone())], Version);
                }
                var changed = changes.Where(c => c.Version > v).Select(c => c.Id).Distinct().OrderBy(i => i).ToList();
                return ([.. changed.Select(i => users.TryGetValue(i, out var u) ? (JsonObject)u.DeepClone()
                    : new JsonObject { ["id"] = i.ToString(), ["@removed"] = new JsonObject { ["reason"] = "deleted" } })], Version);
            }
        }
    }

    public Tenant NewTenant()
    {
        var tenant = new Tenant(Guid.NewGuid());
        tenants[tenant.Id] = tenant;
        return tenant;
    }

    public Tenant TenantOf(Guid id) => tenants.GetOrAdd(id, i => new Tenant(i));

    /// <summary>An account as Graph returns it with the directory's <c>$select</c>.</summary>
    public static JsonObject User(string name, string? mail = null, string userType = "Member", bool enabled = true, string? job = null) => new()
    {
        ["displayName"] = name,
        ["givenName"] = name.Split(' ')[0],
        ["surname"] = name.Contains(' ', StringComparison.Ordinal) ? name[(name.IndexOf(' ', StringComparison.Ordinal) + 1)..] : null,
        ["mail"] = mail,
        ["userPrincipalName"] = (mail ?? $"{name.Replace(' ', '.')}@example.com").ToLowerInvariant(),
        ["jobTitle"] = job,
        ["department"] = "Innovation",
        ["companyName"] = "Example AG",
        ["businessPhones"] = new JsonArray("+41 44 000 00 00", "+41 44 000 00 01"),
        ["mobilePhone"] = null,
        ["accountEnabled"] = enabled,
        ["userType"] = userType,
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        if (uri.Host == "login.microsoftonline.com" && request.Method == HttpMethod.Post && uri.AbsolutePath.EndsWith("/oauth2/v2.0/token", StringComparison.Ordinal))
        {
            var tenant = TenantOf(Guid.Parse(uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)[0]));
            var form = HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(cancellationToken));
            Interlocked.Increment(ref tenant.TokenRequests);
            if (tenant.RefuseToken || form["client_secret"] != ClientSecret || form["grant_type"] != "client_credentials"
                || form["scope"] != "https://graph.microsoft.com/.default")
            {
                return Json(HttpStatusCode.BadRequest, new JsonObject
                {
                    ["error"] = "invalid_client", ["error_description"] = "AADSTS7000215: Invalid client secret provided.\r\nTrace ID: x",
                });
            }
            var token = $"fake-{tenant.Id:N}-{Guid.NewGuid():N}";
            tokens[token] = tenant.Id;
            return Json(HttpStatusCode.OK, new JsonObject { ["token_type"] = "Bearer", ["expires_in"] = 3599, ["access_token"] = token });
        }
        if (uri.Host == "graph.microsoft.com" && uri.AbsolutePath == "/v1.0/users/delta")
        {
            if (request.Headers.Authorization?.Parameter is not { } bearer || !tokens.TryGetValue(bearer, out var tenantId))
            {
                return Json(HttpStatusCode.Unauthorized, new JsonObject { ["error"] = new JsonObject { ["code"] = "InvalidAuthenticationToken" } });
            }
            var tenant = tenants[tenantId];
            var response = Delta(tenant, uri);
            tenant.Requests.Enqueue((uri.PathAndQuery, (int)response.StatusCode));
            return response;
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request };
    }

    private static HttpResponseMessage Delta(Tenant tenant, Uri uri)
    {
        if (tenant.Throttle.TryDequeue(out var throttle))
        {
            var refused = Json(throttle.Status, new JsonObject { ["error"] = new JsonObject { ["code"] = "TooManyRequests", ["message"] = "Slow down." } });
            refused.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(throttle.RetryAfter));
            return refused;
        }
        var query = HttpUtility.ParseQueryString(uri.Query);
        int? since = null;
        var offset = 0;
        if (query["$deltatoken"] is { } delta)
        {
            if (tenant.DeltaExpired)
            {
                return Json(HttpStatusCode.Gone, new JsonObject
                {
                    ["error"] = new JsonObject { ["code"] = "syncStateNotFound", ["message"] = "The delta token has expired." },
                });
            }
            since = int.Parse(delta, System.Globalization.CultureInfo.InvariantCulture);
        }
        else if (query["$skiptoken"] is { } skip)
        {
            var parts = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(skip)).Split('|');
            since = parts[0].Length == 0 ? null : int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
            offset = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
        }
        else if (query["$select"] is not { Length: > 0 } select || !select.Contains("accountEnabled", StringComparison.Ordinal))
        {
            return Json(HttpStatusCode.BadRequest, new JsonObject { ["error"] = new JsonObject { ["message"] = "No $select." } });
        }
        var (items, version) = tenant.Items(since);
        var page = items.Skip(offset).Take(tenant.PageSize).ToList();
        var body = new JsonObject { ["value"] = new JsonArray([.. page]) };
        const string Base = "https://graph.microsoft.com/v1.0/users/delta?";
        if (offset + page.Count < items.Count)
        {
            body["@odata.nextLink"] = Base + "$skiptoken=" + Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"{since}|{offset + page.Count}"));
        }
        else
        {
            body["@odata.deltaLink"] = Base + "$deltatoken=" + version.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return Json(HttpStatusCode.OK, body);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, JsonNode body) =>
        new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

    /// <summary>The HTTP client factory disposes its handlers now and then; this one lives as long as the test class.</summary>
    protected override void Dispose(bool disposing)
    {
    }
}
