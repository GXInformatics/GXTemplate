// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Http.Headers;
using System.Text;
using CleanArchitecture.Blazor.Infrastructure.Configurations;

namespace CleanArchitecture.Blazor.Infrastructure.Services.Mail;

/// <summary>
///     Sends mail through the Mailgun HTTP API.
/// </summary>
/// <remarks>
///     The HTTP API rather than SMTP, which is the GX standard and what this class replaced MailKit
///     to implement. Practically: no long-lived connection to keep alive, no TLS negotiation to get
///     wrong, and a status code and body to report when something fails instead of an SMTP
///     conversation to interpret.
///     <para>
///     <b>Nothing here throws.</b> Every failure - a 4xx from Mailgun, a refused connection, a
///     timeout, a template that will not parse - comes back as <see cref="Result"/>. A password
///     reset email that cannot be sent must not take down the page that triggered it.
///     </para>
/// </remarks>
public sealed class MailgunMailService(
    IHttpClientFactory httpClientFactory,
    MailSettings settings,
    MailTemplateRenderer renderer,
    ILogger<MailgunMailService> logger) : IMailService
{
    /// <summary>The named client configured in <c>DependencyInjection.AddMailServices</c>.</summary>
    public const string HttpClientName = "Mailgun";

    public async Task<Result> SendAsync(
        MailRecipient to,
        string subject,
        string template,
        object? model = null,
        CancellationToken cancellationToken = default)
    {
        // Checked here rather than at startup because mail is not fail-fast: an application with no
        // Mailgun credentials must still start and serve. MailStartupCheck is what says so out loud.
        if (string.IsNullOrWhiteSpace(settings.ApiKey) || string.IsNullOrWhiteSpace(settings.Domain))
        {
            logger.LogError(
                "Cannot send '{Template}' to {Email}: Mailgun is selected but Mail:Domain or the " +
                "Mail__ApiKey environment variable is not set.", template, to.Email);
            return Result.Failure("Mailgun is not configured; set Mail:Domain and the Mail__ApiKey environment variable.");
        }

        var rendered = await renderer.RenderAsync(to, template, model, cancellationToken);
        if (!rendered.Succeeded) return Result.Failure(rendered.Errors);

        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);

            using var content = new MultipartFormDataContent
            {
                { new StringContent(From()), "from" },
                { new StringContent(to.Email), "to" },
                { new StringContent(subject), "subject" },
                { new StringContent(rendered.Data!), "html" }
            };

            using var response = await client.PostAsync(settings.ApiEndpoint, content, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                logger.LogInformation("Sent '{Template}' to {Email}", template, to.Email);
                return Result.Success();
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError(
                "Mailgun refused '{Template}' for {Email}: {Status} {Body}",
                template, to.Email, (int)response.StatusCode, body);
            return Result.Failure($"Mailgun returned {(int)response.StatusCode}: {body}");
        }
        catch (Exception ex)
        {
            // Deliberately broad. A DNS failure, a refused connection, a timeout and a disposed
            // handler all arrive here as different types, and none of them is a reason to fault
            // whatever the caller was doing.
            logger.LogError(ex, "Could not send '{Template}' to {Email}", template, to.Email);
            return Result.Failure($"Could not send mail: {ex.Message}");
        }
    }

    private string From() =>
        string.IsNullOrWhiteSpace(settings.FromName)
            ? settings.FromAddress
            : $"{settings.FromName} <{settings.FromAddress}>";

    /// <summary>
    ///     The Authorization header Mailgun expects: HTTP basic auth, user <c>api</c>, password the
    ///     private key.
    /// </summary>
    public static AuthenticationHeaderValue BasicAuth(string apiKey) =>
        new("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"api:{apiKey}")));
}
