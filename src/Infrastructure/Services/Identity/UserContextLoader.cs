using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Domain.Identity;
using ZiggyCreatures.Caching.Fusion;

namespace CleanArchitecture.Blazor.Infrastructure.Services.Identity;

/// <summary>
/// Implementation of IUserContextLoader that uses UserManager to load user context from ClaimsPrincipal.
/// </summary>
public class UserContextLoader : IUserContextLoader
{
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
                    var allowedTenantIds = await userManager.Users.Where(x => x.Id == user.Id)
                        .Include(x => x.TenantUsers).ThenInclude(tu => tu.Tenant)
                        .SelectMany(x => x.TenantUsers.Where(tu => tu.Tenant != null).Select(tu => tu.Tenant!.Id))
                        .ToListAsync(ct);
                    var roles = await userManager.GetRolesAsync(user);

                    return new UserContext(
                        UserId: user.Id,
                        UserName: user.UserName ?? string.Empty,
                        DisplayName: user.DisplayName,
                        TenantId: user.TenantId,
                        AllowedTenantIds: allowedTenantIds.AsReadOnly(),
                        Email: user.Email,
                        Roles: roles.ToList().AsReadOnly(),
                        ProfilePictureDataUrl: user.ProfilePictureDataUrl,
                        SuperiorId: user.SuperiorId
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
            cancellationToken
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


}
