namespace CleanArchitecture.Blazor.Application.Features.Identity.Notifications.ResetPassword;

public record ResetPasswordNotification(string RequestUrl, string Email, string UserName, string? DisplayName = null)
    : INotification;

public class ResetPasswordNotificationHandler : INotificationHandler<ResetPasswordNotification>
{
    private readonly IStringLocalizer<ResetPasswordNotificationHandler> _localizer;
    private readonly ILogger<ResetPasswordNotificationHandler> _logger;
    private readonly IMailService _mailService;

    public ResetPasswordNotificationHandler(
        IStringLocalizer<ResetPasswordNotificationHandler> localizer,
        ILogger<ResetPasswordNotificationHandler> logger,
        IMailService mailService)
    {
        _localizer = localizer;
        _logger = logger;
        _mailService = mailService;
    }

    public async ValueTask Handle(ResetPasswordNotification notification, CancellationToken cancellationToken)
    {
        // Only the tokens this template alone needs. app_name, company, base_url and user_name are
        // supplied centrally by MailTemplateRenderer - assembling them here, in every handler, is
        // exactly how they come to be missing from the one handler somebody writes next.
        var result = await _mailService.SendAsync(
            new MailRecipient(notification.Email, notification.DisplayName, notification.UserName),
            _localizer["Verify your recovery email"],
            MailTemplates.RecoveryPassword,
            new { RequestUrl = notification.RequestUrl, Email = notification.Email },
            cancellationToken);

        // The result is inspected here because here is the last place it can be. This is a
        // notification handler, and ChannelBasedNoWaitPublisher runs it on a background channel and
        // swallows anything it throws - so a failure that is not logged now is not recorded at all.
        if (result.Succeeded)
        {
            _logger.LogInformation("Password reset email sent to {Email}.", notification.Email);
        }
        else
        {
            _logger.LogError(
                "Failed to send '{Template}' to {Email}: {Errors}",
                MailTemplates.RecoveryPassword, notification.Email, result.ErrorMessage);
        }
    }
}
