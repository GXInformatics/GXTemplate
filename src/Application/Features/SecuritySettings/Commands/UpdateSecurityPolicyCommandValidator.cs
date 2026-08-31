// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace CleanArchitecture.Blazor.Application.Features.SecuritySettings.Commands;

/// <summary>
/// Holds an administrator to the deployment's bounds.
/// </summary>
/// <remarks>
/// The bounds come from configuration rather than from constants here, because the authentication
/// cookie's lifetime is derived from the same values: a policy outside them would be one the cookie
/// cannot honour, producing sessions that end at a time nobody chose. The provider clamps on read as
/// well - this validator is what makes the refusal visible instead of silent.
/// </remarks>
public class UpdateSecurityPolicyCommandValidator : AbstractValidator<UpdateSecurityPolicyCommand>
{
    public UpdateSecurityPolicyCommandValidator(IIdleTimeoutSettings settings)
    {
        RuleFor(v => v.IdleTimeoutMinutes)
            .InclusiveBetween(settings.MinIdleTimeoutMinutes, settings.MaxIdleTimeoutMinutes)
            .WithMessage(_ =>
                $"The idle timeout must be between {settings.MinIdleTimeoutMinutes} and " +
                $"{settings.MaxIdleTimeoutMinutes} minutes.");

        RuleFor(v => v.CountdownSeconds)
            .InclusiveBetween(10, 600)
            .WithMessage("The countdown must be between 10 and 600 seconds.");

        // The warning cannot be longer than the wait that precedes it: the countdown opens AFTER the
        // idle window elapses, so a countdown longer than the window means most of a session's
        // "idle" time is spent showing a dialog.
        RuleFor(v => v)
            .Must(v => v.CountdownSeconds <= v.IdleTimeoutMinutes * 60)
            .WithMessage(v =>
                $"The countdown ({v.CountdownSeconds}s) cannot exceed the idle timeout " +
                $"({v.IdleTimeoutMinutes}m = {v.IdleTimeoutMinutes * 60}s).")
            .OverridePropertyName(nameof(UpdateSecurityPolicyCommand.CountdownSeconds));
    }
}
