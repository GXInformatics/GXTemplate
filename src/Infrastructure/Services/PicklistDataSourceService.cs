using System.Linq.Expressions;
using CleanArchitecture.Blazor.Application.Features.PicklistSets.Caching;
using CleanArchitecture.Blazor.Application.Features.PicklistSets.DTOs;
using Mapster;
using ZiggyCreatures.Caching.Fusion;

namespace CleanArchitecture.Blazor.Infrastructure.Services;

public class PicklistDataSourceService : DataSourceServiceBase<PicklistSetDto>
{
    private readonly IApplicationDbContextFactory _dbContextFactory;
    private readonly TypeAdapterConfig _typeAdapterConfig;

    public PicklistDataSourceService(
        TypeAdapterConfig typeAdapterConfig,
        IFusionCache fusionCache,
        IUserContextAccessor userContextAccessor,
        IApplicationDbContextFactory dbContextFactory)
        : base(fusionCache, userContextAccessor, PicklistSetCacheKey.PicklistCacheKey)
    {
        _dbContextFactory = dbContextFactory;
        _typeAdapterConfig = typeAdapterConfig;
    }

    /// <summary>
    /// <see cref="CacheScope.PerTenant"/> - the list is a function of the caller's tenant and
    /// nothing else.
    /// </summary>
    /// <remarks>
    /// <b>This was <see cref="CacheScope.Global"/> until Pass 31, and it moved in the same change
    /// that scoped the query - which is the only safe way to move it.</b> A tenant-filtered query
    /// behind a process-wide key is a cross-tenant leak that no query test would show: the first
    /// tenant to warm the entry serves its picklists to every other tenant, intermittently and
    /// without reproducing. The filter and this line are one change, not two.
    /// <para>
    /// <b>The partition was re-derived, not carried forward.</b> Pass 28 found
    /// <c>UserDataSourceService</c>'s declared PerTenant had quietly become false once its query was
    /// bounded, because that query reads <c>AllowedTenantIds</c> and a cross-tenant permission - so
    /// two principals sitting in the same tenant could get different answers. The same question was
    /// asked here and the answer is different: the picklist predicate is
    /// <c>TenantId == null || TenantId == current</c>, which reads <c>UserContext.TenantId</c> and
    /// NOTHING else. No permission enters it, no allowed-tenant union, and picklists have no
    /// cross-tenant escape at all. So any two principals in the same tenant genuinely do see the
    /// same list, and <c>t:{TenantId}</c> is exactly the key the filter's own input composes to.
    /// <b>Should a cross-tenant escape ever be added, this must become PerUser in the same change</b>
    /// - the escape is a per-principal fact and would break this partition the moment it existed.
    /// </para>
    /// <para>
    /// A principal with no tenant shares the "no tenant" partition, which is the same thing the
    /// query itself sees: shared rows only.
    /// </para>
    /// </remarks>
    public override CacheScope Scope => CacheScope.PerTenant;

    /// <summary>
    /// Searches the database directly rather than the loaded list.
    /// </summary>
    /// <remarks>
    /// <b>The one method that does not go through the cache, and it is bounded anyway.</b> The base
    /// implementation searches <c>Items</c>, so it inherits whatever <see cref="Scope"/> partitioned;
    /// this override queries <c>PicklistSets</c> itself and never reads or writes the cache at all.
    /// Pass 26 A2 recorded that as harmless while nothing was filtered and a hazard the moment the
    /// scope meant something.
    /// <para>
    /// Pass 31 checked rather than assumed, because "the global filter covers it" is exactly the
    /// claim worth testing on the one method known to be different. It does cover it: the context
    /// comes from <c>IApplicationDbContextFactory</c>, which builds through EF's
    /// <c>IDbContextFactory</c>, which resolves <c>IUserContextAccessor</c> from the container - so
    /// <c>CurrentTenantId</c> is the caller's and the filter applies to this query exactly as it
    /// does to <c>LoadAsync</c>. Bypassing the cache turns out to make this path SAFER than the
    /// cached one, not riskier: there is no entry for it to serve to the wrong tenant.
    /// <c>SearchAsyncIsBoundedByTheGlobalFilter</c> pins it.
    /// </para>
    /// </remarks>
    public override async Task<IEnumerable<PicklistSetDto>> SearchAsync(
        Expression<Func<PicklistSetDto, bool>>? predicate,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateAsync(cancellationToken);

        IQueryable<PicklistSetDto> q = db.PicklistSets
            .AsNoTracking()
            .ProjectToType<PicklistSetDto>(_typeAdapterConfig);

        if (predicate is not null)
            q = q.Where(predicate);

        var take = limit ?? DefaultLimit;

        var list = await q
            .OrderBy(t => t.Name)
            .Take(take)
            .ToListAsync(cancellationToken);

        return list.AsReadOnly();
    }

    protected override async Task<List<PicklistSetDto>?> LoadAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateAsync(cancellationToken);
        return await db.PicklistSets.ProjectToType<PicklistSetDto>(_typeAdapterConfig)
            .OrderBy(x => x.Name).ThenBy(x => x.Value)
            .ToListAsync(cancellationToken);
    }
}
