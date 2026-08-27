// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel.DataAnnotations;

namespace CleanArchitecture.Blazor.Infrastructure.Configurations;

/// <summary>
///     Configuration wrapper for the app configuration section
/// </summary>
public class AppConfigurationSettings : IApplicationSettings, IValidatableObject
{
    /// <summary>
    ///     App configuration key constraint
    /// </summary>
    public const string Key = nameof(AppConfigurationSettings);
    /// <summary>
    ///     Undocumented
    /// </summary>
    public string ApplicationUrl { get; set; } = string.Empty;

    /// <summary>
    ///     The name of the company
    /// </summary>
    public string Company { get; set; } = "Company";

    /// <summary>
    ///     Copyright watermark
    /// </summary>
    public string Copyright { get; set; } = "@2023 Copyright";

    /// <summary>
    ///     Current application version
    /// </summary>
    public string Version { get; set; } = "1.3.0";

    /// <summary>
    ///     Application framework
    /// </summary>

    public string App { get; set; } = "Blazor";

    /// <summary>
    ///     The application name / title
    /// </summary>
    public string AppName { get; set; } = "GX Application";

    /// <inheritdoc />
    public string DefaultTimeZone { get; set; } = "UTC";

    /// <inheritdoc />
    public bool AllowSelfRegistration { get; set; } = true;

    /// <summary>
    ///     Validates the entered configuration
    /// </summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrWhiteSpace(DefaultTimeZone))
        {
            yield return new ValidationResult(
                $"{Key}.{nameof(DefaultTimeZone)} is not configured; use a time zone id such as 'UTC' or 'Africa/Lagos'",
                new[] { nameof(DefaultTimeZone) });
            yield break;
        }

        // Resolved here, once, at startup. Every provisioning path hands this id to
        // TimeZoneInfo.FindSystemTimeZoneById eventually, and that throws - so a typo would
        // otherwise surface as an exception on somebody's first registration.
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(DefaultTimeZone, out _))
        {
            yield return new ValidationResult(
                $"{Key}.{nameof(DefaultTimeZone)} '{DefaultTimeZone}' is not a time zone this system recognises; " +
                "use an IANA id such as 'UTC', 'Africa/Lagos' or 'Europe/London'",
                new[] { nameof(DefaultTimeZone) });
        }
    }
}
