#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Application.Common.Security;
using CleanArchitecture.Blazor.Domain.Entities;
using CleanArchitecture.Blazor.Domain.Identity;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.Persistence;

/// <summary>
/// What a database looks like after the application has started, in each environment.
/// <para>
/// Provisioning (roles, one organisation, an administrator) now runs everywhere; sample data
/// (a second organisation, picklists) only in Development. Before Pass 7-3 the whole lot sat behind
/// an <c>IsDevelopment()</c> gate, so a production deployment came up migrated and unusable - no
/// roles and no account to sign in with. These tests pin both halves of the split, and that each is
/// idempotent.
/// </para>
/// </summary>
[TestFixture]
public class ProvisioningTests
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
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseSqlite(_connection));
        services.AddIdentityCore<ApplicationUser>(o =>
            {
                // Deliberately the strictest shape the template can be configured with, so the
                // generated password is exercised against every rule at once.
                o.Password.RequireDigit = true;
                o.Password.RequiredLength = 8;
                o.Password.RequireNonAlphanumeric = true;
                o.Password.RequireUppercase = true;
                o.Password.RequireLowercase = true;
                o.Password.RequiredUniqueChars = 6;
            })
            .AddRoles<ApplicationRole>()
            .AddEntityFrameworkStores<ApplicationDbContext>();
        services.AddScoped<ApplicationDbContextInitializer>();

        _provider = services.BuildServiceProvider();

        // InitialiseAsync runs migrations, which this provider-agnostic in-memory database does not
        // carry; EnsureCreated builds the same schema for the paths under test.
        using var scope = _provider.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }

    // ---- harness -------------------------------------------------------------------------------

    private async Task ProvisionAsync()
    {
        using var scope = _provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ApplicationDbContextInitializer>().ProvisionAsync();
    }

    private async Task SeedSampleDataAsync()
    {
        using var scope = _provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ApplicationDbContextInitializer>().SeedSampleDataAsync();
    }

    private async Task<string[]> RoleNamesAsync()
    {
        using var scope = _provider.CreateScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        return await roleManager.Roles.Select(r => r.Name!).ToArrayAsync();
    }

    private async Task<string[]> ClaimsOfAsync(string roleName)
    {
        using var scope = _provider.CreateScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        var role = await roleManager.FindByNameAsync(roleName);
        return (await roleManager.GetClaimsAsync(role!))
            .Where(c => c.Type == ApplicationClaimTypes.Permission)
            .Select(c => c.Value)
            .ToArray();
    }

    private async Task<ApplicationUser[]> UsersAsync()
    {
        using var scope = _provider.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        return await userManager.Users.ToArrayAsync();
    }

    private async Task<T> WithContextAsync<T>(Func<ApplicationDbContext, Task<T>> read)
    {
        using var scope = _provider.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        return await read(db);
    }

    // ---- roles ---------------------------------------------------------------------------------

    [Test]
    public async Task Provisioning_CreatesAdminAndBasic_AndNothingElse()
    {
        await ProvisionAsync();

        (await RoleNamesAsync()).Should().BeEquivalentTo(new[] { Roles.Admin, Roles.Basic },
            "Roles.Users was removed in Pass 7-3 - it gated nothing and held the same claims as Basic");
    }

    [Test]
    public async Task TheAdministratorRole_HoldsExactlyTheExplicitlyGrantedPermissions()
    {
        await ProvisionAsync();

        (await ClaimsOfAsync(Roles.Admin)).Should().BeEquivalentTo(
            AdministratorPermissionRegistry.Granted,
            "the grant is an explicit list, not whatever reflection happens to find");
    }

    [Test]
    public async Task TheAdministratorRole_HoldsNoneOfTheExcludedPermissions()
    {
        await ProvisionAsync();

        (await ClaimsOfAsync(Roles.Admin)).Should().NotIntersectWith(
            AdministratorPermissionRegistry.Excluded.Keys,
            "an excluded permission names a feature this template does not have");
    }

    [Test]
    public async Task TheBasicRole_HoldsExactlyTheDocumentsReadGrant()
    {
        await ProvisionAsync();

        (await ClaimsOfAsync(Roles.Basic)).Should().BeEquivalentTo(
            new[] { Permissions.Documents.View, Permissions.Documents.Download },
            "View gates the grid query and Download gates the file stream; nothing else is enforced");
    }

    // ---- the administrator account -------------------------------------------------------------

    [Test]
    public async Task Provisioning_CreatesExactlyOneAccount_AndItIsTheAdministrator()
    {
        await ProvisionAsync();

        var users = await UsersAsync();
        users.Should().HaveCount(1, "the Demo account was removed in Pass 7-3");
        users[0].UserName.Should().Be(Users.Administrator);

        using var scope = _provider.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        (await userManager.IsInRoleAsync(users[0], Roles.Admin)).Should().BeTrue();
    }

    [Test]
    public async Task TheProvisionedAdministrator_MustChangeItsPassword()
    {
        await ProvisionAsync();

        (await UsersAsync())[0].MustChangePassword.Should().BeTrue(
            "the account holds a password nobody chose");
    }

    [Test]
    public async Task TheProvisionedAdministrator_DoesNotHoldTheOldHardcodedPassword()
    {
        await ProvisionAsync();

        using var scope = _provider.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var administrator = (await UsersAsync())[0];

        foreach (var candidate in new[] { "Password123!", "Administrator", "admin", "P@ssw0rd" })
        {
            (await userManager.CheckPasswordAsync(administrator, candidate))
                .Should().BeFalse($"'{candidate}' must not be the provisioned password");
        }
    }

    [Test]
    public async Task TheGeneratedPassword_SatisfiesTheConfiguredPolicy()
    {
        // The proof is indirect but exact: UserManager.CreateAsync runs the configured password
        // validators, and this fixture configures the strictest policy the template supports. An
        // account exists at all only because the generated value passed every one of them - and
        // EnsureAdministratorAsync throws rather than continuing if it did not.
        await ProvisionAsync();

        (await UsersAsync()).Should().ContainSingle();
    }

    [Test]
    public async Task ProvisioningTwice_ChangesNothingAndDoesNotAddASecondAdministrator()
    {
        await ProvisionAsync();
        var firstHash = (await UsersAsync())[0].PasswordHash;

        await ProvisionAsync();

        var users = await UsersAsync();
        users.Should().HaveCount(1, "a second start must not provision another administrator");
        users[0].PasswordHash.Should().Be(firstHash, "nor re-generate the existing one's password");
        (await ClaimsOfAsync(Roles.Admin)).Should().OnlyHaveUniqueItems(
            "a second start must not duplicate claims");
    }

    [Test]
    public async Task AnAdministratorUnderAnyName_SuppressesProvisioning()
    {
        // The check is role membership, not the username: an installation that renamed its
        // administrator must not have a second one provisioned underneath it.
        await ProvisionAsync();

        using (var scope = _provider.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var existing = (await UsersAsync())[0];
            existing.UserName = "root";
            await userManager.UpdateAsync(existing);
        }

        await ProvisionAsync();

        var users = await UsersAsync();
        users.Should().HaveCount(1);
        users[0].UserName.Should().Be("root");
    }

    // ---- the environment split -----------------------------------------------------------------

    [Test]
    public async Task Provisioning_CreatesOneOrganisationAndNoSampleData()
    {
        await ProvisionAsync();

        (await WithContextAsync(db => db.Tenants.CountAsync())).Should().Be(1,
            "an account needs an organisation to belong to; a second one is sample data");
        (await WithContextAsync(db => db.PicklistSets.CountAsync())).Should().Be(0,
            "picklists exist to make a development environment pleasant, not to run");
        (await WithContextAsync(db => db.Documents.CountAsync())).Should().Be(0);
    }

    [Test]
    public async Task SampleData_AddsASecondOrganisationAndThePicklists()
    {
        await ProvisionAsync();
        await SeedSampleDataAsync();

        (await WithContextAsync(db => db.Tenants.CountAsync())).Should().Be(2);
        (await WithContextAsync(db => db.PicklistSets.CountAsync())).Should().BeGreaterThan(0);
        (await UsersAsync()).Should().ContainSingle("sample data must not add a Demo account either");
    }

    [Test]
    public async Task SampleData_KeepsTheAdministratorInEveryOrganisation()
    {
        await ProvisionAsync();
        await SeedSampleDataAsync();

        var administratorId = (await UsersAsync())[0].Id;
        var memberships = await WithContextAsync(db =>
            db.TenantUsers.Where(tu => tu.UserId == administratorId).CountAsync());

        memberships.Should().Be(2, "tenant switching is only demonstrable if the admin is in both");
    }

    [Test]
    public async Task SampleDataTwice_IsIdempotent()
    {
        await ProvisionAsync();
        await SeedSampleDataAsync();
        var tenants = await WithContextAsync(db => db.Tenants.CountAsync());
        var picklists = await WithContextAsync(db => db.PicklistSets.CountAsync());

        await SeedSampleDataAsync();

        (await WithContextAsync(db => db.Tenants.CountAsync())).Should().Be(tenants);
        (await WithContextAsync(db => db.PicklistSets.CountAsync())).Should().Be(picklists);
        (await WithContextAsync(db => db.TenantUsers.CountAsync())).Should().Be(2);
    }
}
#nullable restore
