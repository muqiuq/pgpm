using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PGPM.Data;
using PGPM.Lib.Helper;

namespace PGPM.Mcp;

/// <summary>
/// The MCP log (docs/02 §3 <c>mcp_log</c>, PNMTD's McpLog): one entry per request, in a context of its own, so that a tool's
/// failed save can't take its entry with it. The newest <c>Mcp:LogEntries</c> are kept, trimmed every 25 writes (and by the
/// daily log pruner). Refused sign-ins are summed up per address and minute, so a flood can't push the real entries out.
/// </summary>
public sealed partial class McpLog(IDbContextFactory<PgpmDbContext> dbs, IOptionsMonitor<McpOptions> options, ILogger<McpLog> logger)
{
    public const int MaxArgumentsChars = 16 * 1024;
    public const int MaxResultChars = 256 * 1024;

    private int writes;
    private readonly ConcurrentDictionary<string, (long Minute, long EntryId, int Count)> failures = new();

    public async Task<long?> WriteAsync(McpLogEntity entry)
    {
        try
        {
            await using var db = await dbs.CreateDbContextAsync(CancellationToken.None);
            db.McpLog.Add(entry);
            await db.SaveChangesAsync(CancellationToken.None);
            if (Interlocked.Increment(ref writes) % 25 == 0)
            {
                await TrimAsync(db, options.CurrentValue.LogEntries, CancellationToken.None);
            }
            return entry.Id;
        }
        catch (Exception e)
        {
            logger.LogError(e, "The MCP log entry for {Method} {Tool} could not be written", entry.Method, entry.Tool);
            return null;
        }
    }

    /// <summary>Keeps the newest <paramref name="keep"/> entries (at least 100).</summary>
    public static async Task<int> TrimAsync(PgpmDbContext db, int keep, CancellationToken ct)
    {
        var newest = Math.Max(100, keep);
        return await db.Database.ExecuteSqlAsync($"""
            DELETE FROM mcp_log WHERE id <= (SELECT id FROM mcp_log ORDER BY id DESC OFFSET {newest} LIMIT 1)
            """, ct);
    }

    /// <summary>
    /// A request whose token wasn't accepted: the first of a minute from an address is an entry, the rest of that minute
    /// raise its count. The value presented is never stored, only the hint of the token it matched.
    /// </summary>
    public async Task AuthFailedAsync(string? ip, string? userAgent, string correlationId, string why, McpTokenEntity? token, string? username)
    {
        // Per address (a flood of unknown values is one entry a minute), and per known token: its refusals are its own.
        var key = $"{ip ?? "unknown"}|{token?.Id}";
        var minute = Clock.UtcNow.Ticks / TimeSpan.TicksPerMinute;
        if (failures.TryGetValue(key, out var seen) && seen.Minute == minute)
        {
            var count = seen.Count + 1;
            failures[key] = (minute, seen.EntryId, count);
            try
            {
                await using var db = await dbs.CreateDbContextAsync();
                var summary = $"{count} refused requests from {ip ?? "unknown"} in this minute; the last: {why}";
                await db.McpLog.Where(l => l.Id == seen.EntryId).ExecuteUpdateAsync(u => u.SetProperty(l => l.Summary, summary));
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "The MCP log's count of refused requests could not be updated");
            }
            return;
        }
        var id = await WriteAsync(new McpLogEntity
        {
            At = Clock.UtcNow,
            Method = "auth",
            Outcome = McpOutcomes.Unauthenticated,
            ErrorCode = why,
            Error = McpTokenHandler.Describe(why),
            Summary = $"Refused: {why}" + (token?.Hint is { } hint ? $" (token …{hint})" : ""),
            Ip = ip,
            UserAgent = Cut(userAgent, 300),
            TokenId = token?.Id,
            Kind = token?.Kind,
            UserId = token?.UserId,
            Username = username ?? "",
            CorrelationId = correlationId,
        });
        if (id is { } entryId)
        {
            failures[key] = (minute, entryId, 1);
        }
        if (failures.Count > 10_000)
        {
            foreach (var old in failures.Where(f => f.Value.Minute < minute).Select(f => f.Key).ToList())
            {
                failures.TryRemove(old, out _);
            }
        }
    }

    /// <summary>
    /// Arguments as the log keeps them: PGPM tokens anywhere, and the values of keys that name a secret, replaced; at most
    /// <see cref="MaxArgumentsChars"/>.
    /// </summary>
    public static string? Redact(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }
        var copy = node.DeepClone();
        foreach (var value in Values(copy).ToList())
        {
            var secretKey = value.Parent is JsonObject && SecretKey().IsMatch(value.GetPropertyName());
            if (secretKey)
            {
                Replace(value, JsonValue.Create("[redacted]"));
            }
            else if (value.TryGetValue<string>(out var text) && McpTokenValues.Anywhere().IsMatch(text))
            {
                Replace(value, JsonValue.Create(McpTokenValues.Anywhere().Replace(text, "[redacted: a PGPM token]")));
            }
        }
        var json = copy.ToJsonString();
        return json.Length <= MaxArgumentsChars ? json : new JsonObject { ["cut"] = true, ["start"] = json[..2000] }.ToJsonString();
    }

    [GeneratedRegex("password|secret|token|api_?key|credential", RegexOptions.IgnoreCase)]
    private static partial Regex SecretKey();

    /// <summary>A result as the log keeps it: gzip, the text at most <see cref="MaxResultChars"/>.</summary>
    public static (byte[] Bytes, bool Truncated) Pack(string text)
    {
        var truncated = text.Length > MaxResultChars;
        var kept = truncated ? text[..MaxResultChars] : text;
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest))
        {
            gzip.Write(Encoding.UTF8.GetBytes(kept));
        }
        return (output.ToArray(), truncated);
    }

    public static string Unpack(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return "";
        }
        using var input = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
        using var reader = new StreamReader(input, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public static string? Cut(string? text, int max) => text is null ? null : text.Length <= max ? text : text[..max];

    private static IEnumerable<JsonValue> Values(JsonNode node) => node switch
    {
        JsonValue v => [v],
        JsonObject o => o.Select(p => p.Value).Where(c => c is not null).SelectMany(c => Values(c!)),
        JsonArray a => a.Where(c => c is not null).SelectMany(c => Values(c!)),
        _ => [],
    };

    private static void Replace(JsonNode old, JsonNode replacement)
    {
        switch (old.Parent)
        {
            case JsonObject o:
                o[old.GetPropertyName()] = replacement;
                break;
            case JsonArray a:
                a[old.GetElementIndex()] = replacement;
                break;
        }
    }
}
