using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Caching;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Application.Common.Mappings;
using CleanArchitecture.Blazor.Application.Common.Security;
using CleanArchitecture.Blazor.Application.Features.Identity;
using CleanArchitecture.Blazor.Domain.Entities;
using CleanArchitecture.Blazor.Domain.Identity;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using CleanArchitecture.Blazor.Infrastructure.Services.Identity;
using Mapster;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
using ZiggyCreatures.Caching.Fusion;

namespace CleanArchitecture.Blazor.Infrastructure.UnitTests.Services;

/// <summary>
/// The user list is bounded at the QUERY, not at the view.
/// </summary>
/// <remarks>
/// Until Pass 28 <c>UserDataSourceService</c> loaded every user in the installation and
/// <c>PickSuperiorAutocomplete</c> filtered them in memory. No foreign row reached the screen - so
/// nothing was visibly wrong - but the whole directory, with display names, emails and phone
/// numbers, sat in the circuit's memory and in that principal's cache entry. Filtering at the view
/// rather than the query is the shape of defect this programme has spent several passes removing.
/// <para>
/// The rule is <see cref="UserTenantVisibility"/>, shared with the users grid and the user export
/// rather than restated here - the third consumer, and the reason it was extracted.
/// </para>
/// </remarks>
public class UserVisibilityTests : IDisposable
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";

    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;

    public UserVisibilityTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ApplicationDbContext>(o => o.UseSqlite(_connection));
        services.AddIdentityCore<ApplicationUser>()
            .AddRoles<ApplicationRole>()
            .AddEntityFrameworkStores<ApplicationDbContext>();
        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Database.EnsureCreated();
        db.Tenants.Add(new Tenant { Id = TenantA, Name = "Tenant A" });
        db.Tenants.Add(new Tenant { Id = TenantB, Name = "Tenant B" });
        db.Users.Add(User("a-one", TenantA));
        db.Users.Add(User("a-two", TenantA));
        db.Users.Add(User("b-one", TenantB));
        db.Users.Add(User("orphan", null));
        db.SaveChanges();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private static ApplicationUser User(string name, string? tenantId) => new()
    {
        Id = name, UserName = name, Email = $"{name}@x.com", DisplayName = name, TenantId = tenantId
    };

    /// <param name="allowed">null means no ambient principal at all.</param>
    private UserDataSourceService CreateService(string[]? allowed, bool viewAllTenants = false)
    {
        var accessor = new Mock<IUserContextAccessor>();
        accessor.SetupGet(x => x.Current).Returns(allowed is null
            ? null
            : new UserContext("probe", "probe", TenantId: allowed.FirstOrDefault(), AllowedTenantIds: allowed));

        var permissions = new Mock<IPermissionQueryService>();
        permissions.Setup(x => x.GetAllPermissionsByUserId(It.IsAny<string>()))
            .ReturnsAsync(new List<PermissionModel>
            {
                new()
                {
                    ClaimType = "Permission",
                    ClaimValue = Permissions.Users.ViewAllTenants,
                    Assigned = viewAllTenants
                }
            });

        return new UserDataSourceService(
            MapsterConfiguration.Create(),
            new FusionCache(new FusionCacheOptions()),
            accessor.Object,
            permissions.Object,
            _provider.GetRequiredService<IServiceScopeFactory>());
    }

    private static async Task<string[]> LoadAsync(UserDataSourceService service)
    {
        await service.InitializeAsync();
        return service.DataSource.Select(u => u.UserName).OrderBy(x => x, StringComparer.Ordinal).ToArray();
    }

    // ---- the bound ---------------------------------------------------------------------------------

    [Fact]
    public async Task OnlyUsersInTheVisibleTenants_AreLoaded()
    {
        // RED before Pass 28: every user in the installation, including b-one and orphan.
        var service = CreateService(new[] { TenantA });

        Assert.Equal(new[] { "a-one", "a-two" }, await LoadAsync(service));
    }

    [Fact]
    public async Task APrincipalInTwoTenants_LoadsBoth()
    {
        // Narrowed, not emptied - and not narrowed to ONE tenant either. AllowedTenantIds is a set.
        var service = CreateService(new[] { TenantA, TenantB });

        Assert.Equal(new[] { "a-one", "a-two", "b-one" }, await LoadAsync(service));
    }

    [Fact]
    public async Task EveryColleagueInTheTenantIsStillLoaded()
    {
        // The control that stops a bounded query returning nothing from satisfying every isolation
        // assertion here. Both tenant-A users must come back.
        var service = CreateService(new[] { TenantA });

        var loaded = await LoadAsync(service);

        Assert.Contains("a-one", loaded);
        Assert.Contains("a-two", loaded);
    }

    // ---- the escape --------------------------------------------------------------------------------

    [Fact]
    public async Task ACrossTenantHolder_LoadsEveryUser()
    {
        // Including the tenantless one, who belongs to nobody and is visible only here.
        var service = CreateService(new[] { TenantA }, viewAllTenants: true);

        Assert.Equal(new[] { "a-one", "a-two", "b-one", "orphan" }, await LoadAsync(service));
    }

    // ---- fail closed, the three ways -----------------------------------------------------------------

    [Fact]
    public async Task WithNoAmbientPrincipal_NothingIsLoaded()
    {
        var service = CreateService(allowed: null);

        Assert.Empty(await LoadAsync(service));
    }

    [Fact]
    public async Task WithAnEmptyAllowedSet_NothingIsLoaded()
    {
        var service = CreateService(Array.Empty<string>());

        Assert.Empty(await LoadAsync(service));
    }

    [Fact]
    public async Task ATenantlessUserIsInvisible_ExceptToACrossTenantHolder()
    {
        var bounded = CreateService(new[] { TenantA, TenantB });

        Assert.DoesNotContain("orphan", await LoadAsync(bounded));
        Assert.Contains("orphan", await LoadAsync(CreateService(new[] { TenantA }, viewAllTenants: true)));
    }

    // ---- the partition -------------------------------------------------------------------------------

    [Fact]
    public void TheScopeIsPerUser_BecauseTheBoundIsAPerUserFact()
    {
        // It was PerTenant until Pass 28, declared while the query was unfiltered on the reasoning
        // that the list is "the same answer for everyone in a tenant". Bounding the query made that
        // false: two principals in the same tenant differ if one belongs to a second tenant or holds
        // ViewAllTenants. Under a per-tenant key one would have been served the other's list.
        Assert.Equal(CacheScope.PerUser, CreateService(new[] { TenantA }).Scope);
    }

    [Fact]
    public async Task TwoPrincipalsInTheSameTenantWithDifferentReach_DoNotShareAnEntry()
    {
        // The partition demonstrated rather than asserted from the enum: both principals are "in"
        // tenant A, and they must not see the same list.
        var narrow = CreateService(new[] { TenantA });
        var wide = CreateService(new[] { TenantA, TenantB });

        Assert.Equal(new[] { "a-one", "a-two" }, await LoadAsync(narrow));
        Assert.Equal(new[] { "a-one", "a-two", "b-one" }, await LoadAsync(wide));
    }
}
