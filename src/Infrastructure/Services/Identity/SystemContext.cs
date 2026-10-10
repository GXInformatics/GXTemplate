using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Domain.Identity;

namespace CleanArchitecture.Blazor.Infrastructure.Services.Identity;

/// <inheritdoc cref="ISystemContext"/>
public class SystemContext : ISystemContext
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IUserContextAccessor _userContextAccessor;

    public SystemContext(IServiceScopeFactory scopeFactory, IUserContextAccessor userContextAccessor)
    {
        _scopeFactory = scopeFactory;
        _userContextAccessor = userContextAccessor;
    }

    public async Task RunAsync(string tenantId, Func<CancellationToken, Task> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        var context = await ResolveAsync(tenantId, cancellationToken);
        using (_userContextAccessor.Push(context))
        {
            await work(cancellationToken);
        }
    }

    public async Task<T> RunAsync<T>(string tenantId, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        var context = await ResolveAsync(tenantId, cancellationToken);
        using (_userContextAccessor.Push(context))
        {
            return await work(cancellationToken);
        }
    }

    /// <summary>
    /// The system account's context, in <paramref name="tenantId"/>. Read fresh on each call - a
    /// background run is not a hot path, and a cached id would outlive an account an operator
    /// deleted and provisioning re-created.
    /// </summary>
    private async Task<UserContext> ResolveAsync(string tenantId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new InvalidOperationException("System work must name the tenant it runs in.");

        using var scope = _scopeFactory.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var account = await userManager.FindByNameAsync(Users.System)
            ?? throw new InvalidOperationException(
                $"The system account '{Users.System}' does not exist. It is created by " +
                "ApplicationDbContextInitializer.ProvisionAsync at startup; background work cannot run before that.");

        var dbFactory = scope.ServiceProvider.GetRequiredService<IApplicationDbContextFactory>();
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (!await db.Tenants.AnyAsync(t => t.Id == tenantId, cancellationToken))
            throw new InvalidOperationException($"System work was asked to run in tenant '{tenantId}', which does not exist.");

        return new UserContext(
            UserId: account.Id,
            UserName: account.UserName ?? Users.System,
            DisplayName: account.DisplayName,
            TenantId: tenantId,
            // Exactly the tenant it was given. The system account belongs to no tenant of its own,
            // so anything scoping by AllowedTenantIds confines it to the one this run is for.
            AllowedTenantIds: [tenantId],
            Roles: []);
    }
}
