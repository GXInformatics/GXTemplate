using CleanArchitecture.Blazor.Domain.Identity;
using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Application.Common.Security;

namespace CleanArchitecture.Blazor.Infrastructure.Services;

/// <summary>
/// Service for managing tenant switching functionality
/// </summary>
public class TenantSwitchService : ITenantSwitchService
{
    private readonly IApplicationDbContextFactory _dbContextFactory;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IPermissionService _permissionService;
    private readonly IUserProfileState _userProfileState;
    private readonly IUserContextLoader _userContextLoader;
    private readonly ILogger<TenantSwitchService> _logger;

    public TenantSwitchService(
        IApplicationDbContextFactory dbContextFactory,
        IServiceScopeFactory serviceScopeFactory,
        IPermissionService permissionService,
        IUserProfileState userProfileState,
        IUserContextLoader userContextLoader,
        ILogger<TenantSwitchService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _serviceScopeFactory = serviceScopeFactory;
        _permissionService = permissionService;
        _userProfileState = userProfileState;
        _userContextLoader = userContextLoader;
        _logger = logger;
    }

     

    /// <summary>
    /// Switch user to specified tenant
    /// </summary>
    public async Task<Result> SwitchToTenantAsync(string userId, string tenantId)
    {
        try
        {
            // Enforced HERE, on the arguments actually being used, rather than trusted from the
            // caller. The tenant selector only offers tenants the user belongs to, but that is a
            // property of one component's rendering, not of this service: any other caller, now or
            // later, reaches the write below through this method and must meet the same test.
            // A refusal is the same message whether the tenant is unreachable or does not exist, so
            // the response does not report which tenant ids are real.
            if (!await CanSwitchToTenantAsync(userId, tenantId))
                return Result.Failure("Insufficient permissions to switch to this tenant");

            await using var db = await _dbContextFactory.CreateAsync();
            
            // Get user and tenant information
            using var scope = _serviceScopeFactory.CreateScope();
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
            
            var user = await userManager.FindByIdAsync(userId);
            var tenant = await db.Tenants.FindAsync(tenantId);
            
            if (user == null || tenant == null)
                return Result.Failure("User or tenant not found");

            // Record the original tenant ID for logging
            var originalTenantId = user.TenantId;

            

            // Update user's tenant ID
            user.TenantId = tenantId;

           

            // Update database
            await userManager.UpdateAsync(user);
            
            // Refresh user state and cache
            await _userProfileState.RefreshAsync();

            // Clear user context cache
            _userContextLoader.ClearUserContextCache(userId);
            
            // Update user claims
            await RefreshUserClaimsAsync(user, userManager);
            
            // Log successful tenant switch
            _logger.LogInformation("User {UserId} ({UserName}) successfully switched from tenant {OriginalTenantId} to tenant {NewTenantId} ({TenantName})", 
                userId, user.UserName, originalTenantId ?? "null", tenantId, tenant.Name);
            
            // Record switch result
            var result = Result.Success();
            
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to switch user {UserId} to tenant {TenantId}", userId, tenantId);
            return Result.Failure("Failed to switch tenant");
        }
    }

    /// <summary>
    /// Whether <paramref name="userId"/> may be switched to <paramref name="tenantId"/>.
    /// </summary>
    /// <remarks>
    /// <b>The two permissions are a ladder, not a pair.</b> Their own descriptions say so:
    /// <c>SwitchTenants</c> is "Allows switching between AVAILABLE tenants" - the ones the user
    /// belongs to - and <c>SwitchToAnyTenant</c> is "Allows switching to ANY tenant (admin
    /// privilege)". "Any" contains "available", so the second implies the first and works alone.
    /// <para>
    /// It used to require BOTH, which made the finer-grained permission dead as written: holding
    /// <c>SwitchTenants</c> by itself granted nothing, and an administrator revoking
    /// <c>SwitchToAnyTenant</c> to leave someone switching only among their own tenants actually
    /// took away all switching. Neither permission meant what its description said.
    /// </para>
    /// <para>
    /// <b>And it used neither of its parameters.</b> It answered "may this principal switch tenants
    /// at all?" while its name, its signature and its one caller all say it answers "may this
    /// principal switch to THIS tenant?". Nothing checked membership, so any tenant id reaching
    /// <see cref="SwitchToTenantAsync"/> was accepted and written to <c>ApplicationUser.TenantId</c>.
    /// The exposure was contained only by the tenant selector offering legitimate tenants - and a
    /// check whose correctness depends on its caller's UI is not a check.
    /// </para>
    /// <para>
    /// Membership is read from <c>TenantUsers</c> by the <paramref name="userId"/> ARGUMENT rather
    /// than from the ambient principal's cached context: resolving it from the ambient context would
    /// ignore the parameter all over again, which is the defect this method exists to have fixed.
    /// </para>
    /// </remarks>
    public async Task<bool> CanSwitchToTenantAsync(string userId, string tenantId)
    {
        try
        {
            if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(tenantId))
                return false;

            // The escalated right, checked first because it subsumes the other one. A holder may
            // switch to a tenant they have no membership row for - that is the whole capability -
            // so no further test applies to them.
            if (await _permissionService.HasPermissionAsync(Permissions.Users.SwitchToAnyTenant))
                return true;

            if (!await _permissionService.HasPermissionAsync(Permissions.Users.SwitchTenants))
                return false;

            // Otherwise the target must be one of this user's own tenants.
            await using var db = await _dbContextFactory.CreateAsync();
            return await db.TenantUsers.AnyAsync(tu => tu.UserId == userId && tu.TenantId == tenantId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check tenant switch permission for user {UserId} to tenant {TenantId}", userId, tenantId);
            return false;
        }
    }

     

    /// <summary>
    /// Refresh user claims after tenant switch
    /// </summary>
    private async Task RefreshUserClaimsAsync(ApplicationUser user, UserManager<ApplicationUser> userManager)
    {
        try
        {
            // Get existing claims
            var existingClaims = await userManager.GetClaimsAsync(user);
            
            // Remove only tenant-related claims that need to be updated
            var tenantClaimsToRemove = existingClaims
                .Where(c => c.Type == ApplicationClaimTypes.TenantId || c.Type == ApplicationClaimTypes.TenantName)
                .ToList();
            
            if (tenantClaimsToRemove.Any())
            {
                await userManager.RemoveClaimsAsync(user, tenantClaimsToRemove);
            }
            
            // Add updated tenant claims
            var newTenantClaims = new List<Claim>
            {
                new(ApplicationClaimTypes.TenantId, user.TenantId ?? ""),
                new(ApplicationClaimTypes.TenantName, user.Tenant?.Name ?? "")
            };
            
            await userManager.AddClaimsAsync(user, newTenantClaims);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh claims for user {UserId}", user.Id);
        }
    }

     
}
