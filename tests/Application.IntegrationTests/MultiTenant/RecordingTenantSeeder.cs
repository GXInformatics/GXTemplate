using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Application.Common.Interfaces.MultiTenant;
using CleanArchitecture.Blazor.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CleanArchitecture.Blazor.Application.IntegrationTests.MultiTenant;

/// <summary>
/// The one test seeder (Pass 54). Registered in the harness for the whole suite and INERT unless a
/// test sets <see cref="Enabled"/>, so tests that create tenants for other reasons are unaffected.
/// </summary>
/// <remarks>
/// It is written the way <c>ITenantSeeder</c> tells a project to write one: it reconciles one item -
/// a "SEEDED" unit picklist - by its natural key with the tenant filter lifted and the tenant stated,
/// and it sets <c>TenantId</c> on the row it adds. It also records, per call, which tenant it was
/// given and whether any principal was ambient.
/// </remarks>
public sealed class RecordingTenantSeeder : ITenantSeeder
{
    public const string SeededValue = "SEEDED";

    public static volatile bool Enabled;
    public static ConcurrentQueue<(string TenantId, UserContext? Ambient)> Calls { get; } = new();

    public static void Reset()
    {
        Enabled = false;
        Calls.Clear();
    }

    private readonly IApplicationDbContextFactory _dbContextFactory;
    private readonly IUserContextAccessor _userContextAccessor;

    public RecordingTenantSeeder(IApplicationDbContextFactory dbContextFactory, IUserContextAccessor userContextAccessor)
    {
        _dbContextFactory = dbContextFactory;
        _userContextAccessor = userContextAccessor;
    }

    public async Task SeedAsync(string tenantId, CancellationToken cancellationToken)
    {
        if (!Enabled) return;
        Calls.Enqueue((tenantId, _userContextAccessor.Current));

        await using var db = await _dbContextFactory.CreateAsync(cancellationToken);
        var present = await db.PicklistSets.IgnoreQueryFilters([QueryFilters.Tenant])
            .AnyAsync(p => p.TenantId == tenantId && p.Name == Picklist.Unit && p.Value == SeededValue, cancellationToken);
        if (present) return;

        db.PicklistSets.Add(new PicklistSet
        {
            Name = Picklist.Unit, Value = SeededValue, Text = SeededValue, Description = "Seeded per tenant",
            TenantId = tenantId
        });
        await db.SaveChangesAsync(cancellationToken);
    }
}
