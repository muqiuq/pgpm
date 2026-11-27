namespace PGPM.Lib.Helper;

/// <summary>
/// Primary keys are UUID v7 (docs/02-data-model.md §1): time-ordered, so new rows land at the end of the index. Synced rows
/// get their id on the client (offline creation); the server only accepts version 7 ids it hasn't seen.
/// </summary>
public static class Ids
{
    public static Guid New() => Guid.CreateVersion7();

    /// <summary>A version 7 id with the RFC 9562 variant: what a client may send for a new synced row.</summary>
    public static bool IsVersion7(Guid id) => id.Version == 7 && (id.Variant & 0xC) == 0x8;
}
