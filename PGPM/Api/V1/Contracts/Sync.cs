using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PGPM.Api.V1.Contracts;

/// <summary>
/// <c>POST /api/v1/sync</c> (docs/03 §11.4): the outbox's mutations (≤ Sync:MaxMutations), the conflicts the user decided,
/// and a pull page after <see cref="Since"/>.
/// </summary>
public sealed class SyncRequest
{
    /// <summary>The device (uuid, generated once per local database).</summary>
    public Guid? ClientId { get; set; }

    public string? AppVersion { get; set; }

    /// <summary>The cursor; 0 = everything.</summary>
    public long Since { get; set; }

    /// <summary>The <c>accessEpoch</c> of the last answer; another one makes the server answer <c>reset</c>.</summary>
    public int? AccessEpoch { get; set; }

    /// <summary>Rows per table per page, 1–1000 (default Sync:PageLimit, 500).</summary>
    public int? Limit { get; set; }

    public List<SyncMutationIn>? Mutations { get; set; }

    public List<SyncResolution>? Resolved { get; set; }

    /// <summary>false while the live channel delivers changes: push only.</summary>
    public bool Pull { get; set; } = true;

    /// <summary>Diagnostics: outbox entries still pending, conflicts open.</summary>
    public int? Pending { get; set; }

    public int? Conflicts { get; set; }
}

public enum SyncOp
{
    [JsonStringEnumMemberName("insert")] Insert,
    [JsonStringEnumMemberName("patch")] Patch,
    [JsonStringEnumMemberName("delete")] Delete,
}

/// <summary>An outbox entry (data model §14.1, <c>PGPM.WebUI/src/local/outbox.ts</c>).</summary>
public sealed class SyncMutationIn
{
    /// <summary>uuid v7; a known one is answered with the stored result.</summary>
    public Guid MutationId { get; set; }

    /// <summary>Entries that belong together (a create dialog): initial shares only for an item of the same batch.</summary>
    public Guid? Batch { get; set; }

    /// <summary>The table's wire name: <c>items</c>, <c>item_shares</c>, <c>persons</c>, <c>user_settings</c>.</summary>
    public string Table { get; set; } = "";

    public Guid Id { get; set; }

    public SyncOp Op { get; set; }

    /// <summary>The server rev the change was made on; null for an insert. A delete of a row changed since is a conflict.</summary>
    public int? BaseRev { get; set; }

    /// <summary>patch: <c>{"title": {"old": …, "new": …}, "value.viewport": {…}}</c> – columns or members of a jsonb column.</summary>
    public Dictionary<string, JsonObject>? Changes { get; set; }

    /// <summary>insert: the complete row (camelCase wire keys; server-owned ones are ignored).</summary>
    public JsonObject? Row { get; set; }

    public DateTime? ClientChangedAt { get; set; }

    /// <summary>delete: the user chose <i>Delete anyway</i>.</summary>
    public bool? Force { get; set; }
}

/// <summary>A conflict the user decided: <c>mine</c>, <c>theirs</c> or <c>edited</c> (recorded in the History).</summary>
public sealed class SyncResolution
{
    public string? Table { get; set; }

    public Guid? Id { get; set; }

    public string? Field { get; set; }

    public string? Resolution { get; set; }
}

public enum SyncStatus
{
    [JsonStringEnumMemberName("accepted")] Accepted,
    [JsonStringEnumMemberName("partial")] Partial,
    [JsonStringEnumMemberName("conflict")] Conflict,
    [JsonStringEnumMemberName("rejected")] Rejected,
}

/// <summary>
/// What became of a mutation. <c>accepted</c>: <see cref="Rev"/>, <see cref="Seq"/> (and <see cref="MergedInto"/> for a
/// duplicate pair: the client rewrites its id). <c>partial</c>: applied but <see cref="ConflictFields"/>. <c>conflict</c>:
/// nothing applied – <see cref="Kind"/> <c>field</c>, <c>deleted_by_other</c> or <c>changed_since_delete</c>, with
/// <see cref="Current"/>. <c>rejected</c>: <see cref="Code"/>, <see cref="Message"/>, <see cref="Errors"/>, and
/// <see cref="Current"/> (the row to restore, none to drop the local one).
/// </summary>
public sealed record SyncMutationResult
{
    public Guid MutationId { get; init; }

    public SyncStatus Status { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? Id { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Rev { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Seq { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? MergedInto { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? ConflictFields { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Kind { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Code { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IDictionary<string, string[]>? Errors { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonObject? Current { get; init; }
}

/// <summary>A field held back: their value, who set it when, and the row's rev now.</summary>
public sealed record SyncConflict(Guid MutationId, string Table, Guid Id, string Field, JsonNode? Theirs, string? By, DateTime? At, int Rev);

/// <summary>The user lost access to an item or map: the client deletes it and everything below it.</summary>
public sealed record SyncEviction(string Kind, Guid Id);

/// <summary>
/// The answer (docs/03 §11.4): the push's results and conflicts, then a page. <see cref="Cursor"/> is where the client's
/// cursor moves; <see cref="More"/>: pull again. <see cref="Reset"/>: push the rest, wipe the synced stores, pull from 0
/// with this <see cref="AccessEpoch"/>. Apply <see cref="Evictions"/> before <see cref="Changes"/>.
/// </summary>
public sealed record SyncResponse(
    long Head,
    long Cursor,
    bool More,
    bool Reset,
    int AccessEpoch,
    IReadOnlyList<SyncMutationResult> Results,
    IReadOnlyList<SyncConflict> Conflicts,
    IReadOnlyDictionary<string, List<JsonObject>> Changes,
    IReadOnlyList<SyncEviction> Evictions);
