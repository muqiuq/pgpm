using System.Text.Json.Serialization;
using PGPM.Sync;

namespace PGPM.Files;

/// <summary>What a file is for (docs/02 §6 <c>files</c>): <c>embedded</c> = an image inserted into Markdown text.</summary>
public enum FilePurpose
{
    [JsonStringEnumMemberName("attachment")] Attachment,
    [JsonStringEnumMemberName("cover")] Cover,
    [JsonStringEnumMemberName("map_image")] MapImage,
    [JsonStringEnumMemberName("embedded")] Embedded,
}

/// <summary>A file's bytes: still to be uploaded, or stored.</summary>
public enum FileStatus
{
    [JsonStringEnumMemberName("pending")] Pending,
    [JsonStringEnumMemberName("available")] Available,
}

/// <summary>
/// A file's metadata (docs/02 §6 <c>files</c>) [S]: created by the client with <c>status: pending</c> (offline too), the
/// bytes come through <c>PUT /api/v1/files/{id}/chunks/{n}</c> and <c>POST …/commit</c>. Belongs to an item or a map.
/// <see cref="StoragePath"/> and <see cref="UnreferencedSince"/> are the server's and never sent.
/// </summary>
public sealed class FileEntity : SyncedEntity
{
    public Guid? ItemId { get; set; }

    public Guid? MapId { get; set; }

    public FilePurpose Purpose { get; set; }

    public string FileName { get; set; } = "";

    public string ContentType { get; set; } = "application/octet-stream";

    public long SizeBytes { get; set; }

    /// <summary>Lower-case hex of the stored bytes.</summary>
    public string Sha256 { get; set; } = "";

    public int? Width { get; set; }

    public int? Height { get; set; }

    /// <summary>≤ 32 KB WebP, made by the client.</summary>
    public byte[]? Thumbnail { get; set; }

    public FileStatus Status { get; set; }

    /// <summary><c>yyyy/MM/{id}-{slug}.{ext}</c> below Files:Path; files with the same bytes may share one.</summary>
    public string? StoragePath { get; set; }

    /// <summary>An embedded file no text of its item names, since (FileCleanupTask: deleted after 30 days).</summary>
    public DateTime? UnreferencedSince { get; set; }
}

/// <summary>A chunked upload in progress (docs/02 §6 <c>file_uploads</c>), server-only; removed on commit, stale after 24 h.</summary>
public sealed class FileUploadEntity
{
    public Guid FileId { get; set; }

    public Guid UserId { get; set; }

    public long ReceivedBytes { get; set; }

    /// <summary>The chunk the upload expects next (chunk 0 always starts over).</summary>
    public int NextChunk { get; set; }

    public DateTime StartedAt { get; set; }

    /// <summary>Below Files:Path/.tmp.</summary>
    public string TempPath { get; set; } = "";
}
