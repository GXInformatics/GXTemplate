using CleanArchitecture.Blazor.Application.Common.Interfaces.Caching;
using ZiggyCreatures.Caching.Fusion;

namespace CleanArchitecture.Blazor.Infrastructure.Services.Caching;

public class FusionAppCache : IAppCache
{
    private readonly IFusionCache _cache;

    public FusionAppCache(IFusionCache cache)
    {
        _cache = cache;
    }

    public Task<T> GetOrSetAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        IEnumerable<string>? tags = null,
        CacheEntryOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return _cache.GetOrSetAsync(
            key,
            _ => factory(cancellationToken),
            options: ToEntryOptions(options),
            tags: tags).AsTask();
    }

    /// <summary>
    /// Starts from the configured defaults and changes only what the caller asked for, so an entry
    /// that opts out of fail-safe still inherits the global duration, timeouts and jitter.
    /// </summary>
    private FusionCacheEntryOptions? ToEntryOptions(CacheEntryOptions? options)
    {
        if (options is null || options.AllowStaleOnFailure)
        {
            return null;
        }

        var entryOptions = _cache.DefaultEntryOptions.Duplicate();
        entryOptions.IsFailSafeEnabled = false;
        return entryOptions;
    }

    public void Remove(string key)
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            _cache.Remove(key);
        }
    }

    public Task RemoveByTagAsync(string tag, CancellationToken cancellationToken = default)
    {
        return string.IsNullOrWhiteSpace(tag)
            ? Task.CompletedTask
            : _cache.RemoveByTagAsync(tag, token: cancellationToken).AsTask();
    }

    public async Task RemoveByTagsAsync(IEnumerable<string>? tags, CancellationToken cancellationToken = default)
    {
        if (tags is null)
        {
            return;
        }

        foreach (var tag in tags.Where(static x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal))
        {
            await _cache.RemoveByTagAsync(tag, token: cancellationToken);
        }
    }
}
