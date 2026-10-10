using CleanArchitecture.Blazor.Application.Common.Interfaces.MultiTenant;

namespace CleanArchitecture.Blazor.Infrastructure.Services.MultiTenant;

/// <summary>
/// Runs every registered <see cref="ITenantSeeder"/> for one tenant, in registration order, with
/// the ambient principal hidden.
/// </summary>
/// <remarks>
/// <b>Hiding the principal is what makes the two invocation points the same.</b> At startup there is
/// no principal; when a tenant is created there is - the administrator whose circuit sent the
/// command. Left visible, it would make the create-time run stamp that administrator's own tenant on
/// rows the seeder forgot to tenant, authorize Mediator calls the startup run would refuse, and
/// scope reads to the administrator's tenant rather than to the installation. A seeder that passed
/// its tests at create time could then fail, or write elsewhere, on the next restart. So both runs
/// see what startup sees: nobody.
/// <para>
/// The push is made inside this <c>async</c> method and every seeder is awaited inside it, so the
/// hidden state flows into the seeders and is restored when the method returns - an
/// <c>AsyncLocal</c> change made here never leaks back to the caller.
/// </para>
/// </remarks>
public class TenantSeedRunner : ITenantSeedRunner
{
    private readonly IEnumerable<ITenantSeeder> _seeders;
    private readonly IUserContextAccessor _userContextAccessor;
    private readonly ILogger<TenantSeedRunner> _logger;

    public TenantSeedRunner(
        IEnumerable<ITenantSeeder> seeders,
        IUserContextAccessor userContextAccessor,
        ILogger<TenantSeedRunner> logger)
    {
        _seeders = seeders;
        _userContextAccessor = userContextAccessor;
        _logger = logger;
    }

    public async Task SeedAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(tenantId);

        using (_userContextAccessor.Push(null))
        {
            foreach (var seeder in _seeders)
            {
                try
                {
                    await seeder.SeedAsync(tenantId, cancellationToken);
                }
                catch (Exception ex)
                {
                    // Named, then rethrown: the caller decides what a failed seed means (a failed
                    // tenant creation, a refused start). The log says WHICH seeder and WHICH tenant,
                    // which the exception alone may not.
                    _logger.LogError(ex, "Tenant seeder {Seeder} failed for tenant {TenantId}.",
                        seeder.GetType().Name, tenantId);
                    throw;
                }
            }
        }
    }
}
