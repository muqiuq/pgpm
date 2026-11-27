using PGPM.Sync;

namespace PGPM.Settings;

/// <summary>
/// A personal setting that follows the user to every device (docs/02 §13 <c>user_settings</c>): the last list state
/// (<c>list:&lt;type&gt;</c>), a personal map view (<c>map:&lt;id&gt;</c>). Synced to its own user only.
/// </summary>
public sealed class UserSettingEntity : SyncedEntity
{
    public Guid UserId { get; set; }

    public string Key { get; set; } = "";

    /// <summary>jsonb.</summary>
    public string Value { get; set; } = "null";
}
