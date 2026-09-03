#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Features.Tenants;
using CleanArchitecture.Blazor.Domain.Entities;
using CleanArchitecture.Blazor.Domain.Identity;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using CleanArchitecture.Blazor.Infrastructure.Services.Identity;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.Identity.Users;

/// <summary>
/// A user's two records of their tenancy - <c>ApplicationUser.TenantId</c> and their
/// <c>TenantUsers</c> rows - agree after every administrative save.
/// </summary>
/// <remarks>
/// The edit path assigned the primary tenant only when it was already empty, while rewriting the
/// membership rows unconditionally. Moving a user from tenant A to tenant B therefore produced
/// membership B and primary tenant A, permanently: that user went on creating documents in A - the
/// interceptor stamps from <c>TenantId</c> - went on matching A's grid filter, and reported an
/// <c>AllowedTenantIds</c> of [B]. Nothing compared the two, so nothing noticed.
/// <para>
/// <b>This is wrong in a single-tenant-per-user installation too</b>, independently of any isolation
/// work, which is why it is repaired here rather than waiting for scoping.
/// </para>
/// <para>
/// The rule itself is <see cref="PrimaryTenantRule"/> and is tested directly in
/// <c>PrimaryTenantRuleTests</c>. What is replayed here is the component's SEQUENCE - profile update,
/// then membership rewrite - against a real <c>UserManager</c> on SQLite, following
/// <c>UserRoleChangeSecurityStampTests</c>, because the logic lives in a <c>.razor</c> file with no
/// headless entry point. The replay calls the same <c>PrimaryTenantRule</c> the component calls, so
/// only the ordering is mirrored and not the rule.
/// </para>
/// </remarks>
[TestFixture]
public class UserTenantConsistencyTests
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";
    private const string TenantC = "tenant-c";

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
        await db.SaveChangesAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }

    // ---- the component's sequence ---------------------------------------------------------------

    /// <summary>
    /// Replays UserFormDialog's CREATE path: derive the primary from the selected set, create the
    /// user, then write one membership row per selected tenant.
    /// </summary>
    private async Task<ApplicationUser> CreateThroughDialogAsync(params string[] selectedTenantIds)
    {
        using var scope = _provider.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var user = new ApplicationUser
        {
            UserName = $"u{Guid.NewGuid():N}",
            Email = "u@example.com",
            TenantId = PrimaryTenantRule.Resolve(null, selectedTenantIds)
        };
        (await userManager.CreateAsync(user)).Succeeded.Should().BeTrue();

        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        foreach (var tenantId in selectedTenantIds)
        {
            db.TenantUsers.Add(new TenantUser { UserId = user.Id, TenantId = tenantId });
        }
        await db.SaveChangesAsync();
        return user;
    }

    /// <summary>
    /// Replays UserFormDialog's EDIT path: re-derive the primary on every save, update the profile,
    /// then rewrite the membership rows from the same selected set.
    /// </summary>
    private async Task EditThroughDialogAsync(string userId, params string[] selectedTenantIds)
    {
        using var scope = _provider.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var existingUser = await userManager.FindByIdAsync(userId);
        existingUser.Should().NotBeNull();

        existingUser!.TenantId = PrimaryTenantRule.Resolve(existingUser.TenantId, selectedTenantIds);
        existingUser.LastModifiedAt = DateTime.UtcNow;
        (await userManager.UpdateAsync(existingUser)).Succeeded.Should().BeTrue();

        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var rows = await db.TenantUsers.Where(x => x.UserId == userId).ToListAsync();
        if (rows.Any()) db.TenantUsers.RemoveRange(rows);
        foreach (var tenantId in selectedTenantIds)
        {
            db.TenantUsers.Add(new TenantUser { UserId = userId, TenantId = tenantId });
        }
        await db.SaveChangesAsync();
    }

    /// <summary>The pre-Pass-25 edit: primary assigned only when it was empty.</summary>
    private async Task EditWithPreFixPrimaryAsync(string userId, params string[] selectedTenantIds)
    {
        using var scope = _provider.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var existingUser = await userManager.FindByIdAsync(userId);

        if (string.IsNullOrEmpty(existingUser!.TenantId) && selectedTenantIds.Any())
        {
            existingUser.TenantId = selectedTenantIds.First();
        }
        (await userManager.UpdateAsync(existingUser)).Succeeded.Should().BeTrue();

        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var rows = await db.TenantUsers.Where(x => x.UserId == userId).ToListAsync();
        if (rows.Any()) db.TenantUsers.RemoveRange(rows);
        foreach (var tenantId in selectedTenantIds)
        {
            db.TenantUsers.Add(new TenantUser { UserId = userId, TenantId = tenantId });
        }
        await db.SaveChangesAsync();
    }

    private async Task<(string? Primary, string[] Memberships)> StoredAsync(string userId)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var primary = await db.Users.Where(x => x.Id == userId).Select(x => x.TenantId).SingleAsync();
        var memberships = await db.TenantUsers.Where(x => x.UserId == userId)
            .Select(x => x.TenantId!).OrderBy(x => x).ToArrayAsync();
        return (primary, memberships);
    }

    /// <summary>The invariant: the primary tenant is one of the membership rows, or both are empty.</summary>
    private async Task AssertConsistentAsync(string userId)
    {
        var (primary, memberships) = await StoredAsync(userId);

        if (string.IsNullOrEmpty(primary))
        {
            memberships.Should().BeEmpty(
                "a user with no primary tenant must not hold membership rows - the two would disagree");
        }
        else
        {
            memberships.Should().Contain(primary,
                "the primary tenant must be one the user actually belongs to");
        }
    }

    // ---- the defect ------------------------------------------------------------------------------

    [Test]
    public async Task MovingAUserBetweenTenants_MovesBothRecords()
    {
        // RED before Pass 25: primary stayed "tenant-a" while membership became ["tenant-b"].
        var user = await CreateThroughDialogAsync(TenantA);

        await EditThroughDialogAsync(user.Id, TenantB);

        var (primary, memberships) = await StoredAsync(user.Id);
        primary.Should().Be(TenantB);
        memberships.Should().Equal(TenantB);
        await AssertConsistentAsync(user.Id);
    }

    [Test]
    public async Task ThePreFixSequenceIsWhatDivergenceLookedLike()
    {
        // Kept so the defect is demonstrated rather than only described - the same shape
        // UserRoleChangeSecurityStampTests uses for its own pre-fix replays.
        var user = await CreateThroughDialogAsync(TenantA);

        await EditWithPreFixPrimaryAsync(user.Id, TenantB);

        var (primary, memberships) = await StoredAsync(user.Id);
        primary.Should().Be(TenantA, "the old code assigned the primary only when it was empty");
        memberships.Should().Equal(new[] { TenantB }, "the membership rewrite was unconditional");
        memberships.Should().NotContain(primary!, "which is precisely the divergence");
    }

    // ---- the invariant, across the paths that write tenancy -------------------------------------

    [Test]
    public async Task CreationLeavesTheTwoAgreeing()
    {
        var user = await CreateThroughDialogAsync(TenantA, TenantB);

        var (primary, memberships) = await StoredAsync(user.Id);
        primary.Should().Be(TenantA);
        memberships.Should().Equal(TenantA, TenantB);
        await AssertConsistentAsync(user.Id);
    }

    [Test]
    public async Task AddingATenantKeepsTheExistingPrimary()
    {
        // An edit that widens membership must not silently move the user's primary tenant, because
        // that would change which tenant their new rows are stamped with.
        var user = await CreateThroughDialogAsync(TenantB);

        await EditThroughDialogAsync(user.Id, TenantA, TenantB);

        var (primary, _) = await StoredAsync(user.Id);
        primary.Should().Be(TenantB, "it is still selected, so it stays");
        await AssertConsistentAsync(user.Id);
    }

    [Test]
    public async Task RemovingThePrimaryTenantMovesItToOneThatRemains()
    {
        var user = await CreateThroughDialogAsync(TenantA, TenantB);
        (await StoredAsync(user.Id)).Primary.Should().Be(TenantA);

        await EditThroughDialogAsync(user.Id, TenantB, TenantC);

        var (primary, memberships) = await StoredAsync(user.Id);
        primary.Should().Be(TenantB);
        memberships.Should().Equal(TenantB, TenantC);
        await AssertConsistentAsync(user.Id);
    }

    [Test]
    public async Task ClearingEveryTenantLeavesNoPrimaryEither()
    {
        // The validator refuses this at the form, but the membership rewrite deliberately persists
        // an empty set - so if it is ever bypassed the two must still agree rather than stranding a
        // primary tenant with nothing behind it.
        var user = await CreateThroughDialogAsync(TenantA);

        await EditThroughDialogAsync(user.Id);

        var (primary, memberships) = await StoredAsync(user.Id);
        primary.Should().BeNull();
        memberships.Should().BeEmpty();
        await AssertConsistentAsync(user.Id);
    }

    [Test]
    public async Task ASequenceOfEditsNeverLeavesTheTwoDisagreeing()
    {
        // The property rather than another example: whatever route a user is dragged through, the
        // invariant holds after every step.
        var user = await CreateThroughDialogAsync(TenantA);

        var journey = new[]
        {
            new[] { TenantA, TenantB },
            new[] { TenantB },
            new[] { TenantC },
            new[] { TenantA, TenantB, TenantC },
            Array.Empty<string>(),
            new[] { TenantB }
        };

        foreach (var selection in journey)
        {
            await EditThroughDialogAsync(user.Id, selection);
            await AssertConsistentAsync(user.Id);
        }
    }
}
#nullable restore
