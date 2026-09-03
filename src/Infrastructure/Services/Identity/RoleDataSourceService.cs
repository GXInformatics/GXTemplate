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
    /// <b>It is also the open product question of Pass 23 §2.5.</b> If roles are ever made
    /// per-tenant, this line is one of the things that must change with them - and it will not fail
    /// to compile, which is why the reason is written down here rather than left to be inferred from
    /// the enum value.
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
