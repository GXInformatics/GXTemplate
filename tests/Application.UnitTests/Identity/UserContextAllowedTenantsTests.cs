#nullable enable
using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Domain.Entities;
using CleanArchitecture.Blazor.Domain.Identity;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using CleanArchitecture.Blazor.Infrastructure.Services.Identity;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using ZiggyCreatures.Caching.Fusion;

namespace CleanArchitecture.Blazor.Application.UnitTests.Identity;

/// <summary>
/// <c>UserContext.AllowedTenantIds</c> - the single answer to "which tenants may this principal
/// see?".
/// </summary>
/// <remarks>
/// It was computed from the <c>TenantUsers</c> join alone, which is not the same question. Two
/// things put a tenant on <c>ApplicationUser.TenantId</c> without a membership row behind it:
/// <list type="bullet">
/// <item>a <c>Permissions.Users.SwitchToAnyTenant</c> holder switching into a tenant they do not
/// belong to - that IS the capability, and <c>TenantSwitchService</c> writes the field directly;</item>
/// <item>rows saved before the user-edit dialog was fixed, which rewrote membership and left the
/// primary tenant behind.</item>
/// </list>
/// In both cases membership-only reported a set that did not contain the tenant the principal is
/// actually in - so scoping written as <c>AllowedTenantIds.Contains(row.TenantId)</c> would have
/// shown them nothing at all, including their own tenant. It is read nowhere yet, which is exactly
/// why this was worth fixing now rather than discovering later.
/// </remarks>
[TestFixture]
public class UserContextAllowedTenantsTests
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";
    private const string TenantC = "tenant-c";

    private SqliteConnection _connection = null!;
    private ServiceProvider _provider = null!;

    [SetUp]
    public async Task SetUp()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ApplicationDbContext>(o => o.UseSqlite(_connection));
        services.AddIdentityCore<ApplicationUser>()
            .AddRoles<ApplicationRole>()
            .AddEntityFrameworkStores<ApplicationDbContext>();
        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.EnsureCreatedAsync();
        db.Tenants.Add(new Tenant { Id = TenantA, Name = "Tenant A" });
        db.Tenants.Add(new Tenant { Id = TenantB, Name = "Tenant B" });
        db.Tenants.Add(new Tenant { Id = TenantC, Name = "Tenant C" });
        await db.SaveChangesAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }

    // ---- harness -------------------------------------------------------------------------------

    private async Task<ApplicationUser> CreateUserAsync(string? tenantId, params string[] memberships)
    {
        using var scope = _provider.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser
        {
            UserName = $"u{Guid.NewGuid():N}", Email = "u@example.com", TenantId = tenantId
        };
        (await userManager.CreateAsync(user)).Succeeded.Should().BeTrue();

        if (memberships.Length > 0)
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            foreach (var membership in memberships)
            {
                db.TenantUsers.Add(new TenantUser { UserId = user.Id, TenantId = membership });
            }
            await db.SaveChangesAsync();
        }

        return user;
    }

    private UserContextLoader NewLoader() => new(
        _provider.GetRequiredService<IServiceScopeFactory>(),
        new FusionCache(new FusionCacheOptions()),
        NullLogger<UserContextLoader>.Instance);

    private static ClaimsPrincipal Principal(string userId) =>
        new(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, userId) },
            authenticationType: "TestAuth"));

    private async Task<string[]> AllowedAsync(ApplicationUser user)
    {
        var context = await NewLoader().LoadAsync(Principal(user.Id));
        context.Should().NotBeNull();
        context!.AllowedTenantIds.Should().NotBeNull(
            "the loader always computes this - null is reserved for a context built some other way");
        return context.AllowedTenantIds!.OrderBy(x => x, StringComparer.Ordinal).ToArray();
    }

    // ---- the four combinations -----------------------------------------------------------------

    [Test]
    public async Task MembershipRowsOnly_YieldThoseTenants()
    {
        var user = await CreateUserAsync(tenantId: null, TenantA, TenantB);

        (await AllowedAsync(user)).Should().Equal(TenantA, TenantB);
    }

    [Test]
    public async Task APrimaryTenantWithNoMembershipRow_IsStillAllowed()
    {
        // RED before Pass 25: []. This is the switched-into-another-tenant case and the
        // legacy-divergent case, and membership-only reported a set that excluded the tenant the
        // principal is actually acting in.
        var user = await CreateUserAsync(tenantId: TenantC);

        (await AllowedAsync(user)).Should().Equal(TenantC);
    }

    [Test]
    public async Task BothSourcesAreUnioned()
    {
        // RED before Pass 25: [tenant-a, tenant-b] - tenant C, the one the user is IN, was missing.
        var user = await CreateUserAsync(tenantId: TenantC, TenantA, TenantB);

        (await AllowedAsync(user)).Should().Equal(TenantA, TenantB, TenantC);
    }

    [Test]
    public async Task TheOrdinaryCaseHasNoDuplicate()
    {
        // The primary tenant is normally also a membership row. It must appear once, or a caller
        // counting the set gets the wrong answer and an IN clause carries a pointless repeat.
        var user = await CreateUserAsync(tenantId: TenantA, TenantA, TenantB);

        (await AllowedAsync(user)).Should().Equal(TenantA, TenantB);
        (await AllowedAsync(user)).Should().OnlyHaveUniqueItems();
    }

    [Test]
    public async Task NeitherSource_YieldsAnEmptyListRatherThanNull()
    {
        // The distinction the record documents and consumers must honour: [] means "computed, and
        // this principal belongs to nothing"; null would mean "nobody computed this". Collapsing
        // them turns a known-empty into an unknown, or an unknown into unconstrained.
        var user = await CreateUserAsync(tenantId: null);

        var context = await NewLoader().LoadAsync(Principal(user.Id));

        context!.AllowedTenantIds.Should().NotBeNull();
        context.AllowedTenantIds.Should().BeEmpty();
    }

    // ---- and it still comes back fresh after an eviction ---------------------------------------

    [Test]
    public async Task ClearUserContextCache_MakesTheNextLoadSeeANewTenant()
    {
        // The union is cached for an hour with everything else on the context, so the invalidation
        // path is part of the fix rather than adjacent to it: the tenant switcher and the user-edit
        // dialog both evict, and a stale union would outlive the change that made it wrong.
        var user = await CreateUserAsync(tenantId: TenantA, TenantA);
        var loader = NewLoader();

        var before = await loader.LoadAsync(Principal(user.Id));
        before!.AllowedTenantIds.Should().Equal(TenantA);

        using (var scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.TenantUsers.Add(new TenantUser { UserId = user.Id, TenantId = TenantB });
            await db.SaveChangesAsync();
        }

        // Still the cached answer, which is the point of the cache.
        (await loader.LoadAsync(Principal(user.Id)))!.AllowedTenantIds.Should().Equal(TenantA);

        loader.ClearUserContextCache(user.Id);

        var after = await loader.LoadAsync(Principal(user.Id));
        after!.AllowedTenantIds!.OrderBy(x => x, StringComparer.Ordinal)
            .Should().Equal(TenantA, TenantB);
    }
}
#nullable restore
