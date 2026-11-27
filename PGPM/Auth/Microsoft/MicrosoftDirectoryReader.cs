using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PGPM.Auth.Microsoft;

/// <summary>
/// Reads the signed-in account's own record from Microsoft Graph at every Microsoft sign-in (GGITSV, docs/03 §5.3 step 7):
/// <c>GET /me</c> with the user's delegated token and the scope <c>User.Read</c>, which Entra grants without admin
/// consent. A failure never stops the sign-in: the user keeps the values of the last read, and the log says why.
/// </summary>
public sealed partial class MicrosoftDirectoryReader(IHttpClientFactory clients, ILogger<MicrosoftDirectoryReader> logger)
{
    /// <summary>The name of the HTTP client, with Graph's address; the tests give it a fake handler.</summary>
    public const string ClientName = "microsoft-graph";

    public static readonly Uri GraphAddress = new("https://graph.microsoft.com/v1.0/");

    /// <summary>The delegated scope the sign-in asks for: <c>User.Read</c> of Microsoft Graph.</summary>
    public const string Scope = "User.Read";

    public const string MeQuery = "me?$select=userPrincipalName,mail";

    /// <summary>The record; null when it could not be read.</summary>
    public async Task<MicrosoftDirectoryProfile?> ReadAsync(string accessToken, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, MeQuery);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await clients.CreateClient(ClientName).SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                LogReadFailed(logger, (int)response.StatusCode);
                return null;
            }
            var me = await response.Content.ReadFromJsonAsync<GraphMe>(cancellationToken);
            return me is null ? null : new MicrosoftDirectoryProfile(me.UserPrincipalName, me.Mail);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            LogReadError(logger, exception);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Microsoft Graph answered {StatusCode} to GET /me; the user keeps the directory names of the last read.")]
    private static partial void LogReadFailed(ILogger logger, int statusCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Microsoft Graph could not be read (GET /me); the user keeps the directory names of the last read.")]
    private static partial void LogReadError(ILogger logger, Exception exception);

    private sealed record GraphMe(
        [property: JsonPropertyName("userPrincipalName")] string? UserPrincipalName,
        [property: JsonPropertyName("mail")] string? Mail);
}
