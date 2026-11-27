using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using PGPM.Api;
using PGPM.Lib.Helper;

namespace PGPM.Files;

/// <summary>Files:* (docs/03 §10.1, §14).</summary>
public sealed class FilesOptions
{
    public string Path { get; set; } = "/var/lib/pgpm/files";

    public long MaxAttachmentBytes { get; set; } = 25L * 1024 * 1024;

    public long MaxImageBytes { get; set; } = 2L * 1024 * 1024;

    /// <summary>Space separated; double extensions count (<c>report.pdf.exe</c>, <c>setup.exe.pdf</c>).</summary>
    public string BlockedExtensions { get; set; } = "exe msi dll scr com bat cmd ps1 vbs js jar hta lnk";

    /// <summary>Uploads started (chunk 0) per user and hour.</summary>
    public int UploadsPerHour { get; set; } = 200;

    public long MinFreeMb { get; set; } = 1024;

    public IReadOnlySet<string> BlockedExtensionSet =>
        BlockedExtensions.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries).Select(e => e.Trim('.').ToLowerInvariant()).ToHashSet();

    /// <summary>The limit of a purpose: images (covers, map images, embedded images) are prepared in the browser (docs/03 §18.4).</summary>
    public long LimitFor(FilePurpose purpose) => IsImage(purpose) ? MaxImageBytes : MaxAttachmentBytes;

    /// <summary>The purposes whose files are images, checked by <see cref="ImageCheck"/> and served inline.</summary>
    public static bool IsImage(FilePurpose purpose) => purpose is FilePurpose.Cover or FilePurpose.MapImage or FilePurpose.Embedded;
}

/// <summary>The codes of the files' ProblemDetails (PNMTD's, docs/03 §10).</summary>
public static class FileErrors
{
    /// <summary>413: over the purpose's limit, a chunk over 4 MiB, or an image over 10,000 pixels a side or 40 megapixels.</summary>
    public const string TooLarge = "too_large";

    /// <summary>415: not a PNG, JPEG, GIF or WebP by its bytes, or markup in it.</summary>
    public const string NotAnImage = "not_an_image";

    /// <summary>422: a blocked extension, double ones included.</summary>
    public const string BlockedType = "blocked_type";

    /// <summary>429: Files:UploadsPerHour used up.</summary>
    public const string TooManyUploads = "too_many_uploads";

    /// <summary>503: the folder is missing or not writable.</summary>
    public const string StorageUnavailable = "file_storage_unavailable";

    /// <summary>409: a chunk other than 0 or the next one.</summary>
    public const string ChunkOutOfOrder = "chunk_out_of_order";

    /// <summary>409: a commit without an upload (or another user's).</summary>
    public const string NoUpload = "upload_missing";

    /// <summary>409: the bytes received differ from what the commit (and the row) say.</summary>
    public const string SizeMismatch = "size_mismatch";

    public const string HashMismatch = "sha256_mismatch";

    /// <summary>409: the file is stored already with other bytes.</summary>
    public const string AlreadyAvailable = "file_available";

    /// <summary>404: the row exists, its bytes don't yet.</summary>
    public const string Pending = "file_pending";
}

/// <summary>
/// The files folder (docs/03 §10.1, PNMTD's FileStore): below <c>Files:Path</c> (a folder on the host), uploads in
/// <c>.tmp/</c>, stored files as <c>yyyy/MM/{id}-{slug}.{ext}</c> (0640, folders 0750), moved into place by an atomic rename.
/// Paths come from ids and slugs only; reading checks that a path lies inside the folder and that nothing on the way is a
/// symbolic link. Files never change after writing.
/// </summary>
public sealed class FileStore(IOptionsMonitor<FilesOptions> options)
{
    public const string TempFolder = ".tmp";

    /// <summary>A transfer chunk at most (docs/03 §10.2).</summary>
    public const int MaxChunkBytes = 4 * 1024 * 1024;

    private const UnixFileMode FolderPermissions = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute;

    private const UnixFileMode FilePermissions = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;

    public FilesOptions Options => options.CurrentValue;

    public string Root => System.IO.Path.GetFullPath(options.CurrentValue.Path);

    /// <summary>
    /// Appends one chunk of an upload to its temporary file (chunk 0 starts it over): at most <see cref="MaxChunkBytes"/>,
    /// and the whole at most <paramref name="limit"/> – 413 <c>too_large</c> otherwise. Returns the bytes received in all.
    /// </summary>
    public async Task<long> AppendAsync(string tempPath, Stream body, bool first, long receivedBefore, long limit, CancellationToken ct)
    {
        var buffer = new byte[81920];
        var received = first ? 0 : receivedBefore;
        long chunk = 0;
        var mode = first ? FileMode.Create : FileMode.Append;
        var o = new FileStreamOptions { Mode = mode, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
        if (!OperatingSystem.IsWindows() && first)
        {
            o.UnixCreateMode = FilePermissions;
        }
        await using (var output = new FileStream(tempPath, o))
        {
            int read;
            while ((read = await body.ReadAsync(buffer, ct)) > 0)
            {
                chunk += read;
                received += read;
                if (chunk > MaxChunkBytes)
                {
                    throw new ApiException(StatusCodes.Status413PayloadTooLarge, FileErrors.TooLarge, "A chunk has at most 4 MiB.");
                }
                if (received > limit)
                {
                    throw new ApiException(StatusCodes.Status413PayloadTooLarge, FileErrors.TooLarge,
                        $"The file is larger than {limit / (1024.0 * 1024):0.#} MB.");
                }
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            await output.FlushAsync(ct);
        }
        return received;
    }

    /// <summary>A file's SHA-256, lower-case hex.</summary>
    public static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
    }

    /// <summary>A new temporary file's path in <c>.tmp/</c> (inside the folder: the move into place is a rename).</summary>
    public string NewTempPath(string suffix) => System.IO.Path.Combine(TempDirectory(), $"{Guid.NewGuid():N}.{suffix}");

    /// <summary>Writes bytes into a new temporary file (an image without its metadata).</summary>
    public async Task<string> WriteTempAsync(byte[] bytes, CancellationToken ct)
    {
        var path = NewTempPath("stripped");
        var o = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
        if (!OperatingSystem.IsWindows())
        {
            o.UnixCreateMode = FilePermissions;
        }
        await using var output = new FileStream(path, o);
        await output.WriteAsync(bytes, ct);
        await output.FlushAsync(ct);
        output.Flush(flushToDisk: true);
        return path;
    }

    /// <summary>Moves a temporary file into place as <paramref name="relative"/> (flushed to disk before); never overwrites.</summary>
    public void Place(string tempPath, string relative)
    {
        var target = System.IO.Path.Combine(Root, relative);
        CreateFolder(System.IO.Path.GetDirectoryName(target)!);
        using (var stream = new FileStream(tempPath, FileMode.Open, FileAccess.ReadWrite))
        {
            stream.Flush(flushToDisk: true);
        }
        File.Move(tempPath, target, overwrite: false);
    }

    /// <summary>A file's place on disk, or null: missing, outside the folder, or a symbolic link on the way.</summary>
    public string? Locate(string? relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
        {
            return null;
        }
        var root = Root.TrimEnd(System.IO.Path.DirectorySeparatorChar);
        string full;
        try
        {
            full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relativePath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        if (!full.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return null;
        }
        var file = new FileInfo(full);
        if (!file.Exists || file.LinkTarget is not null)
        {
            return null;
        }
        for (var folder = file.Directory; folder is not null && folder.FullName.Length > root.Length; folder = folder.Parent)
        {
            if (folder.LinkTarget is not null)
            {
                return null;
            }
        }
        return full;
    }

    /// <summary>Removes a stored file's bytes; one already gone is fine. Never anything outside the folder.</summary>
    public void Remove(string? relativePath)
    {
        if (Locate(relativePath) is { } full)
        {
            File.Delete(full);
        }
    }

    /// <summary>Removes a temporary file quietly.</summary>
    public static void DeleteQuietly(string? path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover; FileCleanupTask removes it.
        }
    }

    /// <summary><c>.tmp/</c> inside the folder; 503 <c>file_storage_unavailable</c> when the folder is missing or not writable.</summary>
    public string TempDirectory()
    {
        var root = Root;
        if (!Directory.Exists(root))
        {
            throw new ApiException(StatusCodes.Status503ServiceUnavailable, FileErrors.StorageUnavailable,
                "The files folder is missing: uploads are off until it is mounted again.");
        }
        var temp = System.IO.Path.Combine(root, TempFolder);
        try
        {
            CreateFolder(temp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ApiException(StatusCodes.Status503ServiceUnavailable, FileErrors.StorageUnavailable,
                "The files folder isn't writable: uploads are off until it is fixed.");
        }
        return temp;
    }

    /// <summary>Whether the folder exists and is writable (FileCheckTask, diagnostics); null when all is well.</summary>
    public string? Problem()
    {
        var root = Root;
        if (!Directory.Exists(root))
        {
            return $"The files folder {root} doesn't exist: is it mounted?";
        }
        try
        {
            var probe = NewTempPath("probe");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ApiException)
        {
            return $"The files folder {root} isn't writable: {ex.Message.TrimEnd('.')}.";
        }
    }

    /// <summary>Free bytes on the folder's disk, if known.</summary>
    public long? FreeBytes()
    {
        try
        {
            return new DriveInfo(Root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void CreateFolder(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            Directory.CreateDirectory(path, FolderPermissions);
        }
    }

    /// <summary>The name as sent, without a path, control characters or trailing dots and spaces; null when nothing is left or it is too long.</summary>
    public static string? CleanName(string? name)
    {
        var cleaned = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray());
        cleaned = cleaned[(cleaned.LastIndexOfAny(['/', '\\']) + 1)..].Trim().TrimEnd('.', ' ');
        return cleaned.Length is 0 or > FileNames.MaxName ? null : cleaned;
    }

    /// <summary>The first blocked extension among a name's: <c>report.pdf.exe</c> and <c>setup.exe.pdf</c> both give <c>exe</c>.</summary>
    public static string? BlockedExtension(string name, IReadOnlySet<string> blocked)
    {
        var parts = name.ToLowerInvariant().Split('.');
        return parts.Skip(1).Select(p => p.Trim().Split(':')[0]).FirstOrDefault(blocked.Contains);
    }
}

/// <summary>Uploads per user and hour (Files:UploadsPerHour), in memory (PNMTD's UploadGuard).</summary>
public sealed class UploadGuard
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<Guid, (DateTime Start, int Count)> perUser = new();

    public bool TryEnter(Guid user, int perHour, out TimeSpan retryAfter)
    {
        var now = Clock.UtcNow;
        var entry = perUser.AddOrUpdate(user, _ => (now, 1), (_, e) => now - e.Start >= Window ? (now, 1) : (e.Start, e.Count + 1));
        retryAfter = entry.Start + Window - now;
        return entry.Count <= Math.Max(1, perHour);
    }
}
