#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Application.Common.ExceptionHandlers;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Application.Common.Security;
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

namespace CleanArchitecture.Blazor.Application.UnitTests.Identity;

/// <summary>
/// Guards on <see cref="PermissionAssignmentService"/>. Before these existed the service performed no
/// authorization at all: anyone who reached the permissions editor could grant themselves - or anyone
/// else - every permission in the system, because role and user administration bypasses Mediator and
/// therefore AuthorizationBehaviour's deny-by-default.
/// </summary>
[TestFixture]
public class PermissionAssignmentGuardTests
{
    private const string Granted = "Permissions.Documents.View";
    private const string NotHeld = "Permissions.Users.Delete";

    private SqliteConnection _connection = null!;
    private ServiceProvider _provider = null!;
    private MutableUserContextAccessor _contextAccessor = null!;

    [SetUp]
    public async Task SetUp()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();
        _contextAccessor = new MutableUserContextAccessor();

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
        foreach (var role in new[] { ConstantRoles.Admin, ConstantRoles.Basic, "Editors" })
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

    // ---- harness -------------------------------------------------------------------------------

    private sealed class MutableUserContextAccessor : IUserContextAccessor
    {
        public UserContext? Current { get; set; }
        public IDisposable Push(UserContext context) => throw new NotSupportedException();
        public void Clear() => Current = null;
    }

    /// <summary>Counts principal rebuilds, so "one computation per operation" is observable.</summary>
    private sealed class CountingClaimsPrincipalFactory : IUserClaimsPrincipalFactory<ApplicationUser>
    {
        private readonly IUserClaimsPrincipalFactory<ApplicationUser> _inner;
        public CountingClaimsPrincipalFactory(IUserClaimsPrincipalFactory<ApplicationUser> inner) => _inner = inner;
        public int Builds { get; private set; }

        public Task<ClaimsPrincipal> CreateAsync(ApplicationUser user)
        {
            Builds++;
            return _inner.CreateAsync(user);
        }
    }

    private CountingClaimsPrincipalFactory? _countingFactory;

    /// <summary>
    /// Builds the service over the real Identity stack, optionally wrapping the claims-principal
    /// factory so rebuilds can be counted.
    /// </summary>
    private PermissionAssignmentService CreateService(bool countPrincipalBuilds = false)
    {
        var scopeFactory = countPrincipalBuilds
            ? new DecoratingScopeFactory(_provider.GetRequiredService<IServiceScopeFactory>(), this)
            : (IServiceScopeFactory)_provider.GetRequiredService<IServiceScopeFactory>();

        return new PermissionAssignmentService(
            scopeFactory,
            new StubPermissionQueryService(),
            _contextAccessor,
            new AdministratorProtectionService(_provider.GetRequiredService<IServiceScopeFactory>()),
            NullLogger<PermissionAssignmentService>.Instance);
    }

    private sealed class DecoratingScopeFactory : IServiceScopeFactory
    {
        private readonly IServiceScopeFactory _inner;
        private readonly PermissionAssignmentGuardTests _owner;
        public DecoratingScopeFactory(IServiceScopeFactory inner, PermissionAssignmentGuardTests owner)
        {
            _inner = inner; _owner = owner;
        }

        public IServiceScope CreateScope() => new Scope(_inner.CreateScope(), _owner);

        private sealed class Scope : IServiceScope, IServiceProvider
        {
            private readonly IServiceScope _inner;
            private readonly PermissionAssignmentGuardTests _owner;
            public Scope(IServiceScope inner, PermissionAssignmentGuardTests owner)
            {
                _inner = inner; _owner = owner;
            }
            public IServiceProvider ServiceProvider => this;
            public void Dispose() => _inner.Dispose();

            public object? GetService(Type serviceType)
            {
                var resolved = _inner.ServiceProvider.GetService(serviceType);
                if (serviceType == typeof(IUserClaimsPrincipalFactory<ApplicationUser>) && resolved is not null)
                {
                    _owner._countingFactory ??= new CountingClaimsPrincipalFactory(
                        (IUserClaimsPrincipalFactory<ApplicationUser>)resolved);
                    return _owner._countingFactory;
                }
                return resolved;
            }
        }
    }

    private sealed class StubPermissionQueryService : IPermissionQueryService
    {
        public Task<IList<PermissionModel>> GetAllPermissionsByUserId(string userId) =>
            Task.FromResult<IList<PermissionModel>>(new List<PermissionModel>());
        public Task<IList<PermissionModel>> GetAllPermissionsByRoleId(string roleId) =>
            Task.FromResult<IList<PermissionModel>>(new List<PermissionModel>());
    }

    private async Task<ApplicationUser> CreateUserAsync(string name, string[] roles, string[] permissions)
    {
        using var scope = _provider.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { UserName = name, Email = $"{name}@example.com" };
        (await userManager.CreateAsync(user, "Password123!")).Succeeded.Should().BeTrue();
        if (roles.Length > 0)
        {
            (await userManager.AddToRolesAsync(user, roles)).Succeeded.Should().BeTrue();
        }
        foreach (var permission in permissions)
        {
            await userManager.AddClaimAsync(user, new Claim(ApplicationClaimTypes.Permission, permission));
        }
        return user;
    }

    private async Task<string> RoleIdAsync(string roleName)
    {
        using var scope = _provider.CreateScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        return (await roleManager.FindByNameAsync(roleName))!.Id;
    }

    private async Task<string[]> UserPermissionsAsync(string userId)
    {
        using var scope = _provider.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByIdAsync(userId);
        return (await userManager.GetClaimsAsync(user!))
            .Where(c => c.Type == ApplicationClaimTypes.Permission).Select(c => c.Value).ToArray();
    }

    private async Task<string[]> RolePermissionsAsync(string roleName)
    {
        using var scope = _provider.CreateScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        var role = await roleManager.FindByNameAsync(roleName);
        return (await roleManager.GetClaimsAsync(role!))
            .Where(c => c.Type == ApplicationClaimTypes.Permission).Select(c => c.Value).ToArray();
    }

    private void ActAs(ApplicationUser user) =>
        _contextAccessor.Current = new UserContext(UserId: user.Id, UserName: user.UserName ?? "u");

    private static PermissionModel UserModel(string userId, string permission, bool assigned = true) => new()
    {
        ClaimType = ApplicationClaimTypes.Permission,
        ClaimValue = permission,
        Assigned = assigned,
        UserId = userId
    };

    private static PermissionModel RoleModel(string roleId, string permission, bool assigned = true) => new()
    {
        ClaimType = ApplicationClaimTypes.Permission,
        ClaimValue = permission,
        Assigned = assigned,
        RoleId = roleId
    };

    // ---- grant-what-you-hold -------------------------------------------------------------------

    [Test]
    public async Task AGranterWhoDoesNotHoldThePermission_IsDenied_AndNothingIsWritten()
    {
        var actor = await CreateUserAsync("actor", Array.Empty<string>(), new[] { Granted });
        var target = await CreateUserAsync("target", Array.Empty<string>(), Array.Empty<string>());
        ActAs(actor);

        var act = async () => await CreateService().AssignUserAsync(UserModel(target.Id, NotHeld));

        (await act.Should().ThrowAsync<ForbiddenAccessException>())
            .Which.Message.Should().Contain(NotHeld);
        (await UserPermissionsAsync(target.Id)).Should().BeEmpty("the write must not have happened");
    }

    [Test]
    public async Task AGranterWhoHoldsThePermission_Succeeds()
    {
        var actor = await CreateUserAsync("actor", Array.Empty<string>(), new[] { Granted });
        var target = await CreateUserAsync("target", Array.Empty<string>(), Array.Empty<string>());
        ActAs(actor);

        await CreateService().AssignUserAsync(UserModel(target.Id, Granted));

        (await UserPermissionsAsync(target.Id)).Should().Equal(Granted);
    }

    [Test]
    public async Task RevokingAPermissionTheActorDoesNotHold_IsAlsoDenied()
    {
        // Revocation is as much an attack as a grant: it is how you disable someone else's controls.
        var actor = await CreateUserAsync("actor", Array.Empty<string>(), new[] { Granted });
        var target = await CreateUserAsync("target", Array.Empty<string>(), new[] { NotHeld });
        ActAs(actor);

        var act = async () => await CreateService().AssignUserAsync(UserModel(target.Id, NotHeld, assigned: false));

        await act.Should().ThrowAsync<ForbiddenAccessException>();
        (await UserPermissionsAsync(target.Id)).Should().BeEquivalentTo(new[] { NotHeld },
            "the revoke must not have happened");
    }

    [Test]
    public async Task WithNoAmbientPrincipal_TheServiceFailsClosed()
    {
        var target = await CreateUserAsync("target", Array.Empty<string>(), Array.Empty<string>());
        _contextAccessor.Current = null;

        var act = async () => await CreateService().AssignUserAsync(UserModel(target.Id, Granted));

        (await act.Should().ThrowAsync<ForbiddenAccessException>())
            .Which.Message.Should().Contain("authenticated user");
        (await UserPermissionsAsync(target.Id)).Should().BeEmpty();
    }

    [Test]
    public async Task APermissionHeldOnlyThroughARole_CountsAsHeld()
    {
        // The actor's effective set is user claims PLUS the claims of every role they hold - which is
        // what the claims-principal factory produces, and why one build suffices.
        using (var scope = _provider.CreateScope())
        {
            var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
            var editors = await roleManager.FindByNameAsync("Editors");
            await roleManager.AddClaimAsync(editors!, new Claim(ApplicationClaimTypes.Permission, Granted));
        }
        var actor = await CreateUserAsync("actor", new[] { "Editors" }, Array.Empty<string>());
        var target = await CreateUserAsync("target", Array.Empty<string>(), Array.Empty<string>());
        ActAs(actor);

        await CreateService().AssignUserAsync(UserModel(target.Id, Granted));

        (await UserPermissionsAsync(target.Id)).Should().Equal(Granted);
    }

    [Test]
    public async Task ABulkGrantComputesTheActorExactlyOnce()
    {
        var actor = await CreateUserAsync("actor", Array.Empty<string>(), new[] { Granted, "Permissions.Documents.Edit", "Permissions.Documents.Create" });
        var target = await CreateUserAsync("target", Array.Empty<string>(), Array.Empty<string>());
        ActAs(actor);
        _countingFactory = null;

        var models = new[] { Granted, "Permissions.Documents.Edit", "Permissions.Documents.Create" }
            .Select(p => UserModel(target.Id, p)).ToList();
        await CreateService(countPrincipalBuilds: true).AssignUserBulkAsync(models);

        _countingFactory!.Builds.Should().Be(1,
            "a bulk grant must not rebuild the acting principal once per claim");
        (await UserPermissionsAsync(target.Id)).Should().HaveCount(3);
    }

    [Test]
    public async Task ABulkGrantIsRejectedWholesaleIfAnySingleClaimIsNotHeld()
    {
        var actor = await CreateUserAsync("actor", Array.Empty<string>(), new[] { Granted });
        var target = await CreateUserAsync("target", Array.Empty<string>(), Array.Empty<string>());
        ActAs(actor);

        var models = new[] { UserModel(target.Id, Granted), UserModel(target.Id, NotHeld) };
        var act = async () => await CreateService().AssignUserBulkAsync(models);

        await act.Should().ThrowAsync<ForbiddenAccessException>();
        (await UserPermissionsAsync(target.Id)).Should().BeEmpty(
            "the batch is validated before any claim is written, so a bad claim writes nothing at all");
    }

    // ---- self-target and held-role guards -------------------------------------------------------

    [Test]
    public async Task AnActorCannotChangePermissionsOnTheirOwnAccount()
    {
        var actor = await CreateUserAsync("actor", Array.Empty<string>(), new[] { Granted });
        ActAs(actor);

        var act = async () => await CreateService().AssignUserAsync(UserModel(actor.Id, Granted, assigned: false));

        (await act.Should().ThrowAsync<ForbiddenAccessException>())
            .Which.Message.Should().Contain("your own account");
        (await UserPermissionsAsync(actor.Id)).Should().Equal(Granted);
    }

    [Test]
    public async Task AnActorCannotChangePermissionsOnARoleTheyHold()
    {
        var actor = await CreateUserAsync("actor", new[] { "Editors" }, new[] { Granted });
        ActAs(actor);

        var act = async () => await CreateService().AssignRoleAsync(RoleModel(await RoleIdAsync("Editors"), Granted));

        (await act.Should().ThrowAsync<ForbiddenAccessException>())
            .Which.Message.Should().Contain("Editors");
        (await RolePermissionsAsync("Editors")).Should().BeEmpty();
    }

    [Test]
    public async Task AnActorCanAdministerARoleTheyDoNotHold()
    {
        // The normal administrative case must keep working.
        var actor = await CreateUserAsync("actor", new[] { ConstantRoles.Admin }, new[] { Granted });
        ActAs(actor);

        await CreateService().AssignRoleAsync(RoleModel(await RoleIdAsync("Editors"), Granted));

        (await RolePermissionsAsync("Editors")).Should().Equal(Granted);
    }

    // ---- administrator-role protection ----------------------------------------------------------

    [Test]
    public async Task PermissionsOnTheAdministratorRoleCannotBeModified()
    {
        var actor = await CreateUserAsync("actor", Array.Empty<string>(), new[] { Granted });
        ActAs(actor);

        var act = async () => await CreateService().AssignRoleAsync(RoleModel(await RoleIdAsync(ConstantRoles.Admin), Granted));

        (await act.Should().ThrowAsync<ForbiddenAccessException>())
            .Which.Message.Should().Contain(ConstantRoles.Admin);
        (await RolePermissionsAsync(ConstantRoles.Admin)).Should().BeEmpty();
    }

    [Test]
    public async Task PermissionsOnTheAdministratorRoleCannotBeModifiedInBulkEither()
    {
        var actor = await CreateUserAsync("actor", Array.Empty<string>(), new[] { Granted });
        ActAs(actor);
        var adminRoleId = await RoleIdAsync(ConstantRoles.Admin);

        var act = async () => await CreateService().AssignRoleBulkAsync(new[] { RoleModel(adminRoleId, Granted) });

        await act.Should().ThrowAsync<ForbiddenAccessException>();
        (await RolePermissionsAsync(ConstantRoles.Admin)).Should().BeEmpty();
    }

    [Test]
    public async Task OnlyPermissionClaimsCanBeAssigned()
    {
        var actor = await CreateUserAsync("actor", Array.Empty<string>(), new[] { Granted });
        var target = await CreateUserAsync("target", Array.Empty<string>(), Array.Empty<string>());
        ActAs(actor);

        var model = new PermissionModel
        {
            ClaimType = ClaimTypes.Role, ClaimValue = ConstantRoles.Admin, Assigned = true, UserId = target.Id
        };
        var act = async () => await CreateService().AssignUserAsync(model);

        await act.Should().ThrowAsync<ForbiddenAccessException>();
    }
}
#nullable restore
