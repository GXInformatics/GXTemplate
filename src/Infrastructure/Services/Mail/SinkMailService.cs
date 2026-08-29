// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using CleanArchitecture.Blazor.Infrastructure.Configurations;

namespace CleanArchitecture.Blazor.Infrastructure.Services.Mail;

/// <summary>
///     Renders mail to a file on disk and sends nothing.
/// </summary>
/// <remarks>
///     The default in Development, and the reason a developer machine cannot email a real customer
///     by accident. That is not a hypothetical: the credentials that would let it happen are an
///     environment variable away, and "I was testing" is no comfort to whoever received it.
///     <para>
///     It renders through the same <see cref="MailTemplateRenderer"/> as the Mailgun transport, so
///     what lands in <c>./mail/</c> is byte-for-byte what would have been sent. A sink that rendered
///     differently would give false confidence, which is worse than no sink.
///     </para>
///     <para>
///     It writes a file AND logs a line, because those serve different people: the file is what a
///     developer opens to check that the layout and the tokens came out right, and the log line is
///     what tells them it happened at all.
///     </para>
/// </remarks>
public sealed class SinkMailService(
    MailSettings settings,
    MailTemplateRenderer renderer,
    ILogger<SinkMailService> logger) : IMailService
{
    public async Task<Result> SendAsync(
        MailRecipient to,
        string subject,
        string template,
        object? model = null,
        CancellationToken cancellationToken = default)
    {
        var rendered = await renderer.RenderAsync(to, template, model, cancellationToken);
        if (!rendered.Succeeded) return Result.Failure(rendered.Errors);

        try
        {
            var directory = Path.IsPathRooted(settings.SinkPath)
                ? settings.SinkPath
                : Path.Combine(AppContext.BaseDirectory, settings.SinkPath);

            System.IO.Directory.CreateDirectory(directory);

            // Sortable, unique, and readable at a glance in a directory listing.
            var fileName =
                $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{template}-{Sanitise(to.Email)}-{Guid.NewGuid():N}.html";
            var path = Path.Combine(directory, fileName);

            // A header comment, so a file opened days later still says who it was for. It is an HTML
            // comment rather than markup so the body renders in a browser exactly as it would in a
            // mail client.
            var contents =
                $"<!-- To: {to.Email}{Environment.NewLine}" +
                $"     Greeting: {to.Greeting}{Environment.NewLine}" +
                $"     Subject: {subject}{Environment.NewLine}" +
                $"     Template: {template}{Environment.NewLine}" +
                $"     Written: {DateTime.UtcNow:O} (UTC) -->{Environment.NewLine}" +
                rendered.Data;

            await File.WriteAllTextAsync(path, contents, cancellationToken);

            logger.LogInformation(
                "Mail sink wrote '{Template}' for {Email} (subject: {Subject}) to {Path}. No message was sent.",
                template, to.Email, subject, path);

            return Result.Success();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Mail sink could not write '{Template}' for {Email}", template, to.Email);
            return Result.Failure($"Could not write the mail sink file: {ex.Message}");
        }
    }

    /// <summary>Makes an address safe to put in a file name without losing which address it was.</summary>
    private static string Sanitise(string email) =>
        string.Concat(email.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == '@' ? '_' : c));
}
