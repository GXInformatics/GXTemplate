// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel.DataAnnotations;
using CleanArchitecture.Blazor.Application.Common.Interfaces;

namespace CleanArchitecture.Blazor.Infrastructure.Configurations;

/// <summary>
/// Bounds and bootstrap defaults for the idle timeout, bound from
/// <c>SecuritySettings:IdleTimeout</c>.
/// </summary>
/// <remarks>
/// Validated with <c>ValidateDataAnnotations().ValidateOnStart()</c>, so a deployment that
/// configures a policy the cookie cannot honour fails the process at startup naming the value,
/// rather than producing sessions that end at a time nobody intended.
/// </remarks>
public class IdleTimeoutSettings : IIdleTimeoutSettings, IValidatableObject
{
    /// <summary>The configuration section, as a path: <c>SecuritySettings:IdleTimeout</c>.</summary>
    public const string Key = "SecuritySettings:IdleTimeout";

    /// <summary>The hard ceiling on <see cref="MaxIdleTimeoutMinutes"/> - eight hours.</summary>
    /// <remarks>
    /// A ceiling on the ceiling: past this the cookie outlives a working day and the control stops
    /// being an idle timeout at all. A deployment that genuinely wants a longer session should turn
    /// the feature off deliberately rather than configure it into irrelevance.
    /// </remarks>
    public const int AbsoluteMaxIdleTimeoutMinutes = 480;

    /// <summary>The narrowest and widest countdown the warning dialog is usable at.</summary>
    public const int MinCountdownSeconds = 10;

    /// <summary>See <see cref="MinCountdownSeconds"/>.</summary>
    public const int MaxCountdownSeconds = 600;

    /// <inheritdoc />
    public bool Enabled { get; set; } = true;

    /// <inheritdoc />
    public int DefaultIdleTimeoutMinutes { get; set; } = 15;

    /// <inheritdoc />
    public int DefaultCountdownSeconds { get; set; } = 60;

    /// <inheritdoc />
    public int MinIdleTimeoutMinutes { get; set; } = 1;

    /// <inheritdoc />
    public int MaxIdleTimeoutMinutes { get; set; } = 120;

    /// <inheritdoc />
    public bool AllowUserOverride { get; set; } = true;

    /// <inheritdoc />
    public bool KeepAlivePingEnabled { get; set; } = true;

    /// <inheritdoc />
    public int CookieGraceMinutes { get; set; } = 2;

    /// <summary>
    /// The authentication cookie's absolute lifetime: the widest window an administrator could set,
    /// plus the countdown that follows it, plus grace.
    /// </summary>
    /// <remarks>
    /// Sized from the MAXIMUM rather than from the current policy because the cookie is issued once,
    /// at sign-in, and cannot be shortened afterwards. Tightening the policy is enforced instead by
    /// the principal check on each request, which reads the policy in force at that moment; the
    /// cookie's own expiry is only the outer bound.
    /// </remarks>
    public TimeSpan CookieLifetime => TimeSpan
        .FromMinutes(MaxIdleTimeoutMinutes + CookieGraceMinutes)
        .Add(TimeSpan.FromSeconds(DefaultCountdownSeconds));

    /// <summary>
    /// The cookie lifetime used when the feature is off: a plain fixed session, unrelated to any
    /// idle policy.
    /// </summary>
    public static readonly TimeSpan DisabledCookieLifetime = TimeSpan.FromHours(8);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        // Nothing below is enforced when the feature is off - the values are inert, and failing a
        // start over a setting that does nothing would be noise.
        if (!Enabled)
        {
            yield break;
        }

        if (MinIdleTimeoutMinutes < 1)
        {
            yield return new ValidationResult(
                $"{nameof(MinIdleTimeoutMinutes)} must be at least 1; found {MinIdleTimeoutMinutes}.",
                [nameof(MinIdleTimeoutMinutes)]);
        }

        if (MaxIdleTimeoutMinutes > AbsoluteMaxIdleTimeoutMinutes)
        {
            yield return new ValidationResult(
                $"{nameof(MaxIdleTimeoutMinutes)} must not exceed {AbsoluteMaxIdleTimeoutMinutes} " +
                $"(eight hours); found {MaxIdleTimeoutMinutes}.",
                [nameof(MaxIdleTimeoutMinutes)]);
        }

        if (MaxIdleTimeoutMinutes <= MinIdleTimeoutMinutes)
        {
            yield return new ValidationResult(
                $"{nameof(MaxIdleTimeoutMinutes)} ({MaxIdleTimeoutMinutes}) must be greater than " +
                $"{nameof(MinIdleTimeoutMinutes)} ({MinIdleTimeoutMinutes}).",
                [nameof(MaxIdleTimeoutMinutes)]);
        }

        if (DefaultIdleTimeoutMinutes < MinIdleTimeoutMinutes ||
            DefaultIdleTimeoutMinutes > MaxIdleTimeoutMinutes)
        {
            yield return new ValidationResult(
                $"{nameof(DefaultIdleTimeoutMinutes)} ({DefaultIdleTimeoutMinutes}) must lie within " +
                $"[{MinIdleTimeoutMinutes}, {MaxIdleTimeoutMinutes}].",
                [nameof(DefaultIdleTimeoutMinutes)]);
        }

        if (DefaultCountdownSeconds < MinCountdownSeconds || DefaultCountdownSeconds > MaxCountdownSeconds)
        {
            yield return new ValidationResult(
                $"{nameof(DefaultCountdownSeconds)} must lie within " +
                $"[{MinCountdownSeconds}, {MaxCountdownSeconds}]; found {DefaultCountdownSeconds}.",
                [nameof(DefaultCountdownSeconds)]);
        }

        // The countdown may equal the shortest window but never exceed it. Exceeding means the
        // warning would have to open before the user had finished going idle, which is incoherent -
        // and at the tightest permitted policy it is the shortest window that binds, not the
        // administered one.
        var shortestWindowSeconds = MinIdleTimeoutMinutes * 60;
        if (DefaultCountdownSeconds > shortestWindowSeconds)
        {
            yield return new ValidationResult(
                $"{nameof(DefaultCountdownSeconds)} ({DefaultCountdownSeconds}s) exceeds the shortest " +
                $"idle window {nameof(MinIdleTimeoutMinutes)} allows ({MinIdleTimeoutMinutes}m = " +
                $"{shortestWindowSeconds}s). Shorten the countdown or raise the minimum window.",
                [nameof(DefaultCountdownSeconds)]);
        }

        if (CookieGraceMinutes < 1)
        {
            yield return new ValidationResult(
                $"{nameof(CookieGraceMinutes)} must be at least 1; found {CookieGraceMinutes}.",
                [nameof(CookieGraceMinutes)]);
        }
    }
}
