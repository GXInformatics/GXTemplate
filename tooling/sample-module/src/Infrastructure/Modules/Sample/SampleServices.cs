using System.Collections.Concurrent;
using CleanArchitecture.Blazor.Application.Common.Interfaces.MultiTenant;
using CleanArchitecture.Blazor.Application.Common.Modules;
using CleanArchitecture.Blazor.Application.Modules.Sample.Contracts;
using CleanArchitecture.Blazor.Domain.Modules.Sample;
using CleanArchitecture.Blazor.Domain.Modules.Sample.Entities;
using Microsoft.Extensions.Configuration;

namespace CleanArchitecture.Blazor.Infrastructure.Modules.Sample;

/// <summary>The sample module's Infrastructure registrations: its Contracts implementation and its seeder (item 2).</summary>
public sealed class SampleServices : ModuleServices<SampleModule>
{
    public override void Register(IModuleServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<ISampleBinLookup, SampleBinLookup>();
        services.AddScoped<ITenantSeeder, SampleTenantSeeder>();
    }
}

/// <summary>The implementation of the module's published query.</summary>
public sealed class SampleBinLookup(IApplicationDbContextFactory dbContextFactory) : ISampleBinLookup
{
    public async Task<int> CountAsync(int branchId, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateAsync(cancellationToken);
        return await db.Set<SampleBin>().CountAsync(b => b.BranchId == branchId, cancellationToken);
    }
}

/// <summary>A seeder registered through the hook. It writes nothing; it records the tenants it was run for.</summary>
public sealed class SampleTenantSeeder : ITenantSeeder
{
    public static readonly ConcurrentDictionary<string, int> Seeded = new();

    public Task SeedAsync(string tenantId, CancellationToken cancellationToken)
    {
        Seeded.AddOrUpdate(tenantId, 1, (_, n) => n + 1);
        return Task.CompletedTask;
    }
}
