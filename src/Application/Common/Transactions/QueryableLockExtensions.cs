namespace CleanArchitecture.Blazor.Application.Common.Transactions;

/// <summary>Row locking for posting handlers (Pass 58).</summary>
public static class QueryableLockExtensions
{
    /// <summary>The query tag the Infrastructure command interceptor turns into <c>FOR UPDATE</c>.</summary>
    public const string ForUpdateTag = "gx:for-update";

    /// <summary>
    /// Runs the query as <c>SELECT … FOR UPDATE</c>: the returned rows stay locked until the unit of
    /// work commits or rolls back, and another transaction locking them waits.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description><b>Inside a transaction only.</b> Outside one the lock would be released as
    /// soon as the statement ends, so the interceptor throws.</description></item>
    /// <item><description><b>One table, no JOIN.</b> PostgreSQL refuses <c>FOR UPDATE</c> on the
    /// nullable side of an outer join, so <c>Include</c> and auto-included navigations
    /// (use <c>IgnoreAutoIncludes()</c>) are refused.</description></item>
    /// <item><description><b>Ordered.</b> Rows are locked in the order they are returned, so a
    /// multi-row lock needs an <c>OrderBy</c> IN THIS QUERY, following the global lock order:
    /// document header, document number, item cost rows by ItemId, stock balance rows by
    /// (ItemId, WarehouseId). A query with neither ORDER BY nor LIMIT is refused.</description></item>
    /// <item><description>PostgreSQL only; other providers throw
    /// <see cref="NotSupportedException"/>.</description></item>
    /// </list>
    /// </remarks>
    public static IQueryable<T> ForUpdate<T>(this IQueryable<T> query) => query.TagWith(ForUpdateTag);
}
