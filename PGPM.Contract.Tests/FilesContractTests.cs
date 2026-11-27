using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using static PGPM.Contract.Tests.ContractKit;

namespace PGPM.Contract.Tests;

/// <summary>
/// The file transfer of a running PGPM (docs/03 §10.2–10.3) as the client's pipeline (PGPM.WebUI/src/local/files.ts) uses
/// it: the <c>files</c> row through the sync (server-owned members ignored), the chunks in order, the commit's checks and
/// its answer – an image's metadata stripped, so the stored hash, size and name differ from the uploaded ones –, the
/// download by a reader, the refusals the client tells apart (<c>chunk_out_of_order</c>, <c>sha256_mismatch</c>,
/// <c>upload_missing</c>, <c>file_available</c>, <c>file_pending</c>, <c>missing_access</c>), and the shared bytes of a copy.
/// </summary>
[TestClass]
public sealed class FilesContractTests
{
    /// <summary>A real PNG (2 × 2) with a tEXt chunk – the metadata the commit strips – and correct CRCs.</summary>
    private static byte[] Png()
    {
        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        void Chunk(string type, byte[] data)
        {
            var length = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
            output.Write(length);
            var body = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            output.Write(body);
            var crc = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(body));
            output.Write(crc);
        }
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, 2);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), 2);
        header[8] = 8;
        header[9] = 2;
        using var pixels = new MemoryStream();
        using (var deflate = new ZLibStream(pixels, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write([0, 220, 38, 38, 22, 163, 74, 0, 37, 99, 235, 234, 179, 8]);
        }
        Chunk("IHDR", header);
        Chunk("tEXt", Encoding.Latin1.GetBytes("Author\0Somebody"));
        Chunk("IDAT", pixels.ToArray());
        Chunk("IEND", []);
        return output.ToArray();
    }

    /// <summary>PNG's CRC-32 of a chunk's type and data.</summary>
    private static uint Crc32(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }
        return ~crc;
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static Dictionary<string, object?> FileRow(string itemId, string purpose, string fileName, string contentType, byte[] bytes) => new()
    {
        ["itemId"] = itemId, ["purpose"] = purpose, ["fileName"] = fileName, ["contentType"] = contentType, ["sizeBytes"] = bytes.Length,
        ["sha256"] = Sha(bytes), ["width"] = 2, ["height"] = 2, ["thumbnail"] = null,
        // Server-owned: ignored on an insert (docs/03 §11.4).
        ["status"] = "available", ["storagePath"] = "../../etc/passwd",
    };

    [TestMethod]
    public async Task AnImageGoesUpInChunksIsStrippedAtTheCommitAndComesDownForAReader()
    {
        using var admin = await FirstAdmin.SignInAsync();
        var stamp = Guid.NewGuid().ToString("N")[..8];
        var groupId = await GroupAsync(admin, $"Contract files {stamp}", new() { ["Notes"] = "Create" });
        using var ana = await UserAsync(admin, $"ana-files-{stamp}", $"Ana {stamp}", groupId);
        using var ben = await UserAsync(admin, $"ben-files-{stamp}", $"Ben {stamp}", groupId);
        var anaDevice = new Device(ana);
        var benDevice = new Device(ben);
        await anaDevice.SyncAsync();

        // A note shared with the group at read – the owner and number sent are the server's to set – and its cover row.
        var batch = Guid.CreateVersion7();
        var noteId = Guid.CreateVersion7().ToString();
        var fileId = Guid.CreateVersion7().ToString();
        var png = Png();
        var inserted = await anaDevice.SyncAsync([
            Insert(batch, "items", noteId, new { id = noteId, type = "note", title = "With a cover", ownerUserId = ben.UserId, number = 999_999, tags = Array.Empty<string>() }),
            Insert(batch, "item_shares", Guid.CreateVersion7().ToString(), new { itemId = noteId, groupId, access = "read" }),
            Insert(batch, "files", fileId, FileRow(noteId, "cover", "Sketch", "image/png", png))]);
        foreach (var result in inserted.GetProperty("results").EnumerateArray())
        {
            Assert.AreEqual("accepted", result.GetProperty("status").GetString(), result.ToString());
        }
        var note = Rows(inserted, "items").Single(r => r.GetProperty("id").GetString() == noteId);
        Assert.AreEqual(ana.UserId, note.GetProperty("ownerUserId").GetString(), "ownerUserId is server-owned");
        Assert.AreNotEqual(999_999, note.GetProperty("number").GetInt32(), "number is server-owned");
        var row = Rows(inserted, "files").Single(r => r.GetProperty("id").GetString() == fileId);
        Assert.AreEqual("pending", row.GetProperty("status").GetString(), "status is server-owned");
        Assert.IsFalse(row.TryGetProperty("storagePath", out _), "never sent");
        Assert.AreEqual(Sha(png), row.GetProperty("sha256").GetString());

        // Before the bytes: the download is 404 file_pending.
        using (var pending = await ben.SendAsync(HttpMethod.Get, $"/api/v1/files/{fileId}/Sketch"))
        {
            Assert.AreEqual(HttpStatusCode.NotFound, pending.StatusCode);
            Assert.AreEqual("file_pending", await CodeAsync(pending));
        }
        // Chunks in order: chunk 1 first is refused with the chunk expected (the client starts over from 0).
        using (var early = await ana.SendBytesAsync(HttpMethod.Put, $"/api/v1/files/{fileId}/chunks/1", png[..10]))
        {
            Assert.AreEqual(HttpStatusCode.Conflict, early.StatusCode);
            var problem = await JsonAsync(early);
            Assert.AreEqual("chunk_out_of_order", problem.GetProperty("code").GetString());
            Assert.AreEqual(0, problem.GetProperty("expected").GetInt32());
        }
        // A reader may not upload: 403 missing_access.
        using (var reader = await ben.SendBytesAsync(HttpMethod.Put, $"/api/v1/files/{fileId}/chunks/0", png))
        {
            Assert.AreEqual(HttpStatusCode.Forbidden, reader.StatusCode);
            Assert.AreEqual("missing_access", await CodeAsync(reader));
        }

        async Task UploadAsync()
        {
            var half = png.Length / 2;
            using var first = await ana.SendBytesAsync(HttpMethod.Put, $"/api/v1/files/{fileId}/chunks/0", png[..half]);
            Assert.AreEqual(HttpStatusCode.OK, first.StatusCode, await first.Content.ReadAsStringAsync());
            Assert.AreEqual(half, (await JsonAsync(first)).GetProperty("received").GetInt64());
            using var second = await ana.SendBytesAsync(HttpMethod.Put, $"/api/v1/files/{fileId}/chunks/1", png[half..]);
            Assert.AreEqual(png.Length, (await JsonAsync(second)).GetProperty("received").GetInt64());
        }

        // A damaged upload: 409 sha256_mismatch, the upload discarded – a commit then is 409 upload_missing.
        await UploadAsync();
        using (var damaged = await ana.SendAsync(HttpMethod.Post, $"/api/v1/files/{fileId}/commit", new { sha256 = new string('0', 64), size = png.Length }))
        {
            Assert.AreEqual(HttpStatusCode.Conflict, damaged.StatusCode);
            Assert.AreEqual("sha256_mismatch", await CodeAsync(damaged));
        }
        using (var missing = await ana.SendAsync(HttpMethod.Post, $"/api/v1/files/{fileId}/commit", new { sha256 = Sha(png), size = png.Length }))
        {
            Assert.AreEqual(HttpStatusCode.Conflict, missing.StatusCode);
            Assert.AreEqual("upload_missing", await CodeAsync(missing));
        }

        // Again, committed: the metadata is gone – the stored hash, size and name are the row's now.
        await UploadAsync();
        var committed = await ana.CallAsync(HttpMethod.Post, $"/api/v1/files/{fileId}/commit", new { sha256 = Sha(png), size = png.Length });
        Assert.AreEqual(fileId, committed.GetProperty("id").GetString());
        Assert.AreEqual("available", committed.GetProperty("status").GetString());
        Assert.AreEqual("image/png", committed.GetProperty("contentType").GetString());
        Assert.AreEqual((2, 2), (committed.GetProperty("width").GetInt32(), committed.GetProperty("height").GetInt32()));
        Assert.AreEqual("Sketch.png", committed.GetProperty("fileName").GetString(), "an image's name ends in its type's extension");
        var storedSize = committed.GetProperty("sizeBytes").GetInt64();
        var storedSha = committed.GetProperty("sha256").GetString();
        Assert.AreEqual(png.Length - 12 - "Author\0Somebody".Length, storedSize, "the tEXt chunk is gone");
        Assert.AreNotEqual(Sha(png), storedSha);
        Assert.IsTrue(committed.GetProperty("rev").GetInt32() > 1);
        Assert.IsTrue(committed.GetProperty("seq").GetInt64() > 0);

        // Stored: another chunk is 409 file_available (the client counts it as uploaded); the commit again answers the same.
        using (var again = await ana.SendBytesAsync(HttpMethod.Put, $"/api/v1/files/{fileId}/chunks/0", png))
        {
            Assert.AreEqual(HttpStatusCode.Conflict, again.StatusCode);
            Assert.AreEqual("file_available", await CodeAsync(again));
        }
        var retry = await ana.CallAsync(HttpMethod.Post, $"/api/v1/files/{fileId}/commit", new { sha256 = Sha(png), size = png.Length });
        Assert.AreEqual(storedSha, retry.GetProperty("sha256").GetString());

        // Ben (read) gets the row with the stored facts and downloads exactly those bytes.
        var benPull = await benDevice.SyncAsync();
        var benRow = Rows(benPull, "files").Single(r => r.GetProperty("id").GetString() == fileId);
        Assert.AreEqual("available", benRow.GetProperty("status").GetString());
        Assert.AreEqual(storedSha, benRow.GetProperty("sha256").GetString());
        Assert.AreEqual(storedSize, benRow.GetProperty("sizeBytes").GetInt64());
        Assert.AreEqual("Sketch.png", benRow.GetProperty("fileName").GetString());
        using (var download = await ben.SendAsync(HttpMethod.Get, $"/api/v1/files/{fileId}/Sketch.png"))
        {
            Assert.AreEqual(HttpStatusCode.OK, download.StatusCode);
            var bytes = await download.Content.ReadAsByteArrayAsync();
            Assert.AreEqual(storedSha, Sha(bytes), "the client checks a download against the row's sha256");
            Assert.AreEqual("image/png", download.Content.Headers.ContentType?.MediaType);
            Assert.AreEqual("inline", download.Content.Headers.ContentDisposition?.DispositionType);
            Assert.AreEqual($"\"{storedSha}\"", download.Headers.ETag?.Tag);
        }

        // Shared bytes: a copy (another note, as a conversion makes) with the stored hash and size is available at once.
        var copyBatch = Guid.CreateVersion7();
        var otherNote = Guid.CreateVersion7().ToString();
        var copyId = Guid.CreateVersion7().ToString();
        byte[] stored;
        using (var mine = await ana.SendAsync(HttpMethod.Get, $"/api/v1/files/{fileId}/Sketch.png"))
        {
            stored = await mine.Content.ReadAsByteArrayAsync();
        }
        var copied = await anaDevice.SyncAsync([
            Insert(copyBatch, "items", otherNote, new { id = otherNote, type = "note", title = "The copy", tags = Array.Empty<string>() }),
            Insert(copyBatch, "files", copyId, FileRow(otherNote, "cover", "Sketch.png", "image/png", stored))]);
        Assert.AreEqual("accepted", copied.GetProperty("results")[1].GetProperty("status").GetString(), copied.GetProperty("results").ToString());
        Assert.AreEqual("available", Rows(copied, "files").Single(r => r.GetProperty("id").GetString() == copyId).GetProperty("status").GetString());
        using (var shared = await ana.SendAsync(HttpMethod.Get, $"/api/v1/files/{copyId}/Sketch.png"))
        {
            Assert.AreEqual(storedSha, Sha(await shared.Content.ReadAsByteArrayAsync()));
        }

        // A blocked name never becomes a row: validation_failed with the field.
        var blockedId = Guid.CreateVersion7().ToString();
        var blocked = await anaDevice.SyncAsync([
            Insert(Guid.CreateVersion7(), "files", blockedId, FileRow(noteId, "attachment", "invoice.exe.pdf", "application/pdf", [1, 2, 3]))], pull: false);
        var refusal = blocked.GetProperty("results")[0];
        Assert.AreEqual("rejected", refusal.GetProperty("status").GetString());
        Assert.AreEqual("validation_failed", refusal.GetProperty("code").GetString());
        Assert.IsTrue(refusal.GetProperty("errors").TryGetProperty("fileName", out _), refusal.ToString());
    }
}
