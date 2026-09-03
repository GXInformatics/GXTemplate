#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Application.Common.Security;
using CleanArchitecture.Blazor.Domain.Entities;
using CleanArchitecture.Blazor.Domain.Identity;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using CleanArchitecture.Blazor.Infrastructure.Services;
using CleanArchitecture.Blazor.Infrastructure.Services.Identity;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.Identity.Users;

/// <summary>
/// Who may be switched to which tenant.
/// </summary>
/// <remarks>
/// <c>CanSwitchToTenantAsync(userId, tenantId)</c> used <b>neither argument</b>. It asked only
/// whether the current principal held two permissions, and answered the same for every tenant in the
/// installation - so <c>SwitchToTenantAsync</c> wrote <c>ApplicationUser.TenantId</c> to any id it
/// was handed, with nothing consulting <c>TenantUsers</c>. The only thing standing between that and
/// a cross-tenant move was the tenant selector choosing to offer legitimate tenants, and a check
/// whose correctness lives in its caller's markup is not a check.
/// <para>
/// It also required BOTH permissions, which made the finer-grained one dead: holding
/// <c>SwitchTenants</c> alone granted nothing at all. Their descriptions say they are a ladder -
/// "switching between AVAILABLE tenants" against "switching to ANY tenant (admin privilege)" - so
/// the escalated one implies the other and works alone.
/// </para>
/// </remarks>
[TestFixture]
public class TenantSwitchAuthorizationTests
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";
    private const string TenantC = "tenant-c";
    private const string MemberOfA = "user-a";

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
        db.Users.Add(new ApplicationUser
        {
            Id = MemberOfA, UserName = "a", Email = "a@x.com", TenantId = TenantA
        });
        await db.SaveChangesAsync();

        // Member of A and B; deliberately NOT of C.
        db.TenantUsers.Add(new TenantUser { UserId = MemberOfA, TenantId = TenantA });
        db.TenantUsers.Add(new TenantUser { UserId = MemberOfA, TenantId = TenantB });
        await db.SaveChangesAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }

    // ---- harness -------------------------------------------------------------------------------

    private ApplicationDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);

    /// <summary>The service with a principal holding exactly the permissions named.</summary>
    private TenantSwitchService CreateService(bool switchTenants, bool switchToAnyTenant)
    {
        var factory = new Mock<IApplicationDbContextFactory>();
        factory.Setup(x => x.CreateAsync(It.IsAny<CancellationToken>()))
            .Returns(() => new ValueTask<IApplicationDbContext>(NewContext()));

        // IPermissionQueryService since Pass 28: the service no longer depends on
        // IPermissionService, which resolves the principal through Blazor's
        // AuthenticationStateProvider and therefore cannot be constructed in a non-Blazor host.
        // Behaviourally identical for these tests - the same two permissions, held or not.
        var permissions = new Mock<IPermissionQueryService>();
        permissions.Setup(x => x.GetAllPermissionsByUserId(It.IsAny<string>()))
            .ReturnsAsync(new List<PermissionModel>
            {
                new()
                {
                    ClaimType = "Permission",
                    ClaimValue = Permissions.Users.SwitchTenants,
                    Assigned = switchTenants
                },
                new()
                {
                    ClaimType = "Permission",
                    ClaimValue = Permissions.Users.SwitchToAnyTenant,
                    Assigned = switchToAnyTenant
                }
            });

        return new TenantSwitchService(
            factory.Object,
            _provider.GetRequiredService<IServiceScopeFactory>(),
            permissions.Object,
            Mock.Of<IUserProfileState>(),
            Mock.Of<IUserContextLoader>(),
            NullLogger<TenantSwitchService>.Instance);
    }

    // ---- the membership test the method never made ---------------------------------------------

    [Test]
    public async Task AMemberMaySwitchToTheirOwnTenant()
    {
        var service = CreateService(switchTenants: true, switchToAnyTenant: false);

        (await service.CanSwitchToTenantAsync(MemberOfA, TenantB)).Should().BeTrue();
    }

    [Test]
    public async Task ANonMemberIsRefused()
    {
        // RED before Pass 25: true. The method never looked at the tenant, so a principal holding
        // both permissions could be switched into any tenant in the installation.
        var service = CreateService(switchTenants: true, switchToAnyTenant: false);

        (await service.CanSwitchToTenantAsync(MemberOfA, TenantC)).Should().BeFalse(
            "the user holds no TenantUsers row for tenant C");
    }

    [Test]
    public async Task ACrossTenantHolderMaySwitchToATenantTheyDoNotBelongTo()
    {
        // That is precisely what SwitchToAnyTenant is for, and it is why AllowedTenantIds has to be
        // the union of memberships and TenantId rather than memberships alone.
        var service = CreateService(switchTenants: false, switchToAnyTenant: true);

        (await service.CanSwitchToTenantAsync(MemberOfA, TenantC)).Should().BeTrue();
    }

    // ---- the permissions, each on its own -------------------------------------------------------

    [Test]
    public async Task SwitchToAnyTenantWorksAlone_BecauseItImpliesTheOther()
    {
        // RED before Pass 25: false. Both permissions were required, so the escalated one granted
        // nothing by itself - the exact inverse of "any tenant (admin privilege)".
        var service = CreateService(switchTenants: false, switchToAnyTenant: true);

        (await service.CanSwitchToTenantAsync(MemberOfA, TenantB)).Should().BeTrue();
    }

    [Test]
    public async Task SwitchTenantsWorksAlone_ForATenantTheUserBelongsTo()
    {
        // RED before Pass 25: false. Holding only SwitchTenants granted nothing, which made the
        // finer-grained permission dead as written - Pass 22 finding 4.
        var service = CreateService(switchTenants: true, switchToAnyTenant: false);

        (await service.CanSwitchToTenantAsync(MemberOfA, TenantA)).Should().BeTrue();
    }

    [Test]
    public async Task NeitherPermissionGrantsNothing()
    {
        var service = CreateService(switchTenants: false, switchToAnyTenant: false);

        (await service.CanSwitchToTenantAsync(MemberOfA, TenantA)).Should().BeFalse();
    }

    // ---- the arguments are actually used --------------------------------------------------------

    [Test]
    public async Task TheAnswerDependsOnTheTenantAsked_NotOnlyOnThePermissions()
    {
        // The single assertion that would have caught the original defect on its own: the same
        // principal, the same permissions, two different tenants, two different answers. Before
        // Pass 25 every tenant returned the same value because neither argument was read.
        var service = CreateService(switchTenants: true, switchToAnyTenant: false);

        var toOwn = await service.CanSwitchToTenantAsync(MemberOfA, TenantB);
        var toOther = await service.CanSwitchToTenantAsync(MemberOfA, TenantC);

        toOwn.Should().BeTrue();
        toOther.Should().BeFalse();
    }

    [Test]
    public async Task AnUnknownUserIsRefused()
    {
        var service = CreateService(switchTenants: true, switchToAnyTenant: false);

        (await service.CanSwitchToTenantAsync("no-such-user", TenantA)).Should().BeFalse();
    }

    [TestCase("", TenantA)]
    [TestCase(MemberOfA, "")]
    public async Task MissingArgumentsAreRefusedRatherThanIgnored(string userId, string tenantId)
    {
        var service = CreateService(switchTenants: true, switchToAnyTenant: false);

        (await service.CanSwitchToTenantAsync(userId, tenantId)).Should().BeFalse();
    }

    // ---- and the write path enforces it ---------------------------------------------------------

    [Test]
    public async Task SwitchToTenantAsync_RefusesANonMember_AndDoesNotWriteTheTenant()
    {
        // The check is enforced inside the service rather than trusted from the caller. The tenant
        // selector only offers legitimate tenants, but that is one component's rendering - any other
        // caller reaches this write through the same method.
        var service = CreateService(switchTenants: true, switchToAnyTenant: false);

        var result = await service.SwitchToTenantAsync(MemberOfA, TenantC);

        result.Succeeded.Should().BeFalse();

        await using var db = NewContext();
        var user = await db.Users.FindAsync(MemberOfA);
        user!.TenantId.Should().Be(TenantA, "the refused switch wrote nothing");
    }

    [Test]
    public async Task SwitchToTenantAsync_MovesAMemberAndPersistsIt()
    {
        var service = CreateService(switchTenants: true, switchToAnyTenant: false);

        var result = await service.SwitchToTenantAsync(MemberOfA, TenantB);

        result.Succeeded.Should().BeTrue();

        await using var db = NewContext();
        var user = await db.Users.FindAsync(MemberOfA);
        user!.TenantId.Should().Be(TenantB);
    }
}
#nullable restore
