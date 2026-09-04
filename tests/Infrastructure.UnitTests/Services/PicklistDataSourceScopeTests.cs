using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Caching;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Application.Common.Mappings;
using CleanArchitecture.Blazor.Application.Features.PicklistSets.Caching;
using CleanArchitecture.Blazor.Domain.Entities;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using CleanArchitecture.Blazor.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using ZiggyCreatures.Caching.Fusion;

namespace CleanArchitecture.Blazor.Infrastructure.UnitTests.Services;

/// <summary>
/// The picklist datasource's cache partition, exercised against the real filter rather than a probe.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the half of Pass 31 that no query test can show.</b> A tenant-filtered query behind a
/// process-wide cache key returns the right rows every time it runs and the wrong rows every time it
/// does not: the first tenant to warm the entry serves its picklists to every other tenant, and the
/// query that would have proved it is never executed. So these tests run TWO services over ONE
/// FusionCache - as two circuits share the process-wide cache - and read what the second one is
/// served.
/// </para>
/// <para>
/// <c>DataSourceScopeTests</c> next door asserts the same partitioning through a probe datasource, to
/// pin the base class. This fixture deliberately does not reuse it: what is being checked here is
/// that <see cref="PicklistDataSourceService"/>'s own declared scope matches what its own query
/// actually depends on, which is a claim about this service and not about the mechanism.
/// </para>
/// <para>
/// <b>Same tenant, different users, SHARE - and that is asserted too.</b> Pass 28 found
/// <c>UserDataSourceService</c>'s PerTenant had silently become false once its query read
/// <c>AllowedTenantIds</c> and a cross-tenant permission. The picklist predicate reads
/// <c>UserContext.TenantId</c> and nothing else, and picklists have no cross-tenant escape, so
/// PerTenant is right here - but it is right for a reason that could stop being true, and the
/// sharing assertion is what would fail if it did.
/// </para>
/// </remarks>
public class PicklistDataSourceScopeTests : IDisposable
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ApplicationDbContext> _options;
    private readonly IFusionCache _cache = new FusionCache(new FusionCacheOptions());

    /// <summary>An ambient principal the test moves around, as a circuit's would.</summary>
    private sealed class Ambient : IUserContextAccessor
    {
        public Ambient(string userId, string? tenantId) =>
            Current = new UserContext(userId, userId, TenantId: tenantId);
        public UserContext? Current { get; private set; }
        public IDisposable Push(UserContext context) => throw new NotSupportedException();
        public void Clear() => Current = null;
    }

    /// <summary>A factory over the shared in-memory database, carrying the ambient principal.</summary>
    private sealed class Factory : IApplicationDbContextFactory
    {
        private readonly DbContextOptions<ApplicationDbContext> _options;
        private readonly IUserContextAccessor _accessor;

        public Factory(DbContextOptions<ApplicationDbContext> options, IUserContextAccessor accessor)
        {
            _options = options;
            _accessor = accessor;
        }

        public ValueTask<IApplicationDbContext> CreateAsync(CancellationToken ct = default) =>
            new(new ApplicationDbContext(_options, _accessor));
    }

    public PicklistDataSourceScopeTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;

        // Seeded with no ambient principal, which is the only way to write a shared row.
        using var db = new ApplicationDbContext(_options, userContextAccessor: null);
        db.Database.EnsureCreated();
        db.PicklistSets.AddRange(
            Row(1, "shipped", null),
            Row(2, "a-only", TenantA),
            Row(3, "b-only", TenantB));
        db.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private static PicklistSet Row(int id, string value, string? tenantId) => new()
    {
        Id = id,
        Name = Picklist.Brand,
        Value = value,
        Text = value,
        TenantId = tenantId
    };

    /// <summary>One service, as one circuit's scoped instance, over the shared cache.</summary>
    private PicklistDataSourceService ServiceFor(string userId, string? tenantId)
    {
        var accessor = new Ambient(userId, tenantId);
        return new PicklistDataSourceService(
            MapsterConfiguration.Create(),
            _cache,
            accessor,
            new Factory(_options, accessor));
    }

    private static async Task<string[]> ServedAsync(PicklistDataSourceService service)
    {
        await service.InitializeAsync();
        return service.DataSource.Select(p => p.Value!).OrderBy(v => v).ToArray();
    }

    // ---- the declaration ------------------------------------------------------------------------

    [Fact]
    public void TheDeclaredScopeIsPerTenant()
    {
        // Global until Pass 31. Asserted on its own as well as through behaviour, because the
        // behaviour tests below would also pass under PerUser or PerUserAndTenant - stricter scopes
        // that are safe but wrong, and would cost a database round trip per user for a list that
        // does not vary by user.
        ServiceFor("u1", TenantA).Scope.Should_Be(CacheScope.PerTenant);
    }

    // ---- §E.4: different tenants do not share ---------------------------------------------------

    [Fact]
    public async Task TwoTenantsAreNotServedEachOthersPicklists()
    {
        // Tenant A warms the entry first. Under the Global key this replaced it for everybody.
        var a = await ServedAsync(ServiceFor("u1", TenantA));
        var b = await ServedAsync(ServiceFor("u2", TenantB));

        Assert.Equal(new[] { "a-only", "shipped" }, a);
        Assert.Equal(new[] { "b-only", "shipped" }, b);
    }

    [Fact]
    public async Task TheOrderTheTenantsArriveInDoesNotMatter()
    {
        // The Global-key defect is order-dependent, which is what makes it intermittent in
        // production and easy to miss in a test that only ever warms one way round.
        var b = await ServedAsync(ServiceFor("u2", TenantB));
        var a = await ServedAsync(ServiceFor("u1", TenantA));

        Assert.Equal(new[] { "b-only", "shipped" }, b);
        Assert.Equal(new[] { "a-only", "shipped" }, a);
    }

    // ---- narrowed, not emptied ------------------------------------------------------------------

    [Fact]
    public async Task ATenantStillGetsTheSharedRowsAndItsOwn()
    {
        // A partition that served nobody anything would satisfy both assertions above.
        var a = await ServedAsync(ServiceFor("u1", TenantA));

        Assert.Contains("shipped", a);
        Assert.Contains("a-only", a);
        Assert.DoesNotContain("b-only", a);
    }

    // ---- §B: the partition claim itself ---------------------------------------------------------

    [Fact]
    public async Task TwoPrincipalsInTheSameTenantShareOneEntry()
    {
        // The claim PerTenant actually makes, and the one Pass 28 had to retract for
        // UserDataSourceService. It holds here because the picklist predicate reads TenantId alone -
        // no permission, no allowed-tenant union, and no cross-tenant escape to make one principal
        // in a tenant differ from another.
        var first = await ServedAsync(ServiceFor("u1", TenantA));
        var second = await ServedAsync(ServiceFor("u2", TenantA));

        Assert.Equal(first, second);

        // And they really are one entry rather than two equal ones: removing the composed key
        // empties what the next load finds in cache for BOTH of them.
        Assert.Equal(
            CacheScopeKey.Compose(PicklistSetCacheKey.PicklistCacheKey, CacheScope.PerTenant,
                new UserContext("u1", "u1", TenantId: TenantA)),
            CacheScopeKey.Compose(PicklistSetCacheKey.PicklistCacheKey, CacheScope.PerTenant,
                new UserContext("u2", "u2", TenantId: TenantA)));
    }

    // ---- §D: SearchAsync ------------------------------------------------------------------------

    [Fact]
    public async Task SearchAsyncIsBoundedByTheGlobalFilter()
    {
        // The one method that overrides the base and queries the database instead of the loaded
        // list, so it goes through neither the cache nor the scope. Pass 26 A2 flagged that as a
        // hazard once the scope meant something; it is not, because the filter reaches the query
        // directly - but "the filter covers it" is exactly the claim worth checking on the method
        // known to be different, and this is the check.
        var service = ServiceFor("u2", TenantB);

        var all = (await service.SearchAsync(predicate: null)).Select(p => p.Value).ToArray();
        Assert.Equal(new[] { "b-only", "shipped" }, all.OrderBy(v => v).ToArray());

        // Asked for another tenant's row BY NAME, which is the shape a hostile caller would use.
        var targeted = await service.SearchAsync(p => p.Value == "a-only");
        Assert.Empty(targeted);

        // Narrowed, not emptied: the same call finds this tenant's own row and the shared one.
        Assert.Single(await service.SearchAsync(p => p.Value == "b-only"));
        Assert.Single(await service.SearchAsync(p => p.Value == "shipped"));
    }

    [Fact]
    public async Task SearchAsyncDoesNotServeAWarmedEntryFromAnotherTenant()
    {
        // SearchAsync never reads the cache, so a tenant-A entry warmed first cannot reach tenant B
        // through it. Stated as a test because the reasoning ("it bypasses the cache, therefore it
        // is safe") is the sort that stops being true if someone later routes it through Items.
        await ServedAsync(ServiceFor("u1", TenantA));

        var found = await ServiceFor("u2", TenantB).SearchAsync(p => p.Value == "a-only");
        Assert.Empty(found);
    }
}

/// <summary>Reads better than Assert.Equal for a single enum, and says what failed.</summary>
internal static class ScopeAssertions
{
    public static void Should_Be(this CacheScope actual, CacheScope expected) =>
        Assert.True(actual == expected,
            $"Expected PicklistDataSourceService to declare {expected} but it declares {actual}. " +
            "The scope and the query filter are one change: a tenant-filtered query behind a " +
            "process-wide key serves the first tenant's picklists to every other tenant.");
}
