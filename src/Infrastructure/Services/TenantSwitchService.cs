using CleanArchitecture.Blazor.Application.Features.Tenants.DTOs;
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
    private readonly IPermissionQueryService _permissionQueryService;
    private readonly IUserProfileState _userProfileState;
    private readonly IUserContextLoader _userContextLoader;
    private readonly ILogger<TenantSwitchService> _logger;

    /// <remarks>
    /// <b><see cref="IPermissionQueryService"/> rather than <c>IPermissionService</c>.</b> The latter
    /// resolves the principal through Blazor's <c>AuthenticationStateProvider</c>, so any host that
    /// is not Blazor cannot construct a service that depends on it - which is not hypothetical:
    /// Pass 27 hit exactly that when a datasource took the dependency and
    /// <c>Application.IntegrationTests</c>, whose container is Infrastructure plus Application and
    /// nothing else, stopped being able to build one. This reads role claims through a scope factory
    /// and works anywhere.
    /// <para>
    /// It also changes what "the principal" means here, for the better: <c>IPermissionService</c>
    /// answers about the CURRENT principal, while this answers about the <c>userId</c> argument -
    /// which is what both public methods are actually asking about.
    /// </para>
    /// </remarks>
    public TenantSwitchService(
        IApplicationDbContextFactory dbContextFactory,
        IServiceScopeFactory serviceScopeFactory,
        IPermissionQueryService permissionQueryService,
        IUserProfileState userProfileState,
        IUserContextLoader userContextLoader,
        ILogger<TenantSwitchService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _serviceScopeFactory = serviceScopeFactory;
        _permissionQueryService = permissionQueryService;
        _userProfileState = userProfileState;
        _userContextLoader = userContextLoader;
        _logger = logger;
    }

    /// <summary>
    /// How far a principal may switch: the one rule both public answers derive from.
    /// </summary>
    /// <remarks>
    /// <b>The ladder, stated once.</b> <c>SwitchToAnyTenant</c> is "switching to ANY tenant (admin
    /// privilege)" and <c>SwitchTenants</c> is "switching between AVAILABLE tenants" - so the first
    /// contains the second and works alone.
    /// </remarks>
    private enum SwitchScope
    {
        /// <summary>Neither right: this principal may not switch at all.</summary>
        None,

        /// <summary>Only the tenants this principal holds a <c>TenantUsers</c> row for.</summary>
        Membership,

        /// <summary>Every tenant in the installation.</summary>
        All
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

            return await ScopeForAsync(userId) switch
            {
                // Existence is still required. Without it the check said yes to ids with no tenant
                // behind them, which broke the offered==permitted property (the list can only offer
                // tenants that exist) and made the refusal message distinguishable - "User or tenant
                // not found" rather than "Insufficient permissions" - so a caller could tell a real
                // tenant id from an invented one. The Membership branch never had this hole: a
                // TenantUsers row implies the tenant.
                SwitchScope.All => await TenantExistsAsync(tenantId),
                SwitchScope.Membership => await IsMemberOfAsync(userId, tenantId),
                _ => false
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check tenant switch permission for user {UserId} to tenant {TenantId}", userId, tenantId);
            return false;
        }
    }

    /// <summary>
    /// The tenants this principal may switch into - exactly the set
    /// <see cref="CanSwitchToTenantAsync"/> would say yes to.
    /// </summary>
    /// <remarks>
    /// <b>The menu and the check derive from one rule, so they agree by construction.</b> That
    /// matters more here than it would for a read: switching is a WRITE - <see cref="SwitchToTenantAsync"/>
    /// persists <c>ApplicationUser.TenantId</c>, and the audit interceptor stamps new rows from it -
    /// so offering a tenant in a menu is offering that mutation. A superset would offer one the
    /// service then refuses; a subset would hide a capability the principal was granted. Both are
    /// bugs, and neither is possible while both answers come from <see cref="ScopeForAsync"/>.
    /// <para>
    /// <b>This is a different bound from tenant VISIBILITY.</b> <c>TenantDataSourceService</c> answers
    /// "which tenants may I see", bounded by <c>AllowedTenantIds</c> and widened by
    /// <c>Users.ViewAllTenants</c>. A principal may legitimately see a tenant without being able to
    /// become it, or the reverse. The two must not be collapsed, which is why this list has its own
    /// source rather than reusing that datasource.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<TenantDto>> GetSwitchableTenantsAsync(string userId)
    {
        try
        {
            if (string.IsNullOrEmpty(userId)) return Array.Empty<TenantDto>();

            var scope = await ScopeForAsync(userId);
            if (scope == SwitchScope.None) return Array.Empty<TenantDto>();

            await using var db = await _dbContextFactory.CreateAsync();

            var query = db.Tenants.AsQueryable();
            if (scope == SwitchScope.Membership)
            {
                query = query.Where(t => db.TenantUsers.Any(tu => tu.UserId == userId && tu.TenantId == t.Id));
            }

            return await query
                .OrderBy(t => t.Name)
                .Select(t => new TenantDto { Id = t.Id, Name = t.Name, Description = t.Description })
                .ToListAsync();
        }
        catch (Exception ex)
        {
            // Fail closed: an error yields no options rather than every option.
            _logger.LogError(ex, "Failed to list switchable tenants for user {UserId}", userId);
            return Array.Empty<TenantDto>();
        }
    }

    /// <summary>Resolves the ladder for one principal. The single source of both answers above.</summary>
    private async Task<SwitchScope> ScopeForAsync(string userId)
    {
        var permissions = await _permissionQueryService.GetAllPermissionsByUserId(userId);

        bool Holds(string permission) => permissions.Any(p =>
            p.Assigned && string.Equals(p.ClaimValue, permission, StringComparison.Ordinal));

        // The escalated right first, because it subsumes the other: a holder may switch to a tenant
        // they have no membership row for, which is the whole capability.
        if (Holds(Permissions.Users.SwitchToAnyTenant)) return SwitchScope.All;
        if (Holds(Permissions.Users.SwitchTenants)) return SwitchScope.Membership;
        return SwitchScope.None;
    }

    private async Task<bool> TenantExistsAsync(string tenantId)
    {
        await using var db = await _dbContextFactory.CreateAsync();
        return await db.Tenants.AnyAsync(t => t.Id == tenantId);
    }

    private async Task<bool> IsMemberOfAsync(string userId, string tenantId)
    {
        await using var db = await _dbContextFactory.CreateAsync();
        return await db.TenantUsers.AnyAsync(tu => tu.UserId == userId && tu.TenantId == tenantId);
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
