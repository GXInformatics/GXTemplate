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
            // THE ADDRESS IS DELIBERATELY LOGGED. Considered in Pass 22 §D and kept, so it is not
            // removed later as an apparent enumeration leak by someone who has not seen this
            // decision. It looks like one and is not: this line records mail this system actually
            // SENT, which is an operational record every mail-sending component should keep, and it
            // is written only on the path where a message really went out. Forgot.razor is where
            // the enumeration risk lived, and it no longer logs the address an anonymous stranger
            // typed - the two are different acts and only one of them is attacker-controlled.
            // Reading this requires Permissions.Logs.View, and a holder of that is already trusted
            // with far more than the list of addresses that were sent a reset.
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
