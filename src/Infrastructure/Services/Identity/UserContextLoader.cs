using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Domain.Identity;
using CleanArchitecture.Blazor.Application.Common.Security;
using ZiggyCreatures.Caching.Fusion;

namespace CleanArchitecture.Blazor.Infrastructure.Services.Identity;

/// <summary>
/// Implementation of IUserContextLoader that uses UserManager to load user context from ClaimsPrincipal.
/// </summary>
public class UserContextLoader : IUserContextLoader, IUserContextInvalidator
{
    /// <summary>The FusionCache tag every cached user context carries (Pass 59), so a role change can drop them all.</summary>
    public const string ContextTag = "user-context";

    /// <summary>How long a successfully loaded user context is cached.</summary>
    public static readonly TimeSpan ContextCacheDuration = TimeSpan.FromHours(1);

    /// <summary>
    /// How long a genuine "no such user" result is cached. Deliberately short: it is a negative
    /// result rather than data, and the account may be created or restored moments later.
    /// </summary>
    public static readonly TimeSpan NotFoundCacheDuration = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IFusionCache _fusionCache;
    private readonly ILogger<UserContextLoader> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserContextLoader"/> class.
    /// </summary>
    /// <param name="scopeFactory">The service scope factory.</param>
    /// <param name="fusionCache">The fusion cache instance.</param>
    /// <param name="logger">The logger.</param>
    public UserContextLoader(IServiceScopeFactory scopeFactory, IFusionCache fusionCache, ILogger<UserContextLoader> logger)
    {
        _scopeFactory = scopeFactory;
        _fusionCache = fusionCache;
        _logger = logger;
    }

    /// <summary>
    /// Loads user context from the provided ClaimsPrincipal.
    /// </summary>
    /// <param name="principal">The ClaimsPrincipal containing user information.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The loaded UserContext, or null if the user is not authenticated.</returns>
    public async Task<UserContext?> LoadAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
    {
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var userId = principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            return null;
        }

        var cacheKey = UserCacheKeys.GetCacheKey(userId, UserCacheType.Context);

        return await _fusionCache.GetOrSetAsync<UserContext?>(
            cacheKey,
            async (ctx, ct) =>
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                    var user = await userManager.GetUserAsync(principal);
                    if (user == null)
                    {
                        // Genuine "no such user": cacheable, but only for NotFoundCacheDuration.
                        ctx.Options.Duration = NotFoundCacheDuration;
                        return null;
                    }

                    // Pass 60, Part D item 4: a deactivated user, or one locked out by an administrator, has no
                    // context, so the hub filter closes their connection and the cookie check signs them out. An
                    // automatic lockout after failed sign-ins is NOT checked here (owner decision 15): it blocks new
                    // sign-ins only. Cached briefly; the save that changes access invalidates it at once.
                    if (!UserAccess.HasAccess(user))
                    {
                        ctx.Options.Duration = NotFoundCacheDuration;
                        return null;
                    }
                    var memberships = await userManager.Users.Where(x => x.Id == user.Id)
                        .Include(x => x.TenantUsers).ThenInclude(tu => tu.Tenant)
                        .SelectMany(x => x.TenantUsers.Where(tu => tu.Tenant != null).Select(tu => tu.Tenant!.Id))
                        .ToListAsync(ct);

                    // The UNION of the membership rows and the user's own current tenant, which is
                    // the single answer to "which tenants may this principal see?".
                    //
                    // It used to be the membership rows alone, and the two can legitimately differ:
                    // TenantSwitchService lets a Permissions.Users.SwitchToAnyTenant holder move
                    // into a tenant they hold no membership row for - that IS the capability - and
                    // it writes ApplicationUser.TenantId directly. Membership alone would then
                    // report a set not containing the tenant the user is actually in. Any scoping
                    // written as AllowedTenantIds.Contains(row.TenantId) would show that principal
                    // nothing at all, including their own current tenant.
                    //
                    // The same union also absorbs databases that diverged before this was fixed:
                    // the user-edit dialog used to rewrite TenantUsers while leaving TenantId
                    // behind, so existing rows can carry a primary tenant with no membership row.
                    // New divergence is prevented at the source (UserFormDialog.ResolvePrimaryTenantId);
                    // this is what keeps already-saved data readable.
                    //
                    // Distinct, so a tenant that is both the primary and a membership row - the
                    // ordinary case - appears once.
                    var allowedTenantIds = string.IsNullOrEmpty(user.TenantId)
                        ? memberships.Distinct().ToList()
                        : memberships.Append(user.TenantId).Distinct().ToList();
                    var roles = await userManager.GetRolesAsync(user);
                    var branches = await LoadBranchAccessAsync(scope.ServiceProvider, user.Id, ct);

                    return new UserContext(
                        UserId: user.Id,
                        UserName: user.UserName ?? string.Empty,
                        DisplayName: user.DisplayName,
                        TenantId: user.TenantId,
                        AllowedTenantIds: allowedTenantIds.AsReadOnly(),
                        Email: user.Email,
                        Roles: roles.ToList().AsReadOnly(),
                        ProfilePictureDataUrl: user.ProfilePictureDataUrl,
                        SuperiorId: user.SuperiorId,
                        Branches: branches
                    );
                }
                catch (Exception ex)
                {
                    // A transient failure must never become an hour of cached null: log it and let it
                    // propagate, so nothing is written to the cache and the next call retries.
                    _logger.LogError(ex, "Failed to load user context for user {UserId}.", userId);
                    throw;
                }
            },
            options: new FusionCacheEntryOptions(ContextCacheDuration),
            tags: [ContextTag],
            token: cancellationToken
        );
    }

    /// <summary>
    /// Clears the cached user context for a specific user.
    /// </summary>
    /// <param name="userId">The user ID to clear cache for.</param>
    public void ClearUserContextCache(string userId)
    {
        if (!string.IsNullOrEmpty(userId))
        {
            var cacheKey = UserCacheKeys.GetCacheKey(userId, UserCacheType.Context);
            _fusionCache.Remove(cacheKey);
        }
    }

    /// <inheritdoc />
    public void Invalidate(string userId) => ClearUserContextCache(userId);

    /// <inheritdoc />
    public void InvalidateAll() => _fusionCache.RemoveByTag(ContextTag);

    /// <summary>
    /// The user's branch access (Pass 59): the assigned branches (read), the assigned active ones (write),
    /// the default, and whether they hold <c>Branches.ViewAll</c> or <c>Branches.WriteAll</c>.
    /// </summary>
    /// <remarks>
    /// Read through this scope's own <see cref="ApplicationDbContext"/> - the one <c>UserManager</c> uses above
    /// - not <c>IApplicationDbContextFactory</c>: this can run while a unit of work is active (a cache miss
    /// during a query), and the unit of work's context must never be used from here, mid-query, by a second
    /// caller.
    /// </remarks>
    private static async Task<BranchAccess> LoadBranchAccessAsync(IServiceProvider services, string userId, CancellationToken ct)
    {
        var db = services.GetRequiredService<ApplicationDbContext>();
        var rows = await db.UserBranches.AsNoTracking()
            .Where(ub => ub.UserId == userId)
            .OrderBy(ub => ub.BranchId)
            .Select(ub => new { ub.BranchId, ub.IsDefault, ub.Branch!.IsActive })
            .ToListAsync(ct);

        // The two permissions, from the user's own claims and their roles' claims: exactly what the
        // policies of the same names require (RequireClaim(Permission, value)).
        string[] wanted = [Permissions.Branches.ViewAll, Permissions.Branches.WriteAll];
        var held = await db.UserClaims.AsNoTracking()
            .Where(c => c.UserId == userId && c.ClaimType == ApplicationClaimTypes.Permission && wanted.Contains(c.ClaimValue!))
            .Select(c => c.ClaimValue!)
            .Concat(db.RoleClaims.AsNoTracking()
                .Where(rc => rc.ClaimType == ApplicationClaimTypes.Permission && wanted.Contains(rc.ClaimValue!)
                             && db.UserRoles.Any(ur => ur.UserId == userId && ur.RoleId == rc.RoleId))
                .Select(rc => rc.ClaimValue!))
            .Distinct()
            .ToListAsync(ct);
        var readsAll = held.Contains(Permissions.Branches.ViewAll);
        var writesAll = held.Contains(Permissions.Branches.WriteAll);

        return new BranchAccess(
            ReadsAll: readsAll,
            ReadIds: rows.Select(r => r.BranchId).ToArray(),
            WriteIds: rows.Where(r => r.IsActive).Select(r => r.BranchId).ToArray(),
            WritesAll: writesAll,
            DefaultBranchId: rows.FirstOrDefault(r => r.IsDefault && r.IsActive)?.BranchId);
    }


}
