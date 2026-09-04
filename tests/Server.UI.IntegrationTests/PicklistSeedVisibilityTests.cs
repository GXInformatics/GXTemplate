#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Domain.Entities;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Server.UI.IntegrationTests;

/// <summary>
/// A fresh seed produces picklists every tenant can see - checked against a real boot.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only a live run can show this, and getting it backwards would be silent.</b> Pass 29 A4
/// established that seeded AUDIT rows are invisible to every tenant principal, and that is correct
/// for audit trails: an installation-level event belongs to nobody. Picklists must come out the
/// opposite way - the shipped Status, Unit and Brand values have to reach every tenant - and the two
/// entities share a filter name, so the failure mode is a seed that produces reference data nobody
/// can see, with no error anywhere.
/// </para>
/// <para>
/// The unit-level version of this claim is in <c>PicklistSetTenantFilterTests</c>, over rows the
/// test inserted itself. What that cannot show is whether the REAL seeding path leaves a null tenant
/// on the rows it writes - which depends on <c>HostExtensions</c> running the initializer with no
/// ambient principal, and on <c>AuditableEntityInterceptor</c> stamping from that same absent
/// principal. So this one boots the actual application and reads what it actually wrote.
/// </para>
/// </remarks>
[TestFixture]
public class PicklistSeedVisibilityTests
{
    private const string ForeignTenant = "a-tenant-that-does-not-exist";

    private GxWebApplicationFactory _factory = null!;

    [SetUp]
    public async Task SetUp()
    {
        _factory = new GxWebApplicationFactory();

        // A request, not merely construction: WebApplicationFactory builds the host lazily, so
        // asserting before anything has been served would test a process that has not seeded yet.
        // The status code is irrelevant - reaching the pipeline is the point.
        using var client = _factory.CreateNonRedirectingClient();
        await client.GetAsync("/");
    }

    [TearDown]
    public void TearDown() => _factory.Dispose();

    /// <summary>Reads the picklists as a principal in the given tenant, through the real filter.</summary>
    private async Task<string[]> VisibleToAsync(string? tenantId)
    {
        using var scope = _factory.Services.CreateScope();
        var accessor = scope.ServiceProvider.GetRequiredService<IUserContextAccessor>();
        var contextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();

        // The accessor is a singleton over AsyncLocal, so pushing here is what a hub method or a
        // request would do - and the context resolves CurrentTenantId from it at query time.
        using var _ = tenantId is null
            ? (IDisposable)new NoPush()
            : accessor.Push(new UserContext("probe-user", "probe", TenantId: tenantId));

        await using var db = await contextFactory.CreateDbContextAsync();
        return await db.PicklistSets.Select(p => p.Value!).OrderBy(v => v).ToArrayAsync();
    }

    private sealed class NoPush : IDisposable
    {
        public void Dispose() { }
    }

    [Test]
    public async Task TheSeededPicklistsAreVisibleToATenantScopedPrincipal()
    {
        // The assertion this fixture exists for. A tenant that does not even have a row in Tenants
        // is used deliberately: nothing about the seed can have arranged for it, so anything this
        // principal sees is shared reference data and nothing else.
        var visible = await VisibleToAsync(ForeignTenant);

        visible.Should().NotBeEmpty(
            "a fresh seed must produce picklists every tenant can see - a filter that hid them " +
            "would leave every dropdown in the application empty, with no error anywhere");

        visible.Should().Contain("initialization",
            "the shipped Status values are the ones the seeder writes");
    }

    [Test]
    public async Task EveryTenantSeesTheSameSeededSet_AndItIsTheSameOneAnUnscopedContextSees()
    {
        // Three readings of the same rows: two unrelated tenants and the infrastructure path. All
        // three must agree, because every seeded row is shared. If they diverge, the seeder stamped
        // a tenant onto rows that were meant for everybody.
        var infrastructure = await VisibleToAsync(tenantId: null);
        var first = await VisibleToAsync("tenant-one");
        var second = await VisibleToAsync("tenant-two");

        infrastructure.Should().NotBeEmpty();
        first.Should().Equal(infrastructure);
        second.Should().Equal(infrastructure);
    }

    [Test]
    public async Task TheSeededRowsCarryNoTenant()
    {
        // The property the two tests above depend on, stated directly rather than inferred - so a
        // failure says "the seeder stamped a tenant" instead of "a dropdown was empty".
        using var scope = _factory.Services.CreateScope();
        var contextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();

        await using var db = await contextFactory.CreateDbContextAsync();

        // No ambient principal here, so the filter itself admits only null-tenant rows; asserting
        // the count is non-zero is what proves the seeder took that path too.
        var shared = await db.PicklistSets.CountAsync();
        shared.Should().BeGreaterThan(0);

        // And the exemption view: every row in the table, tenant or not.
        var all = await db.PicklistSets.IgnoreQueryFilters().CountAsync();
        all.Should().Be(shared,
            "a fresh installation has no per-tenant picklists at all - every seeded row is shared");
    }
}
#nullable restore
