using CleanArchitecture.Blazor.Application.Features.Identity.DTOs;
using CleanArchitecture.Blazor.Domain.Identity;
using Mapster;
using ZiggyCreatures.Caching.Fusion;

namespace CleanArchitecture.Blazor.Infrastructure.Services.Identity;

public class RoleDataSourceService : DataSourceServiceBase<ApplicationRoleDto>, IDisposable
{
    private const string CACHEKEY = "ALL-ApplicationRoleDto";
    private readonly TypeAdapterConfig _typeAdapterConfig;
    private readonly IServiceScopeFactory _scopeFactory;

    public RoleDataSourceService(
        TypeAdapterConfig typeAdapterConfig,
        IFusionCache fusionCache,
        IUserContextAccessor userContextAccessor,
        IServiceScopeFactory scopeFactory)
        : base(fusionCache, userContextAccessor, CACHEKEY)
    {
        _typeAdapterConfig = typeAdapterConfig;
        _scopeFactory = scopeFactory;
    }

    /// <summary>
    /// <see cref="CacheScope.Global"/> - roles genuinely are installation-wide.
    /// </summary>
    /// <remarks>
    /// <b>This is a claim about the data, not a default.</b> <c>ApplicationRole</c> carries no
    /// TenantId at all, and <c>ApplicationRoleConfiguration</c> puts a unique index on
    /// <c>NormalizedName</c> across the whole installation - so two tenants cannot even hold roles of
    /// the same name. There is exactly one role list and every principal sees it.
    /// <para>
    /// <b>Pass 23 §2.5's open question is now CLOSED, and the answer left this line alone.</b> Pass
    /// 33 ratified option (a) - roles stay installation-wide, and DEFINING one requires
    /// <c>Permissions.Roles.ManageDefinitions</c>. That is a pure authorization change: who may
    /// WRITE a role changed, and writes do not enter a read's cache key. The list this service
    /// caches is still identical for every principal, so <c>Global</c> is still the truth. That it
    /// needed no change here was a point in the option's favour, weighed in Pass 32 §4.5.
    /// </para>
    /// <para>
    /// <b>What would still change it.</b> Only making roles per-tenant, which Pass 32 §4.3 costed
    /// and rejected: this would become <c>PerTenant</c>, and <c>PerUser</c> if a cross-tenant role
    /// escape were ever added, per Pass 28's finding. Neither follows from a permission guard - and
    /// neither would fail to compile, which is why the reason is written down here rather than left
    /// to be inferred from the enum value.
    /// </para>
    /// </remarks>
    public override CacheScope Scope => CacheScope.Global;

    protected override async Task<List<ApplicationRoleDto>?> LoadAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        return await roleManager.Roles
            .ProjectToType<ApplicationRoleDto>(_typeAdapterConfig)
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
