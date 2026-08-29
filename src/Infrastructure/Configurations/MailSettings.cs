// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel.DataAnnotations;

namespace CleanArchitecture.Blazor.Infrastructure.Configurations;

/// <summary>
///     Which Mailgun region the sending domain lives in.
/// </summary>
/// <remarks>
///     An enum rather than a free string so an unknown region is a bind-time failure naming the
///     value, not a 404 from Mailgun on the first send.
/// </remarks>
public enum MailRegion
{
    US = 0,
    EU = 1
}

/// <summary>
///     How mail leaves the application.
/// </summary>
public enum MailDelivery
{
    /// <summary>
    ///     Not stated. Resolved at registration: the sink in Development, Mailgun everywhere else.
    /// </summary>
    Unspecified = 0,

    /// <summary>Rendered to a file on disk and logged. No network call is made.</summary>
    Sink = 1,

    /// <summary>Sent through the Mailgun HTTP API.</summary>
    Mailgun = 2
}

/// <summary>
///     Configuration wrapper for the mail section.
/// </summary>
/// <remarks>
///     One class, not three. Earlier GX projects split this across several settings objects and
///     stored the finished endpoint URL beside the domain, which lets the two disagree - a bug that
///     only shows up as mail silently going to the wrong place. Here the endpoint is
///     <see cref="ApiEndpoint">composed</see> from region and domain, so there is one source of
///     truth for each fact.
///     <para>
///     <b><see cref="ApiKey"/> is never written to appsettings.json.</b> It is read from the
///     environment as <c>Mail__ApiKey</c>, like any other secret. Everything else here is
///     environment-true rather than secret - which domain, which from-address, which region - and
///     belongs in committed configuration where a reviewer can see it.
///     </para>
/// </remarks>
public class MailSettings : IValidatableObject
{
    /// <summary>
    ///     Mail key constraint. The section is named "Mail", so the validation messages below quote
    ///     "Mail:Region" rather than the class name - it is what an operator actually sets.
    /// </summary>
    public const string Key = "Mail";

    /// <summary>The Mailgun region the <see cref="Domain"/> is provisioned in.</summary>
    public MailRegion Region { get; set; } = MailRegion.US;

    /// <summary>The Mailgun sending domain, for example <c>mg.example.com</c>.</summary>
    public string Domain { get; set; } = string.Empty;

    /// <summary>The address mail is sent from.</summary>
    /// <remarks>
    ///     example.com is IANA-reserved and can never route, so a deployment that configures a
    ///     domain but forgets this sends mail that is self-evidently unconfigured rather than mail
    ///     claiming to come from a third party's real address. Inherited from the SMTP settings this
    ///     class replaces, where the same reasoning applied.
    /// </remarks>
    public string FromAddress { get; set; } = "noreply@example.com";

    /// <summary>The display name shown beside <see cref="FromAddress"/>.</summary>
    public string FromName { get; set; } = string.Empty;

    /// <summary>
    ///     How mail leaves the application, as configured. Empty, "Sink" or "Mailgun".
    /// </summary>
    /// <remarks>
    ///     A string rather than the enum, deliberately. Binding an enum directly makes
    ///     <c>"Delivery": ""</c> - which is what the shipped appsettings.json carries and what the
    ///     README tells an operator to leave - a hard binder failure at startup:
    ///     "Failed to convert configuration value '' ... is not a valid value for MailDelivery",
    ///     thrown from inside the options system before any validation can produce a decent message.
    ///     Taking the string and resolving it ourselves means an empty value is the documented
    ///     default and a misspelled one is a validation error naming the supported set.
    /// </remarks>
    public string Delivery { get; set; } = string.Empty;

    /// <summary>
    ///     How mail actually leaves the application, resolved from <see cref="Delivery"/> and the
    ///     hosting environment during options post-configuration.
    /// </summary>
    public MailDelivery DeliveryMode { get; set; } = MailDelivery.Unspecified;

    /// <summary>Parses <see cref="Delivery"/>, or null when it is empty or unrecognised.</summary>
    public MailDelivery? ParseDelivery() =>
        Enum.TryParse<MailDelivery>(Delivery, ignoreCase: true, out var parsed)
        && parsed != MailDelivery.Unspecified
            ? parsed
            : null;

    /// <summary>
    ///     The Mailgun private API key, from the environment only - <c>Mail__ApiKey</c>.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Directory the <see cref="MailDelivery.Sink"/> writes rendered messages into.</summary>
    public string SinkPath { get; set; } = "mail";

    /// <summary>
    ///     How long to wait on Mailgun before giving up.
    /// </summary>
    /// <remarks>
    ///     Ten seconds, not <see cref="System.Net.Http.HttpClient"/>'s 100-second default. An
    ///     administrator pressing "resend verification" waits on this synchronously, and a minute
    ///     and a half of spinner is indistinguishable from a hung application. Mail is best-effort:
    ///     failing quickly and saying so beats succeeding eventually and saying nothing.
    /// </remarks>
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>
    ///     The Mailgun messages endpoint, composed rather than stored.
    /// </summary>
    public string ApiEndpoint =>
        $"https://api{(Region == MailRegion.EU ? ".eu" : string.Empty)}.mailgun.net/v3/{Domain}/messages";

    /// <summary>
    ///     Validates the entered configuration.
    /// </summary>
    /// <remarks>
    ///     <b>Absent mail configuration is deliberately not an error.</b> The application must serve
    ///     without mail - a registration page that fails to start because nobody set a Mailgun
    ///     domain is worse than one that cannot send a welcome email. Misconfiguration is reported
    ///     loudly at startup by <c>MailStartupCheck</c> instead, in the shape Pass 11C established
    ///     for the log database.
    ///     <para>
    ///     What IS fatal here is the narrow set an operator cannot have chosen on purpose: a region
    ///     outside the known set, a from-address that is not an address, and a nonsensical timeout.
    ///     Those are typos in committed configuration, cheap to catch, and they would otherwise
    ///     surface as a 404 or an exception on somebody's first password reset.
    ///     </para>
    /// </remarks>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Enum.IsDefined(Region))
            yield return new ValidationResult(
                $"{Key}:{nameof(Region)} '{Region}' is not supported; supported regions are: " +
                $"{string.Join(", ", Enum.GetNames<MailRegion>())}",
                new[] { nameof(Region) });

        // Empty is the documented default and means "decide from the hosting environment". A value
        // that is neither empty nor a known mode is a typo, and saying so beats silently sending
        // through a transport nobody chose.
        if (!string.IsNullOrWhiteSpace(Delivery) && ParseDelivery() is null)
        {
            yield return new ValidationResult(
                $"{Key}:{nameof(Delivery)} '{Delivery}' is not supported; leave it empty to decide " +
                $"from the environment, or use one of: {nameof(MailDelivery.Sink)}, {nameof(MailDelivery.Mailgun)}",
                new[] { nameof(Delivery) });
        }

        // Not "is it configured" - it has a default - but "is what is there an address at all".
        if (!string.IsNullOrWhiteSpace(FromAddress) &&
            !new EmailAddressAttribute().IsValid(FromAddress))
        {
            yield return new ValidationResult(
                $"{Key}:{nameof(FromAddress)} '{FromAddress}' is not a valid email address",
                new[] { nameof(FromAddress) });
        }

        if (TimeoutSeconds <= 0)
            yield return new ValidationResult(
                $"{Key}:{nameof(TimeoutSeconds)} must be greater than zero",
                new[] { nameof(TimeoutSeconds) });
    }
}
