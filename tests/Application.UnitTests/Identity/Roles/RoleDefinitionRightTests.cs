#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Application.Common.ExceptionHandlers;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Application.Common.Security;
using CleanArchitecture.Blazor.Application.Features.Identity;
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
using ConstantRoles = CleanArchitecture.Blazor.Application.Common.Constants.Roles;

namespace CleanArchitecture.Blazor.Application.UnitTests.Identity.Roles;

/// <summary>
/// <c>Permissions.Roles.ManageDefinitions</c>: who may create, rename, delete, re-permission or
/// import a role, and what a principal without it can still do.
/// </summary>
/// <remarks>
/// <para>
/// <b>Through the service, not the rule.</b> The re-permissioning assertions send a real
/// <c>PermissionAssignmentService</c> call and then read the role's claims back, because a guard
/// proved only at <c>RoleDefinitionWrite.MayDefineRolesAsync</c> would prove the rule and not its
/// enforcement. The dialog and page paths - create, rename, delete, import - are proved the same way
/// one layer up, in <c>RoleDefinitionComponentTests</c>, which drives the real components against a
/// real <c>RoleManager</c> and reads the role store afterwards.
/// </para>
/// <para>
/// <b>Narrowed, not emptied.</b> The control that matters most here is
/// <see cref="ANonHolderCanStillAssignAUserToAnExistingRole"/>. The failure mode of a permission
/// guard is over-refusal, and a guard that also blocked assignment would satisfy every negative
/// assertion in this file while removing the operation a tenant administrator actually needs.
/// </para>
/// <para>
/// <b>A different guarantee from <c>AdministratorProtectionService</c>'s.</b> Those rules keep the
/// installation administrable and bind the holder of this right too;
/// <see cref="AHolderIsStillBoundByAdministratorProtection"/> holds the two apart, so a future pass
/// cannot satisfy one by deleting the other.
/// </para>
/// <para>
/// <b>Pass 32 A5's trap</b> - the auditable interceptor's FK to <c>AspNetUsers</c> - does not bite
/// here because role claims are not audited entities, but every success path below still creates
/// real user rows through <c>UserManager</c> rather than asserting refusals alone. A fixture testing
/// only refusals is green while proving nothing works.
/// </para>
/// </remarks>
[TestFixture]
public class RoleDefinitionRightTests
{
    private const string TargetRole = "Editors";
    private const string GrantablePermission = "Permissions.Documents.View";

    private SqliteConnection _connection = null!;
    private ServiceProvider _provider = null!;
    private MutableUserContextAccessor _contextAccessor = null!;
    private ConfigurablePermissionQueryService _permissionQuery = null!;

    [SetUp]
    public async Task SetUp()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();
        _contextAccessor = new MutableUserContextAccessor();
        _permissionQuery = new ConfigurablePermissionQueryService();

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
        foreach (var role in new[] { ConstantRoles.Admin, ConstantRoles.Basic, TargetRole })
        {
            (await roleManager.CreateAsync(new ApplicationRole { Name = role })).Succeeded.Should().BeTrue();
        }
    }

    [TearDown]
    public async Task TearDown()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }

    // ---- harness -------------------------------------------------------------------------------

    private sealed class MutableUserContextAccessor : IUserContextAccessor
    {
        public UserContext? Current { get; set; }
        public IDisposable Push(UserContext context) => throw new NotSupportedException();
        public void Clear() => Current = null;
    }

    /// <summary>
    /// Returns whatever the test says a given user holds. <b>Fails loudly for an unconfigured user</b>
    /// rather than returning an empty list, so a test that forgets to set the actor up sees a
    /// missing-setup error instead of a refusal that looks like the rule working.
    /// </summary>
    private sealed class ConfigurablePermissionQueryService : IPermissionQueryService
    {
        private readonly Dictionary<string, List<PermissionModel>> _byUser = new(StringComparer.Ordinal);

        public int UserQueryCount { get; private set; }

        public void Holds(string userId, params string[] permissions) =>
            _byUser[userId] = permissions
                .Select(p => new PermissionModel
                {
                    ClaimType = ApplicationClaimTypes.Permission,
                    ClaimValue = p,
                    Assigned = true,
                    UserId = userId
                })
                .ToList();

        /// <summary>A row that EXISTS but is switched off - the case a naive "any row" check misses.</summary>
        public void HoldsUnassigned(string userId, string permission) =>
            _byUser[userId] = new List<PermissionModel>
            {
                new()
                {
                    ClaimType = ApplicationClaimTypes.Permission,
                    ClaimValue = permission,
                    Assigned = false,
                    UserId = userId
                }
            };

        public Task<IList<PermissionModel>> GetAllPermissionsByUserId(string userId)
        {
            UserQueryCount++;
            if (!_byUser.TryGetValue(userId, out var permissions))
            {
                Assert.Fail($"The test did not say what user '{userId}' holds.");
                permissions = new List<PermissionModel>();
            }
            return Task.FromResult<IList<PermissionModel>>(permissions);
        }

        public Task<IList<PermissionModel>> GetAllPermissionsByRoleId(string roleId) =>
            Task.FromResult<IList<PermissionModel>>(new List<PermissionModel>());
    }

    private PermissionAssignmentService CreateService() =>
        new(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            _permissionQuery,
            _contextAccessor,
            new AdministratorProtectionService(_provider.GetRequiredService<IServiceScopeFactory>()),
            NullLogger<PermissionAssignmentService>.Instance);

    private async Task<ApplicationUser> CreateUserAsync(
        string name, string[] roles, string[] claimPermissions)
    {
        using var scope = _provider.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { UserName = name, Email = $"{name}@example.com" };
        (await userManager.CreateAsync(user, "Password123!")).Succeeded.Should().BeTrue();
        if (roles.Length > 0)
        {
            (await userManager.AddToRolesAsync(user, roles)).Succeeded.Should().BeTrue();
        }
        foreach (var permission in claimPermissions)
        {
            await userManager.AddClaimAsync(user, new Claim(ApplicationClaimTypes.Permission, permission));
        }
        return user;
    }

    /// <summary>
    /// An actor who can pass every guard on the re-permissioning path EXCEPT the one under test:
    /// they hold the permission they are granting (grant-what-you-hold) and are not a member of the
    /// target role. <paramref name="mayDefineRoles"/> is therefore the only variable.
    /// </summary>
    private async Task<ApplicationUser> CreateActorAsync(bool mayDefineRoles)
    {
        var actor = await CreateUserAsync("actor", Array.Empty<string>(), new[] { GrantablePermission });
        _permissionQuery.Holds(actor.Id, mayDefineRoles
            ? new[] { Permissions.Roles.ManageDefinitions }
            : Array.Empty<string>());
        _contextAccessor.Current = new UserContext(UserId: actor.Id, UserName: actor.UserName ?? "actor");
        return actor;
    }

    private async Task<string> RoleIdAsync(string roleName)
    {
        using var scope = _provider.CreateScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        return (await roleManager.FindByNameAsync(roleName))!.Id;
    }

    private async Task<string[]> RolePermissionsAsync(string roleName)
    {
        using var scope = _provider.CreateScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        var role = await roleManager.FindByNameAsync(roleName);
        return (await roleManager.GetClaimsAsync(role!))
            .Where(c => c.Type == ApplicationClaimTypes.Permission)
            .Select(c => c.Value)
            .ToArray();
    }

    private static PermissionModel RoleModel(string roleId, string permission, bool assigned = true) => new()
    {
        ClaimType = ApplicationClaimTypes.Permission,
        ClaimValue = permission,
        Assigned = assigned,
        RoleId = roleId
    };

    // ---- the rule itself -----------------------------------------------------------------------

    [Test]
    public async Task TheRuleFailsClosedWithNoPrincipal()
    {
        (await RoleDefinitionWrite.MayDefineRolesAsync(_permissionQuery, null))
            .Should().BeFalse("no ambient principal must not be an affirmative grant");
        (await RoleDefinitionWrite.MayDefineRolesAsync(_permissionQuery, string.Empty))
            .Should().BeFalse();

        _permissionQuery.UserQueryCount.Should().Be(
            0, "an empty user id is refused before the permission query is reached");
    }

    [Test]
    public async Task AnUnassignedGrantIsNotAGrant()
    {
        _permissionQuery.HoldsUnassigned("u1", Permissions.Roles.ManageDefinitions);

        (await RoleDefinitionWrite.MayDefineRolesAsync(_permissionQuery, "u1"))
            .Should().BeFalse("the row exists but is switched off; 'any row with this value' would pass");
    }

    [Test]
    public async Task AnUnrelatedPermissionIsNotThisOne()
    {
        _permissionQuery.Holds("u1", Permissions.Roles.Edit, Permissions.Roles.Delete,
            Permissions.Roles.ManagePermissions, Permissions.PicklistSets.ManageShared);

        (await RoleDefinitionWrite.MayDefineRolesAsync(_permissionQuery, "u1"))
            .Should().BeFalse("the old per-verb rights do not imply the new one");
    }

    [Test]
    public async Task TheRefusalSaysWhatIsStillAllowed()
    {
        _permissionQuery.Holds("u1");

        var act = async () => await RoleDefinitionWrite.EnsureAllowedAsync(_permissionQuery, "u1");

        var message = (await act.Should().ThrowAsync<ForbiddenAccessException>()).Which.Message;
        message.Should().Contain("Assigning users to existing roles does not",
            "the commonest misreading of the refusal is 'you may not use roles at all'");
    }

    [Test]
    public void TheAdministratorHoldsTheRightByDefault()
    {
        AdministratorPermissionRegistry.Granted.Should().Contain(
            Permissions.Roles.ManageDefinitions,
            "the single-tenant deployment's sole administrator must manage roles out of the box");
        AdministratorPermissionRegistry.Excluded.Keys.Should().NotContain(
            Permissions.Roles.ManageDefinitions);
    }

    [Test]
    public void TheAccessRightsPropertyIsSpelledLikeTheConstant()
    {
        // PermissionService turns the PROPERTY NAME into the claim string, so a mismatch here is a
        // right that is checked in the UI under a name nothing ever grants - see LogsAccessRights.
        typeof(RolesAccessRights).GetProperty(nameof(RolesAccessRights.ManageDefinitions))
            .Should().NotBeNull();
        Permissions.Roles.ManageDefinitions.Should().EndWith(
            "." + nameof(RolesAccessRights.ManageDefinitions));
    }

    // ---- re-permissioning, through PermissionAssignmentService --------------------------------

    [Test]
    public async Task ANonHolderCannotRePermissionARoleThroughTheService()
    {
        await CreateActorAsync(mayDefineRoles: false);
        var roleId = await RoleIdAsync(TargetRole);

        var act = async () => await CreateService()
            .AssignRoleAsync(RoleModel(roleId, GrantablePermission));

        (await act.Should().ThrowAsync<ForbiddenAccessException>())
            .Which.Message.Should().Contain("manage role definitions");
        (await RolePermissionsAsync(TargetRole)).Should().BeEmpty("the write must not have happened");
    }

    [Test]
    public async Task ANonHolderCannotRePermissionARoleInBulk()
    {
        // The actor holds BOTH permissions in their claims principal, so grant-what-you-hold would
        // let this through: the refusal is attributable to the definition guard and to nothing
        // else. The first draft granted only one, and stayed green with the guard removed - a
        // control that could not have failed for the reason it names.
        var actor = await CreateUserAsync("actor", Array.Empty<string>(),
            new[] { GrantablePermission, Permissions.Documents.Download });
        _permissionQuery.Holds(actor.Id);
        _contextAccessor.Current = new UserContext(UserId: actor.Id, UserName: "actor");
        var roleId = await RoleIdAsync(TargetRole);

        var act = async () => await CreateService().AssignRoleBulkAsync(new[]
        {
            RoleModel(roleId, GrantablePermission),
            RoleModel(roleId, Permissions.Documents.Download)
        });

        await act.Should().ThrowAsync<ForbiddenAccessException>();
        (await RolePermissionsAsync(TargetRole)).Should().BeEmpty(
            "a refused bulk grant leaves the role exactly as it was, not half-applied");
    }

    [Test]
    public async Task ANonHolderCannotREVOKEAPermissionEither()
    {
        // The direction that made this the strongest cross-tenant write: revoking a claim from a
        // shared role takes the capability from every ordinary user in every tenant at once.
        var holder = await CreateActorAsync(mayDefineRoles: true);
        var roleId = await RoleIdAsync(TargetRole);
        await CreateService().AssignRoleAsync(RoleModel(roleId, GrantablePermission));
        (await RolePermissionsAsync(TargetRole)).Should().Equal(GrantablePermission);

        _permissionQuery.Holds(holder.Id);

        var act = async () => await CreateService()
            .AssignRoleAsync(RoleModel(roleId, GrantablePermission, assigned: false));

        await act.Should().ThrowAsync<ForbiddenAccessException>();
        (await RolePermissionsAsync(TargetRole)).Should().BeEquivalentTo(
            new[] { GrantablePermission }, "the revocation must not have happened");
    }

    [Test]
    public async Task AHolderCanRePermissionARole()
    {
        await CreateActorAsync(mayDefineRoles: true);
        var roleId = await RoleIdAsync(TargetRole);

        await CreateService().AssignRoleAsync(RoleModel(roleId, GrantablePermission));

        (await RolePermissionsAsync(TargetRole)).Should().Equal(GrantablePermission);
    }

    [Test]
    public async Task AHolderCanRePermissionARoleInBulk()
    {
        var actor = await CreateUserAsync("actor", Array.Empty<string>(),
            new[] { GrantablePermission, Permissions.Documents.Download });
        _permissionQuery.Holds(actor.Id, Permissions.Roles.ManageDefinitions);
        _contextAccessor.Current = new UserContext(UserId: actor.Id, UserName: "actor");
        var roleId = await RoleIdAsync(TargetRole);

        await CreateService().AssignRoleBulkAsync(new[]
        {
            RoleModel(roleId, GrantablePermission),
            RoleModel(roleId, Permissions.Documents.Download)
        });

        (await RolePermissionsAsync(TargetRole)).Should()
            .BeEquivalentTo(new[] { GrantablePermission, Permissions.Documents.Download });
    }

    [Test]
    public async Task AHolderIsStillBoundByAdministratorProtection()
    {
        await CreateActorAsync(mayDefineRoles: true);
        var adminRoleId = await RoleIdAsync(ConstantRoles.Admin);

        var act = async () => await CreateService()
            .AssignRoleAsync(RoleModel(adminRoleId, GrantablePermission));

        (await act.Should().ThrowAsync<ForbiddenAccessException>())
            .Which.Message.Should().Contain("protected",
                "the new right does not supersede the rules that keep the installation administrable");
        (await RolePermissionsAsync(ConstantRoles.Admin)).Should().BeEmpty();
    }

    [Test]
    public async Task TheDefinitionGuardIsAskedBeforeTheActorIsBuilt()
    {
        // Ordering, asserted rather than assumed: the coarse gate runs first, so a refused caller
        // costs one permission query instead of a claims-principal rebuild, and the message they
        // get is the broadest true reason rather than a narrower one that happens to fire first.
        var actor = await CreateUserAsync("actor", new[] { TargetRole }, Array.Empty<string>());
        _permissionQuery.Holds(actor.Id);
        _contextAccessor.Current = new UserContext(UserId: actor.Id, UserName: "actor");
        var roleId = await RoleIdAsync(TargetRole);

        var act = async () => await CreateService()
            .AssignRoleAsync(RoleModel(roleId, GrantablePermission));

        // The actor is a MEMBER of the target role and holds neither right, so both
        // EnsureNotTargetingAHeldRole and EnsureActorHolds would also refuse. The definition guard
        // is the one that speaks.
        (await act.Should().ThrowAsync<ForbiddenAccessException>())
            .Which.Message.Should().Contain("manage role definitions");
    }

    // ---- narrowed, not emptied -----------------------------------------------------------------

    [Test]
    public async Task ANonHolderCanStillAssignAUserToAnExistingRole()
    {
        // THE control for this pass. Assigning a user to a role is an operation on the USER, gated
        // by Permissions.Users.*, and it is the operation a tenant administrator actually needs. It
        // runs here exactly as UserFormDialog.SubmitAsync runs it - the administrator-protection
        // check, then the UserManager rewrite - with an actor who cannot define roles at all.
        var actor = await CreateActorAsync(mayDefineRoles: false);
        var target = await CreateUserAsync("target", Array.Empty<string>(), Array.Empty<string>());

        using var scope = _provider.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var protection = new AdministratorProtectionService(
            _provider.GetRequiredService<IServiceScopeFactory>());

        var user = await userManager.FindByIdAsync(target.Id);
        var existingRoles = await userManager.GetRolesAsync(user!);
        await protection.EnsureRoleRewriteKeepsAnAdministratorAsync(
            user!.Id, existingRoles, new[] { TargetRole });
        (await userManager.AddToRolesAsync(user, new[] { TargetRole })).Succeeded.Should().BeTrue();

        (await userManager.GetRolesAsync(user)).Should().Contain(TargetRole,
            "a guard that also blocked assignment would satisfy every refusal test above while " +
            "removing the operation the right was never meant to take away");

        // And the actor really could not have defined the role they just assigned.
        (await RoleDefinitionWrite.MayDefineRolesAsync(_permissionQuery, actor.Id)).Should().BeFalse();
    }

    [Test]
    public async Task ANonHolderCanStillRemoveAUserFromARole()
    {
        await CreateActorAsync(mayDefineRoles: false);
        var target = await CreateUserAsync("target", new[] { TargetRole }, Array.Empty<string>());

        using var scope = _provider.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByIdAsync(target.Id);

        (await userManager.RemoveFromRolesAsync(user!, new[] { TargetRole })).Succeeded.Should().BeTrue();

        (await userManager.GetRolesAsync(user!)).Should().BeEmpty();
    }

    [Test]
    public async Task ANonHolderCanStillREADTheRoleList()
    {
        await CreateActorAsync(mayDefineRoles: false);

        using var scope = _provider.CreateScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();

        (await roleManager.Roles.ToListAsync()).Should().HaveCount(3,
            "this is a WRITE right; nothing about who may see the installation's roles changed, " +
            "which is why RoleDataSourceService.Scope stays Global");
    }
}
