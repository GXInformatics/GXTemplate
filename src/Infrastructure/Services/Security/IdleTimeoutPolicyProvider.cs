// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Claims;
using CleanArchitecture.Blazor.Domain.Identity;
using CleanArchitecture.Blazor.Infrastructure.Configurations;
using ZiggyCreatures.Caching.Fusion;

namespace CleanArchitecture.Blazor.Infrastructure.Services.Security;

/// <summary>
/// Reads the administered idle policy, caches it, and narrows it by the signed-in user's own
/// preference.
/// </summary>
/// <remarks>
/// <b>This runs on every authenticated HTTP request</b> - the cookie handler's principal validation
/// calls it - so both reads are behind a cache with explicit invalidation, not a short TTL. A stale
/// policy would mean an administrator's change not taking effect, which is precisely the behaviour
/// putting the setting on a screen was meant to provide; each save invalidates rather than waiting
/// for an expiry.
/// <para>
/// <b>Why the user preference is read from the database rather than carried as a claim.</b> A claim
/// would be free to read, and it is how <c>MustChangePassword</c> is done - but a claim only changes
/// when the authentication cookie is reissued, and reissuing it means
/// <c>SignInManager.RefreshSignInAsync</c>, which cannot run inside a Blazor circuit: the response
/// has already started and the cookie cannot be written. A user changing their own timeout on a
/// Blazor page would therefore see it take effect at their NEXT sign-in, which is not what the
/// screen says it does. A per-user cache entry, invalidated on save, is correct on the very next
/// request and costs a dictionary lookup.
/// </para>
/// </remarks>
public sealed class IdleTimeoutPolicyProvider : IIdleTimeoutPolicyProvider
{
    /// <summary>
    /// One key, because there is one row. A multi-tenant deployment keys this by tenant and changes
    /// nothing else - which is why every reader goes through this type rather than querying the
    /// table.
    /// </summary>
    public const string CacheKey = "security-policy:idle-timeout";

    /// <summary>Per-user preference cache key.</summary>
    public static string UserCacheKey(string userId) => $"security-policy:idle-timeout:user:{userId}";

    /// <summary>
    /// Long, and deliberately so: both caches are invalidated on save, so the duration is a backstop
    /// against a missed invalidation rather than the mechanism by which changes propagate.
    /// </summary>
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(12);

    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
    private readonly IFusionCache _cache;
    private readonly IIdleTimeoutSettings _settings;
    private readonly ILogger<IdleTimeoutPolicyProvider> _logger;

    public IdleTimeoutPolicyProvider(
        IDbContextFactory<ApplicationDbContext> dbContextFactory,
        IFusionCache cache,
        IIdleTimeoutSettings settings,
        ILogger<IdleTimeoutPolicyProvider> logger)
    {
        _dbContextFactory = dbContextFactory;
        _cache = cache;
        _settings = settings;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool Enabled => _settings.Enabled;

    /// <inheritdoc />
    public async Task<AdministeredIdleTimeoutPolicy> GetAdministeredAsync(
        CancellationToken cancellationToken = default)
    {
        if (!_settings.Enabled)
        {
            return new AdministeredIdleTimeoutPolicy(
                _settings.DefaultIdleTimeoutMinutes, _settings.DefaultCountdownSeconds);
        }

        var stored = await _cache.GetOrSetAsync(
            CacheKey,
            async ct => await LoadAdministeredAsync(ct),
            options => options.SetDuration(CacheDuration),
            cancellationToken).ConfigureAwait(false);

        // Clamped on the way OUT, not only on the way in. A row written before the bounds were
        // tightened - or edited around the screen - is still held to the deployment's limits, and
        // the authentication cookie was sized from those limits.
        return new AdministeredIdleTimeoutPolicy(
            ClampIdleMinutes(stored.IdleMinutes),
            ClampCountdown(stored.CountdownSeconds));
    }

    /// <inheritdoc />
    public async Task<IdleTimeoutPolicy> GetEffectiveAsync(
        ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        if (!_settings.Enabled)
        {
            return IdleTimeoutPolicy.Disabled;
        }

        var administered = await GetAdministeredAsync(cancellationToken).ConfigureAwait(false);
        var preference = await ReadPreferenceAsync(user, cancellationToken).ConfigureAwait(false);

        // min(), never max(): a user preference may only tighten. Clamped afterwards so a value
        // forced into the database below the floor still lands on the floor.
        var idleMinutes = preference is { } chosen
            ? Math.Min(chosen, administered.IdleMinutes)
            : administered.IdleMinutes;

        return new IdleTimeoutPolicy(
            Enabled: true,
            IdleMinutes: ClampIdleMinutes(idleMinutes),
            // Not user-adjustable: the countdown is how long the warning is shown, not how long a
            // session may sit idle. It is a warning, not a policy.
            CountdownSeconds: administered.CountdownSeconds);
    }

    /// <inheritdoc />
    public void Invalidate() => _cache.Remove(CacheKey);

    /// <inheritdoc />
    public void InvalidateUser(string userId) => _cache.Remove(UserCacheKey(userId));

    /// <summary>
    /// Reads the single policy row, seeding it from configuration the first time.
    /// </summary>
    /// <remarks>
    /// Seeding lazily here rather than in the database initializer keeps the feature working on a
    /// database provisioned before it existed, with no data migration - the first read after the
    /// upgrade writes the row.
    /// </remarks>
    private async Task<AdministeredIdleTimeoutPolicy> LoadAdministeredAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var row = await db.SecurityPolicies
            .OrderBy(p => p.Id)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (row is not null)
        {
            return new AdministeredIdleTimeoutPolicy(row.IdleTimeoutMinutes, row.CountdownSeconds);
        }

        var seeded = new SecurityPolicy
        {
            IdleTimeoutMinutes = _settings.DefaultIdleTimeoutMinutes,
            CountdownSeconds = _settings.DefaultCountdownSeconds
        };

        db.SecurityPolicies.Add(seeded);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Seeded the security policy from configuration: idle {Idle}m, countdown {Countdown}s.",
            seeded.IdleTimeoutMinutes, seeded.CountdownSeconds);

        return new AdministeredIdleTimeoutPolicy(seeded.IdleTimeoutMinutes, seeded.CountdownSeconds);
    }

    /// <summary>
    /// The user's chosen window, or null when they have not chosen one - or when the deployment has
    /// switched the choice off, in which case an existing preference is ignored rather than honoured.
    /// </summary>
    private async Task<int?> ReadPreferenceAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (!_settings.AllowUserOverride)
        {
            return null;
        }

        var userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            return null;
        }

        // Nullable<int> is not cacheable through FusionCache's generic path as cleanly as a sentinel,
        // so "no preference" is cached as 0 and mapped back here. Caching the absence matters: most
        // users never set one, and without it every request would be a database read for a null.
        var cached = await _cache.GetOrSetAsync(
            UserCacheKey(userId),
            async ct =>
            {
                await using var db = await _dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

                return await db.Users
                    .Where(u => u.Id == userId)
                    .Select(u => u.IdleTimeoutMinutes ?? 0)
                    .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            },
            options => options.SetDuration(CacheDuration),
            cancellationToken).ConfigureAwait(false);

        return cached > 0 ? cached : null;
    }

    private int ClampIdleMinutes(int minutes) =>
        Math.Clamp(minutes, _settings.MinIdleTimeoutMinutes, _settings.MaxIdleTimeoutMinutes);

    private int ClampCountdown(int seconds) => Math.Clamp(
        seconds, IdleTimeoutSettings.MinCountdownSeconds, IdleTimeoutSettings.MaxCountdownSeconds);
}
