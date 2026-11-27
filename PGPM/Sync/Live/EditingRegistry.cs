using System.Collections.Concurrent;
using PGPM.Lib.Helper;

namespace PGPM.Sync.Live;

/// <summary>Someone has an editor open on a part of an item (docs/03 §18.6).</summary>
public sealed record EditingEntry(Guid ItemId, string Target, Guid UserId, string UserName, DateTime Since, DateTime LastSeen);

/// <summary>
/// Who else is editing (docs/03 §18.6, PNMTD's): <c>POST /api/v1/editing</c> every 30 s while an editor is open,
/// <c>DELETE</c> on close, expired after 90 s; kept in memory, blocks nothing. <see cref="LiveSyncHub"/> tells the live
/// connections of the users who can read the item (<c>editing</c> / <c>editing_end</c>).
/// </summary>
public sealed class EditingRegistry
{
    public static readonly TimeSpan Expiry = TimeSpan.FromSeconds(90);

    private readonly ConcurrentDictionary<(Guid ItemId, string Target, Guid UserId), EditingEntry> entries = new();

    /// <summary>An editing session started (true) or ended (false).</summary>
    public event Action<EditingEntry, bool>? Changed;

    public IReadOnlyList<EditingEntry> Active => [.. entries.Values];

    /// <summary>The heartbeat; a new entry is announced.</summary>
    public void Touch(Guid itemId, string target, Guid userId, string userName)
    {
        var now = Clock.UtcNow;
        var key = (itemId, target, userId);
        var added = false;
        entries.AddOrUpdate(key, _ =>
        {
            added = true;
            return new EditingEntry(itemId, target, userId, userName, now, now);
        }, (_, existing) => existing with { LastSeen = now });
        if (added)
        {
            Changed?.Invoke(entries[key], true);
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
