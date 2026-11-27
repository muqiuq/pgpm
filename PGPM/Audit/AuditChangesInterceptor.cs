using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PGPM.Data;
using PGPM.Lib.Helper;

namespace PGPM.Audit;

/// <summary>
/// The changes of a write request, field by field (PNMTD, docs/03 §3.4): before each <c>SaveChanges</c> inside an
/// <see cref="AuditScope"/>, the added, modified and deleted rows of <see cref="AuditedAttribute"/> entities go into the
/// scope, and the scope's entry is added to the first save, so it commits with the change or not at all. Secrets are
/// recorded as changed, long texts as line counts, noise not at all. Bulk writes (<c>ExecuteUpdate</c>,
/// <c>ExecuteDelete</c>) bypass it: their services name the target themselves.
/// </summary>
public sealed class AuditChangesInterceptor : SaveChangesInterceptor
{
    public static readonly AuditChangesInterceptor Instance = new();

    private AuditChangesInterceptor()
    {
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Before(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Before(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        After(eventData.Context, saved: true);
        return result;
    }

    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        After(eventData.Context, saved: true);
        return ValueTask.FromResult(result);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData) => After(eventData.Context, saved: false);

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        After(eventData.Context, saved: false);
        return Task.CompletedTask;
    }

    private static void Before(DbContext? context)
    {
        if (context is not PgpmDbContext db || AuditScope.Current is not { } scope)
        {
            return;
        }
        db.ChangeTracker.DetectChanges();
        scope.Checkpoint();
        Collect(db.ChangeTracker, scope);
        if (scope.Context is null)
        {
            db.AuditLog.Add(scope.Apply());
            scope.Context = db;
        }
        else if (scope.Context == db)
        {
            scope.Apply();
            scope.Dirty = !scope.Persisted;
        }
    }

    private static void After(DbContext? context, bool saved)
    {
        if (AuditScope.Current is not { } scope)
        {
            return;
        }
        if (!saved)
        {
            // What this save brought never happened.
            scope.Restore();
        }
        if (scope.Context != context || scope.Persisted)
        {
            return;
        }
        if (saved)
        {
            scope.Persisted = true;
            scope.Dirty = false;
        }
        else
        {
            // The save failed: its transaction is gone, the entry with it. The filter writes it as failed.
            context!.Entry(scope.Entry).State = EntityState.Detached;
            scope.Context = null;
        }
    }

    /// <summary>The changed rows of audited entities, into the scope; the target, if the service named none.</summary>
    internal static void Collect(ChangeTracker tracker, AuditScope scope)
    {
        var subjectType = AuditCatalog.Get(scope.Event).SubjectType;
        foreach (var entry in tracker.Entries().ToList())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted) || !Policy.Of(entry.Metadata.ClrType).Audited)
            {
                continue;
            }
            var policy = Policy.Of(entry.Metadata.ClrType);
            var id = entry.Metadata.FindPrimaryKey() is { Properties: [{ ClrType: var t } key] } && t == typeof(Guid)
                ? (Guid?)entry.Property(key.Name).CurrentValue : null;
            var name = policy.NameProperty is { } p ? entry.Property(p).CurrentValue as string ?? entry.Property(p).OriginalValue as string : null;
            var label = name is null ? policy.Label : $"{policy.Label} {name}";
            var isTarget = subjectType == policy.Subject;
            if (isTarget && id is not null && (scope.Target is null || scope.Target.Id == id || scope.RouteId == id || scope.RouteId is null))
            {
                scope.SuggestTarget(new AuditTarget(policy.Subject, id, name));
            }
            switch (entry.State)
            {
                case EntityState.Added:
                    scope.AddChange(new AuditChange(label, policy.Label, null, null, "created"));
                    if (isTarget)
                    {
                        foreach (var property in entry.Properties)
                        {
                            if (Change(property, policy, added: true) is { } change)
                            {
                                scope.AddChange(change with { Object = label });
                            }
                        }
                    }
                    break;
                case EntityState.Deleted:
                    scope.AddChange(new AuditChange(label, policy.Label, null, null, "deleted"));
                    break;
                default:
                    foreach (var property in entry.Properties)
                    {
                        if (property.IsModified && Change(property, policy, added: false) is { } change)
                        {
                            scope.AddChange(change with { Object = label });
                        }
                    }
                    break;
            }
        }
    }

    private static AuditChange? Change(PropertyEntry property, Policy policy, bool added)
    {
        var meta = property.Metadata;
        if (meta.IsPrimaryKey() || policy.Ignored.Contains(meta.Name) || (meta.IsShadowProperty() && !meta.IsForeignKey()))
        {
            return null;
        }
        var old = added ? null : property.OriginalValue;
        var current = property.CurrentValue;
        if (added ? IsDefault(current) : Equals(old, current) || Format(old) == Format(current))
        {
            return null;
        }
        if (policy.Secrets.Contains(meta.Name))
        {
            return new AuditChange(null, meta.Name, null, null, "secret");
        }
        if (policy.Texts.Contains(meta.Name))
        {
            var (plus, minus) = LineCount(old as string, current as string);
            return new AuditChange(null, meta.Name, null, null, "text", $"+{plus} −{minus} lines");
        }
        return new AuditChange(null, meta.Name, AuditLog.Cut(Format(old), 500), AuditLog.Cut(Format(current), 500));
    }

    /// <summary>Lines added and removed, as a multiset difference (no diff algorithm: the counts are all the audit shows).</summary>
    internal static (int Plus, int Minus) LineCount(string? old, string? current)
    {
        static Dictionary<string, int> Lines(string? text) =>
            (text ?? "").Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).GroupBy(l => l).ToDictionary(g => g.Key, g => g.Count());
        var before = Lines(old);
        var after = Lines(current);
        var plus = after.Sum(kv => Math.Max(0, kv.Value - before.GetValueOrDefault(kv.Key)));
        var minus = before.Sum(kv => Math.Max(0, kv.Value - after.GetValueOrDefault(kv.Key)));
        return (plus, minus);
    }

    private static bool IsDefault(object? value) => value switch
    {
        null => true,
        string s => s.Length == 0,
        bool b => !b,
        _ => value.GetType().IsValueType && value.Equals(Activator.CreateInstance(value.GetType())),
    };

    internal static string? Format(object? value) => value switch
    {
        null => null,
        string s => s,
        DateTime d => Clock.AsUtc(d).ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        DateTimeOffset d => d.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        bool b => b ? "yes" : "no",
        byte[] bytes => $"({bytes.Length} bytes)",
        Enum e => e.ToString(),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        System.Collections.IEnumerable list => string.Join(", ", list.Cast<object?>().Select(Format)),
        _ => value.ToString(),
    };

    /// <summary>What an entity type records, read once from its attributes.</summary>
    private sealed record Policy(bool Audited, string Label, string Subject, string? NameProperty, IReadOnlySet<string> Secrets,
        IReadOnlySet<string> Ignored, IReadOnlySet<string> Texts)
    {
        private static readonly ConcurrentDictionary<Type, Policy> Cache = new();

        private static readonly string[] NameCandidates = ["Name", "Title", "Username", "DisplayName", "Key"];

        public static Policy Of(Type type) => Cache.GetOrAdd(type, Read);

        private static Policy Read(Type type)
        {
            var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            HashSet<string> With<T>() where T : Attribute => properties.Where(p => p.GetCustomAttribute<T>() is not null).Select(p => p.Name).ToHashSet();
            var label = type.Name.EndsWith("Entity", StringComparison.Ordinal) ? type.Name[..^"Entity".Length] : type.Name;
            return new Policy(
                type.GetCustomAttribute<AuditedAttribute>() is not null,
                label,
                label.ToLowerInvariant(),
                NameCandidates.FirstOrDefault(n => properties.Any(p => p.Name == n && p.PropertyType == typeof(string))),
                With<AuditSecretAttribute>(),
                With<AuditIgnoreAttribute>(),
                With<AuditTextAttribute>());
        }
    }
}
