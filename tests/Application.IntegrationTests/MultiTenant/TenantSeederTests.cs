using System;
using System.Linq;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Application.Features.Tenants.Commands.Create;
using CleanArchitecture.Blazor.Domain.Entities;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.IntegrationTests.MultiTenant;

using static Testing;

/// <summary>
/// Pass 54: <c>ITenantSeeder</c> runs for a new tenant when it is created, and for every tenant on
/// every start - both with no ambient principal, and both through the same runner. One seeder,
/// <see cref="RecordingTenantSeeder"/>, proves both invocation points.
/// </summary>
public class TenantSeederTests : TestBase
{
    [SetUp]
    public void EnableTheSeeder() => RecordingTenantSeeder.Enabled = true;

    private static async Task<PicklistSet[]> SeededRowsAsync()
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.PicklistSets.IgnoreQueryFilters([QueryFilters.Tenant])
            .Where(p => p.Value == RecordingTenantSeeder.SeededValue)
            .ToArrayAsync();
    }

    private static async Task ProvisionAsync()
    {
        using var scope = CreateScope();
        await scope.ServiceProvider.GetRequiredService<ApplicationDbContextInitializer>().ProvisionAsync();
    }

    [Test]
    public async Task CreatingATenant_SeedsIt_WithNoAmbientPrincipal()
    {
        // The harness user is signed in and sends the command - exactly the administrator-in-a-circuit
        // case. The seeder must still see nobody.
        var result = await SendAsync(new CreateTenantCommand { Name = "Seeded on create" });

        result.Succeeded.Should().BeTrue(result.ErrorMessage);
        var calls = RecordingTenantSeeder.Calls.ToArray();
        calls.Should().ContainSingle().Which.TenantId.Should().Be(result.Data);
        calls[0].Ambient.Should().BeNull("the runner hides the principal that sent the command");

        var rows = await SeededRowsAsync();
        rows.Should().ContainSingle();
        rows[0].TenantId.Should().Be(result.Data, "the seeder's explicit tenant was not overwritten by stamping");
        rows[0].CreatedById.Should().BeNull("and nothing stamped the administrator as its author either");
    }

    [Test]
    public async Task Provisioning_SeedsEveryTenant_AndIsIdempotentPerItem()
    {
        await AddAsync(new Tenant { Id = "t-one", Name = "One" });
        await AddAsync(new Tenant { Id = "t-two", Name = "Two" });

        await ProvisionAsync();

        RecordingTenantSeeder.Calls.Select(c => c.TenantId).Should().BeEquivalentTo(["t-one", "t-two"]);
        RecordingTenantSeeder.Calls.Should().OnlyContain(c => c.Ambient == null);
        (await SeededRowsAsync()).Select(r => r.TenantId).Should().BeEquivalentTo(["t-one", "t-two"]);

        // The second start calls every seeder again - that is how a later release reaches existing
        // tenants - and the seeder, reconciling by natural key, adds nothing it already added.
        await ProvisionAsync();

        RecordingTenantSeeder.Calls.Should().HaveCount(4);
        (await SeededRowsAsync()).Should().HaveCount(2);
    }

    [Test]
    public async Task Provisioning_ReachesATenantCreatedBeforeTheSeederExisted()
    {
        // The reconcile half: a tenant created while the seeder was disabled (standing in for "before
        // this release shipped the seeder") is caught up on the next start.
        RecordingTenantSeeder.Enabled = false;
        var created = await SendAsync(new CreateTenantCommand { Name = "Older tenant" });
        (await SeededRowsAsync()).Should().BeEmpty();

        RecordingTenantSeeder.Enabled = true;
        await ProvisionAsync();

        (await SeededRowsAsync()).Should().ContainSingle().Which.TenantId.Should().Be(created.Data);
    }
}
