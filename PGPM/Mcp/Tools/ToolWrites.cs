using System.Text.Json.Nodes;
using PGPM.Api.V1.Contracts;
using PGPM.Data;
using PGPM.Lib.Helper;
using PGPM.Sync;

namespace PGPM.Mcp.Tools;

/// <summary>
/// The mutations the tools write, built as a client's outbox entries (docs/03 §11.4 "wire format") and applied through the
/// call's <see cref="SyncWrite"/>: an insert with its row, a patch whose <c>old</c> values are the row's as read inside the
/// write unit (the counter's lock: nobody changes it meanwhile), a delete with the row's rev.
/// </summary>
public static class ToolWrites
{
    /// <summary>An insert; a refusal is the tool's error (<see cref="ToolContext.ApplyAsync"/>). Answers the row's id (or the one it merged into).</summary>
    public static async Task<Guid> InsertAsync(SyncWrite write, string table, JsonObject row, Guid batch, CancellationToken ct, Guid? id = null)
    {
        var mutation = new SyncMutationIn { Batch = batch, Table = table, Id = id ?? Ids.New(), Op = SyncOp.Insert, Row = row };
        var result = await ToolContext.ApplyAsync(write, mutation, ct);
        return result.MergedInto ?? result.Id ?? mutation.Id;
    }

    /// <summary>
    /// A patch of <paramref name="row"/>'s wire keys to these values; keys whose value is the row's already are left out
    /// (nothing at all: no mutation). Answers the keys sent.
    /// </summary>
    public static async Task<IReadOnlyList<string>> PatchAsync(SyncWrite write, string table, ISyncedEntity row, IReadOnlyDictionary<string, JsonNode?> values,
        CancellationToken ct, Guid? batch = null)
    {
        var current = SyncTables.Get(table).ToWire(write.Db, row);
        var changes = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            var old = current[key];
            if (!WireJson.Same(old, value))
            {
                changes[key] = new JsonObject { ["old"] = old?.DeepClone(), ["new"] = value?.DeepClone() };
            }
        }
        if (changes.Count == 0)
        {
            return [];
        }
        await ToolContext.ApplyAsync(write, new SyncMutationIn
        {
            Batch = batch ?? Ids.New(), Table = table, Id = row.Id, Op = SyncOp.Patch, BaseRev = row.Rev, Changes = changes,
        }, ct);
        return [.. changes.Keys];
    }

    /// <summary>A delete of the row as it was read (its rev: a change meanwhile would be a conflict).</summary>
    public static Task<SyncMutationResult> DeleteAsync(SyncWrite write, string table, ISyncedEntity row, CancellationToken ct) =>
        ToolContext.ApplyAsync(write, new SyncMutationIn { Batch = Ids.New(), Table = table, Id = row.Id, Op = SyncOp.Delete, BaseRev = row.Rev }, ct);

    /// <summary>
    /// An insert that may be refused without failing the call (a conversion's copies: a link to an item the user no longer
    /// reads stays behind, as the client's outbox entry would be rejected alone). Answers the row's id, or null.
    /// </summary>
    public static async Task<Guid?> TryInsertAsync(SyncWrite write, string table, JsonObject row, Guid batch, CancellationToken ct, Guid? id = null)
    {
        var mutation = new SyncMutationIn
        {
            MutationId = Ids.New(), Batch = batch, Table = table, Id = id ?? Ids.New(), Op = SyncOp.Insert, Row = row, ClientChangedAt = Clock.UtcNow,
        };
        var result = await write.ApplyAsync(mutation, null, ct);
        return result.Status is SyncStatus.Accepted or SyncStatus.Partial ? result.MergedInto ?? result.Id ?? mutation.Id : null;
    }
}
