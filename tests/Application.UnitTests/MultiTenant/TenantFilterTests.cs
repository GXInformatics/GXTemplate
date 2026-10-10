#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Domain.Common.Entities;
using CleanArchitecture.Blazor.Domain.Entities;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.MultiTenant;

/// <summary>
/// Pass 54: the tenant filter is registered by marker, and a user in one tenant cannot read another
/// tenant's rows through the filtered <c>DbSet</c> - not by listing, not by key.
/// </summary>
/// <remarks>
/// Two entities, one per marker. <see cref="Document"/> is the template's own
/// <see cref="IMayHaveTenant"/> entity, and before this pass it was not filtered at all - only
/// <c>VisibleDocumentSpecification</c> scoped it, so any query that did not apply the specification
/// read every tenant's documents. <see cref="TenantProbe"/> is what a project entity looks like.
/// Both tests are RED on the pre-pass <c>ApplicationDbContext</c>.
/// </remarks>
[TestFixture]
public class TenantFilterTests
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";
    private string _connectionString = null!;

    [SetUp]
    public async Task SetUp()
    {
        _connectionString = UnitTestDatabase.ConnectionString;
        await UnitTestDatabase.ResetAsync();
        await TenantProbeDbContext.EnsureTableAsync(_connectionString);

        await using var db = Context(tenantId: null);
        db.Tenants.Add(new Tenant { Id = TenantA, Name = "A" });
        db.Tenants.Add(new Tenant { Id = TenantB, Name = "B" });
        await db.SaveChangesAsync();

        // Written with explicit tenants and no principal - the seeding shape - so the rows are
        // exactly what the test says they are, whatever the stamping does.
        db.Documents.Add(new Document { Title = "doc-a", TenantId = TenantA, IsPublic = true });
        db.Documents.Add(new Document { Title = "doc-b", TenantId = TenantB, IsPublic = true });
        db.Documents.Add(new Document { Title = "doc-shared", TenantId = null, IsPublic = true });
        db.Probes.Add(new TenantProbe { Note = "probe-a", TenantId = TenantA });
        db.Probes.Add(new TenantProbe { Note = "probe-b", TenantId = TenantB });
        await db.SaveChangesAsync();
    }

    [OneTimeTearDown]
    public Task DropProbeTable() => TenantProbeDbContext.DropAsync(UnitTestDatabase.ConnectionString);

    /// <summary>A context whose ambient principal is in <paramref name="tenantId"/>, or nobody.</summary>
    private TenantProbeDbContext Context(string? tenantId)
    {
        var accessor = new Mock<IUserContextAccessor>();
        accessor.SetupGet(x => x.Current).Returns(
            tenantId is null ? null : new UserContext("user-in-" + tenantId, "user", TenantId: tenantId));
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(_connectionString).Options;
        return new TenantProbeDbContext(options, accessor.Object);
    }

    // ---- the control: A cannot read B ----------------------------------------------------------

    [Test]
    public async Task AUserInTenantA_CannotReadATenantBDocument_ThroughTheDbSet()
    {
        await using var db = Context(TenantA);

        var titles = await db.Documents.Select(d => d.Title).OrderBy(t => t).ToListAsync();

        titles.Should().Equal(["doc-a", "doc-shared"],
            "a tenant sees its own documents and the tenantless ones, and never another tenant's");
    }

    [Test]
    public async Task AUserInTenantA_CannotReadATenantBRow_OfAMustHaveTenantEntity()
    {
        await using var db = Context(TenantA);

        var notes = await db.Probes.Select(p => p.Note).ToListAsync();

        notes.Should().Equal(["probe-a"]);
    }

    [Test]
    public async Task NorByKey()
    {
        // FindAsync goes to the database through the same filter; knowing the id is not enough.
        int docB, probeB;
        await using (var any = Context(TenantB))
        {
            docB = (await any.Documents.SingleAsync(d => d.Title == "doc-b")).Id;
            probeB = (await any.Probes.SingleAsync()).Id;
        }

        await using var db = Context(TenantA);
        (await db.Documents.FindAsync(docB)).Should().BeNull();
        (await db.Probes.FindAsync(probeB)).Should().BeNull();
    }

    [Test]
    public async Task TenantB_SeesItsOwnRows_TheRuleIsNotJustDenial()
    {
        // The other half of any scoping test: a filter that returned nothing would pass the three
        // above and be useless.
        await using var db = Context(TenantB);

        (await db.Documents.Select(d => d.Title).OrderBy(t => t).ToListAsync()).Should().Equal(["doc-b", "doc-shared"]);
        (await db.Probes.Select(p => p.Note).ToListAsync()).Should().Equal(["probe-b"]);
    }

    // ---- the two predicates --------------------------------------------------------------------

    [Test]
    public async Task WithNoPrincipal_AMustHaveTenantEntity_ShowsNothing()
    {
        // TenantId == current with current null is TenantId IS NULL, which a must-have row never is.
        // Background work therefore sees nothing until it runs inside ISystemContext.
        await using var db = Context(tenantId: null);

        (await db.Probes.CountAsync()).Should().Be(0);
    }

    [Test]
    public async Task WithNoPrincipal_AMayHaveTenantEntity_ShowsOnlyTheSharedPartition()
    {
        await using var db = Context(tenantId: null);

        (await db.Documents.Select(d => d.Title).ToListAsync()).Should().Equal(["doc-shared"]);
    }

    [Test]
    public async Task TheNamedExemption_StillReadsAcrossTenants()
    {
        // The escape hatch is unchanged: lifting QueryFilters.Tenant by name, which is what the
        // permission-gated cross-tenant readers (AuditTrailTenantScope) do.
        await using var db = Context(TenantA);

        (await db.Probes.IgnoreQueryFilters([QueryFilters.Tenant]).CountAsync()).Should().Be(2);
        (await db.Documents.IgnoreQueryFilters([QueryFilters.Tenant]).CountAsync()).Should().Be(3);
    }

    // ---- the registration ----------------------------------------------------------------------

    [Test]
    public void EveryMarkedEntity_CarriesTheNamedTenantFilter()
    {
        using var db = Context(TenantA);

        foreach (var type in new[] { typeof(Document), typeof(PicklistSet), typeof(TenantProbe), typeof(AuditTrail) })
        {
            db.Model.FindEntityType(type)!.FindDeclaredQueryFilter(QueryFilters.Tenant)
                .Should().NotBeNull($"{type.Name} must be tenant-filtered");
        }
    }

    [Test]
    public void UnmarkedEntities_AreNotTenantFiltered()
    {
        // The walk keys off the markers and nothing else. Tenant and TenantUser carry tenant ids but
        // are the tenancy itself, and ApplicationUser's TenantId is the user's CURRENT tenant - none
        // of them may vanish from a query because of who is asking.
        using var db = Context(TenantA);

        foreach (var type in new[] { typeof(Tenant), typeof(TenantUser), typeof(CleanArchitecture.Blazor.Domain.Identity.ApplicationUser) })
        {
            db.Model.FindEntityType(type)!.FindDeclaredQueryFilter(QueryFilters.Tenant).Should().BeNull(type.Name);
        }
    }
}
#nullable restore
