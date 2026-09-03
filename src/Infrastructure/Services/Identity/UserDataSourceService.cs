using CleanArchitecture.Blazor.Application.Common.Security;
using CleanArchitecture.Blazor.Application.Features.Identity;
using CleanArchitecture.Blazor.Application.Features.Identity.DTOs;
using CleanArchitecture.Blazor.Domain.Identity;
using Mapster;
using ZiggyCreatures.Caching.Fusion;

namespace CleanArchitecture.Blazor.Infrastructure.Services.Identity;

public class UserDataSourceService : DataSourceServiceBase<ApplicationUserDto>, IDisposable
{
    private const string CACHEKEY = "ALL-ApplicationUserDto";
    private readonly TypeAdapterConfig _typeAdapterConfig;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IUserContextAccessor _userContextAccessor;
    private readonly IPermissionQueryService _permissionQueryService;

    public UserDataSourceService(
        TypeAdapterConfig typeAdapterConfig,
        IFusionCache fusionCache,
        IUserContextAccessor userContextAccessor,
        IPermissionQueryService permissionQueryService,
        IServiceScopeFactory scopeFactory)
        : base(fusionCache, userContextAccessor, CACHEKEY)
    {
        _typeAdapterConfig = typeAdapterConfig;
        _scopeFactory = scopeFactory;
        _userContextAccessor = userContextAccessor;
        _permissionQueryService = permissionQueryService;
    }

    /// <summary>
    /// <see cref="CacheScope.PerUser"/> - the bound is a per-principal fact, not a per-tenant one.
    /// </summary>
    /// <remarks>
    /// <b>This was <see cref="CacheScope.PerTenant"/> until Pass 28, and bounding the query is what
    /// made that wrong.</b> Pass 26 declared PerTenant while the query was unfiltered, reasoning that
    /// the list is "who exists in a tenant, the same answer for everyone in it". Once the rows are
    /// bounded by <c>AllowedTenantIds</c> that stops being true: two principals sitting in the same
    /// tenant get different answers if one of them also belongs to a second tenant, or holds
    /// <c>Permissions.Users.ViewAllTenants</c>. Under a per-tenant key one of them would have been
    /// served the other's list.
    /// <para>
    /// This is why the scope had to be re-derived rather than carried forward: a partition is a
    /// claim about who may share an entry, and changing what the query returns can invalidate it
    /// without touching the line that declares it.
    /// </para>
    /// <para>
    /// PerUser rather than PerUserAndTenant: the bound is a function of the principal alone -
    /// their allowed tenants and their permission - so the tenant they are currently in adds
    /// nothing to the key. <c>DataSourceServiceBase</c> reloads when the composed key moves, so a
    /// principal whose allowed set changes mid-circuit is not served the old list.
    /// </para>
    /// </remarks>
    public override CacheScope Scope => CacheScope.PerUser;

    /// <summary>
    /// The users this principal may see.
    /// </summary>
    /// <remarks>
    /// <b>Bounded at the query, not at the view.</b> Until Pass 28 this loaded every user in the
    /// installation and <c>PickSuperiorAutocomplete</c> filtered the list in memory - so no foreign
    /// row reached the screen, but the whole directory, with display names, emails and phone
    /// numbers, sat in the circuit's memory and in this principal's cache entry. Filtering at the
    /// view rather than the query is the shape of defect this programme has spent several passes
    /// removing.
    /// <para>
    /// The rule is <see cref="UserTenantVisibility.IsVisibleTo"/>, shared with the users grid and
    /// the user export rather than restated here. Fail-closed follows from it: no ambient principal,
    /// or a principal belonging to no tenant, yields no rows.
    /// </para>
    /// </remarks>
    protected override async Task<List<ApplicationUserDto>?> LoadAsync(CancellationToken cancellationToken)
    {
        var user = _userContextAccessor.Current;
        if (user is null) return new List<ApplicationUserDto>();

        using var scope = _scopeFactory.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var visible = UserTenantVisibility.IsVisibleTo(
            await HoldsViewAllTenantsAsync(user.UserId),
            user.AllowedTenantIds);

        return await userManager.Users
            .Where(visible)
            .Include(x => x.UserRoles).ThenInclude(x => x.Role)
            .ProjectToType<ApplicationUserDto>(_typeAdapterConfig)
            .OrderBy(x => x.UserName)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Whether this principal holds the cross-tenant visibility right.</summary>
    /// <remarks>
    /// <see cref="IPermissionQueryService"/> rather than <c>IPermissionService</c>, for the reason
    /// Pass 27 found the hard way in <c>TenantDataSourceService</c>: the latter resolves the
    /// principal through Blazor's <c>AuthenticationStateProvider</c> and cannot be constructed
    /// outside a Blazor host. Costs one query per cache miss, not per read.
    /// </remarks>
    private async Task<bool> HoldsViewAllTenantsAsync(string userId)
    {
        var permissions = await _permissionQueryService.GetAllPermissionsByUserId(userId);
        return permissions.Any(p =>
            p.Assigned && string.Equals(p.ClaimValue, Permissions.Users.ViewAllTenants, StringComparison.Ordinal));
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
