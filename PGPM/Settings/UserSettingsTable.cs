using System.Linq.Expressions;
using PGPM.Api;
using PGPM.Api.V1.Contracts;
using PGPM.Sharing;
using PGPM.Sync;

namespace PGPM.Settings;

/// <summary>
/// <c>user_settings</c> (docs/02 §13): own rows only, no History. Two devices inserting the same key (offline) are merged:
/// the later insert patches the earlier row's value (personal UI state, no conflict dialog for it).
/// </summary>
public sealed class UserSettingsTable() : SyncTable<UserSettingEntity>("user_settings")
{
    public const int MaxValueLength = 65_536;

    public override bool History => false;

    public override IReadOnlySet<string> ServerOwned { get; } = new HashSet<string>(StringComparer.Ordinal) { "userId" };

    public override IReadOnlySet<string> Patchable { get; } = new HashSet<string>(StringComparer.Ordinal) { "value", "value.*" };

    protected override UserSettingEntity Create() => new();

    public override IQueryable<UserSettingEntity> Visible(IQueryable<UserSettingEntity> rows, Viewer viewer, AccessService access)
    {
        var me = viewer.UserId;
        return rows.Where(s => s.UserId == me);
    }

    protected override Task AuthorizeInsertAsync(SyncWrite w, UserSettingEntity row, SyncMutationIn m, CancellationToken ct) => Task.CompletedTask;

    protected override Task PrepareInsertAsync(SyncWrite w, UserSettingEntity row, CancellationToken ct)
    {
        row.UserId = w.Viewer.UserId;
        return Task.CompletedTask;
    }

    /// <summary>Own rows only – also for a restore from the Trash, which finds the row without the pull's filter (docs/06 S-05).</summary>
    protected override Task AuthorizeDeleteAsync(SyncWrite w, UserSettingEntity row, CancellationToken ct) =>
        row.UserId == w.Viewer.UserId ? Task.CompletedTask
            : throw new SyncRejection(SyncCodes.MissingAccess, "Only its user changes a setting.");

    protected override void Validate(UserSettingEntity row, ApiValidator v) =>
        v.Check(row.Key.Length is >= 1 and <= 200, "key", "1 to 200 characters.")
            .Check(row.Value.Length <= MaxValueLength, "value", $"At most {MaxValueLength} characters of JSON.");

    protected override Expression<Func<UserSettingEntity, bool>> SamePair(UserSettingEntity row) =>
        s => s.UserId == row.UserId && s.Key == row.Key;

    protected override DuplicateMerge Merge => DuplicateMerge.PatchOverwrite;

    protected override IReadOnlyList<string> MergeKeys { get; } = ["value"];
}
