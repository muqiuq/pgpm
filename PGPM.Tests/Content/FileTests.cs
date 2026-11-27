using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PGPM.Files;
using PGPM.Lib.Helper;
using PGPM.Tests.Sync;
using static PGPM.Tests.Content.ContentKit;
using static PGPM.Tests.Sync.SyncKit;

namespace PGPM.Tests.Content;

/// <summary>
/// The file pipeline (docs/03 §10): the row through the sync, the bytes in chunks, the commit's checks (size, SHA-256, blocked
/// names, images by their bytes with their metadata stripped), the download (access, inline or attachment, ranges, ETag), the
/// shared bytes of a copy, and the cleanup and check tasks.
/// </summary>
[TestClass]
public sealed class FileTests
{
    private static TestApp app = null!;

    [ClassInitialize]
    public static void Start(TestContext _) => app = new TestApp();

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    /// <summary>A PNG of <paramref name="width"/> × <paramref name="height"/> with a text chunk (metadata to strip). Nothing is decoded: the data is filler.</summary>
    internal static byte[] Png(int width = 1, int height = 1, string text = "Author\0Somebody")
    {
        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        void Chunk(string type, byte[] data)
        {
            var length = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
            output.Write(length);
            output.Write(Encoding.ASCII.GetBytes(type));
            output.Write(data);
            output.Write(new byte[4]);
        }
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)height);
        header[8] = 8;
        header[9] = 2;
        Chunk("IHDR", header);
        Chunk("tEXt", Encoding.Latin1.GetBytes(text));
        Chunk("IDAT", [0x78, 0x9C, 0x63, 0x60, 0x00, 0x00, 0x00, 0x02, 0x00, 0x01]);
        Chunk("IEND", []);
        return output.ToArray();
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static JsonObject FileRow(Guid owner, string purpose, string name, string type, byte[] bytes, string ownerKey = "itemId") => new()
    {
        [ownerKey] = owner.ToString(), ["purpose"] = purpose, ["fileName"] = name, ["contentType"] = type, ["sizeBytes"] = bytes.Length, ["sha256"] = Sha(bytes),
    };

    private static async Task<HttpResponseMessage> ChunkAsync(HttpClient http, Guid id, int n, byte[] bytes) =>
        await http.PutAsync($"/api/v1/files/{id}/chunks/{n}", new ByteArrayContent(bytes) { Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") } });

    private static async Task<HttpResponseMessage> CommitAsync(HttpClient http, Guid id, string sha, long size) =>
        await http.PostJsonNodeAsync($"/api/v1/files/{id}/commit", new JsonObject { ["sha256"] = sha, ["size"] = size });

    private static async Task<(SyncClient Client, Guid Note)> OwnerAsync(Guid? group = null)
    {
        var user = await UserAsync(app, "files", group is { } g ? [g] : []);
        var client = new SyncClient(app.ClientFor(user, "Notes:Create"));
        var note = Ids.New();
        var batch = Ids.New();
        await client.SyncAllAsync(group is { } shared
            ? [Insert("items", note, Note("With files"), batch), Insert("item_shares", Ids.New(), Share(note, shared, "read"), batch)]
            : [Insert("items", note, Note("With files"))]);
        return (client, note);
    }

    [TestMethod]
    public async Task AnImageIsUploadedInChunksStrippedAndServedInline()
    {
        var (client, note) = await OwnerAsync();
        var png = Png();
        var id = Ids.New();
        Assert.AreEqual("accepted", Codes(await client.SyncAllAsync([Insert("files", id, FileRow(note, "cover", "Cover", "image/png", png))]))[0]);
        Assert.AreEqual("pending", client.Row("files", id).Str("status"));
        Assert.IsFalse(client.Row("files", id)!.ContainsKey("storagePath"), "never sent");
        using (var pending = await client.Http.GetAsync($"/api/v1/files/{id}/cover.png"))
        {
            Assert.AreEqual("file_pending", await pending.CodeAsync());
        }
        using (var first = await ChunkAsync(client.Http, id, 0, png[..20]))
        {
            Assert.AreEqual(20, (await first.JsonAsync()).Long("received"));
        }
        using (var second = await ChunkAsync(client.Http, id, 1, png[20..]))
        {
            Assert.AreEqual(png.Length, (await second.JsonAsync()).Long("received"));
        }
        using var commit = await CommitAsync(client.Http, id, Sha(png), png.Length);
        var committed = await commit.JsonAsync();
        Assert.AreEqual("available", committed.Str("status"));
        Assert.AreEqual(png.Length - 12 - "Author\0Somebody".Length, committed.Long("sizeBytes"), "the text chunk is gone");
        Assert.AreNotEqual(Sha(png), committed.Str("sha256"));
        Assert.AreEqual((1, 1), (committed.Int("width"), committed.Int("height")));
        await client.SyncAllAsync();
        var row = client.Row("files", id)!;
        Assert.AreEqual(("available", "Cover.png"), (row.Str("status"), row.Str("fileName")));

        using var download = await client.Http.GetAsync($"/api/v1/files/{id}/Cover.png");
        var bytes = await download.Content.ReadAsByteArrayAsync();
        Assert.AreEqual(committed.Str("sha256"), Sha(bytes));
        Assert.AreEqual("image/png", download.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual("inline", download.Content.Headers.ContentDisposition?.DispositionType);
        Assert.AreEqual($"\"{committed.Str("sha256")}\"", download.Headers.ETag?.Tag);
        Assert.Contains("immutable", download.Headers.CacheControl!.ToString());
        using var range = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/files/{id}/Cover.png") { Headers = { Range = new RangeHeaderValue(0, 7) } };
        using var partial = await client.Http.SendAsync(range);
        Assert.AreEqual(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.AreEqual(8, (await partial.Content.ReadAsByteArrayAsync()).Length);
        // The item's cover may now be the file.
        var item = client.Row("items", note)!;
        Assert.AreEqual("accepted", Codes(await client.SyncAllAsync([Patch("items", note, item.Int("rev"), ("imageFileId", null, id.ToString()))]))[0]);
        // A commit again (a retry) answers with the stored file.
        using var retry = await CommitAsync(client.Http, id, Sha(png), png.Length);
        Assert.AreEqual(committed.Str("sha256"), (await retry.JsonAsync()).Str("sha256"));
    }

    [TestMethod]
    public async Task TheCommitChecksWhatArrived()
    {
        var (client, note) = await OwnerAsync();
        var text = Encoding.UTF8.GetBytes("Minutes of the kick-off.");
        var (a, b, c, d) = (Ids.New(), Ids.New(), Ids.New(), Ids.New());
        var answer = await client.SyncAllAsync(
        [
            Insert("files", a, FileRow(note, "attachment", "minutes.txt", "text/plain", text)),
            Insert("files", b, FileRow(note, "cover", "fake.png", "image/png", text)),
            Insert("files", c, FileRow(note, "attachment", "invoice.pdf.exe", "application/pdf", text)),
            Insert("files", d, FileRow(note, "cover", "huge.png", "image/png", Png(20_000, 10))),
            Insert("files", Ids.New(), FileRow(note, "map_image", "map.png", "image/png", text)),
        ]);
        CollectionAssert.AreEqual(new[] { "accepted", "accepted", "validation_failed", "accepted", "validation_failed" }, Codes(answer),
            answer["results"]!.ToJsonString());

        // Out of order, and more than the row says.
        using (var skipped = await ChunkAsync(client.Http, a, 1, text))
        {
            Assert.AreEqual("chunk_out_of_order", await skipped.CodeAsync());
        }
        using (var tooMuch = await ChunkAsync(client.Http, a, 0, [.. text, .. text]))
        {
            Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, tooMuch.StatusCode);
        }
        // Another hash, another size: the upload is gone and must start over.
        (await ChunkAsync(client.Http, a, 0, text)).Dispose();
        using (var wrong = await CommitAsync(client.Http, a, new string('0', 64), text.Length))
        {
            Assert.AreEqual("sha256_mismatch", await wrong.CodeAsync());
        }
        using (var gone = await CommitAsync(client.Http, a, Sha(text), text.Length))
        {
            Assert.AreEqual("upload_missing", await gone.CodeAsync());
        }
        (await ChunkAsync(client.Http, a, 0, text[..5])).Dispose();
        using (var short_ = await CommitAsync(client.Http, a, Sha(text), text.Length))
        {
            Assert.AreEqual("size_mismatch", await short_.CodeAsync());
        }
        (await ChunkAsync(client.Http, a, 0, text)).Dispose();
        using (var ok = await CommitAsync(client.Http, a, Sha(text), text.Length))
        {
            Assert.AreEqual(HttpStatusCode.OK, ok.StatusCode);
        }
        using (var attachment = await client.Http.GetAsync($"/api/v1/files/{a}/minutes.txt"))
        {
            Assert.AreEqual("application/octet-stream", attachment.Content.Headers.ContentType?.MediaType);
            Assert.AreEqual("attachment", attachment.Content.Headers.ContentDisposition?.DispositionType);
            CollectionAssert.AreEqual(text, await attachment.Content.ReadAsByteArrayAsync());
        }
        // Images by their bytes: text isn't one, and 20,000 px is too wide.
        (await ChunkAsync(client.Http, b, 0, text)).Dispose();
        using (var notAnImage = await CommitAsync(client.Http, b, Sha(text), text.Length))
        {
            Assert.AreEqual("not_an_image", await notAnImage.CodeAsync());
        }
        var huge = Png(20_000, 10);
        (await ChunkAsync(client.Http, d, 0, huge)).Dispose();
        using (var tooLarge = await CommitAsync(client.Http, d, Sha(huge), huge.Length))
        {
            Assert.AreEqual("too_large", await tooLarge.CodeAsync());
        }
        // A chunk has at most 4 MiB.
        var big = new byte[FileStore.MaxChunkBytes + 1];
        var e = Ids.New();
        await client.SyncAllAsync([Insert("files", e, FileRow(note, "attachment", "big.bin", "application/octet-stream", big))]);
        using (var oversized = await ChunkAsync(client.Http, e, 0, big))
        {
            Assert.AreEqual("too_large", await oversized.CodeAsync());
        }
    }

    [TestMethod]
    public async Task OnlyEditorsUploadAndReadersDownload()
    {
        var group = await GroupAsync(app, "readers");
        var (client, note) = await OwnerAsync(group);
        var reader = new SyncClient(app.ClientFor(await UserAsync(app, "reader", group), ""));
        var stranger = new SyncClient(app.ClientFor(await UserAsync(app, "stranger"), ""));
        var text = Encoding.UTF8.GetBytes("Shared document.");
        var id = Ids.New();
        await client.SyncAllAsync([Insert("files", id, FileRow(note, "attachment", "doc.txt", "text/plain", text))]);
        using (var refused = await ChunkAsync(reader.Http, id, 0, text))
        {
            Assert.AreEqual(HttpStatusCode.Forbidden, refused.StatusCode);
        }
        using (var hidden = await ChunkAsync(stranger.Http, id, 0, text))
        {
            Assert.AreEqual(HttpStatusCode.NotFound, hidden.StatusCode);
        }
        (await ChunkAsync(client.Http, id, 0, text)).Dispose();
        (await CommitAsync(client.Http, id, Sha(text), text.Length)).Dispose();
        using (var read = await reader.Http.GetAsync($"/api/v1/files/{id}/doc.txt"))
        {
            Assert.AreEqual(HttpStatusCode.OK, read.StatusCode);
        }
        using (var notYours = await stranger.Http.GetAsync($"/api/v1/files/{id}/doc.txt"))
        {
            Assert.AreEqual(HttpStatusCode.NotFound, notYours.StatusCode);
        }
        // A copy of readable bytes (a conversion, another item) needs no upload: it is stored at once.
        var (other, otherNote) = await OwnerAsync(group);
        var copy = Ids.New();
        await other.SyncAllAsync([Insert("files", copy, FileRow(otherNote, "attachment", "copy.txt", "text/plain", text))]);
        Assert.AreEqual("available", other.Row("files", copy).Str("status"));
        using var copied = await other.Http.GetAsync($"/api/v1/files/{copy}/copy.txt");
        CollectionAssert.AreEqual(text, await copied.Content.ReadAsByteArrayAsync());
        // A stranger's copy of the same bytes doesn't get them.
        var strangerNote = Ids.New();
        var strangerFile = Ids.New();
        var writer = new SyncClient(app.ClientFor(await UserAsync(app, "copier"), "Notes:Create"));
        await writer.SyncAllAsync([Insert("items", strangerNote, Note("Mine")), Insert("files", strangerFile, FileRow(strangerNote, "attachment", "x.txt", "text/plain", text))]);
        Assert.AreEqual("pending", writer.Row("files", strangerFile).Str("status"));
    }

    [TestMethod]
    public async Task TheCleanupRemovesStaleUploadsOrphansAndUnreferencedEmbeddedImages()
    {
        var (client, note) = await OwnerAsync();
        var png = Png(text: "x\0y");
        var (kept, dropped, stale) = (Ids.New(), Ids.New(), Ids.New());
        var description = $"See ![chart](/api/v1/files/{kept}/chart.png) and [the minutes][m].\n\n[m]: /api/v1/files/{Ids.New()}/minutes.pdf";
        await client.SyncAllAsync(
        [
            Insert("files", kept, FileRow(note, "embedded", "chart.png", "image/png", png)),
            Insert("files", dropped, FileRow(note, "embedded", "old.png", "image/png", Png(2, 2))),
            Insert("files", stale, FileRow(note, "attachment", "stale.txt", "text/plain", [1, 2, 3])),
        ]);
        var item = client.Row("items", note)!;
        await client.SyncAllAsync([Patch("items", note, item.Int("rev"), ("description", null, description))]);
        (await ChunkAsync(client.Http, stale, 0, [1, 2])).Dispose();
        var store = app.Services.GetRequiredService<FileStore>();
        var orphan = Path.Combine(store.Root, "2020", "01", $"{Ids.New()}-orphan.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(orphan)!);
        await File.WriteAllBytesAsync(orphan, [9]);
        File.SetLastWriteTimeUtc(orphan, DateTime.UtcNow.AddHours(-2));
        await using (var db = app.NewDb())
        {
            await db.Files.Where(f => f.Id == dropped).ExecuteUpdateAsync(u => u.SetProperty(f => f.UnreferencedSince, Clock.UtcNow.AddDays(-31)));
            await db.FileUploads.Where(u => u.FileId == stale).ExecuteUpdateAsync(u => u.SetProperty(x => x.StartedAt, Clock.UtcNow.AddHours(-25)));
        }
        await using (var scope = app.Services.CreateAsyncScope())
        await using (var db = app.NewDb())
        {
            var result = await FileCleanup.RunOnceAsync(db, store, scope.ServiceProvider, CancellationToken.None);
            Assert.IsTrue(result.StaleUploads >= 1 && result.Orphans >= 1 && result.Deleted >= 1, result.ToString());
        }
        Assert.IsFalse(File.Exists(orphan));
        await using (var db = app.NewDb())
        {
            Assert.IsFalse(await db.FileUploads.AnyAsync(u => u.FileId == stale));
            Assert.IsTrue(await db.Files.AnyAsync(f => f.Id == dropped && f.Deleted), "unreferenced for 30 days");
            Assert.IsTrue(await db.Files.AnyAsync(f => f.Id == kept && !f.Deleted && f.UnreferencedSince == null), "the description names it");
        }
        await client.SyncAllAsync();
        Assert.IsNull(client.Row("files", dropped), "every device drops it");
        Assert.AreEqual(1, FileCleanup.ReferencedFiles([description]).Count(i => i == kept));
    }

    [TestMethod]
    public async Task TheCheckFindsMissingBytes()
    {
        var (client, note) = await OwnerAsync();
        var text = Encoding.UTF8.GetBytes("Will vanish.");
        var id = Ids.New();
        await client.SyncAllAsync([Insert("files", id, FileRow(note, "attachment", "gone.txt", "text/plain", text))]);
        (await ChunkAsync(client.Http, id, 0, text)).Dispose();
        (await CommitAsync(client.Http, id, Sha(text), text.Length)).Dispose();
        var store = app.Services.GetRequiredService<FileStore>();
        await using var db = app.NewDb();
        var path = await db.Files.Where(f => f.Id == id).Select(f => f.StoragePath).SingleAsync();
        File.Delete(store.Locate(path)!);
        var report = await FileCheck.RunOnceAsync(db, store, CancellationToken.None);
        Assert.IsGreaterThanOrEqualTo(1, report.Missing, report.ToString());
    }
}
