using CleanArchitecture.Blazor.Application.Features.Identity.Notifications.ResetPassword;
using CleanArchitecture.Blazor.Application.Features.Identity.Notifications.UserActivation;

namespace CleanArchitecture.Blazor.Application.Features.Identity.Notifications.SendMail;

/// <summary>
///     Sends an identity email and reports whether it actually went.
/// </summary>
/// <remarks>
///     A request, not a notification, and that is the whole point of it existing.
///     <para>
///     The notification path is fire-and-forget by construction: <c>ChannelBasedNoWaitPublisher</c>
///     queues the handler and returns, then swallows anything the handler throws. That is right for
///     a visitor-facing flow - a registration must not fail because a welcome email did not send,
///     and telling an anonymous stranger that mail is broken leaks information with no upside to
///     them. It is wrong for the user-management page, where an administrator presses "resend
///     verification" and is shown "Verification email sent to X" whether or not anything was sent.
///     Those two messages differ by a support ticket.
///     </para>
///     <para>
///     So the administrator sites send through this instead, await the <see cref="Result"/>, and say
///     what really happened. Visitor-facing flows keep publishing notifications and are unchanged.
///     </para>
/// </remarks>
[RequestAuthorize(Policy = Permissions.Users.Edit)]
public class SendIdentityMailCommand : IRequest<Result>
{
    /// <summary>Which identity email to send.</summary>
    public required IdentityMailKind Kind { get; init; }

    public required string Email { get; init; }
    public required string UserName { get; init; }
    public string? DisplayName { get; init; }

    /// <summary>The activation or reset callback the recipient must follow.</summary>
    public required string CallbackUrl { get; init; }
}

/// <summary>The identity emails an administrator can trigger by hand.</summary>
public enum IdentityMailKind
{
    Activation = 0,
    PasswordReset = 1
}

public class SendIdentityMailCommandHandler(
    IStringLocalizer<SendIdentityMailCommandHandler> localizer,
    ILogger<SendIdentityMailCommandHandler> logger,
    IMailService mailService)
    : IRequestHandler<SendIdentityMailCommand, Result>
{
    public async ValueTask<Result> Handle(SendIdentityMailCommand request, CancellationToken cancellationToken)
    {
        var (subject, template, model) = request.Kind switch
        {
            IdentityMailKind.Activation => (
                localizer["Account Activation Required"].Value,
                MailTemplates.UserActivation,
                (object)new { ActivationUrl = request.CallbackUrl, Email = request.Email }),

            IdentityMailKind.PasswordReset => (
                localizer["Verify your recovery email"].Value,
                MailTemplates.RecoveryPassword,
                new { RequestUrl = request.CallbackUrl, Email = request.Email }),

            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Kind, "Unknown identity mail kind.")
        };

        // app_name, company, base_url and user_name are supplied centrally by MailTemplateRenderer.
        var result = await mailService.SendAsync(
            new MailRecipient(request.Email, request.DisplayName, request.UserName),
            subject,
            template,
            model,
            cancellationToken);

        if (result.Succeeded)
        {
            logger.LogInformation("Sent '{Template}' to {Email}.", template, request.Email);
        }
        else
        {
            logger.LogError(
                "Failed to send '{Template}' to {Email}: {Errors}", template, request.Email, result.ErrorMessage);
        }

        return result;
    }
}
