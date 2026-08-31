// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Claims;

namespace CleanArchitecture.Blazor.Application.Common.Interfaces;

/// <summary>The idle policy in force for one session.</summary>
/// <param name="Enabled">False when the feature is off; the other values are then meaningless.</param>
/// <param name="IdleMinutes">How long the session may sit idle before the warning opens.</param>
/// <param name="CountdownSeconds">How long the warning counts down before signing the user out.</param>
public readonly record struct IdleTimeoutPolicy(bool Enabled, int IdleMinutes, int CountdownSeconds)
{
    /// <summary>Total time from last activity to sign-out.</summary>
    public TimeSpan TotalWindow =>
        TimeSpan.FromMinutes(IdleMinutes).Add(TimeSpan.FromSeconds(CountdownSeconds));

    /// <summary>The policy for a deployment with the feature turned off.</summary>
    public static readonly IdleTimeoutPolicy Disabled = new(false, 0, 0);
}

/// <summary>The policy an administrator has set, before any per-user tightening.</summary>
public readonly record struct AdministeredIdleTimeoutPolicy(int IdleMinutes, int CountdownSeconds);

/// <summary>
/// Reads the effective idle policy. The single source both the browser countdown and the server-side
/// principal check are driven from.
/// </summary>
/// <remarks>
/// <b>Read on every authenticated HTTP request</b>, by the cookie handler's principal validation, so
/// implementations must cache the administered policy and invalidate on save rather than querying
/// per request.
/// <para>
/// Reading the CURRENT policy on each request - rather than baking it into the cookie at sign-in -
/// is what makes the setting administrable: shortening the window takes effect on sessions already
/// in progress, which is the entire point of putting it on a screen.
/// </para>
/// </remarks>
public interface IIdleTimeoutPolicyProvider
{
    /// <summary>Whether the feature is switched on for this deployment at all.</summary>
    bool Enabled { get; }

    /// <summary>
    /// The administered policy, clamped into the configured bounds. Cached; safe to call per request.
    /// </summary>
    Task<AdministeredIdleTimeoutPolicy> GetAdministeredAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The administered policy narrowed by this user's own preference.
    /// </summary>
    /// <remarks>
    /// A user may only <b>shorten</b> their window, never lengthen it. An idle timeout is a control
    /// against unattended workstations; if a user could raise their own, the first person who finds
    /// it inconvenient sets it to eight hours and the control is gone - the same reasoning that keeps
    /// password policy out of a user profile. Tightening is both safe and genuinely useful: someone
    /// on a shared shop-floor terminal can choose five minutes.
    /// <para>
    /// The narrowing is applied HERE, at read time, and not only in the screen's validator - so a
    /// value forced into the database by other means is still clamped before it reaches enforcement.
    /// </para>
    /// </remarks>
    Task<IdleTimeoutPolicy> GetEffectiveAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default);

    /// <summary>Drops the cached administered policy. Call immediately after saving one.</summary>
    void Invalidate();

    /// <summary>Drops one user's cached preference. Call immediately after saving one.</summary>
    void InvalidateUser(string userId);
}
