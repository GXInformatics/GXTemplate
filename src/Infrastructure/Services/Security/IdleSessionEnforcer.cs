// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;

namespace CleanArchitecture.Blazor.Infrastructure.Services.Security;

/// <summary>Routes the idle-timeout feature owns, named once so two layers cannot disagree.</summary>
public static class IdleTimeoutRoutes
{
    /// <summary>
    /// The keep-alive ping. Mapped by the UI, recognised here - <see cref="IdleSessionEnforcer"/>
    /// treats a request to this path as the user being active, and it is the only path that renews
    /// the idle window.
    /// </summary>
    public const string KeepAlive = "/account/keep-alive";

    /// <summary>Where a browser is sent after an idle sign-out, so the login page can explain.</summary>
    public const string LoginAfterIdle = "/account/login?reason=idle";
}

/// <summary>
/// The server-side half of the idle timeout: the part that is enforcement rather than user
/// experience.
/// </summary>
/// <remarks>
/// A JavaScript timer is not security. It can be disabled, paused on a breakpoint, or stopped when
/// the Blazor circuit drops; while the authentication cookie is still valid the user is still
/// authenticated, and a modal that covers the UI has signed nobody out. This type is what actually
/// ends the session, and it runs inside the cookie handler's principal validation - on every
/// authenticated HTTP request.
/// <para>
/// It reads the policy in force <b>at that moment</b> rather than one baked into the cookie at
/// sign-in, so an administrator tightening the window takes effect on sessions already open. The
/// cookie's own <c>ExpireTimeSpan</c> is only the outer bound, sized from the widest window any
/// policy could reach because a cookie cannot be shortened after it is issued.
/// </para>
/// </remarks>
public sealed class IdleSessionEnforcer
{
    /// <summary>
    /// Where the last-activity stamp lives: the ticket's own properties.
    /// </summary>
    /// <remarks>
    /// In the ticket rather than in a table, so the check costs no database round-trip per request.
    /// This deployment stores tickets server-side (<c>MemoryCacheTicketStore</c>), so the value never
    /// travels to the browser and cannot be tampered with there; with a cookie-borne ticket it would
    /// still be inside the protected payload.
    /// </remarks>
    public const string LastActivityKey = "gx:idle:lastActivity";

    private readonly IIdleTimeoutPolicyProvider _policy;
    private readonly ILogger<IdleSessionEnforcer> _logger;

    public IdleSessionEnforcer(IIdleTimeoutPolicyProvider policy, ILogger<IdleSessionEnforcer> logger)
    {
        _policy = policy;
        _logger = logger;
    }

    /// <summary>
    /// Decides whether the session may continue, and stamps activity when the request is a
    /// keep-alive ping.
    /// </summary>
    /// <returns>False when the session has been idle past its effective window.</returns>
    public async Task<bool> IsStillValidAsync(CookieValidatePrincipalContext context)
    {
        if (!_policy.Enabled || context.Principal?.Identity?.IsAuthenticated != true)
        {
            return true;
        }

        var policy = await _policy.GetEffectiveAsync(context.Principal, context.HttpContext.RequestAborted)
            .ConfigureAwait(false);

        if (!policy.Enabled)
        {
            return true;
        }

        var now = DateTimeOffset.UtcNow;
        var lastActivity = ReadLastActivity(context) ?? context.Properties.IssuedUtc ?? now;

        if (now - lastActivity > policy.TotalWindow)
        {
            _logger.LogInformation(
                "Signing {User} out: idle for {IdleMinutes:F1} minutes, past the effective window of " +
                "{Window} (idle {Policy}m + countdown {Countdown}s).",
                context.Principal.Identity?.Name,
                (now - lastActivity).TotalMinutes,
                policy.TotalWindow,
                policy.IdleMinutes,
                policy.CountdownSeconds);

            return false;
        }

        // Only the keep-alive ping renews the window. Every other authenticated request - a static
        // asset, a framework callback, the browser reconnecting a circuit - must NOT count as the
        // user being present, or an unattended workstation would keep itself signed in.
        if (IsKeepAlive(context.HttpContext.Request))
        {
            context.Properties.Items[LastActivityKey] =
                now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);

            // Renews the stored ticket in place. Cheaper than re-issuing through SignInAsync, which
            // with a server-side ticket store would rotate the session key on every ping.
            context.ShouldRenew = true;
        }

        return true;
    }

    private static bool IsKeepAlive(HttpRequest request) =>
        request.Path.Equals(IdleTimeoutRoutes.KeepAlive, StringComparison.OrdinalIgnoreCase);

    private static DateTimeOffset? ReadLastActivity(CookieValidatePrincipalContext context)
    {
        if (!context.Properties.Items.TryGetValue(LastActivityKey, out var raw) ||
            !long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epochMs))
        {
            // Absent on a freshly issued ticket, which is the common case for a first request after
            // sign-in. The caller falls back to the ticket's issue time, so a session is never
            // treated as having been idle since the epoch.
            return null;
        }

        return DateTimeOffset.FromUnixTimeMilliseconds(epochMs);
    }
}
