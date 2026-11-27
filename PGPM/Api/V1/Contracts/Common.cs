namespace PGPM.Api.V1.Contracts;

/// <summary>A page of an online-only list (docs/03 §3.3: audit log, users).</summary>
public sealed record Paged<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

/// <summary>Paging, sort and search of a list: <c>?page=2&amp;pageSize=50&amp;sort=-created&amp;search=jo</c>.</summary>
public class PageQuery
{
    public const int MaxPageSize = 500;

    /// <summary>1-based.</summary>
    public int Page { get; set; } = 1;

    public int PageSize { get; set; } = 50;

    /// <summary>A field name, "-" first for descending (<c>-created</c>). Each list documents its fields.</summary>
    public string? Sort { get; set; }

    /// <summary>Case-insensitive substring match.</summary>
    public string? Search { get; set; }
}

/// <summary>An object by id and name.</summary>
public sealed record Ref(Guid Id, string Name);
