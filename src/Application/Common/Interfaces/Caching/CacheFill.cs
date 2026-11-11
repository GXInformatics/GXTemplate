namespace CleanArchitecture.Blazor.Application.Common.Interfaces.Caching;

/// <summary>
/// Marks the call chain while <c>FusionCacheBehaviour</c> fills a cache entry (Pass 60, architecture rule 3).
/// </summary>
/// <remarks>
/// <b>Why at run time.</b> Rule 3 says a cacheable request never reads an append-only or branch-scoped table: a cached
/// ledger or stock figure is stale until a write happens to clear the same tag, and a cached branch-scoped list is
/// served to users with a different read set. Which table a handler reads lives in its LINQ, which no static rule
/// resolves reliably. So the cache behaviour marks the fill, and <c>GuardedTableCommandInterceptor</c> refuses any
/// SQL that reads such a table while the mark is set, naming the request and the table. It fails on the first
/// execution of the query, which any test of the query performs.
/// </remarks>
public static class CacheFill
{
    private sealed class Mark(string requestName, Mark? parent)
    {
        public string RequestName { get; } = requestName;
        public Mark? Parent { get; } = parent;
    }

    private static readonly AsyncLocal<Mark?> Ambient = new();

    /// <summary>The cacheable request whose entry is being filled, or null.</summary>
    public static string? Current => Ambient.Value?.RequestName;

    public static IDisposable Begin(string requestName)
    {
        var mark = new Mark(requestName, Ambient.Value);
        Ambient.Value = mark;
        return new End(mark);
    }

    private sealed class End(Mark mark) : IDisposable
    {
        public void Dispose()
        {
            if (Ambient.Value == mark) Ambient.Value = mark.Parent;
        }
    }
}
