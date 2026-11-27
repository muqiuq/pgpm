using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using PGPM.Api.V1.Contracts;
using PGPM.Audit;
using PGPM.Files;
using PGPM.Sharing;

namespace PGPM.Api.V1;

/// <summary>
/// The bytes of <c>files</c> rows (docs/03 §10.2): uploaded in chunks and committed for a row the user may edit (its item's
/// or map's access), downloaded with read access. The rows themselves come and go through the sync.
/// </summary>
[Route("api/v1/files")]
public sealed class FilesController(FileService files) : V1ControllerBase
{
    /// <summary>
    /// One chunk of the file's bytes, the raw body (any content type), at most 4 MiB: chunk 0 starts the upload (over),
    /// every other one must be the next (409 <c>chunk_out_of_order</c> with <c>expected</c>). 413 <c>too_large</c> beyond the
    /// row's size or the purpose's limit, 429 <c>too_many_uploads</c>. Answers the bytes received so far.
    /// </summary>
    [HttpPut("{id:guid}/chunks/{n:int}")]
    [RawBody]
    [NotAudited("A file's bytes; the row's insert and commit are in the History.")]
    public async Task<FileChunkReceived> PutChunk(Guid id, int n, CancellationToken ct) =>
        await files.PutChunkAsync(await Viewer.OfAsync(HttpContext, ct), id, n, Request.Body, ct);

    /// <summary>
    /// The upload checked (<c>size</c> and <c>sha256</c> as received and as the row says, 409 <c>size_mismatch</c> /
    /// <c>sha256_mismatch</c>), an image's metadata removed (415 <c>not_an_image</c>, 413 <c>too_large</c> over 10,000 px a side
    /// or 40 MP), a blocked name refused (422 <c>blocked_type</c>), the bytes stored and the row <c>available</c>.
    /// </summary>
    [HttpPost("{id:guid}/commit")]
    [NotAudited("A file's bytes; the row's change is in the History.")]
    public async Task<FileCommitted> Commit(Guid id, FileCommitRequest request, CancellationToken ct) =>
        await files.CommitAsync(await Viewer.OfAsync(HttpContext, ct), id, request, ct);

    /// <summary>
    /// The bytes (read access to the file's item or map): images (covers, map and embedded images) inline as their type,
    /// everything else <c>application/octet-stream</c> as an attachment; ranges, ETag = the SHA-256, cached for a year (files
    /// never change). The name is only there to be read. 404 <c>file_pending</c> before the upload.
    /// </summary>
    [HttpGet("{id:guid}/{name}")]
    [BinaryResponse]
    public async Task<IActionResult> Download(Guid id, string name, CancellationToken ct)
    {
        var (file, path) = await files.OpenAsync(await Viewer.OfAsync(HttpContext, ct), id, ct);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        Response.Headers.CacheControl = "private, max-age=31536000, immutable";
        var etag = new EntityTagHeaderValue($"\"{file.Sha256}\"");
        var modified = new DateTimeOffset(DateTime.SpecifyKind(file.CreatedAt, DateTimeKind.Utc));
        if (FilesOptions.IsImage(file.Purpose) && ImageCheck.ExtensionOf(file.ContentType) is not null)
        {
            var inline = new ContentDispositionHeaderValue("inline");
            inline.SetHttpFileName(FileNames.NameOf(file));
            Response.Headers.ContentDisposition = inline.ToString();
            return File(stream, file.ContentType, modified, etag, enableRangeProcessing: true);
        }
        return File(stream, "application/octet-stream", FileNames.NameOf(file), modified, etag, enableRangeProcessing: true);
    }
}

/// <summary>An action that reads its raw body itself (a file chunk): OpenAPI documents it as <c>application/octet-stream</c>.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RawBodyAttribute : Attribute;

/// <summary>An action that answers with a file's bytes: OpenAPI documents a binary 200.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class BinaryResponseAttribute : Attribute;
