// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;

namespace CleanArchitecture.Blazor.Application.Pipeline;

/// <summary>
/// Serves cacheable requests from the cache, keyed by the request's declared key folded together with
/// its <see cref="CacheScope"/>.
/// <para>
/// The scope components come from the ambient <see cref="IUserContextAccessor"/>, not from anything
/// the request carries. That is the point: a key built from a principal the calling page remembered
/// to pass is only as correct as that page, whereas the ambient context is the same principal the
/// authorization behaviour already checked.
/// </para>
/// <para>
/// <b>No ambient context and a non-Global scope means the cache is bypassed entirely</b> - the
/// handler runs, nothing is read, nothing is written. Falling back to an unscoped key would be the
/// exact cross-principal leak scopes exist to prevent. Like
/// <c>AuthorizationBehaviour</c>, this relies on <c>App.razor</c>'s
/// <c>new InteractiveServerRenderMode(false)</c>: with prerendering disabled, components dispatch
/// only over an established circuit, where the hub filter has populated the context. Re-enabling
/// prerendering would not break correctness here - it would silently stop caching first renders.
/// </para>
/// </summary>
public class FusionCacheBehaviour<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : ICacheableRequest<TResponse>
{
    /// <summary>
    /// Pipeline entries opt out of fail-safe. A duration is a staleness bound; serving an entry that
    /// has outlived it because the refresh failed silently breaks that bound, and a query whose data
    /// is stale beyond its own declared window is worse than one that takes the error.
    /// </summary>
    private static readonly CacheEntryOptions PipelineEntryOptions = new(AllowStaleOnFailure: false);

    private readonly IAppCache _cache;
    private readonly IUserContextAccessor _userContextAccessor;
    private readonly ILogger<FusionCacheBehaviour<TRequest, TResponse>> _logger;

    public FusionCacheBehaviour(
        IAppCache cache,
        IUserContextAccessor userContextAccessor,
        ILogger<FusionCacheBehaviour<TRequest, TResponse>> logger
    )
    {
        _cache = cache;
        _userContextAccessor = userContextAccessor;
        _logger = logger;
    }

    public async ValueTask<TResponse> Handle(TRequest request, MessageHandlerDelegate<TRequest, TResponse> next,
        CancellationToken cancellationToken)
    {
        var user = _userContextAccessor.Current;

        if (CacheScopeKey.RequiresUserContext(request.Scope) && user is null)
        {
            _logger.LogDebug(
                "Bypassing cache for {RequestType}: scope {Scope} needs an ambient user context and there is none.",
                typeof(TRequest).Name, request.Scope);
            return await next(request, cancellationToken).ConfigureAwait(false);
        }

        var cacheKey = CacheScopeKey.Compose(request.CacheKey, request.Scope, user);

        _logger.LogTrace("Handling request of type {RequestType} with cache key {CacheKey}",
            typeof(TRequest).Name, cacheKey);

        var response = await _cache.GetOrSetAsync(
            cacheKey,
            _ => next(request, cancellationToken).AsTask(),
            request.Tags,
            PipelineEntryOptions,
            cancellationToken
            ).ConfigureAwait(false);

        return response;
    }
}
