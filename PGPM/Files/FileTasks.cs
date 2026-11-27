using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.EntityFrameworkCore;
using PGPM.Auth.Rights;
using PGPM.Config;
using PGPM.Data;
using PGPM.Lib.Helper;
using PGPM.Sharing;
using PGPM.Sync;

namespace PGPM.Files;

/// <summary>
/// The files' cleanup (docs/03 §9, PNMTD + PLM), hourly: uploads not committed within 24 h (and leftovers in <c>.tmp/</c>),
/// stored bytes no row names any more (the rows were purged, or their tombstones pruned), and <c>embedded</c> images no
/// text of their item references for 30 days (Markdig over the description, the <c>markdown</c> field values and the
/// milestones' descriptions) – tombstoned through a <see cref="SyncWrite"/>, so every device drops them. The delay keeps
/// History restores working.
/// </summary>
public static partial class FileCleanup
{
    public const string HealthKey = "task.file_cleanup";

    public static readonly TimeSpan StaleUpload = TimeSpan.FromHours(24);

    /// <summary>Stored bytes younger than this are never orphans: a commit may be moving them into place.</summary>
    public static readonly TimeSpan OrphanAge = TimeSpan.FromHours(1);

    public static readonly TimeSpan EmbeddedUnreferenced = TimeSpan.FromDays(30);

    public sealed record Result(int StaleUploads, int Orphans, int Unreferenced, int Deleted)
    {
        public override string ToString() =>
            $"{StaleUploads} stale uploads, {Orphans} orphaned files removed; {Unreferenced} embedded files unreferenced, {Deleted} deleted";
    }

    public static async Task<Result> RunOnceAsync(PgpmDbContext db, FileStore store, IServiceProvider services, CancellationToken ct)
    {
        var now = Clock.UtcNow;
        // 1. Uploads that never got their commit.
        var staleBefore = now - StaleUpload;
        var stale = await db.FileUploads.Where(u => u.StartedAt < staleBefore).ToListAsync(ct);
        foreach (var upload in stale)
        {
            FileStore.DeleteQuietly(upload.TempPath);
        }
        db.FileUploads.RemoveRange(stale);
        await db.SaveChangesAsync(ct);
        var temp = Path.Combine(store.Root, FileStore.TempFolder);
        if (Directory.Exists(temp))
        {
            var inUse = (await db.FileUploads.AsNoTracking().Select(u => u.TempPath).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
            foreach (var leftover in new DirectoryInfo(temp).EnumerateFiles().Where(f => now - f.LastWriteTimeUtc > StaleUpload && !inUse.Contains(f.FullName)))
            {
                FileStore.DeleteQuietly(leftover.FullName);
            }
        }

        // 2. Bytes no row names (purged, pruned): yyyy/MM/{id}-{slug}.
        var orphans = 0;
        if (Directory.Exists(store.Root))
        {
            var known = (await db.Files.IgnoreQueryFilters().AsNoTracking().Where(f => f.StoragePath != null).Select(f => f.StoragePath!).ToListAsync(ct))
                .ToHashSet(StringComparer.Ordinal);
            foreach (var year in new DirectoryInfo(store.Root).EnumerateDirectories().Where(d => YearPattern().IsMatch(d.Name)))
            {
                foreach (var month in year.EnumerateDirectories().Where(d => MonthPattern().IsMatch(d.Name)))
                {
                    foreach (var file in month.EnumerateFiles())
                    {
                        var relative = $"{year.Name}/{month.Name}/{file.Name}";
                        if (!known.Contains(relative) && now - file.LastWriteTimeUtc > OrphanAge && file.LinkTarget is null)
                        {
                            store.Remove(relative);
                            orphans++;
                        }
                    }
                }
            }
        }

        // 3. Embedded images their item's texts no longer name.
        var (unreferenced, deleted) = await EmbeddedAsync(db, services, now, ct);
        return new Result(stale.Count, orphans, unreferenced, deleted);
    }

    private static async Task<(int Unreferenced, int Deleted)> EmbeddedAsync(PgpmDbContext db, IServiceProvider services, DateTime now, CancellationToken ct)
    {
        var embedded = await db.Files.AsNoTracking().Where(f => f.Purpose == FilePurpose.Embedded && !f.Deleted && f.ItemId != null)
            .Select(f => new { f.Id, ItemId = f.ItemId!.Value, f.UnreferencedSince, f.CreatedAt }).ToListAsync(ct);
        if (embedded.Count == 0)
        {
            return (0, 0);
        }
        var markdownFields = await db.FieldDefinitions.AsNoTracking().Where(f => f.Type == FieldType.Markdown).Select(f => f.Id).ToListAsync(ct);
        var due = new List<Guid>();
        var unreferenced = 0;
        foreach (var group in embedded.GroupBy(f => f.ItemId))
        {
            var texts = new List<string?>
            {
                await db.Items.AsNoTracking().Where(i => i.Id == group.Key).Select(i => i.Description).FirstOrDefaultAsync(ct),
            };
            texts.AddRange(await db.Milestones.AsNoTracking().Where(m => m.ItemId == group.Key && !m.Deleted).Select(m => m.Description).ToListAsync(ct));
            texts.AddRange((await db.FieldValues.AsNoTracking().Where(v => v.ItemId == group.Key && !v.Deleted && markdownFields.Contains(v.FieldId))
                .Select(v => v.Value).ToListAsync(ct)).Select(TextOf));
            var referenced = ReferencedFiles(texts);
            foreach (var file in group)
            {
                if (referenced.Contains(file.Id))
                {
                    if (file.UnreferencedSince is not null)
                    {
                        await db.Files.Where(f => f.Id == file.Id).ExecuteUpdateAsync(u => u.SetProperty(f => f.UnreferencedSince, (DateTime?)null), ct);
                    }
                    continue;
                }
                unreferenced++;
                if (file.UnreferencedSince is null)
                {
                    // Counted from its creation: a paste the text never kept goes after 30 days too.
                    var since = file.CreatedAt < now ? file.CreatedAt : now;
                    await db.Files.Where(f => f.Id == file.Id).ExecuteUpdateAsync(u => u.SetProperty(f => f.UnreferencedSince, (DateTime?)since), ct);
                    if (now - since > EmbeddedUnreferenced)
                    {
                        due.Add(file.Id);
                    }
                }
                else if (now - file.UnreferencedSince.Value > EmbeddedUnreferenced)
                {
                    due.Add(file.Id);
                }
            }
        }
        if (due.Count > 0)
        {
            var system = new Viewer(Guid.Empty, true, [], new UserRights(true, new Dictionary<Area, Level>()));
            await using var write = await new SyncWriter(db, services).BeginAsync(system, ChangeSource.System, null, ct);
            using var _ = ChangeContext.Use(ChangeContext.System);
            var table = SyncTables.Get("files");
            foreach (var row in await db.Files.Where(f => due.Contains(f.Id) && !f.Deleted).ToListAsync(ct))
            {
                var batch = write.NewBatch(DeleteRootKind.Other, "files", row.Id);
                await table.MarkDeletedAsync(write, row, batch.Id, ct);
            }
            await db.SaveChangesAsync(ct);
            await write.CommitAsync(ct);
        }
        return (unreferenced, due.Count);
    }

    /// <summary>A <c>markdown</c> field's value (a JSON string) as its text.</summary>
    private static string? TextOf(string json)
    {
        try
        {
            return JsonNode.Parse(json) is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    /// <summary>The ids of <c>/api/v1/files/{id}/…</c> links and images in Markdown texts (docs/03 §18.3).</summary>
    public static HashSet<Guid> ReferencedFiles(IEnumerable<string?> texts)
    {
        var ids = new HashSet<Guid>();
        foreach (var text in texts)
        {
            if (string.IsNullOrEmpty(text) || !text.Contains(FileNames.UrlPrefix, StringComparison.Ordinal))
            {
                continue;
            }
            var document = Markdown.Parse(text, Pipeline);
            var urls = document.Descendants<LinkInline>().Select(l => l.Url)
                .Concat(document.Descendants<AutolinkInline>().Select(l => l.Url))
                .Concat(document.Descendants<LinkReferenceDefinition>().Select(d => d.Url));
            foreach (var url in urls)
            {
                if (url is not null && FileUrl().Match(url) is { Success: true } match && Guid.TryParse(match.Groups[1].Value, out var id))
                {
                    ids.Add(id);
                }
            }
        }
        return ids;
    }

    [GeneratedRegex(@"^(?:https?://[^/]+)?/api/v1/files/([0-9a-fA-F-]{36})(?:/|$)")]
    private static partial Regex FileUrl();

    [GeneratedRegex(@"^\d{4}$")]
    private static partial Regex YearPattern();

    [GeneratedRegex(@"^\d{2}$")]
    private static partial Regex MonthPattern();
}

/// <summary><see cref="FileCleanup"/> hourly.</summary>
public sealed class FileCleanupTask(IDbContextFactory<PgpmDbContext> dbs, FileStore store, IServiceProvider services, ILogger<FileCleanupTask> logger)
    : PeriodicSyncTask(FileCleanup.HealthKey, TimeSpan.FromHours(1), dbs, logger)
{
    protected override async Task<string> RunAsync(PgpmDbContext db, CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        return (await FileCleanup.RunOnceAsync(db, store, scope.ServiceProvider, ct)).ToString();
    }
}

/// <summary>
/// The files folder's check (docs/03 §9, PNMTD's FileCheckTask), hourly: the folder exists and is writable, its disk has
/// Files:MinFreeMb free, and every available file's bytes are there with their size. Problems are logged as warnings and
/// in the task's health entry.
/// </summary>
public static class FileCheck
{
    public const string HealthKey = "task.file_check";

    public sealed record Report(IReadOnlyList<string> Problems, int Files, int Missing, int Changed, long? FreeBytes)
    {
        public override string ToString() => Problems.Count == 0
            ? $"File storage fine: {Files} files{(FreeBytes is { } free ? $", {free / (1024 * 1024)} MB free" : "")}."
            : $"File storage: {string.Join(" ", Problems)}";
    }

    public static async Task<Report> RunOnceAsync(PgpmDbContext db, FileStore store, CancellationToken ct)
    {
        var problems = new List<string>();
        int count = 0, missing = 0, changed = 0;
        var free = store.FreeBytes();
        if (store.Problem() is { } folder)
        {
            problems.Add(folder);
        }
        else
        {
            if (free is { } bytes && bytes < store.Options.MinFreeMb * 1024 * 1024)
            {
                problems.Add($"Only {bytes / (1024 * 1024)} MB free on the files folder's disk (Files:MinFreeMb {store.Options.MinFreeMb}).");
            }
            var files = await db.Files.AsNoTracking().Where(f => f.Status == FileStatus.Available && f.StoragePath != null)
                .Select(f => new { f.StoragePath, f.SizeBytes }).Distinct().ToListAsync(ct);
            count = files.Count;
            foreach (var file in files)
            {
                if (store.Locate(file.StoragePath) is not { } path)
                {
                    missing++;
                }
                else if (new FileInfo(path).Length != file.SizeBytes)
                {
                    changed++;
                }
            }
            if (missing > 0)
            {
                problems.Add($"{missing} file(s) missing.");
            }
            if (changed > 0)
            {
                problems.Add($"{changed} file(s) changed.");
            }
        }
        return new Report(problems, count, missing, changed, free);
    }
}

/// <summary><see cref="FileCheck"/> hourly.</summary>
public sealed class FileCheckTask(IDbContextFactory<PgpmDbContext> dbs, FileStore store, ILogger<FileCheckTask> logger)
    : PeriodicSyncTask(FileCheck.HealthKey, TimeSpan.FromHours(1), dbs, logger)
{
    protected override async Task<string> RunAsync(PgpmDbContext db, CancellationToken ct)
    {
        var report = await FileCheck.RunOnceAsync(db, store, ct);
        if (report.Problems.Count > 0)
        {
            logger.LogWarning("{Report}", report);
        }
        return report.ToString();
    }
}
