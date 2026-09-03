using System.Linq.Expressions;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using ZiggyCreatures.Caching.Fusion;

namespace CleanArchitecture.Blazor.Infrastructure.Services;

/// <summary>
/// The lookup lists the pickers and dropdowns are built from, cached and shared.
/// </summary>
/// <remarks>
/// <b>The cache entry is process-wide; the loaded list is per-circuit.</b> Both facts matter and they
/// used to be in tension. These services are registered <c>Scoped</c>, which in Blazor Server means
/// one instance per circuit, so <see cref="Items"/> belongs to a single principal - but it is filled
/// from a FusionCache entry that, until this class had a <see cref="Scope"/>, was addressed by a
/// constant key with no principal in it. Every circuit read and wrote the same entry.
/// <para>
/// That was harmless only for as long as nothing was filtered. The moment a datasource returns
/// tenant-dependent rows, a constant key means the first tenant to warm the entry serves it to the
/// next - which is worse than being open, because it is intermittent and does not reproduce. This
/// class exists in its current shape so that scoping a query later is a change to that query and not
/// a silent cross-tenant leak.
/// </para>
/// <para>
/// <b>Nothing here filters anything.</b> Declaring a scope changes which key an entry is stored
/// under, never which rows are loaded.
/// </para>
/// </remarks>
public abstract class DataSourceServiceBase<T> : IDataSourceService<T>
{
    private readonly IFusionCache _fusionCache;
    private readonly IUserContextAccessor _userContextAccessor;
    private readonly string _declaredKey;
    private readonly FusionCacheEntryOptions? _cacheOptions;
    protected int DefaultLimit { get; init; } = 20;
    private readonly Func<T, string> _textSelector;

    /// <summary>
    /// The key <see cref="Items"/> was last loaded under, or null if it never has been.
    /// </summary>
    /// <remarks>
    /// Without this a scope would be decorative for the life of a circuit. <see cref="InitializeAsync"/>
    /// loads only when it has nothing, so a principal whose effective key CHANGES mid-circuit - which
    /// is exactly what switching tenant does, since a <see cref="CacheScope.PerTenant"/> key carries
    /// the tenant id - would go on being served the list it loaded before the switch. Nothing
    /// refreshes these services on a tenant switch: <c>TenantSwitchService</c> refreshes the user
    /// profile state and evicts the user context, and never touches the datasources.
    /// </remarks>
    private string? _loadedKey;

    protected DataSourceServiceBase(
        IFusionCache fusionCache,
        IUserContextAccessor userContextAccessor,
        string cacheKey,
        FusionCacheEntryOptions? cacheOptions = null,
        Func<T, string>? textSelector = null)
    {
        _fusionCache = fusionCache;
        _userContextAccessor = userContextAccessor;
        _declaredKey = cacheKey;
        _cacheOptions = cacheOptions;
        _textSelector = textSelector ?? (static _ => string.Empty);
    }

    /// <summary>
    /// How this list's cache entry is partitioned between principals.
    /// </summary>
    /// <remarks>
    /// <b>Abstract on purpose - there is no default.</b> A scope is a claim about who may see the
    /// same rows, and the wrong claim is a cross-principal leak that no test will report unless it
    /// happens to run two principals. Every subclass has to answer, and a
    /// <see cref="CacheScope.Global"/> answer has to say in a comment why the data really is
    /// installation-wide.
    /// </remarks>
    public abstract CacheScope Scope { get; }

    protected List<T> Items { get; private set; } = new();

    public IReadOnlyList<T> DataSource => Items;
    public event Func<Task>? OnChange;

    /// <summary>
    /// The key this principal's entry lives under, or null when the scope needs a principal and
    /// there is none.
    /// </summary>
    /// <remarks>
    /// Composed through <see cref="CacheScopeKey"/>, the same helper <c>FusionCacheBehaviour</c> uses
    /// for Mediator requests, rather than a second implementation of the same rule. Its own remarks
    /// say why: "a scoped read and a scoped write that disagreed would be worse than no scoping."
    /// This class is now the third caller of that one rule.
    /// </remarks>
    private string? EffectiveKey()
    {
        var user = _userContextAccessor.Current;

        // Fail closed, matching FusionCacheBehaviour exactly: a scope that needs a principal and has
        // none does not fall back to the unscoped key. Falling back is the precise leak scopes exist
        // to prevent - it would put one principal's rows under a key every principal reads.
        if (CacheScopeKey.RequiresUserContext(Scope) && user is null) return null;

        return CacheScopeKey.Compose(_declaredKey, Scope, user);
    }

    public async Task InitializeAsync()
    {
        // Reload when there is nothing, and ALSO when the effective key has moved since the last
        // load - see _loadedKey. The second condition is what makes a scope real rather than
        // declarative.
        if (Items.Count == 0 || _loadedKey != EffectiveKey())
        {
            await LoadAndCacheAsync();
            if (OnChange != null)
            {
                await OnChange.Invoke();
            }
        }
    }

    public async Task RefreshAsync()
    {
        // Evicts THIS principal's entry and no one else's. Under a constant key this used to clear
        // the single shared entry for the whole installation, so one administrator's refresh made
        // every other circuit reload.
        var key = EffectiveKey();
        if (key is not null)
        {
            _fusionCache.Remove(key);
        }

        await LoadAndCacheAsync();
        if (OnChange != null)
        {
            await OnChange.Invoke();
        }
    }

    public virtual Task<IEnumerable<T>> SearchAsync(
        Expression<Func<T, bool>>? predicate,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var query = Items.AsQueryable();

        if (predicate != null)
        {
            query = query.Where(predicate);
        }

        var result = query.Take(limit ?? DefaultLimit).ToList();
        return Task.FromResult<IEnumerable<T>>(result);
    }

    private async Task LoadAndCacheAsync(CancellationToken cancellationToken = default)
    {
        var key = EffectiveKey();

        if (key is null)
        {
            // Bypass, not fallback: load the list so the component still works, and neither read nor
            // write the cache. Same posture FusionCacheBehaviour takes when a scoped request arrives
            // with no ambient principal - the work happens, the sharing does not.
            Items = await LoadAsync(cancellationToken) ?? new List<T>();
            _loadedKey = null;
            return;
        }

        var list = await GetOrSetAsync(key, async () => await LoadAsync(cancellationToken));
        Items = list ?? new List<T>();
        _loadedKey = key;
    }

    private async Task<List<T>?> GetOrSetAsync(string key, Func<Task<List<T>?>> factory)
    {
        if (_cacheOptions is null)
        {
            return await _fusionCache.GetOrSetAsync(key, async _ => await factory());
        }

        return await _fusionCache.GetOrSetAsync(key, async _ => await factory(), _cacheOptions);
    }

    protected abstract Task<List<T>?> LoadAsync(CancellationToken cancellationToken);
}
