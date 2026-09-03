using CleanArchitecture.Blazor.Application.Common.Interfaces.Caching;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Infrastructure.Services;
using Xunit;
using ZiggyCreatures.Caching.Fusion;

namespace CleanArchitecture.Blazor.Infrastructure.UnitTests.Services;

/// <summary>
/// The datasource cache is partitioned by the scope each service declares.
/// </summary>
/// <remarks>
/// These lists back <c>TenantSelect</c>, <c>PickSuperiorAutocomplete</c>, the picklist selectors and
/// the Users page's dropdowns, and until Pass 26 every one of them was cached under a CONSTANT key -
/// <c>"ALL-ApplicationUserDto"</c>, <c>TenantCacheKey.TenantsCacheKey</c> - with no principal in it.
/// <para>
/// <b>Nothing is filtered yet, so none of this changes what anyone sees today.</b> What it changes is
/// what happens when a query IS scoped: with a constant key the first tenant to warm an entry serves
/// its rows to every other tenant, intermittently and unreproducibly. These tests exist so the
/// partition is in place and asserted before anything depends on it.
/// </para>
/// </remarks>
public class DataSourceScopeTests
{
    private const string DeclaredKey = "ALL-Probe";

    // ---- harness -------------------------------------------------------------------------------

    /// <summary>An accessor whose ambient context the test moves around, as a circuit's would.</summary>
    private sealed class MutableUserContextAccessor : IUserContextAccessor
    {
        public UserContext? Current { get; set; }
        public IDisposable Push(UserContext context) => throw new NotSupportedException();
        public void Clear() => Current = null;
    }

    /// <summary>
    /// A datasource whose loaded value is a fresh string each time, so two entries are always
    /// distinguishable and a shared entry is provably shared rather than coincidentally equal.
    /// </summary>
    private sealed class ProbeDataSource : DataSourceServiceBase<string>
    {
        private readonly Func<string> _next;
        public int Loads { get; private set; }

        public ProbeDataSource(IFusionCache cache, IUserContextAccessor accessor, CacheScope scope, Func<string> next)
            : base(cache, accessor, DeclaredKey)
        {
            Scope = scope;
            _next = next;
        }

        public override CacheScope Scope { get; }

        protected override Task<List<string>?> LoadAsync(CancellationToken cancellationToken)
        {
            Loads++;
            return Task.FromResult<List<string>?>(new List<string> { _next() });
        }
    }

    private static UserContext User(string userId, string? tenantId) =>
        new(userId, userId, TenantId: tenantId);

    /// <summary>
    /// One cache shared by every service in a test, as the process-wide FusionCache is shared by
    /// every circuit. A separate ProbeDataSource per principal, as a Scoped service is per circuit.
    /// </summary>
    private static (IFusionCache Cache, Func<CacheScope, MutableUserContextAccessor, ProbeDataSource> New) Fixture()
    {
        var cache = new FusionCache(new FusionCacheOptions());
        var counter = 0;
        return (cache, (scope, accessor) =>
            new ProbeDataSource(cache, accessor, scope, () => $"value-{Interlocked.Increment(ref counter)}"));
    }

    private static async Task<string> ServedAsync(ProbeDataSource source)
    {
        await source.InitializeAsync();
        return source.DataSource.Single();
    }

    // ---- A.3.1: different tenants do not share ---------------------------------------------------

    [Fact]
    public async Task PerTenant_TwoTenantsGetDifferentKeys()
    {
        // The composed keys, asserted directly rather than inferred from behaviour.
        var a = CacheScopeKey.Compose(DeclaredKey, CacheScope.PerTenant, User("u1", "tenant-a"));
        var b = CacheScopeKey.Compose(DeclaredKey, CacheScope.PerTenant, User("u2", "tenant-b"));

        Assert.NotEqual(a, b);
        Assert.Contains("tenant-a", a);
        Assert.Contains("tenant-b", b);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task PerTenant_TwoTenantsAreServedDifferentValues()
    {
        // And the values, because matching keys prove nothing if the lookup ignores them. This is
        // the leak the scope exists to prevent, measured end to end through the real FusionCache.
        var (cache, newSource) = Fixture();

        var accessorA = new MutableUserContextAccessor { Current = User("u1", "tenant-a") };
        var accessorB = new MutableUserContextAccessor { Current = User("u2", "tenant-b") };

        var served1 = await ServedAsync(newSource(CacheScope.PerTenant, accessorA));
        var served2 = await ServedAsync(newSource(CacheScope.PerTenant, accessorB));

        Assert.NotEqual(served1, served2);

        // Both entries exist, side by side, under their own keys.
        var entryA = await cache.TryGetAsync<List<string>>(
            CacheScopeKey.Compose(DeclaredKey, CacheScope.PerTenant, accessorA.Current));
        var entryB = await cache.TryGetAsync<List<string>>(
            CacheScopeKey.Compose(DeclaredKey, CacheScope.PerTenant, accessorB.Current));

        Assert.True(entryA.HasValue);
        Assert.True(entryB.HasValue);
        Assert.Equal(served1, entryA.Value.Single());
        Assert.Equal(served2, entryB.Value.Single());

        // And nothing was written under the bare declared key, which is what every circuit used to
        // read and write.
        Assert.False((await cache.TryGetAsync<List<string>>(DeclaredKey)).HasValue);
    }

    // ---- A.3.2: the same tenant DOES share -------------------------------------------------------

    [Fact]
    public async Task PerTenant_TwoPrincipalsInTheSameTenantShareOneEntry()
    {
        // Otherwise PerTenant is just PerUser under another name, and the cache stops being a cache:
        // every user in a tenant would load the same list separately.
        var (_, newSource) = Fixture();

        var first = newSource(CacheScope.PerTenant,
            new MutableUserContextAccessor { Current = User("u1", "tenant-a") });
        var second = newSource(CacheScope.PerTenant,
            new MutableUserContextAccessor { Current = User("u2", "tenant-a") });

        var served1 = await ServedAsync(first);
        var served2 = await ServedAsync(second);

        Assert.Equal(served1, served2);
        Assert.Equal(1, first.Loads);
        Assert.Equal(0, second.Loads);
    }

    // ---- A.3.3: Global serves everyone one entry -------------------------------------------------

    [Fact]
    public async Task Global_ServesOneEntryToEveryPrincipal()
    {
        var (_, newSource) = Fixture();

        var inA = newSource(CacheScope.Global, new MutableUserContextAccessor { Current = User("u1", "tenant-a") });
        var inB = newSource(CacheScope.Global, new MutableUserContextAccessor { Current = User("u2", "tenant-b") });
        var anonymous = newSource(CacheScope.Global, new MutableUserContextAccessor { Current = null });

        var served1 = await ServedAsync(inA);
        var served2 = await ServedAsync(inB);
        var served3 = await ServedAsync(anonymous);

        Assert.Equal(served1, served2);
        Assert.Equal(served1, served3);
        Assert.Equal(1, inA.Loads);
        Assert.Equal(0, inB.Loads);
        Assert.Equal(0, anonymous.Loads);
    }

    // ---- A.3.4: invalidation is per scope --------------------------------------------------------

    [Fact]
    public async Task Refresh_EvictsOnlyThisPrincipalsEntry()
    {
        // Under the old constant key one administrator's refresh cleared the single shared entry and
        // made every other circuit in the installation reload.
        var (_, newSource) = Fixture();

        var accessorA = new MutableUserContextAccessor { Current = User("u1", "tenant-a") };
        var accessorB = new MutableUserContextAccessor { Current = User("u2", "tenant-b") };

        var inA = newSource(CacheScope.PerTenant, accessorA);
        var inB = newSource(CacheScope.PerTenant, accessorB);

        await ServedAsync(inA);
        var bBefore = await ServedAsync(inB);

        await inA.RefreshAsync();

        // A reloaded...
        Assert.Equal(2, inA.Loads);

        // ...and B's entry is untouched: a fresh service in tenant B reads the cached value rather
        // than loading again.
        var bAgain = newSource(CacheScope.PerTenant, accessorB);
        Assert.Equal(bBefore, await ServedAsync(bAgain));
        Assert.Equal(0, bAgain.Loads);
    }

    // ---- A.3.5: the instance field follows the key ------------------------------------------------

    [Fact]
    public async Task WhenThePrincipalsTenantChanges_TheLoadedListFollowsIt()
    {
        // The sharper half of the problem, and the one a cache key alone does not solve.
        //
        // These services are Scoped - one instance per circuit - and hold the loaded list in a field.
        // InitializeAsync used to load only when that field was empty, so a principal whose effective
        // key changed MID-CIRCUIT went on being served the list from before. Switching tenant does
        // exactly that: a PerTenant key carries the tenant id. Nothing refreshes these services on a
        // switch - TenantSwitchService refreshes the user profile state and evicts the user context,
        // and never touches the datasources - so without this the declared scope would be decorative
        // for the life of the circuit.
        var (_, newSource) = Fixture();

        var accessor = new MutableUserContextAccessor { Current = User("u1", "tenant-a") };
        var source = newSource(CacheScope.PerTenant, accessor);

        var inTenantA = await ServedAsync(source);

        // The same principal switches tenant, exactly as TenantSwitchService moves them.
        accessor.Current = User("u1", "tenant-b");

        var inTenantB = await ServedAsync(source);

        Assert.NotEqual(inTenantA, inTenantB);
        Assert.Equal(2, source.Loads);
    }

    [Fact]
    public async Task WhenNothingChanges_TheListIsNotReloaded()
    {
        // The other half: the reload must be triggered by the key MOVING, not by every call. A
        // datasource that reloaded on each InitializeAsync would be correct and useless - these back
        // autocompletes and are initialised on every render.
        var (_, newSource) = Fixture();

        var source = newSource(CacheScope.PerTenant,
            new MutableUserContextAccessor { Current = User("u1", "tenant-a") });

        var first = await ServedAsync(source);
        var second = await ServedAsync(source);
        var third = await ServedAsync(source);

        Assert.Equal(first, second);
        Assert.Equal(first, third);
        Assert.Equal(1, source.Loads);
    }

    // ---- the null-context posture ------------------------------------------------------------------

    [Fact]
    public async Task AScopedSourceWithNoPrincipal_LoadsButDoesNotCache()
    {
        // Fail closed, matching FusionCacheBehaviour: the work happens so the component still
        // renders, and nothing is read from or written to the shared cache. Falling back to the bare
        // declared key would be the precise leak the scope exists to prevent - one principal's rows
        // under a key every principal reads.
        var (cache, newSource) = Fixture();

        var anonymous = new MutableUserContextAccessor { Current = null };
        var source = newSource(CacheScope.PerTenant, anonymous);

        var served = await ServedAsync(source);

        Assert.NotNull(served);
        Assert.Equal(1, source.Loads);
        Assert.False((await cache.TryGetAsync<List<string>>(DeclaredKey)).HasValue,
            "an unscoped entry must never be written for a scoped source");

        // A second service in the same position loads again rather than reading a shared entry.
        var another = newSource(CacheScope.PerTenant, anonymous);
        var servedAgain = await ServedAsync(another);

        Assert.NotEqual(served, servedAgain);
        Assert.Equal(1, another.Loads);
    }

    [Fact]
    public void ComposeRefusesAScopedKeyWithNoPrincipal()
    {
        // The guard that makes the bypass above mandatory rather than optional: there is no way to
        // compose a scoped key without a principal, so a caller that forgets to check gets an
        // exception rather than an unscoped key.
        Assert.True(CacheScopeKey.RequiresUserContext(CacheScope.PerTenant));
        Assert.False(CacheScopeKey.RequiresUserContext(CacheScope.Global));
        Assert.Throws<InvalidOperationException>(
            () => CacheScopeKey.Compose(DeclaredKey, CacheScope.PerTenant, null));
    }

    // ---- what each shipped service declares --------------------------------------------------------

    [Fact]
    public void EveryDatasourceDeclaresAScope()
    {
        // Scope is abstract, so this cannot fail to compile - but it CAN drift as services are added.
        // Naming the four here means a new datasource with a thoughtless Global shows up in a diff
        // next to four that each carry a written justification.
        var declared = typeof(DataSourceServiceBase<>).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsClass: true })
            .Where(t => t.BaseType is { IsGenericType: true } b
                        && b.GetGenericTypeDefinition() == typeof(DataSourceServiceBase<>))
            .Select(t => t.Name)
            .OrderBy(n => n)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "PicklistDataSourceService",
                "RoleDataSourceService",
                "TenantDataSourceService",
                "UserDataSourceService"
            },
            declared);
    }
}
