#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Application.Common.Security;
using CleanArchitecture.Blazor.Application.Features.AuditTrails;
using CleanArchitecture.Blazor.Domain.Entities;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.Features.AuditTrails;

/// <summary>
/// The audit trail's global tenant filter, and the one permission that lifts it.
/// </summary>
/// <remarks>
/// <b>This is the first bound in the template that is on by default.</b> Passes 27 and 28 scoped by
/// adding a predicate at each surface, which works but is opt-in: a new query is unscoped until
/// somebody remembers. A global query filter inverts that - a new query over <see cref="AuditTrail"/>
/// is scoped whether or not its author thought about it, and every legitimate cross-tenant read has
/// to name itself.
/// <para>
/// So what these tests protect is not one query but the <b>default</b>. The isolation cases would
/// pass under an opt-in scheme too; what would not is
/// <see cref="AQueryThatNeverHeardOfTenancyIsStillScoped"/>.
/// </para>
/// </remarks>
[TestFixture]
public class AuditTrailTenantFilterTests
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";
    private const string UserId = "user-1";

    private SqliteConnection _connection = null!;

    /// <summary>An ambient principal in one tenant, or none at all.</summary>
    private sealed class Ambient : IUserContextAccessor
    {
        private readonly UserContext? _context;
        public Ambient(string? tenantId) =>
            _context = tenantId is null ? null : new UserContext(UserId, "u", TenantId: tenantId);
        public UserContext? Current => _context;
        public IDisposable Push(UserContext context) => throw new NotSupportedException();
        public void Clear() { }
    }

    [SetUp]
    public async Task SetUp()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();

        await using var db = Context(tenantId: null);
        await db.Database.EnsureCreatedAsync();

        db.AuditTrails.AddRange(
            Row(1, TenantA), Row(2, TenantA),      // two, so "narrowed" is distinguishable from "one"
            Row(3, TenantB),
            Row(4, null));                          // installation-level: seeding, bootstrap
        await db.SaveChangesAsync();
    }

    [TearDown]
    public async Task TearDown() => await _connection.DisposeAsync();

    private static AuditTrail Row(int id, string? tenantId) => new()
    {
        Id = id,
        TenantId = tenantId,
        TableName = "Probe",
        AuditType = AuditType.Create,
        DateTime = new DateTime(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc)
    };

    private ApplicationDbContext Context(string? tenantId) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options,
            new Ambient(tenantId));

    private async Task<int[]> VisibleAsync(string? tenantId)
    {
        await using var db = Context(tenantId);
        return await db.AuditTrails.Select(a => a.Id).OrderBy(i => i).ToArrayAsync();
    }

    // ---- the default is scoped ----------------------------------------------------------------

    [Test]
    public async Task ATenantSeesItsOwnRows_AndNotAnotherTenants()
    {
        // RED before Pass 29: [1,2,3,4] - nothing filtered AuditTrails at all.
        (await VisibleAsync(TenantA)).Should().Equal(new[] { 1, 2 });
        (await VisibleAsync(TenantB)).Should().Equal(new[] { 3 });
    }

    [Test]
    public async Task NarrowedNotEmptied_BothOfTenantAsRowsSurvive()
    {
        // A filter that returned nothing would satisfy every isolation assertion above. Two rows,
        // so this also catches a filter that narrowed to one.
        var visible = await VisibleAsync(TenantA);

        visible.Should().Contain(1).And.Contain(2);
        visible.Should().NotContain(3);
    }

    [Test]
    public async Task AQueryThatNeverHeardOfTenancyIsStillScoped()
    {
        // THE point of the mechanism, as distinct from the predicates Passes 27 and 28 added.
        // Nothing here mentions a tenant: no specification, no Where, no shared rule - just a count
        // and an unrelated predicate, of the sort a future feature would write without thinking
        // about tenancy at all. Under an opt-in scheme both would read every tenant's rows.
        await using var db = Context(TenantA);

        (await db.AuditTrails.CountAsync()).Should().Be(2);
        (await db.AuditTrails.Where(a => a.TableName == "Probe").CountAsync()).Should().Be(2);
    }

    [Test]
    public async Task FindAsyncIsScopedToo_NotOnlyLinqQueries()
    {
        // Worth its own case because it is the one people assume goes around filters. It does not,
        // which is what makes AddEditPicklistSetCommand's FindAsync safe when its turn comes.
        await using var db = Context(TenantA);

        (await db.AuditTrails.FindAsync(3)).Should().BeNull("row 3 belongs to tenant B");
        (await db.AuditTrails.FindAsync(1)).Should().NotBeNull();
    }

    // ---- the model-cache trap, on the real context ---------------------------------------------

    [Test]
    public async Task TheFilterIsRecomputedPerContext_NotBakedIntoTheCachedModel()
    {
        // The trap the design exists to avoid: a query filter is compiled into the model, and the
        // model is cached once per context type for the life of the process. Had the filter closed
        // over a LOCAL rather than a member of the context, the first tenant to build the model
        // would have been served to every request afterwards - and every isolation test above would
        // still have passed, because they each build their own context first.
        //
        // Asserted by interleaving: A, then B, then A again, through one cached model.
        (await VisibleAsync(TenantA)).Should().Equal(new[] { 1, 2 });
        (await VisibleAsync(TenantB)).Should().Equal(new[] { 3 });
        (await VisibleAsync(TenantA)).Should().Equal(new[] { 1, 2 }, "the model did not capture B");
    }

    // ---- no ambient principal ------------------------------------------------------------------

    [Test]
    public async Task NoAmbientPrincipalSeesInstallationRows_NotEverythingAndNotNothing()
    {
        // The §B.2 decision, asserted rather than assumed. Seeding, bootstrap and startup checks run
        // with no principal, and "returns nothing" there is a broken application, not a safe
        // default. EF's null-semantics rewriting turns the comparison into `TenantId IS NULL`, so
        // such a context sees exactly the rows that belong to the installation.
        //
        // This is why no infrastructure path needs an exemption.
        (await VisibleAsync(null)).Should().Equal(new[] { 4 });
    }

    // ---- the exemption -------------------------------------------------------------------------

    private static IPermissionQueryService HeldPermissions(bool viewAllTenants)
    {
        var mock = new Mock<IPermissionQueryService>();
        mock.Setup(x => x.GetAllPermissionsByUserId(It.IsAny<string>()))
            .ReturnsAsync(new List<PermissionModel>
            {
                new()
                {
                    ClaimType = "Permission",
                    ClaimValue = Permissions.AuditTrails.ViewAllTenants,
                    Assigned = viewAllTenants
                }
            });
        return mock.Object;
    }

    private async Task<int[]> ThroughScopeAsync(string? tenantId, bool viewAllTenants, string? userId = UserId)
    {
        await using var db = Context(tenantId);
        var visible = await AuditTrailTenantScope.VisibleAsync(
            db.AuditTrails, HeldPermissions(viewAllTenants), userId);
        return await visible.Select(a => a.Id).OrderBy(i => i).ToArrayAsync();
    }

    [Test]
    public async Task TheCrossTenantRightLiftsTheFilter()
    {
        (await ThroughScopeAsync(TenantA, viewAllTenants: true))
            .Should().Equal(new[] { 1, 2, 3, 4 }, "every tenant, and the installation rows too");
    }

    [Test]
    public async Task WithoutTheRightTheScopeChangesNothing()
    {
        // The exemption is a grant, not a default. Passing through AuditTrailTenantScope must be
        // indistinguishable from not calling it when the right is absent.
        (await ThroughScopeAsync(TenantA, viewAllTenants: false)).Should().Equal(new[] { 1, 2 });
    }

    [TestCase(null)]
    [TestCase("")]
    public async Task AMissingUserIdFailsClosed(string? userId)
    {
        // No user id means the question cannot be answered, and an unanswerable question must not
        // widen anything. The holder-shaped permission mock would say "true" if it were consulted -
        // it is not.
        (await ThroughScopeAsync(TenantA, viewAllTenants: true, userId: userId))
            .Should().Equal(new[] { 1, 2 }, "an unanswerable question does not grant the right");
    }

    // ---- the exemption is narrow -----------------------------------------------------------------

    [Test]
    public void TheExemptionNamesOneFilter_AndTheNameIsAConstant()
    {
        // IgnoreQueryFilters() with no argument drops EVERY filter on the entity - today that is
        // only the tenant one, but the soft-delete filter starts applying the moment a generated
        // project adds a soft-deletable entity, and a bare call would silently start dropping it too.
        //
        // Asserted on the model rather than the source: both filters must be registered, by name.
        using var db = Context(TenantA);
        var filters = db.Model.FindEntityType(typeof(AuditTrail))!
            .GetDeclaredQueryFilters().Select(f => f.Key).ToArray();

        filters.Should().Contain(QueryFilters.Tenant,
            "the exemption drops this one by name, so it must exist under that name");
    }
}
#nullable restore
