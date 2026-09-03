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

    public UserDataSourceService(
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
    /// <see cref="CacheScope.PerTenant"/> - the user list is tenant-visible data.
    /// </summary>
    /// <remarks>
    /// <b>Declared ahead of the filtering that will justify it.</b> The query below is still
    /// unfiltered, so today every tenant's entry holds the same rows and this scope only costs one
    /// cache partition per tenant. It is declared now because it must be in place BEFORE the query
    /// is scoped: with a constant key, the first tenant to warm the entry would serve its user list
    /// to every other tenant, intermittently and unreproducibly. The order matters more than the
    /// timing.
    /// <para>
    /// PerTenant rather than PerUserAndTenant: this list is who exists in a tenant, which is the
    /// same answer for everyone in it. Partitioning per user as well would multiply the entries by
    /// the user count to no purpose - and this list backs an autocomplete, so it is read often.
    /// </para>
    /// </remarks>
    public override CacheScope Scope => CacheScope.PerTenant;

    protected override async Task<List<ApplicationUserDto>?> LoadAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        return await userManager.Users
            .Include(x => x.UserRoles).ThenInclude(x => x.Role)
            .ProjectToType<ApplicationUserDto>(_typeAdapterConfig)
            .OrderBy(x => x.UserName)
            .ToListAsync(cancellationToken);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
