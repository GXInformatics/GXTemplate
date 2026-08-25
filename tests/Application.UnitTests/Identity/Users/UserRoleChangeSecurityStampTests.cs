#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Domain.Identity;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.Identity.Users;

/// <summary>
/// Covers the security-stamp behaviour that UserFormDialog's edit path relies on: when a user's role
/// membership is rewritten, the stamp must change so the user's existing authentication cookie fails
/// its next revalidation (IdentityRevalidatingAuthenticationStateProvider compares the stamp claim on
/// the principal against the stored stamp every 30 minutes). Before the fix no site bumped the stamp,
/// so stale role claims survived for the whole cookie lifetime.
/// </summary>
[TestFixture]
public class UserRoleChangeSecurityStampTests
{
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
        services.AddIdentityCore<ApplicationUser>(o =>
            {
                o.Password.RequireDigit = false;
                o.Password.RequiredLength = 6;
                o.Password.RequireNonAlphanumeric = false;
                o.Password.RequireUppercase = false;
                o.Password.RequireLowercase = false;
            })
            .AddRoles<ApplicationRole>()
            .AddEntityFrameworkStores<ApplicationDbContext>();

        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.EnsureCreatedAsync();

        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        foreach (var role in new[] { "Basic", "Admin" })
        {
            await roleManager.CreateAsync(new ApplicationRole { Name = role });
        }
    }

    [TearDown]
    public async Task TearDown()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private async Task<(UserManager<ApplicationUser> Users, ApplicationUser User)> CreateUserAsync(params string[] roles)
    {
        var scope = _provider.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { UserName = "victim", Email = "victim@example.com" };
        (await userManager.CreateAsync(user, "Password123!")).Succeeded.Should().BeTrue();
        if (roles.Length > 0)
        {
            (await userManager.AddToRolesAsync(user, roles)).Succeeded.Should().BeTrue();
        }
        return (userManager, user);
    }

    /// <summary>
    /// Replays the role-membership rewrite that UserFormDialog.SubmitAsync performs on an existing
    /// user: remove every current role, re-add the selected ones, then bump the stamp if the effective
    /// set changed.
    /// </summary>
    private static async Task ApplyRoleChangeAsync(
        UserManager<ApplicationUser> userManager, ApplicationUser user, string[] assignedRoles)
    {
        var existingRoles = await userManager.GetRolesAsync(user);
        if (existingRoles.Any())
        {
            await userManager.RemoveFromRolesAsync(user, existingRoles);
        }
        if (assignedRoles.Length > 0)
        {
            await userManager.AddToRolesAsync(user, assignedRoles);
        }

        if (!existingRoles.OrderBy(r => r, StringComparer.Ordinal)
                .SequenceEqual(assignedRoles.OrderBy(r => r, StringComparer.Ordinal), StringComparer.Ordinal))
        {
            await userManager.UpdateSecurityStampAsync(user);
        }
    }

    [Test]
    public async Task RemovingARole_ChangesTheSecurityStamp()
    {
        var (userManager, user) = await CreateUserAsync("Admin");
        var before = await userManager.GetSecurityStampAsync(user);

        await ApplyRoleChangeAsync(userManager, user, new[] { "Basic" });

        var after = await userManager.GetSecurityStampAsync(user);
        after.Should().NotBe(before, "a demoted user's existing session must fail its next revalidation");
        (await userManager.GetRolesAsync(user)).Should().BeEquivalentTo(new[] { "Basic" });
    }

    [Test]
    public async Task RevokingAllRoles_ChangesTheSecurityStamp()
    {
        var (userManager, user) = await CreateUserAsync("Admin");
        var before = await userManager.GetSecurityStampAsync(user);

        await ApplyRoleChangeAsync(userManager, user, Array.Empty<string>());

        (await userManager.GetSecurityStampAsync(user)).Should().NotBe(before);
        (await userManager.GetRolesAsync(user)).Should().BeEmpty();
    }

    [Test]
    public async Task GrantingARole_ChangesTheSecurityStamp()
    {
        var (userManager, user) = await CreateUserAsync("Basic");
        var before = await userManager.GetSecurityStampAsync(user);

        await ApplyRoleChangeAsync(userManager, user, new[] { "Admin", "Basic" });

        (await userManager.GetSecurityStampAsync(user)).Should().NotBe(before);
    }

    [Test]
    public async Task EditingAUserWithoutChangingRoles_LeavesTheSecurityStampAlone()
    {
        // The edit path always removes and re-adds roles, so the stamp is bumped only when the
        // effective set actually changed. Otherwise every profile edit would sign the user out.
        var (userManager, user) = await CreateUserAsync("Basic", "Admin");
        var before = await userManager.GetSecurityStampAsync(user);

        await ApplyRoleChangeAsync(userManager, user, new[] { "Admin", "Basic" });

        (await userManager.GetSecurityStampAsync(user)).Should().Be(before);
    }
}
#nullable restore
