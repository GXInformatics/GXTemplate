namespace CleanArchitecture.Blazor.Application.Common.Interfaces.Caching;

/// <summary>
/// Per-entry caching options the application can express without depending on the cache library.
/// </summary>
/// <param name="AllowStaleOnFailure">
/// When false, an entry that has expired is never served again just because the factory failed.
/// The pipeline sets this false: a duration is a staleness bound, and quietly serving data older
/// than the bound because a refresh errored breaks the only promise the duration made.
/// </param>
public sealed record CacheEntryOptions(bool AllowStaleOnFailure = true);

public interface IAppCache
{
    Task<T> GetOrSetAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        IEnumerable<string>? tags = null,
        CacheEntryOptions? options = null,
        CancellationToken cancellationToken = default);

    void Remove(string key);

    Task RemoveByTagAsync(string tag, CancellationToken cancellationToken = default);

    Task RemoveByTagsAsync(IEnumerable<string>? tags, CancellationToken cancellationToken = default);
}
