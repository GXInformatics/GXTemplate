using CleanArchitecture.Blazor.Application.Common.Security;
using CleanArchitecture.Blazor.Application.Features.Tenants.Caching;
using CleanArchitecture.Blazor.Application.Features.Tenants.DTOs;
using Mapster;
using ZiggyCreatures.Caching.Fusion;

namespace CleanArchitecture.Blazor.Infrastructure.Services.MultiTenant;

public class TenantDataSourceService : DataSourceServiceBase<TenantDto>
{
    private readonly IApplicationDbContextFactory _dbContextFactory;
    private readonly TypeAdapterConfig _typeAdapterConfig;
    private readonly IUserContextAccessor _userContextAccessor;
    private readonly IPermissionQueryService _permissionQueryService;

    /// <remarks>
    /// <b><see cref="IPermissionQueryService"/> rather than <c>IPermissionService</c>, deliberately.</b>
    /// The latter resolves the principal through Blazor's <c>AuthenticationStateProvider</c>, so a
    /// datasource depending on it cannot be constructed outside a Blazor host - which is not
    /// hypothetical: it broke <c>Application.IntegrationTests</c>, whose container is Infrastructure
    /// plus Application and nothing else. This one reads role claims through a scope factory and
    /// works in any host.
    /// </remarks>
    public TenantDataSourceService(
        TypeAdapterConfig typeAdapterConfig,
        IFusionCache fusionCache,
        IUserContextAccessor userContextAccessor,
        IPermissionQueryService permissionQueryService,
        IApplicationDbContextFactory dbContextFactory)
        : base(fusionCache, userContextAccessor, TenantCacheKey.TenantsCacheKey)
    {
        _dbContextFactory = dbContextFactory;
        _typeAdapterConfig = typeAdapterConfig;
        _userContextAccessor = userContextAccessor;
        _permissionQueryService = permissionQueryService;
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
    /// Pass 26 declared this scope while the query was still unfiltered, so that the key would be
    /// right before the rows narrowed. Pass 27 narrowed them; this is the partition that makes that
    /// safe.
    /// </para>
    /// </remarks>
    public override CacheScope Scope => CacheScope.PerUser;

    /// <summary>
    /// The tenants this principal may see.
    /// </summary>
    /// <remarks>
    /// <b>One list, two dropdowns, one bound.</b> This backs both the Users page's tenant filter
    /// ("which tenants may I filter by") and <c>TenantSelect</c> in the user dialog ("which tenants
    /// may I assign a user to"). They are the same question: you cannot assign a user into a tenant
    /// you cannot see, and before Pass 27 both offered every tenant in the installation - so an
    /// administrator of one tenant could move a user into another one they had no visibility of.
    /// <para>
    /// <b>Not the tenant SWITCHER.</b> That control reads <c>UserProfile.AvailableTenants</c>, a
    /// different source with a different question - which tenants may I switch into - bounded by
    /// membership and <c>SwitchToAnyTenant</c> rather than by visibility. It is deliberately
    /// untouched here.
    /// </para>
    /// <para>
    /// <b>Fail closed.</b> No ambient principal yields an empty list rather than every tenant. The
    /// datasource base already declines to CACHE an unscoped load; returning everything from it
    /// would defeat that by putting the whole installation into the caller's hands anyway.
    /// </para>
    /// </remarks>
    protected override async Task<List<TenantDto>?> LoadAsync(CancellationToken cancellationToken)
    {
        var user = _userContextAccessor.Current;
        if (user is null) return new List<TenantDto>();

        await using var db = await _dbContextFactory.CreateAsync(cancellationToken);

        var query = db.Tenants.ProjectToType<TenantDto>(_typeAdapterConfig);

        if (!await HoldsViewAllTenantsAsync(user.UserId))
        {
            // AllowedTenantIds is the union of membership and the principal's own tenant (Pass 25),
            // so a principal switched into a tenant they hold no membership row for still sees it.
            var allowed = user.AllowedTenantIds?.ToArray() ?? Array.Empty<string>();
            query = query.Where(t => allowed.Contains(t.Id));
        }

        return await query.OrderBy(x => x.Name).ToListAsync(cancellationToken);
    }

    /// <summary>Whether this principal holds the cross-tenant visibility right.</summary>
    /// <remarks>
    /// Costs one permission query per cache miss, not per read: the result is baked into the entry
    /// this service caches, and the entry is <see cref="CacheScope.PerUser"/> - so the answer and the
    /// list it produced always belong to the same principal.
    /// </remarks>
    private async Task<bool> HoldsViewAllTenantsAsync(string userId)
    {
        var permissions = await _permissionQueryService.GetAllPermissionsByUserId(userId);
        return permissions.Any(p =>
            p.Assigned && string.Equals(p.ClaimValue, Permissions.Users.ViewAllTenants, StringComparison.Ordinal));
    }
}
