using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PGPM.Api;
using PGPM.Api.V1.Contracts;
using PGPM.Items;
using PGPM.Sharing;
using PGPM.Sync;

namespace PGPM.Files;

/// <summary>
/// <c>files</c> (docs/02 §6, docs/03 §10.2): a file's metadata, created by the client with the item or map it belongs to
/// (edit access); the bytes follow through the chunk endpoints, which make it <c>available</c>. Covers and attachments
/// belong to items, map images to maps, embedded images (Markdown) to items. <c>storagePath</c> is never sent. An insert
/// whose bytes the user can read already (same SHA-256 and size, an available file of an item or map they read) is
/// <c>available</c> at once and shares the stored bytes – no upload needed (conversion, copies).
/// </summary>
public sealed partial class FilesTable() : SyncTable<FileEntity>("files")
{
    public override IReadOnlyList<ParentRef> Parents { get; } =
        [new("items", "itemId", DeletedWithParent: true), new("maps", "mapId", DeletedWithParent: true)];

    public override IReadOnlySet<string> Hidden { get; } = new HashSet<string>(StringComparer.Ordinal) { "storagePath", "unreferencedSince" };

    public override IReadOnlySet<string> ServerOwned { get; } = new HashSet<string>(StringComparer.Ordinal) { "status" };

    /// <summary>A rename, a new thumbnail.</summary>
    public override IReadOnlySet<string> Patchable { get; } = new HashSet<string>(StringComparer.Ordinal) { "fileName", "thumbnail" };

    public const int MaxThumbnailBytes = 32 * 1024;

    protected override FileEntity Create() => new();

    public override IQueryable<FileEntity> Visible(IQueryable<FileEntity> rows, Viewer viewer, AccessService access)
    {
        var items = access.ReadableItems(viewer);
        var maps = access.ReadableMaps(viewer);
        return rows.Where(f => (f.ItemId != null && items.Any(i => i.Id == f.ItemId)) || (f.MapId != null && maps.Any(m => m.Id == f.MapId)));
    }

    protected override Guid? ItemOf(FileEntity row) => row.ItemId;

    protected override Guid? MapOf(FileEntity row) => row.MapId;

    /// <summary>The owner's access: an item's or a map's.</summary>
    public static async Task<ItemAccess> OwnerAccessAsync(AccessService access, Viewer viewer, FileEntity row, CancellationToken ct) =>
        row.ItemId is { } item ? await access.ItemAccessAsync(viewer, item, ct)
        : row.MapId is { } map ? await access.MapAccessAsync(viewer, map, ct)
        : ItemAccess.None;

    protected override async Task AuthorizeInsertAsync(SyncWrite w, FileEntity row, SyncMutationIn m, CancellationToken ct)
    {
        if ((row.ItemId is null) == (row.MapId is null))
        {
            throw Invalid("itemId", "Either an item or a map.");
        }
        if (row.ItemId is { } item)
        {
            await RequireParentItemAsync(w, item, ItemAccess.Edit, ct);
        }
        else
        {
            await RequireParentMapAsync(w, row.MapId!.Value, ItemAccess.Edit, ct);
        }
    }

    protected override async Task PrepareInsertAsync(SyncWrite w, FileEntity row, CancellationToken ct)
    {
        row.Status = FileStatus.Pending;
        row.StoragePath = null;
        row.UnreferencedSince = null;
        row.Sha256 = row.Sha256.ToLowerInvariant();
        row.FileName = FileStore.CleanName(row.FileName) ?? row.FileName;
        // The same bytes, stored and readable already: shared, nothing to upload.
        var sha = row.Sha256;
        var size = row.SizeBytes;
        var candidates = await w.Db.Files.AsNoTracking()
            .Where(f => f.Sha256 == sha && f.SizeBytes == size && f.Status == FileStatus.Available && f.StoragePath != null && !f.Deleted)
            .OrderByDescending(f => f.Seq).Take(20).ToListAsync(ct);
        var store = w.Services.GetRequiredService<FileStore>();
        foreach (var candidate in candidates)
        {
            if (await OwnerAccessAsync(w.Access, w.Viewer, candidate, ct) >= ItemAccess.Read && store.Locate(candidate.StoragePath) is not null
                // An image's bytes only from an image (checked and stripped at its commit), never from an attachment that
                // merely says image/png (docs/06 S-07).
                && (!FilesOptions.IsImage(row.Purpose) || FilesOptions.IsImage(candidate.Purpose)))
            {
                row.Status = FileStatus.Available;
                row.StoragePath = candidate.StoragePath;
                row.ContentType = candidate.ContentType;
                row.Width = candidate.Width;
                row.Height = candidate.Height;
                break;
            }
        }
    }

    protected override async Task AuthorizePatchAsync(SyncWrite w, FileEntity row, IReadOnlyCollection<string> keys, CancellationToken ct)
    {
        if (await OwnerAccessAsync(w.Access, w.Viewer, row, ct) < ItemAccess.Edit)
        {
            throw new SyncRejection(SyncCodes.MissingAccess, "You need edit access to the file's item or map.", await CurrentAsync(w, row, ct));
        }
    }

    protected override Task AuthorizeDeleteAsync(SyncWrite w, FileEntity row, CancellationToken ct) => AuthorizePatchAsync(w, row, [], ct);

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex Sha256Pattern();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9!#$&^_.+-]{0,126}/[a-z0-9][a-z0-9!#$&^_.+-]{0,126}$")]
    private static partial Regex MediaType();

    protected override void Validate(FileEntity row, ApiValidator v)
    {
        var image = FilesOptions.IsImage(row.Purpose);
        v.Check(row.Purpose == FilePurpose.MapImage ? row.MapId is not null : row.ItemId is not null, "purpose",
                "Map images belong to maps; covers, attachments and embedded images to items.")
            .Check(FileStore.CleanName(row.FileName) == row.FileName, "fileName", "1 to 255 characters, no path.")
            .Check(MediaType().IsMatch(row.ContentType), "contentType", "A media type (image/png).")
            .Check(!image || ImageCheck.ExtensionOf(row.ContentType) is not null, "contentType", "Images are PNG, JPEG, GIF or WebP.")
            .Check(Sha256Pattern().IsMatch(row.Sha256), "sha256", "The SHA-256 of the bytes, hex.")
            .Check(row.SizeBytes > 0, "sizeBytes", "More than 0 bytes.")
            .Check(row.Width is null or (> 0 and <= ImageCheck.MaxSide) && row.Height is null or (> 0 and <= ImageCheck.MaxSide), "width",
                $"1 to {ImageCheck.MaxSide:N0} pixels.")
            .Check(row.Thumbnail is null || row.Thumbnail.Length <= MaxThumbnailBytes, "thumbnail", "At most 32 KB.");
    }

    protected override Task ValidateAsync(SyncWrite w, FileEntity row, IReadOnlyCollection<string>? changed, ApiValidator v, CancellationToken ct)
    {
        var options = w.Services.GetRequiredService<IOptionsMonitor<FilesOptions>>().CurrentValue;
        if (FileStore.BlockedExtension(row.FileName, options.BlockedExtensionSet) is { } blocked)
        {
            v.Add("fileName", $"Files ending in .{blocked} can't be stored (Files:BlockedExtensions).");
        }
        var limit = options.LimitFor(row.Purpose);
        v.Check(row.SizeBytes <= limit, "sizeBytes", $"At most {limit / (1024.0 * 1024):0.#} MB for {EnumNames(row.Purpose)} files.");
        return Task.CompletedTask;
    }

    private static string EnumNames(FilePurpose purpose) => Data.EnumText.Of(purpose);
}
