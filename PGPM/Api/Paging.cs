using Microsoft.EntityFrameworkCore;
using PGPM.Api.V1.Contracts;

namespace PGPM.Api;

/// <summary>Paging and sorting of the online-only lists (PNMTD's Paging).</summary>
internal static class Paging
{
    public static (int Page, int PageSize) Normalize(PageQuery query) =>
        (Math.Max(1, query.Page), Math.Clamp(query.PageSize, 1, PageQuery.MaxPageSize));

    public static async Task<Paged<T>> PageAsync<T>(IQueryable<T> query, PageQuery page, CancellationToken ct)
    {
        var (number, size) = Normalize(page);
        var total = await query.CountAsync(ct);
        var items = await query.Skip((number - 1) * size).Take(size).ToListAsync(ct);
        return new Paged<T>(items, number, size, total);
    }

    /// <summary>("name", false) for "name", ("name", true) for "-name"; the default when empty; 400 for another field.</summary>
    public static (string Field, bool Descending) Sort(string? sort, string defaultSort, params string[] allowed)
    {
        var value = string.IsNullOrWhiteSpace(sort) ? defaultSort : sort.Trim();
        var descending = value.StartsWith('-');
        var field = value.TrimStart('-', '+').ToLowerInvariant();
        if (!allowed.Contains(field))
        {
            throw ApiException.Invalid("sort", $"Sort by one of: {string.Join(", ", allowed)} (\"-\" first for descending).");
        }
        return (field, descending);
    }
}

internal static class SearchText
{
    /// <summary>A case-insensitive substring pattern for ILIKE, with % and _ taken literally.</summary>
    public static string? Pattern(string? search) =>
        string.IsNullOrWhiteSpace(search)
            ? null
            : "%" + search.Trim().Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_") + "%";
}
