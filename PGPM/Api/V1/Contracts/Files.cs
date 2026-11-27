namespace PGPM.Api.V1.Contracts;

/// <summary>The answer to a chunk: the bytes received so far.</summary>
public sealed record FileChunkReceived(long Received);

/// <summary><c>POST /api/v1/files/{id}/commit</c>: what the client says it sent.</summary>
public sealed class FileCommitRequest
{
    /// <summary>Hex SHA-256 of all bytes (the row's <c>sha256</c>).</summary>
    public string? Sha256 { get; set; }

    public long? Size { get; set; }
}

/// <summary>
/// A stored file: <c>status</c> <c>available</c>, with the stored bytes' hash and size (an image's metadata removed: they may
/// differ from what was sent), type and pixel size, and the row's new <c>rev</c>/<c>seq</c> (the sync delivers the row too).
/// </summary>
public sealed record FileCommitted(Guid Id, string Status, string Sha256, long SizeBytes, string ContentType, int? Width, int? Height, int Rev,
    long Seq);
