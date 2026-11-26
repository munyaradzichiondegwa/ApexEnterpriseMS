using Microsoft.EntityFrameworkCore;

namespace ApexERP.API.Common
{
    /// <summary>Page of results plus the total, for list screens that must scale beyond a few hundred rows (spec 27).</summary>
    public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total)
    {
        public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(Total / (double)PageSize);
    }

    public static class Paging
    {
        public const int MaxPageSize = 200;

        public static (int Page, int PageSize) Normalise(int? page, int pageSize) =>
            (Math.Max(1, page ?? 1), Math.Clamp(pageSize <= 0 ? 50 : pageSize, 1, MaxPageSize));

        /// <summary>The query must already be ordered; paging an unordered query returns unstable pages.</summary>
        public static async Task<PagedResult<T>> ToPagedAsync<T>(this IQueryable<T> ordered, int? page, int pageSize, CancellationToken ct = default)
        {
            var (p, size) = Normalise(page, pageSize);
            var total = await ordered.CountAsync(ct);
            var items = await ordered.Skip((p - 1) * size).Take(size).ToListAsync(ct);
            return new PagedResult<T>(items, p, size, total);
        }
    }
}
