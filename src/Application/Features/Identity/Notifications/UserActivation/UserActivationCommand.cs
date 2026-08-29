namespace CleanArchitecture.Blazor.Application.Features.Identity.Notifications.UserActivation;

public record UserActivationNotification(
    string ActivationUrl,
    string Email,
    string UserId,
    string UserName,
    string? DisplayName = null) : INotification;

public class UserActivationNotificationHandler : INotificationHandler<UserActivationNotification>
{
    private readonly IStringLocalizer<UserActivationNotificationHandler> _localizer;
    private readonly ILogger<UserActivationNotificationHandler> _logger;
    private readonly IMailService _mailService;

    public UserActivationNotificationHandler(
        ILogger<UserActivationNotificationHandler> logger,
        IStringLocalizer<UserActivationNotificationHandler> localizer,
        IMailService mailService)
    {
        _logger = logger;
        _localizer = localizer;
        _mailService = mailService;
    }

    public async ValueTask Handle(UserActivationNotification notification, CancellationToken cancellationToken)
    {
        // app_name, company, base_url and user_name arrive centrally; see MailTemplateRenderer.
        var result = await _mailService.SendAsync(
            new MailRecipient(notification.Email, notification.DisplayName, notification.UserName),
            _localizer["Account Activation Required"],
            MailTemplates.UserActivation,
            new { ActivationUrl = notification.ActivationUrl, Email = notification.Email },
            cancellationToken);

        // Last place a failure can be observed: the publisher swallows handler exceptions.
        if (result.Succeeded)
        {
            _logger.LogInformation(
                "Activation email sent to {Email}, Activation Callback URL: {ActivationUrl}.",
                notification.Email, notification.ActivationUrl);
        }
        else
        {
            _logger.LogError(
                "Failed to send '{Template}' to {Email}: {Errors}",
                MailTemplates.UserActivation, notification.Email, result.ErrorMessage);
        }
    }
}
