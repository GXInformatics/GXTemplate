namespace CleanArchitecture.Blazor.Application.Common.Interfaces.Caching;

/// <summary>
/// Declares whose data a cached response belongs to. The caching pipeline reads this and folds the
/// matching identity components into the cache key, so principal scoping is structural rather than
/// something each query re-implements by hand in a <c>ToString()</c>.
/// <para>
/// <b>A declaration is not a correctness proof.</b> Choosing the right scope stays the developer's
/// job: the pipeline can guarantee that entries are separated exactly as declared, but it cannot know
/// what a given query actually depends on. Declaring <see cref="Global"/> for a query whose results
/// vary by user still leaks between users - it just does so visibly, at a declaration you can review,
/// instead of invisibly, through a key someone forgot to extend.
/// </para>
/// </summary>
public enum CacheScope
{
    /// <summary>
    /// The response is identical for every principal, so one entry serves everyone. Correct only when
    /// the query applies no per-user or per-tenant filtering of any kind.
    /// </summary>
    Global = 0,

    /// <summary>
    /// The response depends on which user asked. Entries are separated by user id.
    /// </summary>
    PerUser = 1,

    /// <summary>
    /// The response depends on which tenant the caller belongs to, but not on which user within it.
    /// Entries are separated by tenant id; callers with no tenant share the "no tenant" partition,
    /// which is the same thing the query itself would see.
    /// </summary>
    PerTenant = 2,

    /// <summary>
    /// The response depends on both. Entries are separated by user id and tenant id together - the
    /// strictest scope, and the right default when in doubt.
    /// </summary>
    PerUserAndTenant = 3
}
