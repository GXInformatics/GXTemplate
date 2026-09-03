#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
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
/// The tenants offered for switching, and their agreement with the check that guards the switch.
/// </summary>
/// <remarks>
/// <b>Switching is a WRITE.</b> <c>SwitchToTenantAsync</c> persists <c>ApplicationUser.TenantId</c>,
/// and the audit interceptor stamps new rows from it - so offering a tenant in the menu is offering
/// that mutation. A list wider than the check offers a switch that will be refused; a list narrower
/// hides a capability the principal was granted. Both are bugs.
/// <para>
/// <b>So the two derive from one private rule</b> inside the service, and the agreement is asserted
/// as a single property over every principal shape and every tenant - not as two separate checks
/// that happen to match today.
/// </para>
/// </remarks>
[TestFixture]
public class SwitchableTenantsTests
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";
    private const string TenantC = "tenant-c";
    private const string MemberOfAB = "user-ab";

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

        db.Tenants.Add(new Tenant { Id = TenantA, Name = "A Tenant" });
        db.Tenants.Add(new Tenant { Id = TenantB, Name = "B Tenant" });
        db.Tenants.Add(new Tenant { Id = TenantC, Name = "C Tenant" });
        db.Users.Add(new ApplicationUser
        {
            Id = MemberOfAB, UserName = "ab", Email = "ab@x.com", TenantId = TenantA
        });
        await db.SaveChangesAsync();

        // Member of A and B; deliberately NOT of C.
        db.TenantUsers.Add(new TenantUser { UserId = MemberOfAB, TenantId = TenantA });
        db.TenantUsers.Add(new TenantUser { UserId = MemberOfAB, TenantId = TenantB });
        await db.SaveChangesAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private ApplicationDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);

    private TenantSwitchService CreateService(bool switchTenants, bool switchToAnyTenant)
    {
        var factory = new Mock<IApplicationDbContextFactory>();
        factory.Setup(x => x.CreateAsync(It.IsAny<CancellationToken>()))
            .Returns(() => new ValueTask<IApplicationDbContext>(NewContext()));

        var permissions = new Mock<IPermissionQueryService>();
        permissions.Setup(x => x.GetAllPermissionsByUserId(It.IsAny<string>()))
            .ReturnsAsync(new List<PermissionModel>
            {
                new() { ClaimType = "Permission", ClaimValue = Permissions.Users.SwitchTenants, Assigned = switchTenants },
                new() { ClaimType = "Permission", ClaimValue = Permissions.Users.SwitchToAnyTenant, Assigned = switchToAnyTenant }
            });

        return new TenantSwitchService(
            factory.Object,
            _provider.GetRequiredService<IServiceScopeFactory>(),
            permissions.Object,
            Mock.Of<IUserProfileState>(),
            Mock.Of<IUserContextLoader>(),
            NullLogger<TenantSwitchService>.Instance);
    }

    private static async Task<string[]> OfferedAsync(TenantSwitchService service, string userId) =>
        (await service.GetSwitchableTenantsAsync(userId))
        .Select(t => t.Id!).OrderBy(x => x, StringComparer.Ordinal).ToArray();

    // ---- THE property: the menu and the check agree, for every principal and every tenant ----------

    [Test]
    public async Task WhatIsOfferedIsExactlyWhatIsPermitted()
    {
        // The single assertion the design exists to make true. Two separate checks - "the list looks
        // right" and "the check looks right" - could both pass while disagreeing with each other,
        // which is the failure that puts a refused switch in front of a user or hides a granted one.
        //
        // Every principal shape, including None, against every tenant in the installation.
        var everyTenant = new[] { TenantA, TenantB, TenantC };

        foreach (var (switchTenants, anyTenant) in new[]
                 {
                     (false, false),   // None
                     (true, false),    // Membership
                     (false, true),    // All, via the escalated right alone
                     (true, true)      // All, holding both
                 })
        {
            var service = CreateService(switchTenants, anyTenant);
            var offered = await OfferedAsync(service, MemberOfAB);

            foreach (var tenantId in everyTenant)
            {
                var permitted = await service.CanSwitchToTenantAsync(MemberOfAB, tenantId);

                offered.Contains(tenantId).Should().Be(permitted,
                    $"offering and permitting must agree for tenant {tenantId} when " +
                    $"SwitchTenants={switchTenants}, SwitchToAnyTenant={anyTenant}");
            }
        }
    }

    // ---- the three principal shapes -------------------------------------------------------------------

    [Test]
    public async Task ACrossTenantHolderIsOfferedEveryTenant_HoldingThatRightAlone()
    {
        // RED before Pass 28 at the UI: the menu was gated on SwitchTenants alone, so this principal
        // got a DISABLED menu - the escalated permission had no interface at all.
        var service = CreateService(switchTenants: false, switchToAnyTenant: true);

        (await OfferedAsync(service, MemberOfAB)).Should().Equal(TenantA, TenantB, TenantC);
    }

    [Test]
    public async Task AMembershipHolderIsOfferedOnlyTheirOwnTenants()
    {
        var service = CreateService(switchTenants: true, switchToAnyTenant: false);

        (await OfferedAsync(service, MemberOfAB)).Should().Equal(TenantA, TenantB);
    }

    [Test]
    public async Task AMembershipHolderIsOfferedBOTHOfTheirTenants()
    {
        // Narrowed, not emptied - and not narrowed to one. A list that returned only the current
        // tenant would satisfy "does not offer C" while removing a real capability.
        var service = CreateService(switchTenants: true, switchToAnyTenant: false);

        var offered = await OfferedAsync(service, MemberOfAB);

        offered.Should().Contain(TenantA).And.Contain(TenantB);
        offered.Should().NotContain(TenantC);
    }

    [Test]
    public async Task APrincipalWithNeitherRightIsOfferedNothing()
    {
        var service = CreateService(switchTenants: false, switchToAnyTenant: false);

        (await OfferedAsync(service, MemberOfAB)).Should().BeEmpty();
    }

    // ---- the service refuses regardless of what a menu might have said ---------------------------------

    [Test]
    public async Task AMembershipHolderIsRefusedANonMemberTenant_EvenIfTheMenuWereWrong()
    {
        // The list and the check agree, but the check is the one that guards the write - so it is
        // asserted independently. A caller that fabricated a tenant id reaches this, not the menu.
        var service = CreateService(switchTenants: true, switchToAnyTenant: false);

        var result = await service.SwitchToTenantAsync(MemberOfAB, TenantC);

        result.Succeeded.Should().BeFalse();

        await using var db = NewContext();
        (await db.Users.FindAsync(MemberOfAB))!.TenantId.Should().Be(TenantA, "the refusal wrote nothing");
    }

    [Test]
    public async Task ACrossTenantHolderCanActuallySwitchToANonMemberTenant()
    {
        // The capability end to end: offered, permitted, and the write lands.
        var service = CreateService(switchTenants: false, switchToAnyTenant: true);

        (await OfferedAsync(service, MemberOfAB)).Should().Contain(TenantC);

        var result = await service.SwitchToTenantAsync(MemberOfAB, TenantC);

        result.Succeeded.Should().BeTrue();

        await using var db = NewContext();
        (await db.Users.FindAsync(MemberOfAB))!.TenantId.Should().Be(TenantC);
    }

    [Test]
    public async Task APrincipalWithNeitherRightCannotSwitchAtAll()
    {
        var service = CreateService(switchTenants: false, switchToAnyTenant: false);

        (await service.SwitchToTenantAsync(MemberOfAB, TenantA)).Succeeded.Should().BeFalse();
    }


    [Test]
    public async Task ATenantThatDoesNotExistIsRefused_EvenForACrossTenantHolder()
    {
        // Found by the Pass 28 live probe: the All branch answered true unconditionally, so the
        // check said yes to ids with no tenant behind them. The write was still refused - the
        // tenant lookup in SwitchToTenantAsync failed - but two things were wrong anyway.
        //
        // First, it broke the property this fixture exists to hold: permitted was true while
        // offered was false, since the list can only offer tenants that exist. The property test
        // quantifies over real tenants and so could not see it.
        //
        // Second, the refusal message differed - "User or tenant not found" rather than
        // "Insufficient permissions" - which let a caller distinguish a real tenant id from an
        // invented one, exactly the enumeration leak SwitchToTenantAsync's own comment says the
        // uniform message is there to prevent.
        var service = CreateService(switchTenants: false, switchToAnyTenant: true);

        (await service.CanSwitchToTenantAsync(MemberOfAB, "no-such-tenant")).Should().BeFalse();

        var result = await service.SwitchToTenantAsync(MemberOfAB, "no-such-tenant");
        result.Succeeded.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("Insufficient permissions"),
            "a refusal must not report which tenant ids are real");
    }

    // ---- fail closed ------------------------------------------------------------------------------------

    [Test]
    public async Task AnUnknownOrMissingUserIsOfferedNothing()
    {
        var service = CreateService(switchTenants: true, switchToAnyTenant: false);

        (await OfferedAsync(service, "no-such-user")).Should().BeEmpty();
        (await service.GetSwitchableTenantsAsync(string.Empty)).Should().BeEmpty();
    }

    // ---- and it is not the VISIBILITY bound -------------------------------------------------------------

    [Test]
    public async Task SwitchabilityIsNotVisibility()
    {
        // The two bounds are deliberately separate: Users.ViewAllTenants widens what you may SEE,
        // and has no effect on what you may BECOME. A principal holding neither switch right is
        // offered nothing here however much they can see - which is why this list does not come from
        // TenantDataSourceService.
        var service = CreateService(switchTenants: false, switchToAnyTenant: false);

        (await OfferedAsync(service, MemberOfAB)).Should().BeEmpty(
            "visibility rights do not grant switching");
    }
}
#nullable restore
