using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;

namespace CleanArchitecture.Blazor.Application.Common.Interfaces.Caching;

/// <summary>
/// Composes the effective cache key from a request's declared key and its <see cref="CacheScope"/>.
/// <para>
/// Kept as a static so the composition is testable on its own and so both caching behaviours compose
/// identically - a scoped read and a scoped write that disagreed would be worse than no scoping.
/// </para>
/// </summary>
public static class CacheScopeKey
{
    /// <summary>
    /// Returns the key the cache should actually be addressed with.
    /// </summary>
    /// <param name="declaredKey">The request's own <c>CacheKey</c>.</param>
    /// <param name="scope">The scope the request declares.</param>
    /// <param name="user">
    /// The ambient user context. Must be non-null for any scope other than <see cref="CacheScope.Global"/>;
    /// callers are expected to have checked <see cref="RequiresUserContext"/> first.
    /// </param>
    public static string Compose(string declaredKey, CacheScope scope, UserContext? user)
    {
        if (scope == CacheScope.Global)
        {
            return declaredKey;
        }

        if (user is null)
        {
            throw new InvalidOperationException(
                $"Cache scope '{scope}' needs an ambient user context. Callers must check {nameof(RequiresUserContext)} and bypass the cache when there is none.");
        }

        return scope switch
        {
            CacheScope.PerUser => $"u:{user.UserId}|{declaredKey}",
            CacheScope.PerTenant => $"t:{user.TenantId}|{declaredKey}",
            CacheScope.PerUserAndTenant => $"u:{user.UserId}|t:{user.TenantId}|{declaredKey}",
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown cache scope.")
        };
    }

    /// <summary>
    /// True when the scope cannot be honoured without an ambient principal. A request in that
    /// position with no context must bypass the cache entirely rather than fall back to an unscoped
    /// key, which would be the exact cross-principal leak the scopes exist to prevent.
    /// </summary>
    public static bool RequiresUserContext(CacheScope scope) => scope != CacheScope.Global;
}
