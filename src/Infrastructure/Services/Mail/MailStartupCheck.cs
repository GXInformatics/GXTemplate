// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using CleanArchitecture.Blazor.Infrastructure.Configurations;
using Microsoft.Extensions.Hosting;

namespace CleanArchitecture.Blazor.Infrastructure.Services.Mail;

/// <summary>
///     Says out loud, once, at startup, whether mail is configured and whether its templates are
///     usable.
/// </summary>
/// <remarks>
///     The same posture Pass 11C gave the log database, for the same reason: mail is best-effort, so
///     a missing Mailgun key must not stop the application serving - but best-effort must not mean
///     silent. Without this, a deployment with no credentials looks exactly like a deployment that
///     is working, until somebody asks why they never got their password reset.
///     <para>
///     <b>Templates are the exception.</b> A missing or corrupt template is not a configuration
///     choice, it is a broken deployment, and outside Development it fails the application. In
///     Development it logs an error and continues, so a developer part-way through editing a
///     template is not locked out of their own application.
///     </para>
/// </remarks>
public static class MailStartupCheck
{
    public const string SinkActiveMessage =
        "Mail delivery is set to the development sink: messages will be rendered to disk and NOT sent. " +
        "Set Mail:Delivery to Mailgun, with Mail:Domain and the Mail__ApiKey environment variable, to send for real.";

    public const string NotConfiguredMessage =
        "Mail delivery is set to Mailgun but it is not configured: Mail:Domain or the Mail__ApiKey " +
        "environment variable is missing. The application will run normally, but no email will be sent.";

    /// <summary>
    ///     Reports on mail, and fails the host if a template is unusable outside Development.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     A template is missing, mis-encoded or unparseable, and this is not a Development
    ///     environment.
    /// </exception>
    public static void CheckMail(this IHost host)
    {
        using var scope = host.Services.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Mail");
        var settings = scope.ServiceProvider.GetRequiredService<MailSettings>();
        var environment = scope.ServiceProvider.GetRequiredService<IHostEnvironment>();

        if (settings.DeliveryMode == MailDelivery.Sink)
        {
            logger.LogInformation(SinkActiveMessage);
        }
        else if (string.IsNullOrWhiteSpace(settings.Domain) || string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            logger.LogWarning(NotConfiguredMessage);
        }

        var problems = MailTemplateGuard.Check();
        if (problems.Count == 0) return;

        foreach (var problem in problems) logger.LogError("{Problem}", problem);

        if (environment.IsDevelopment())
        {
            logger.LogError(
                "{Count} mail template(s) are unusable. The application is continuing because this is " +
                "the Development environment; it would refuse to start anywhere else.", problems.Count);
            return;
        }

        throw new InvalidOperationException(
            $"{problems.Count} mail template(s) are unusable and this is not a Development environment: " +
            string.Join(" ", problems));
    }
}
