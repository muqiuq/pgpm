using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PGPM.Api;
using PGPM.Api.V1.Contracts;
using PGPM.Data;
using PGPM.Items;
using PGPM.Lib.Helper;
using PGPM.Sharing;
using PGPM.Sync;

namespace PGPM.Files;

/// <summary>
/// The bytes of <c>files</c> (docs/03 §10.2, PLM's chunked transfer): chunks into <c>.tmp/</c> (<c>file_uploads</c>), the commit
/// that checks size and SHA-256, strips an image's metadata (<see cref="ImageCheck"/>), moves the file into place and makes
/// the row <c>available</c> through a <see cref="SyncWrite"/> (every device sees it), and the download. Only for a row that
/// exists and whose item or map the user may edit (read, to download).
/// </summary>
public sealed partial class FileService(PgpmDbContext db, FileStore store, SyncWriter writer, UploadGuard uploads, IHttpContextAccessor accessor)
{
    /// <summary>A live file row the user may <paramref name="needed"/>: 404 when it doesn't exist (or is hidden), 403 below.</summary>
    private async Task<FileEntity> FileAsync(Viewer viewer, Guid id, ItemAccess needed, CancellationToken ct)
    {
        var file = await db.Files.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id && !f.Deleted, ct) ?? throw ApiException.NotFound("File");
        var access = await FilesTable.OwnerAccessAsync(new AccessService(db), viewer, file, ct);
        if (access == ItemAccess.None)
        {
            throw ApiException.NotFound("File");
        }
        if (access < needed)
        {
            throw AccessService.MissingAccess(needed);
        }
        return file;
    }

    /// <summary><c>PUT /api/v1/files/{id}/chunks/{n}</c>: chunk 0 starts (over), every other one must be the next.</summary>
    public async Task<FileChunkReceived> PutChunkAsync(Viewer viewer, Guid id, int n, Stream body, CancellationToken ct)
    {
        if (n < 0)
        {
            throw ApiException.Invalid("n", "0 or the next chunk.");
        }
        var file = await FileAsync(viewer, id, ItemAccess.Edit, ct);
        if (file.Status == FileStatus.Available)
        {
            throw ApiException.Conflict(FileErrors.AlreadyAvailable, "The file's bytes are stored already.");
        }
        var upload = await db.FileUploads.FirstOrDefaultAsync(u => u.FileId == id, ct);
        if (n == 0)
        {
            var settings = store.Options;
            if (!uploads.TryEnter(viewer.UserId, settings.UploadsPerHour, out var retryAfter))
            {
                accessor.HttpContext?.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                throw new ApiException(StatusCodes.Status429TooManyRequests, FileErrors.TooManyUploads,
                    $"At most {settings.UploadsPerHour} uploads per hour (Files:UploadsPerHour).");
            }
            if (upload is not null)
            {
                FileStore.DeleteQuietly(upload.TempPath);
                db.FileUploads.Remove(upload);
            }
            upload = new FileUploadEntity { FileId = id, UserId = viewer.UserId, StartedAt = Clock.UtcNow, TempPath = store.NewTempPath("upload") };
            db.FileUploads.Add(upload);
        }
        else if (upload is null || upload.UserId != viewer.UserId || upload.NextChunk != n)
        {
            throw new ApiException(StatusCodes.Status409Conflict, FileErrors.ChunkOutOfOrder, "Start the upload again with chunk 0.",
                extensions: new Dictionary<string, object?> { ["expected"] = upload?.UserId == viewer.UserId ? upload.NextChunk : 0 });
        }
        try
        {
            upload.ReceivedBytes = await store.AppendAsync(upload.TempPath, body, n == 0, upload.ReceivedBytes, file.SizeBytes, ct);
        }
        catch
        {
            // The chunk is lost: the upload starts over.
            FileStore.DeleteQuietly(upload.TempPath);
            if (db.Entry(upload).State != EntityState.Added)
            {
                db.FileUploads.Remove(upload);
                await db.SaveChangesAsync(CancellationToken.None);
            }
            throw;
        }
        upload.NextChunk = n + 1;
        await db.SaveChangesAsync(ct);
        return new FileChunkReceived(upload.ReceivedBytes);
    }

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex Sha256Pattern();

    /// <summary>
    /// <c>POST /api/v1/files/{id}/commit</c>: the upload's size and SHA-256 must be those of the commit and of the row; then an
    /// image is checked and stripped, the bytes move into place and the row becomes <c>available</c> (with the stored bytes'
    /// hash, size, type and pixel size). A commit of a file stored already answers with it (a retry).
    /// </summary>
    public async Task<FileCommitted> CommitAsync(Viewer viewer, Guid id, FileCommitRequest request, CancellationToken ct)
    {
        new ApiValidator()
            .Check(request.Sha256 is { } h && Sha256Pattern().IsMatch(h), "sha256", "The SHA-256 of the bytes, hex.")
            .Check(request.Size is > 0, "size", "The size in bytes.")
            .ThrowIfInvalid();
        var sha = request.Sha256!.ToLowerInvariant();
        var file = await FileAsync(viewer, id, ItemAccess.Edit, ct);
        if (file.Status == FileStatus.Available)
        {
            // A retry of a commit that went through (or bytes shared at the insert): stored, as the answer says.
            return Committed(file);
        }
        var upload = await db.FileUploads.FirstOrDefaultAsync(u => u.FileId == id && u.UserId == viewer.UserId, ct)
            ?? throw ApiException.Conflict(FileErrors.NoUpload, "No upload of this file: send its chunks first.");
        var options = store.Options;
        string? stored = null;
        try
        {
            if (upload.ReceivedBytes != request.Size || request.Size != file.SizeBytes)
            {
                throw Mismatch(FileErrors.SizeMismatch, $"Received {upload.ReceivedBytes} bytes; the commit says {request.Size}, the file {file.SizeBytes}.");
            }
            var actual = await FileStore.HashAsync(upload.TempPath, ct);
            if (actual != sha || sha != file.Sha256)
            {
                throw Mismatch(FileErrors.HashMismatch, "The bytes received have another SHA-256 than the commit or the file says.");
            }
            if (FileStore.BlockedExtension(file.FileName, options.BlockedExtensionSet) is { } blocked)
            {
                throw new ApiException(StatusCodes.Status422UnprocessableEntity, FileErrors.BlockedType,
                    $"Files ending in .{blocked} can't be stored (Files:BlockedExtensions).");
            }
            stored = upload.TempPath;
            var size = upload.ReceivedBytes;
            ImageInfo? image = null;
            if (FilesOptions.IsImage(file.Purpose))
            {
                var (stripped, info, refusal) = ImageCheck.Check(await File.ReadAllBytesAsync(upload.TempPath, ct));
                if (refusal is not null)
                {
                    throw refusal.TooLarge
                        ? new ApiException(StatusCodes.Status413PayloadTooLarge, FileErrors.TooLarge, refusal.Message)
                        : new ApiException(StatusCodes.Status415UnsupportedMediaType, FileErrors.NotAnImage, refusal.Message);
                }
                image = info;
                if (stripped!.Length != size || Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stripped)) != sha)
                {
                    stored = await store.WriteTempAsync(stripped, ct);
                    size = stripped.Length;
                    sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stripped));
                }
            }
            var now = Clock.UtcNow;
            var name = image is null ? file.FileName : FileNames.ImageName(file.FileName, image.Extension);
            var relative = $"{now:yyyy}/{now:MM}/{id}-{FileNames.Slug(name)}";
            store.Place(stored, relative);
            var placed = relative;
            try
            {
                await using var write = await writer.BeginAsync(viewer, ChangeSource.Ui, accessor.HttpContext?.TraceIdentifier, ct);
                using var _ = ChangeContext.Use(write.Context());
                var row = await db.Files.FirstAsync(f => f.Id == id, ct);
                row.Status = FileStatus.Available;
                row.StoragePath = placed;
                row.Sha256 = sha;
                row.SizeBytes = size;
                if (image is not null)
                {
                    row.ContentType = image.ContentType;
                    row.Width = image.Width;
                    row.Height = image.Height;
                    row.FileName = name.Length <= FileNames.MaxName ? name : row.FileName;
                }
                db.FileUploads.Remove(await db.FileUploads.FirstAsync(u => u.FileId == id, ct));
                await db.SaveChangesAsync(ct);
                await write.CommitAsync(ct);
                FileStore.DeleteQuietly(upload.TempPath);
                return Committed(row);
            }
            catch
            {
                store.Remove(placed);
                throw;
            }
        }
        finally
        {
            if (stored is not null && stored != upload.TempPath)
            {
                FileStore.DeleteQuietly(stored);
            }
        }

        ApiException Mismatch(string code, string message)
        {
            // The upload is of no use: the client starts over.
            FileStore.DeleteQuietly(upload.TempPath);
            db.FileUploads.Remove(upload);
            db.SaveChanges();
            return ApiException.Conflict(code, message);
        }
    }

    private static FileCommitted Committed(FileEntity f) =>
        new(f.Id, EnumText.Of(f.Status), f.Sha256, f.SizeBytes, f.ContentType, f.Width, f.Height, f.FileName, f.Rev, f.Seq);

    /// <summary>
    /// A stored file for <c>GET /api/v1/files/{id}/{name}</c> (read access to its item or map): its row and where it is.
    /// 404 <c>file_pending</c> before its bytes came, 404 when they are gone.
    /// </summary>
    public async Task<(FileEntity File, string Path)> OpenAsync(Viewer viewer, Guid id, CancellationToken ct)
    {
        var file = await FileAsync(viewer, id, ItemAccess.Read, ct);
        if (file.Status != FileStatus.Available)
        {
            throw new ApiException(StatusCodes.Status404NotFound, FileErrors.Pending, "The file's bytes haven't been uploaded yet.");
        }
        return (file, store.Locate(file.StoragePath) ?? throw ApiException.NotFound("File"));
    }
}
