#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.ExceptionHandlers;
using CleanArchitecture.Blazor.Domain.Identity;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using CleanArchitecture.Blazor.Infrastructure.Services.Identity;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using ConstantRoles = CleanArchitecture.Blazor.Application.Common.Constants.Roles;

namespace CleanArchitecture.Blazor.Application.UnitTests.Identity;

/// <summary>
/// The rules that keep the application administrable. Role and user administration bypasses Mediator,
/// so deny-by-default does not reach it; without these guards the Administrator role could be deleted,
/// stripped of its permissions, or emptied of members, and nothing recreates it outside first-run
/// seeding.
/// </summary>
[TestFixture]
public class AdministratorProtectionTests
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
        foreach (var role in new[] { ConstantRoles.Admin, ConstantRoles.Basic })
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

    private AdministratorProtectionService Service() =>
        new(_provider.GetRequiredService<IServiceScopeFactory>());

    private async Task<ApplicationUser> CreateUserAsync(string name, params string[] roles)
    {
        using var scope = _provider.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { UserName = name, Email = $"{name}@example.com" };
        (await userManager.CreateAsync(user, "Password123!")).Succeeded.Should().BeTrue();
        if (roles.Length > 0)
        {
            (await userManager.AddToRolesAsync(user, roles)).Succeeded.Should().BeTrue();
        }
        return user;
    }

    // ---- role deletion --------------------------------------------------------------------------

    [Test]
    public void TheAdministratorRoleCannotBeDeleted()
    {
        var act = () => Service().EnsureRoleCanBeDeleted(ConstantRoles.Admin);

        act.Should().Throw<ForbiddenAccessException>()
            .WithMessage($"*{ConstantRoles.Admin}*cannot be deleted*");
    }

    [Test]
    public void TheAdministratorRoleCheckIsCaseInsensitive()
    {
        // Role names round-trip through normalisation; a differently-cased name is the same role.
        var act = () => Service().EnsureRoleCanBeDeleted("admin");

        act.Should().Throw<ForbiddenAccessException>();
    }

    [Test]
    public void AnyOtherRoleCanBeDeleted()
    {
        var act = () => Service().EnsureRoleCanBeDeleted(ConstantRoles.Basic);

        act.Should().NotThrow();
    }

    // ---- role permissions -----------------------------------------------------------------------

    [Test]
    public void PermissionsOnTheAdministratorRoleCannotBeModified()
    {
        var act = () => Service().EnsureRolePermissionsCanBeModified(ConstantRoles.Admin);

        act.Should().Throw<ForbiddenAccessException>()
            .WithMessage($"*{ConstantRoles.Admin}*cannot be modified*");
    }

    [Test]
    public void PermissionsOnAnyOtherRoleCanBeModified()
    {
        var act = () => Service().EnsureRolePermissionsCanBeModified(ConstantRoles.Basic);

        act.Should().NotThrow();
    }

    // ---- last administrator ---------------------------------------------------------------------

    [Test]
    public async Task TheLastAdministratorCannotBeRemoved()
    {
        var admin = await CreateUserAsync("solo", ConstantRoles.Admin);

        var act = async () => await Service().EnsureNotRemovingLastAdministratorAsync(admin.Id, "deleted");

        (await act.Should().ThrowAsync<ForbiddenAccessException>())
            .Which.Message.Should().Contain("last remaining member");
    }

    [Test]
    public async Task ANonLastAdministratorCanBeRemoved()
    {
        var first = await CreateUserAsync("admin-one", ConstantRoles.Admin);
        await CreateUserAsync("admin-two", ConstantRoles.Admin);

        var act = async () => await Service().EnsureNotRemovingLastAdministratorAsync(first.Id, "deleted");

        await act.Should().NotThrowAsync();
    }

    [Test]
    public async Task ANonAdministratorIsNeverBlocked()
    {
        var basic = await CreateUserAsync("basic", ConstantRoles.Basic);

        var act = async () => await Service().EnsureNotRemovingLastAdministratorAsync(basic.Id, "deleted");

        await act.Should().NotThrowAsync();
    }

    [Test]
    public async Task AnUnknownUserIsNeverBlocked()
    {
        var act = async () => await Service().EnsureNotRemovingLastAdministratorAsync("no-such-user", "deleted");

        await act.Should().NotThrowAsync();
    }

    // ---- role-membership rewrite ----------------------------------------------------------------

    [Test]
    public async Task ARewriteThatDropsTheLastAdministratorIsRefused()
    {
        var admin = await CreateUserAsync("solo", ConstantRoles.Admin);

        var act = async () => await Service().EnsureRoleRewriteKeepsAnAdministratorAsync(
            admin.Id, new[] { ConstantRoles.Admin }, new[] { ConstantRoles.Basic });

        (await act.Should().ThrowAsync<ForbiddenAccessException>())
            .Which.Message.Should().Contain("last remaining member");
    }

    [Test]
    public async Task ARewriteThatKeepsTheAdministratorRoleIsAllowed()
    {
        var admin = await CreateUserAsync("solo", ConstantRoles.Admin);

        var act = async () => await Service().EnsureRoleRewriteKeepsAnAdministratorAsync(
            admin.Id, new[] { ConstantRoles.Admin }, new[] { ConstantRoles.Admin, ConstantRoles.Basic });

        await act.Should().NotThrowAsync();
    }

    [Test]
    public async Task ARewriteDroppingAdministratorFromOneOfTwoIsAllowed()
    {
        var first = await CreateUserAsync("admin-one", ConstantRoles.Admin);
        await CreateUserAsync("admin-two", ConstantRoles.Admin);

        var act = async () => await Service().EnsureRoleRewriteKeepsAnAdministratorAsync(
            first.Id, new[] { ConstantRoles.Admin }, new[] { ConstantRoles.Basic });

        await act.Should().NotThrowAsync();
    }

    [Test]
    public async Task ARewriteOnANonAdministratorIsNeverBlocked()
    {
        var basic = await CreateUserAsync("basic", ConstantRoles.Basic);

        var act = async () => await Service().EnsureRoleRewriteKeepsAnAdministratorAsync(
            basic.Id, new[] { ConstantRoles.Basic }, Array.Empty<string>());

        await act.Should().NotThrowAsync();
    }
}
#nullable restore
