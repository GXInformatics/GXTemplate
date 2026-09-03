using CleanArchitecture.Blazor.Application.Features.Tenants.Caching;
using CleanArchitecture.Blazor.Application.Features.Tenants.DTOs;
using Mapster;
using ZiggyCreatures.Caching.Fusion;

namespace CleanArchitecture.Blazor.Infrastructure.Services.MultiTenant;

public class TenantDataSourceService : DataSourceServiceBase<TenantDto>
{
    private readonly IApplicationDbContextFactory _dbContextFactory;
    private readonly TypeAdapterConfig _typeAdapterConfig;

    public TenantDataSourceService(
        TypeAdapterConfig typeAdapterConfig,
        IFusionCache fusionCache,
        IUserContextAccessor userContextAccessor,
        IApplicationDbContextFactory dbContextFactory)
        : base(fusionCache, userContextAccessor, TenantCacheKey.TenantsCacheKey)
    {
        _dbContextFactory = dbContextFactory;
        _typeAdapterConfig = typeAdapterConfig;
    }

    /// <summary>
    /// <see cref="CacheScope.PerUser"/> - which tenants a principal may see differs by principal, not
    /// by the tenant they are currently in.
    /// </summary>
    /// <remarks>
    /// PerUser rather than PerTenant, and the distinction is the point: two administrators sitting in
    /// the same tenant can legitimately have different answers, because tenant membership is
    /// per-user (<c>TenantUsers</c>) and a <c>Permissions.Users.SwitchToAnyTenant</c> holder sees
    /// more than a colleague beside them. Keying by tenant would hand one of them the other's list.
    /// <para>
    /// Unfiltered today - this still returns every tenant - so the scope currently buys a partition
    /// and no behaviour change. It is declared now for the same reason as the user list: the key has
    /// to be right before the query narrows, not after.
    /// </para>
    /// </remarks>
    public override CacheScope Scope => CacheScope.PerUser;

    protected override async Task<List<TenantDto>?> LoadAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateAsync(cancellationToken);
        return await db.Tenants.ProjectToType<TenantDto>(_typeAdapterConfig)
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);
    }
}
