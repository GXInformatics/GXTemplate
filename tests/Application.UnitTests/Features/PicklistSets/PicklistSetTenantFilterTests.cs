#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Domain.Entities;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.Features.PicklistSets;

/// <summary>
/// Picklists are shared reference data with per-tenant additions, enforced by a global query filter.
/// </summary>
/// <remarks>
/// <para>
/// <b>The predicate is <c>TenantId == null || TenantId == current</c>, and the null half is the
/// whole point.</b> A null tenant on a picklist row means "everyone's" - the installation ships it -
/// so it is admitted alongside the caller's own rows. That is the OPPOSITE meaning the same value
/// carries on <see cref="AuditTrail"/>, where a null tenant is an installation-level event belonging
/// to nobody and the filter is strict equality.
/// <see cref="TheTwoFilteredEntitiesTreatANullTenantOppositely"/> pins that contrast, because the two
/// share a filter NAME and the natural mistake is to give them a shared predicate too.
/// </para>
/// <para>
/// <b>Every negative here has a positive beside it.</b> The shared/private split makes an over-broad
/// filter look plausible - a filter that admitted everything would still show a tenant its own rows -
/// and an under-broad one look safe. So the assertions are written as equalities over the full
/// visible set rather than as "does not contain", and
/// <c>NarrowedNotEmptied_TenantASeesEverySharedRowAndEveryTenantARow</c> is the control that fails
/// for both mistakes.
/// </para>
/// </remarks>
[TestFixture]
public class PicklistSetTenantFilterTests
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";
    private const string UserId = "user-1";

    /// <summary>Ids, so an assertion can name the exact visible set.</summary>
    private const int SharedStatus = 1;
    private const int SharedBrand = 2;
    private const int TenantAStatus = 3;
    private const int TenantABrand = 4;
    private const int TenantBStatus = 5;

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

        // Seeded through a context with NO ambient principal, which is what the real seeding path
        // does - and the only way to write a shared row.
        await using var db = Context(tenantId: null);
        await db.Database.EnsureCreatedAsync();

        db.PicklistSets.AddRange(
            Row(SharedStatus, Picklist.Status, "shipped-status", null),
            Row(SharedBrand, Picklist.Brand, "shipped-brand", null),
            Row(TenantAStatus, Picklist.Status, "a-status", TenantA),
            Row(TenantABrand, Picklist.Brand, "a-brand", TenantA),
            Row(TenantBStatus, Picklist.Status, "b-status", TenantB));

        await db.SaveChangesAsync();
    }

    [TearDown]
    public async Task TearDown() => await _connection.DisposeAsync();

    private static PicklistSet Row(int id, Picklist name, string value, string? tenantId) => new()
    {
        Id = id,
        Name = name,
        Value = value,
        Text = value,
        TenantId = tenantId
    };

    private ApplicationDbContext Context(string? tenantId) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options,
            new Ambient(tenantId));

    private async Task<int[]> VisibleAsync(string? tenantId)
    {
        await using var db = Context(tenantId);
        return await db.PicklistSets.Select(p => p.Id).OrderBy(i => i).ToArrayAsync();
    }

    // ---- §E.1: both halves, in one test -------------------------------------------------------

    [Test]
    public async Task SharedRowsAreVisibleToEveryTenant_AndPrivateRowsOnlyToTheirOwn()
    {
        // RED before Pass 31: every tenant saw all five rows - nothing filtered PicklistSets.
        //
        // Written as two full equalities rather than a pair of NotContain assertions. "Tenant B
        // cannot see tenant A's row" is satisfied by a filter that returns nothing at all, and the
        // sharing half is satisfied by a filter that returns everything; only stating the exact
        // visible set rules out both.
        (await VisibleAsync(TenantA)).Should().Equal(
            new[] { SharedStatus, SharedBrand, TenantAStatus, TenantABrand },
            "tenant A sees what the installation ships PLUS its own additions, and nothing of B's");

        (await VisibleAsync(TenantB)).Should().Equal(
            new[] { SharedStatus, SharedBrand, TenantBStatus },
            "tenant B sees the same shared rows and its own, and nothing of A's");
    }

    // ---- §E.2: narrowed, not emptied ----------------------------------------------------------

    [Test]
    public async Task NarrowedNotEmptied_TenantASeesEverySharedRowAndEveryTenantARow()
    {
        // The control that matters most. Two shared rows and two tenant-A rows, so this fails
        // against a filter that dropped the shared half, against one that dropped the private half,
        // and against one that narrowed either to a single row.
        var visible = await VisibleAsync(TenantA);

        visible.Should().Contain(SharedStatus).And.Contain(SharedBrand,
            "a shipped picklist must stay visible to every tenant - that is the point of the null half");
        visible.Should().Contain(TenantAStatus).And.Contain(TenantABrand,
            "a tenant's own additions must still reach it");
        visible.Should().NotContain(TenantBStatus);
        visible.Should().HaveCount(4);
    }

    // ---- the mechanism, not the predicate ------------------------------------------------------

    [Test]
    public async Task AQueryThatNeverHeardOfTenancyIsStillScoped()
    {
        // What a global filter buys over the per-surface predicates of Passes 27 and 28: nothing
        // below mentions a tenant, and a future query written the same way starts scoped.
        await using var db = Context(TenantB);

        (await db.PicklistSets.CountAsync()).Should().Be(3);
        (await db.PicklistSets.Where(p => p.Name == Picklist.Status).CountAsync()).Should().Be(2,
            "one shared status row and one of tenant B's");
        (await db.PicklistSets.AnyAsync(p => p.Value == "a-status")).Should().BeFalse(
            "tenant A's value is invisible to a query that never asked about tenants");
    }

    [Test]
    public async Task AContextWithNoAmbientPrincipalSeesTheSharedRowsOnly()
    {
        // The seeding shape, at unit level. EF's null semantics reduce
        // "TenantId == null || TenantId == null" to "TenantId IS NULL", so an infrastructure path
        // sees the installation's own rows - which is what makes SeedPicklistsAsync's AnyAsync guard
        // ask "have the SHARED picklists been seeded?" rather than "does any tenant have one?".
        // PicklistSeedVisibilityTests proves the same thing against a real boot.
        (await VisibleAsync(tenantId: null)).Should().Equal(new[] { SharedStatus, SharedBrand });
    }

    [Test]
    public void TheTwoFilteredEntitiesTreatANullTenantOppositely()
    {
        // Both are registered under QueryFilters.Tenant, so the natural implementation is one shared
        // predicate - and it would be wrong. This asserts the model itself carries two.
        using var db = Context(TenantA);
        var model = db.Model;

        var picklist = TenantFilterOf(model, typeof(PicklistSet));
        var audit = TenantFilterOf(model, typeof(AuditTrail));

        picklist.Should().Contain("null",
            "a picklist row with no tenant is SHARED, so the predicate has to admit it");
        audit.Should().NotBe(picklist,
            "an audit row with no tenant is an installation-level event belonging to nobody - the " +
            "same value, the opposite meaning, and one shared predicate could only serve one of them");
    }

    /// <summary>
    /// The compiled text of the <see cref="QueryFilters.Tenant"/> filter registered on an entity.
    /// </summary>
    /// <remarks>
    /// Fails loudly rather than returning null: a missing filter is the defect this fixture exists
    /// to catch, and a null-propagating read of it would turn that into a confusing comparison
    /// failure somewhere else.
    /// </remarks>
    private static string TenantFilterOf(IModel model, Type clrType)
    {
        var entity = model.FindEntityType(clrType)
                     ?? throw new InvalidOperationException($"{clrType.Name} is not on the model.");

        var filter = entity.GetDeclaredQueryFilters().SingleOrDefault(f => f.Key == QueryFilters.Tenant)
                     ?? throw new InvalidOperationException(
                         $"{clrType.Name} carries no '{QueryFilters.Tenant}' query filter.");

        return filter.Expression?.ToString()
               ?? throw new InvalidOperationException(
                   $"{clrType.Name}'s '{QueryFilters.Tenant}' filter has no expression.");
    }

    // ---- §E.5: the import duplicate check ------------------------------------------------------

    [Test]
    public async Task TheImportDuplicateCheckIsNowPerTenant()
    {
        // ImportPicklistSetsCommandHandler asks exactly this question, and takes no code to be
        // per-tenant: the filter bounds the AnyAsync like any other read.
        await using var b = Context(TenantB);

        // Tenant A already has "a-status". Tenant B importing the same name/value is NOT a
        // duplicate - a behaviour change this decision implies, and the one worth proving.
        (await b.PicklistSets.AnyAsync(x => x.Name == Picklist.Status && x.Value == "a-status"))
            .Should().BeFalse("two tenants may import the same picklist value without colliding");

        // But a SHARED row still counts as a duplicate for everybody, which is the other half: a
        // shadowing row would render twice in the same dropdown.
        (await b.PicklistSets.AnyAsync(x => x.Name == Picklist.Status && x.Value == "shipped-status"))
            .Should().BeTrue("nobody may shadow a value the installation already ships");
    }

    [Test]
    public void TheImportHandlerAsksTheQuestionTheTestAboveAnswers()
    {
        // The test above runs the predicate against the real filter; this one closes the gap between
        // that predicate and the one the handler actually executes. Without it the pair proves only
        // that a predicate I typed behaves correctly.
        //
        // Read from source rather than reflected over, because the predicate is a lambda inside a
        // method body - there is nothing to reflect on - and because the handler is a place a future
        // edit could reasonably add IgnoreQueryFilters to "fix" a duplicate that is now per-tenant.
        var source = ReadSource("src/Application/Features/PicklistSets/Commands/Import/ImportPicklistSetsCommand.cs");

        source.Should().Contain("x.Name == item.Name && x.Value == item.Value",
            "the duplicate check must still be the name/value pair this fixture exercises");
        source.Should().NotContain("IgnoreQueryFilters",
            "the import's duplicate check is per-tenant BECAUSE the global filter bounds it; an " +
            "exemption here would silently restore the installation-wide collision");
    }

    /// <summary>
    /// Reads a repository file, walking up from the test assembly until it appears.
    /// </summary>
    /// <remarks>
    /// Anchored on the path under <c>src/</c> rather than on a solution file or a namespace, because
    /// both of those are renamed when the template is generated and the folder layout is not - so a
    /// generated project runs this against its own copy. Follows
    /// <c>GetDateRangeKindTests.SourcePath</c>.
    /// </remarks>
    private static string ReadSource(string relative)
    {
        var directory = new DirectoryInfo(
            Path.GetDirectoryName(typeof(PicklistSetTenantFilterTests).Assembly.Location)!);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not find {relative} above {typeof(PicklistSetTenantFilterTests).Assembly.Location}. " +
            "This test reads the handler's source so a reintroduction is caught; it fails rather " +
            "than silently testing nothing.");
    }

    // ---- reaching a row by id -------------------------------------------------------------------

    [Test]
    public async Task ARowCannotBeReachedByIdFromAnotherTenant()
    {
        // The edit and delete commands both address rows by id - AddEditPicklistSetCommand through
        // FindAsync, DeletePicklistSetCommand through Where(Id.Contains). Whether EF applies a
        // global filter to Find is the sort of thing worth asserting rather than believing.
        await using var b = Context(TenantB);

        (await b.PicklistSets.FindAsync(TenantAStatus)).Should().BeNull(
            "FindAsync must not be a way around the filter");
        (await b.PicklistSets.Where(p => p.Id == TenantAStatus).ToListAsync()).Should().BeEmpty();

        // Narrowed, not emptied: tenant B can still reach its own row and the shared ones by id.
        (await b.PicklistSets.FindAsync(TenantBStatus)).Should().NotBeNull();
        (await b.PicklistSets.FindAsync(SharedStatus)).Should().NotBeNull(
            "a shared row is reachable by every tenant - see the pass report on whether it should " +
            "also be EDITABLE by them");
    }
}
