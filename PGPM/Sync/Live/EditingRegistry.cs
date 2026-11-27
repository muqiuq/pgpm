using System.Collections.Concurrent;
using PGPM.Lib.Helper;

namespace PGPM.Sync.Live;

/// <summary>Someone has an editor open on a part of an item (docs/03 §18.6).</summary>
public sealed record EditingEntry(Guid ItemId, string Target, Guid UserId, string UserName, DateTime Since, DateTime LastSeen)
{
    /// <summary>The order of the heartbeats (ties of <see cref="LastSeen"/> apart): the least recent ends first.</summary>
    internal long Touched { get; init; }
}

/// <summary>
/// Who else is editing (docs/03 §18.6, PNMTD's): <c>POST /api/v1/editing</c> every 30 s while an editor is open,
/// <c>DELETE</c> on close, expired after 90 s; kept in memory, blocks nothing. <see cref="LiveSyncHub"/> tells the live
/// connections of the users who can read the item (<c>editing</c> / <c>editing_end</c>).
/// </summary>
public sealed class EditingRegistry
{
    public static readonly TimeSpan Expiry = TimeSpan.FromSeconds(90);

    /// <summary>Open editors per user at most (S-12, docs/06): a new one beyond them ends the user's least recently seen.</summary>
    public const int MaxPerUser = 50;

    private readonly ConcurrentDictionary<(Guid ItemId, string Target, Guid UserId), EditingEntry> entries = new();
    private long touches;

    /// <summary>An editing session started (true) or ended (false).</summary>
    public event Action<EditingEntry, bool>? Changed;

    public IReadOnlyList<EditingEntry> Active => [.. entries.Values];

    /// <summary>The heartbeat; a new entry is announced.</summary>
    public void Touch(Guid itemId, string target, Guid userId, string userName)
    {
        var now = Clock.UtcNow;
        var touched = Interlocked.Increment(ref touches);
        var key = (itemId, target, userId);
        var added = false;
        entries.AddOrUpdate(key, _ =>
        {
            added = true;
            return new EditingEntry(itemId, target, userId, userName, now, now) { Touched = touched };
        }, (_, existing) => existing with { LastSeen = now, Touched = touched });
        if (!added)
        {
            return;
        }
        Changed?.Invoke(entries[key], true);
        var mine = entries.Values.Where(e => e.UserId == userId).ToList();
        foreach (var oldest in mine.OrderBy(e => e.Touched).Take(Math.Max(0, mine.Count - MaxPerUser)))
        {
            if ((oldest.ItemId, oldest.Target) != (itemId, target))
            {
                End(oldest.ItemId, oldest.Target, userId);
            }
        }
    }

    public void End(Guid itemId, string target, Guid userId)
    {
        if (entries.TryRemove((itemId, target, userId), out var entry))
        {
            Changed?.Invoke(entry, false);
        }
    }

    /// <summary>Entries without a heartbeat for <see cref="Expiry"/> end.</summary>
    public void Sweep()
    {
        var cutoff = Clock.UtcNow - Expiry;
        foreach (var (key, entry) in entries)
        {
            if (entry.LastSeen < cutoff && entries.TryRemove(key, out var removed))
            {
                Changed?.Invoke(removed, false);
            }
        }
    }
}
