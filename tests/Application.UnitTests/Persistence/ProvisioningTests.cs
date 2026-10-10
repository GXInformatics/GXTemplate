#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Application.Common.Security;
using CleanArchitecture.Blazor.Domain.Entities;
using CleanArchitecture.Blazor.Domain.Identity;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Infrastructure.Configurations;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Application.Common.Interfaces.MultiTenant;
using CleanArchitecture.Blazor.Infrastructure.Services.Identity;
using CleanArchitecture.Blazor.Infrastructure.Services.MultiTenant;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Npgsql;
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
    private NpgsqlConnection _connection = null!;
    private ServiceProvider _provider = null!;

    [SetUp]
    public async Task SetUp()
    {
        _connection = UnitTestDatabase.NewConnection();
        await _connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connection));
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
        // The initializer takes the configured default time zone for the administrator it creates;
        // the defaults of AppConfigurationSettings are what a project with no configuration gets.
        services.AddSingleton<IApplicationSettings>(new AppConfigurationSettings());
        services.AddScoped<ApplicationDbContextInitializer>();
        // Pass 54: provisioning runs every tenant seeder for every tenant, through the runner, which
        // hides the ambient principal. None are registered here, so the runner runs nothing.
        services.AddSingleton<IUserContextAccessor, UserContextAccessor>();
        services.AddScoped<ITenantSeedRunner, TenantSeedRunner>();

        _provider = services.BuildServiceProvider();

        // The shared test database is migrated through the real migrations once per run
        // (UnitTestDatabase), so the schema under test is the migrated one; the reset gives each test
        // an empty database.
        await UnitTestDatabase.ResetAsync();
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

    /// <summary>
    /// The accounts a PERSON can sign in as - every account except the system one.
    /// </summary>
    /// <remarks>
    /// Pass 54 provisions <see cref="Users.System"/> in every environment, so "the users" now
    /// includes an account nobody can sign in as. The assertions below are about people - exactly one
    /// administrator, no Demo account, the administrator's password and memberships - so they read
    /// through this, and the system account has its own tests further down.
    /// </remarks>
    private async Task<ApplicationUser[]> UsersAsync()
    {
        using var scope = _provider.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        return await userManager.Users.Where(u => u.UserName != Users.System).ToArrayAsync();
    }

    private async Task<ApplicationUser?> SystemAccountAsync()
    {
        using var scope = _provider.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        return await userManager.FindByNameAsync(Users.System);
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

    // ---- reconciliation ------------------------------------------------------------------------
    //
    // Provisioning has to be idempotent per ITEM, not per RUN. The shape these tests exist to
    // prevent is `if (RoleExistsAsync) return;`, which passes every test above - the roles and
    // claims are all correct on a fresh database - while silently never delivering a permission
    // added in a later release to any database provisioned before it.

    private async Task RevokeAsync(string roleName, string permission)
    {
        using var scope = _provider.CreateScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        var role = await roleManager.FindByNameAsync(roleName);
        await roleManager.RemoveClaimAsync(
            role!, new System.Security.Claims.Claim(ApplicationClaimTypes.Permission, permission));
    }

    private async Task GrantAsync(string roleName, string permission)
    {
        using var scope = _provider.CreateScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        var role = await roleManager.FindByNameAsync(roleName);
        await roleManager.AddClaimAsync(
            role!, new System.Security.Claims.Claim(ApplicationClaimTypes.Permission, permission));
    }

    [Test]
    public async Task Provisioning_RestoresAGrantRemovedBehindItsBack()
    {
        // Stands in for the case that actually bites: a permission that did not exist when this
        // database was provisioned, added to AdministratorPermissionRegistry in a later release.
        // Removing an existing grant is the same situation from the database's point of view, and
        // it is one a test can arrange.
        await ProvisionAsync();

        var revoked = AdministratorPermissionRegistry.Granted.First();
        await RevokeAsync(Roles.Admin, revoked);
        (await ClaimsOfAsync(Roles.Admin)).Should().NotContain(revoked, "the arrangement must take");

        await ProvisionAsync();

        (await ClaimsOfAsync(Roles.Admin)).Should().Contain(revoked,
            "a later start must deliver a grant the database does not hold");
    }

    [Test]
    public async Task Provisioning_RestoresAGrantOnTheBasicRoleToo()
    {
        // The old guard tested the ADMIN role and returned, so the Basic role's grants were never
        // reconciled either - a second failure hiding behind the first.
        await ProvisionAsync();

        var revoked = Permissions.Documents.Download;
        await RevokeAsync(Roles.Basic, revoked);

        await ProvisionAsync();

        (await ClaimsOfAsync(Roles.Basic)).Should().Contain(revoked);
    }

    [Test]
    public async Task Provisioning_RecreatesARoleThatWasDeleted()
    {
        await ProvisionAsync();

        using (var scope = _provider.CreateScope())
        {
            var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
            await roleManager.DeleteAsync((await roleManager.FindByNameAsync(Roles.Basic))!);
        }

        await ProvisionAsync();

        (await RoleNamesAsync()).Should().Contain(Roles.Basic);
        (await ClaimsOfAsync(Roles.Basic)).Should().BeEquivalentTo(
            new[] { Permissions.Documents.View, Permissions.Documents.Download });
    }

    [Test]
    public async Task Provisioning_DoesNotRevokeAGrantAnOperatorAdded()
    {
        // Grant-only. Reconciling to an exact set would make every deployment quietly undo the
        // permission someone granted at runtime to unblock a user, which is a worse failure than
        // the one being fixed - and a silent one.
        await ProvisionAsync();

        var extra = Permissions.Documents.Create;
        await GrantAsync(Roles.Basic, extra);

        await ProvisionAsync();

        (await ClaimsOfAsync(Roles.Basic)).Should().Contain(extra,
            "provisioning restores what is missing; it does not enforce an exact set");
    }

    [Test]
    public async Task ProvisioningTwice_AddsNoDuplicateGrants()
    {
        // The reconcile compares against what the role already holds, so the second run inserts
        // nothing. Without that comparison this is where AddClaimAsync would duplicate every row.
        await ProvisionAsync();
        var afterFirst = await ClaimsOfAsync(Roles.Admin);

        await ProvisionAsync();

        (await ClaimsOfAsync(Roles.Admin)).Should().BeEquivalentTo(afterFirst);
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

    // ---- the system account (Pass 54) ----------------------------------------------------------

    [Test]
    public async Task Provisioning_CreatesTheSystemAccount_AndNothingCanSignInAsIt()
    {
        await ProvisionAsync();

        var account = await SystemAccountAsync();
        account.Should().NotBeNull();
        account!.PasswordHash.Should().BeNull("password sign-in must have nothing to check against");
        account.LockoutEnabled.Should().BeTrue();
        account.LockoutEnd.Should().Be(DateTimeOffset.MaxValue);
        account.IsActive.Should().BeFalse();
        account.EmailConfirmed.Should().BeFalse();

        using var scope = _provider.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        (await userManager.GetRolesAsync(account)).Should().BeEmpty(
            "in the Admin role it would suppress provisioning of the administrator a person signs in as");
        (await userManager.GetLoginsAsync(account)).Should().BeEmpty();
    }

    [Test]
    public async Task TheSystemAccount_HoldsTheAdministratorRegistryAsUserClaims()
    {
        await ProvisionAsync();

        using var scope = _provider.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var claims = (await userManager.GetClaimsAsync((await SystemAccountAsync())!))
            .Where(c => c.Type == ApplicationClaimTypes.Permission)
            .Select(c => c.Value);

        claims.Should().BeEquivalentTo(AdministratorPermissionRegistry.Granted);
    }

    [Test]
    public async Task ProvisioningTwice_DoesNotDuplicateTheSystemAccountOrItsGrants()
    {
        await ProvisionAsync();
        await ProvisionAsync();

        using var scope = _provider.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        (await userManager.Users.CountAsync(u => u.UserName == Users.System)).Should().Be(1);
        (await userManager.GetClaimsAsync((await SystemAccountAsync())!))
            .Select(c => c.Value).Should().OnlyHaveUniqueItems();
    }

    [Test]
    public async Task Provisioning_RestoresTheSystemAccountsLockout()
    {
        await ProvisionAsync();
        using (var scope = _provider.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var account = (await userManager.FindByNameAsync(Users.System))!;
            await userManager.SetLockoutEndDateAsync(account, null);
        }

        await ProvisionAsync();

        (await SystemAccountAsync())!.LockoutEnd.Should().Be(DateTimeOffset.MaxValue);
    }

    [Test]
    public async Task Provisioning_RefusesToAdoptAnAccountOfThatNameThatHasAPassword()
    {
        // Somebody registered "gx-system" as a person. Granting it every permission would hand them
        // the installation, so provisioning stops instead - and grants nothing.
        using (var scope = _provider.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var person = new ApplicationUser { UserName = Users.System, Email = "someone@example.com" };
            (await userManager.CreateAsync(person, "A-Strong-Passw0rd!")).Succeeded.Should().BeTrue();
        }

        var provisioning = async () => await ProvisionAsync();

        await provisioning.Should().ThrowAsync<InvalidOperationException>().WithMessage($"*'{Users.System}'*");
        using var check = _provider.CreateScope();
        var users = check.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        (await users.GetClaimsAsync((await SystemAccountAsync())!)).Should().BeEmpty();
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
