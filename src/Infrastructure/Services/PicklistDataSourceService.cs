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
    /// <see cref="CacheScope.Global"/> - picklists are shared reference data today.
    /// </summary>
    /// <remarks>
    /// <b>A claim, and the one most likely to stop being true.</b> As shipped these are Status, Unit
    /// and Brand - seeded once, the same for everybody - and nothing filters them, so one entry is
    /// correct. Pass 24 gave <c>PicklistSet</c> a <c>TenantId</c> and it is stamped on insert, but
    /// stamped is not scoped: no query reads it.
    /// <para>
    /// <b>This becomes <see cref="CacheScope.PerTenant"/> in the same change that scopes the query</b>,
    /// and the two must move together - a scoped query behind a Global key would serve the first
    /// tenant's picklists to the rest. Pass 23 §2.6 records that whether picklists SHOULD be
    /// per-tenant is a product decision still open; the moment it is answered yes, this line is part
    /// of the answer.
    /// </para>
    /// </remarks>
    public override CacheScope Scope => CacheScope.Global;

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
