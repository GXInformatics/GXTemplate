using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Application.Common.Security;
using CleanArchitecture.Blazor.Domain.Entities;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using CleanArchitecture.Blazor.Infrastructure.Services.MultiTenant;
using Mapster;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;
using ZiggyCreatures.Caching.Fusion;

namespace CleanArchitecture.Blazor.Infrastructure.UnitTests.Services;

/// <summary>
/// Which tenants a principal is offered.
/// </summary>
/// <remarks>
/// <b>One list behind two dropdowns.</b> <c>TenantDataSourceService</c> feeds the Users page's tenant
/// filter ("which tenants may I filter by") and <c>TenantSelect</c> in the user dialog ("which
/// tenants may I assign a user to"). Both used to offer every tenant in the installation, so an
/// administrator of one tenant could move a user into another one they had no visibility of - an
/// escalation in the opposite direction to the one the grid leaked.
/// <para>
/// It does <b>not</b> feed the tenant SWITCHER in the app shell. That reads
/// <c>UserProfile.AvailableTenants</c> and asks a different question, bounded by membership rather
/// than visibility. Deliberately untouched by Pass 27.
/// </para>
/// </remarks>
public class TenantVisibilityTests : IDisposable
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";
    private const string TenantC = "tenant-c";

    private readonly SqliteConnection _connection;

    public TenantVisibilityTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        using var db = NewContext();
        db.Database.EnsureCreated();
        db.Tenants.Add(new Tenant { Id = TenantA, Name = "A Tenant" });
        db.Tenants.Add(new Tenant { Id = TenantB, Name = "B Tenant" });
        db.Tenants.Add(new Tenant { Id = TenantC, Name = "C Tenant" });
        db.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private ApplicationDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);

    /// <param name="allowed">null means "no ambient principal at all".</param>
    private TenantDataSourceService CreateService(string[]? allowed, bool viewAllTenants = false)
    {
        var factory = new Mock<IApplicationDbContextFactory>();
        factory.Setup(x => x.CreateAsync(It.IsAny<CancellationToken>()))
            .Returns(() => new ValueTask<IApplicationDbContext>(NewContext()));

        var accessor = new Mock<IUserContextAccessor>();
        accessor.SetupGet(x => x.Current).Returns(allowed is null
            ? null
            : new UserContext("u1", "u1", TenantId: allowed.FirstOrDefault(), AllowedTenantIds: allowed));

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

        return new TenantDataSourceService(
            new TypeAdapterConfig(),
            new FusionCache(new FusionCacheOptions()),
            accessor.Object,
            permissions.Object,
            factory.Object);
    }

    private static async Task<string[]> LoadAsync(TenantDataSourceService service)
    {
        await service.InitializeAsync();
        return service.DataSource.Select(t => t.Id!).OrderBy(x => x, StringComparer.Ordinal).ToArray();
    }

    // ---- the bound --------------------------------------------------------------------------------

    [Fact]
    public async Task OnlyTheTenantsThePrincipalMaySee_AreOffered()
    {
        // RED before Pass 27: all three. Both dropdowns bound TenantService.DataSource, which loaded
        // every tenant in the installation.
        var service = CreateService(new[] { TenantA });

        Assert.Equal(new[] { TenantA }, await LoadAsync(service));
    }

    [Fact]
    public async Task APrincipalInSeveralTenants_IsOfferedAllOfThem()
    {
        // The narrowed-not-emptied control at this surface: the bound must be the principal's set,
        // not "one tenant". A service returning a single tenant would satisfy the test above.
        var service = CreateService(new[] { TenantA, TenantB });

        Assert.Equal(new[] { TenantA, TenantB }, await LoadAsync(service));
    }

    // ---- the escape -------------------------------------------------------------------------------

    [Fact]
    public async Task ACrossTenantHolder_IsOfferedEveryTenant()
    {
        // Permissions.Users.ViewAllTenants, the right ratified in Pass 27's gate. Without it reaching
        // this list, a cross-tenant holder could see other tenants' users in the grid and be unable
        // to filter by them - the two surfaces would disagree.
        var service = CreateService(new[] { TenantA }, viewAllTenants: true);

        Assert.Equal(new[] { TenantA, TenantB, TenantC }, await LoadAsync(service));
    }

    // ---- fail closed ------------------------------------------------------------------------------

    [Fact]
    public async Task WithNoAmbientPrincipal_NoTenantsAreOffered()
    {
        // Asserted, not assumed. An isolation bound that opens when it cannot answer is the defect
        // it exists to prevent.
        var service = CreateService(allowed: null);

        Assert.Empty(await LoadAsync(service));
    }

    [Fact]
    public async Task WithAnEmptyAllowedSet_NoTenantsAreOffered()
    {
        var service = CreateService(Array.Empty<string>());

        Assert.Empty(await LoadAsync(service));
    }

    [Fact]
    public async Task ATenantThePrincipalIsNotIn_IsNeverOffered_EvenWhenItExists()
    {
        // The negative stated directly: three tenants exist, one is allowed, and the other two are
        // absent from the result rather than merely not asserted on.
        var service = CreateService(new[] { TenantB });

        var offered = await LoadAsync(service);

        Assert.DoesNotContain(TenantA, offered);
        Assert.DoesNotContain(TenantC, offered);
    }
}
