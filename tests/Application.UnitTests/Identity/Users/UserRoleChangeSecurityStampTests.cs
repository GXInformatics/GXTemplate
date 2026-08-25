#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Domain.Entities;
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
using ZiggyCreatures.Caching.Fusion;

namespace CleanArchitecture.Blazor.Application.UnitTests.Identity.Users;

/// <summary>
/// Covers the edit path of UserFormDialog: the security-stamp bump that invalidates a user's existing
/// session when their role membership changes, the cached-context invalidation that goes with it, and
/// the ordering that keeps a failed profile update from wiping the user's roles.
///
/// The role-change logic lives inside a .razor component with no headless entry point and the project
/// carries no bUnit reference, so these tests replay the component's exact sequence against a real
/// UserManager on SQLite. <see cref="ApplyEditAsync"/> mirrors SubmitAsync step for step; the single
/// call site in the component is verified by inspection.
/// </summary>
[TestFixture]
public class UserRoleChangeSecurityStampTests
{
    private SqliteConnection _connection = null!;
    private ServiceProvider _provider = null!;

    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";

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

        db.Tenants.Add(new Tenant { Id = TenantA, Name = "Tenant A" });
        db.Tenants.Add(new Tenant { Id = TenantB, Name = "Tenant B" });
        await db.SaveChangesAsync();
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

    /// <summary>Reads role membership straight from the database through a scope of its own.</summary>
    private async Task<string[]> StoredRolesAsync(string userId)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await (from ur in db.UserRoles
                      join r in db.Roles on ur.RoleId equals r.Id
                      where ur.UserId == userId
                      select r.Name!).ToArrayAsync();
    }

    /// <summary>Reads tenant membership straight from the database through a scope of its own.</summary>
    private async Task<string[]> StoredTenantIdsAsync(string userId)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.TenantUsers.Where(x => x.UserId == userId).Select(x => x.TenantId!).ToArrayAsync();
    }

    private async Task AssignTenantsAsync(string userId, params string[] tenantIds)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        foreach (var tenantId in tenantIds)
        {
            db.TenantUsers.Add(new TenantUser { TenantId = tenantId, UserId = userId });
        }
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Replays the tenant-membership rewrite the dialog performs: drop every current row, add the
    /// selected ones. The save sits inside the non-empty check exactly as the component has it.
    /// </summary>
    private async Task RewriteTenantsAsync(string userId, string[] assignedTenantIds)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var tenantUsers = await db.TenantUsers.Where(x => x.UserId == userId).ToListAsync();
        if (tenantUsers.Any())
        {
            db.TenantUsers.RemoveRange(tenantUsers);
        }
        if (assignedTenantIds.Length > 0)
        {
            foreach (var tenantId in assignedTenantIds)
            {
                db.TenantUsers.Add(new TenantUser { TenantId = tenantId, UserId = userId });
            }
            await db.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Replays UserFormDialog.SubmitAsync's edit path: apply the profile changes, and only once that
    /// update has succeeded rewrite role membership, bump the stamp if the effective set changed, and
    /// drop the user's cached UserContext.
    /// </summary>
    private async Task<IdentityResult> ApplyEditAsync(
        UserManager<ApplicationUser> userManager,
        IUserContextLoader userContextLoader,
        ApplicationUser existingUser,
        string[] assignedRoles,
        Action<ApplicationUser>? applyProfileChanges = null,
        string[]? assignedTenantIds = null)
    {
        var existingRoles = await userManager.GetRolesAsync(existingUser);

        applyProfileChanges?.Invoke(existingUser);
        existingUser.LastModifiedAt = DateTime.UtcNow;
        var updateResult = await userManager.UpdateAsync(existingUser);
        if (!updateResult.Succeeded)
        {
            // The component surfaces the errors and returns without closing the dialog.
            return updateResult;
        }

        if (assignedTenantIds is not null)
        {
            await RewriteTenantsAsync(existingUser.Id, assignedTenantIds);
        }

        if (existingRoles.Any())
        {
            await userManager.RemoveFromRolesAsync(existingUser, existingRoles);
        }
        if (assignedRoles.Length > 0)
        {
            await userManager.AddToRolesAsync(existingUser, assignedRoles);
        }

        if (!existingRoles.OrderBy(r => r, StringComparer.Ordinal)
                .SequenceEqual(assignedRoles.OrderBy(r => r, StringComparer.Ordinal), StringComparer.Ordinal))
        {
            await userManager.UpdateSecurityStampAsync(existingUser);
            userContextLoader.ClearUserContextCache(existingUser.Id);
        }
        return updateResult;
    }

    /// <summary>
    /// The sequence this method used to have: roles were stripped before the fallible profile update,
    /// so a failed update left the user with no roles and nothing to restore them. Kept only so the
    /// regression it caused is demonstrated rather than asserted.
    /// </summary>
    private async Task<IdentityResult> ApplyEditWithPreFixOrderAsync(
        UserManager<ApplicationUser> userManager, ApplicationUser existingUser, string[] assignedRoles,
        Action<ApplicationUser>? applyProfileChanges = null,
        string[]? assignedTenantIds = null)
    {
        var existingRoles = await userManager.GetRolesAsync(existingUser);
        if (existingRoles.Any())
        {
            await userManager.RemoveFromRolesAsync(existingUser, existingRoles);
        }
        if (assignedTenantIds is not null)
        {
            await RewriteTenantsAsync(existingUser.Id, assignedTenantIds);
        }

        applyProfileChanges?.Invoke(existingUser);
        existingUser.LastModifiedAt = DateTime.UtcNow;
        var updateResult = await userManager.UpdateAsync(existingUser);
        if (updateResult.Succeeded && assignedRoles.Length > 0)
        {
            await userManager.AddToRolesAsync(existingUser, assignedRoles);
        }
        return updateResult;
    }

    /// <summary>
    /// Seeds a second account and returns its user name. Assigning that name to the user under edit
    /// makes UpdateAsync fail on Identity's uniqueness validator - the realistic way this dialog's
    /// update fails, since the form lets an administrator retype the user name and the email.
    /// Validation runs before anything is written, which is exactly why the role rewrite must not
    /// have happened yet.
    /// </summary>
    private async Task<string> SeedAConflictingUserNameAsync()
    {
        using var scope = _provider.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var other = new ApplicationUser { UserName = "already-taken", Email = "taken@example.com" };
        (await userManager.CreateAsync(other, "Password123!")).Succeeded.Should().BeTrue();
        return other.UserName!;
    }

    // ---- security stamp -------------------------------------------------------------------------

    [Test]
    public async Task RemovingARole_ChangesTheSecurityStamp()
    {
        var (userManager, user) = await CreateUserAsync("Admin");
        var before = await userManager.GetSecurityStampAsync(user);

        await ApplyEditAsync(userManager, new RecordingUserContextLoader(), user, new[] { "Basic" });

        var after = await userManager.GetSecurityStampAsync(user);
        after.Should().NotBe(before, "a demoted user's existing session must fail its next revalidation");
        (await userManager.GetRolesAsync(user)).Should().BeEquivalentTo(new[] { "Basic" });
    }

    [Test]
    public async Task RevokingAllRoles_ChangesTheSecurityStamp()
    {
        var (userManager, user) = await CreateUserAsync("Admin");
        var before = await userManager.GetSecurityStampAsync(user);

        await ApplyEditAsync(userManager, new RecordingUserContextLoader(), user, Array.Empty<string>());

        (await userManager.GetSecurityStampAsync(user)).Should().NotBe(before);
        (await userManager.GetRolesAsync(user)).Should().BeEmpty();
    }

    [Test]
    public async Task GrantingARole_ChangesTheSecurityStamp()
    {
        var (userManager, user) = await CreateUserAsync("Basic");
        var before = await userManager.GetSecurityStampAsync(user);

        await ApplyEditAsync(userManager, new RecordingUserContextLoader(), user, new[] { "Admin", "Basic" });

        (await userManager.GetSecurityStampAsync(user)).Should().NotBe(before);
    }

    [Test]
    public async Task EditingAUserWithoutChangingRoles_LeavesTheSecurityStampAlone()
    {
        // The edit path always removes and re-adds roles, so the stamp is bumped only when the
        // effective set actually changed. Otherwise every profile edit would sign the user out.
        var (userManager, user) = await CreateUserAsync("Basic", "Admin");
        var before = await userManager.GetSecurityStampAsync(user);

        await ApplyEditAsync(userManager, new RecordingUserContextLoader(), user, new[] { "Admin", "Basic" });

        (await userManager.GetSecurityStampAsync(user)).Should().Be(before);
    }

    // ---- cached user context --------------------------------------------------------------------

    [Test]
    public async Task ARoleChange_ClearsTheUsersCachedContext_SoTheNextLoadSeesTheNewRoles()
    {
        var (userManager, user) = await CreateUserAsync("Basic");
        var scopeFactory = new CountingScopeFactory(_provider.GetRequiredService<IServiceScopeFactory>());
        var loader = new UserContextLoader(
            scopeFactory, new FusionCache(new FusionCacheOptions()), NullLogger<UserContextLoader>.Instance);
        var principal = Principal(user.Id);

        var primed = await loader.LoadAsync(principal);
        primed!.Roles.Should().BeEquivalentTo(new[] { "Basic" });
        scopeFactory.ScopesCreated.Should().Be(1);

        await ApplyEditAsync(userManager, loader, user, new[] { "Admin" });

        var reloaded = await loader.LoadAsync(principal);
        scopeFactory.ScopesCreated.Should().Be(2, "the cached entry was cleared, so the factory ran again");
        reloaded!.Roles.Should().BeEquivalentTo(new[] { "Admin" },
            "the ambient context must not keep serving the role set the user no longer has");
    }

    [Test]
    public async Task WithoutTheCacheClear_TheOldRolesStayAmbient()
    {
        // Demonstrates what the clear is for: the stamp bump forces re-authentication, but the loader
        // caches the context - Roles included - for an hour, so the stale set would survive that long.
        var (userManager, user) = await CreateUserAsync("Basic");
        var scopeFactory = new CountingScopeFactory(_provider.GetRequiredService<IServiceScopeFactory>());
        var loader = new UserContextLoader(
            scopeFactory, new FusionCache(new FusionCacheOptions()), NullLogger<UserContextLoader>.Instance);
        var principal = Principal(user.Id);

        await loader.LoadAsync(principal);
        await ApplyEditAsync(userManager, new RecordingUserContextLoader(), user, new[] { "Admin" });

        var reloaded = await loader.LoadAsync(principal);
        scopeFactory.ScopesCreated.Should().Be(1);
        reloaded!.Roles.Should().BeEquivalentTo(new[] { "Basic" }, "this is the stale read the fix removes");
    }

    [Test]
    public async Task AnEditThatDoesNotChangeMembership_ClearsNothing()
    {
        var (userManager, user) = await CreateUserAsync("Basic");
        var loader = new RecordingUserContextLoader();

        await ApplyEditAsync(userManager, loader, user, new[] { "Basic" });

        loader.ClearedUserIds.Should().BeEmpty("a rename or a phone-number change must not evict anything");
    }

    [Test]
    public async Task AMembershipChange_ClearsExactlyThatUsersEntry()
    {
        var (userManager, user) = await CreateUserAsync("Basic");
        var loader = new RecordingUserContextLoader();

        await ApplyEditAsync(userManager, loader, user, new[] { "Admin" });

        loader.ClearedUserIds.Should().Equal(user.Id);
    }

    // ---- ordering of the role rewrite -----------------------------------------------------------

    [Test]
    public async Task WhenTheProfileUpdateFails_RoleMembershipIsUntouched()
    {
        var (userManager, user) = await CreateUserAsync("Basic", "Admin");
        var takenName = await SeedAConflictingUserNameAsync();

        var result = await ApplyEditAsync(userManager, new RecordingUserContextLoader(), user, new[] { "Basic" },
            u => u.UserName = takenName);

        result.Succeeded.Should().BeFalse("the user name collides with another account");
        (await StoredRolesAsync(user.Id)).Should().BeEquivalentTo(new[] { "Basic", "Admin" },
            "a failed profile update must not cost the user their roles");
    }

    [Test]
    public async Task WhenTheProfileUpdateFails_TheOldOrderLostTheRoles()
    {
        // The same scenario against the pre-fix sequence, so the regression is shown, not asserted.
        var (userManager, user) = await CreateUserAsync("Basic", "Admin");
        var takenName = await SeedAConflictingUserNameAsync();

        var result = await ApplyEditWithPreFixOrderAsync(userManager, user, new[] { "Basic" },
            u => u.UserName = takenName);

        result.Succeeded.Should().BeFalse();
        (await StoredRolesAsync(user.Id)).Should().BeEmpty(
            "stripping roles before the fallible update left nothing to restore them");
    }

    [Test]
    public async Task WhenTheProfileUpdateFails_TheSecurityStampIsNotBumped()
    {
        var (userManager, user) = await CreateUserAsync("Basic");
        var before = await userManager.GetSecurityStampAsync(user);
        var takenName = await SeedAConflictingUserNameAsync();
        var loader = new RecordingUserContextLoader();

        await ApplyEditAsync(userManager, loader, user, new[] { "Admin" }, u => u.UserName = takenName);

        (await userManager.GetSecurityStampAsync(user)).Should().Be(before,
            "nothing changed, so no session should be invalidated");
        loader.ClearedUserIds.Should().BeEmpty();
    }

    // ---- ordering of the tenant rewrite ---------------------------------------------------------

    [Test]
    public async Task WhenTheProfileUpdateFails_TenantMembershipIsUntouched()
    {
        var (userManager, user) = await CreateUserAsync("Basic");
        await AssignTenantsAsync(user.Id, TenantA);
        var takenName = await SeedAConflictingUserNameAsync();

        var result = await ApplyEditAsync(userManager, new RecordingUserContextLoader(), user, new[] { "Basic" },
            u => u.UserName = takenName, new[] { TenantB });

        result.Succeeded.Should().BeFalse("the user name collides with another account");
        (await StoredTenantIdsAsync(user.Id)).Should().BeEquivalentTo(new[] { TenantA },
            "a failed profile update must not cost the user their tenant memberships");
    }

    [Test]
    public async Task WhenTheProfileUpdateFails_TheOldOrderReplacedTheTenantMemberships()
    {
        // The same scenario against the pre-fix sequence, so the regression is shown, not asserted.
        var (userManager, user) = await CreateUserAsync("Basic");
        await AssignTenantsAsync(user.Id, TenantA);
        var takenName = await SeedAConflictingUserNameAsync();

        var result = await ApplyEditWithPreFixOrderAsync(userManager, user, new[] { "Basic" },
            u => u.UserName = takenName, new[] { TenantB });

        result.Succeeded.Should().BeFalse();
        (await StoredTenantIdsAsync(user.Id)).Should().BeEquivalentTo(new[] { TenantB },
            "the rewrite had already committed by the time the update was rejected");
    }

    [Test]
    public async Task WhenTheProfileUpdateSucceeds_TenantMembershipIsRewritten()
    {
        var (userManager, user) = await CreateUserAsync("Basic");
        await AssignTenantsAsync(user.Id, TenantA);

        var result = await ApplyEditAsync(userManager, new RecordingUserContextLoader(), user, new[] { "Basic" },
            assignedTenantIds: new[] { TenantB });

        result.Succeeded.Should().BeTrue();
        (await StoredTenantIdsAsync(user.Id)).Should().BeEquivalentTo(new[] { TenantB },
            "the reorder must not stop the rewrite happening on the success path");
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static ClaimsPrincipal Principal(string userId) =>
        new(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, userId) },
            authenticationType: "TestAuth"));

    /// <summary>Records which users' cached contexts were evicted; loads nothing.</summary>
    private sealed class RecordingUserContextLoader : IUserContextLoader
    {
        public List<string> ClearedUserIds { get; } = new();

        public Task<UserContext?> LoadAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default) =>
            Task.FromResult<UserContext?>(null);

        public void ClearUserContextCache(string userId) => ClearedUserIds.Add(userId);
    }

    /// <summary>Counts how many times UserContextLoader's cache factory actually ran.</summary>
    private sealed class CountingScopeFactory : IServiceScopeFactory
    {
        private readonly IServiceScopeFactory _inner;
        public int ScopesCreated { get; private set; }

        public CountingScopeFactory(IServiceScopeFactory inner) => _inner = inner;

        public IServiceScope CreateScope()
        {
            ScopesCreated++;
            return _inner.CreateScope();
        }
    }
}
#nullable restore
