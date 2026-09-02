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
            // The ACTIVATION URL IS NOT LOGGED, and must not be added back as a debugging
            // convenience. It carries userId plus the base64url confirmation token, and this
            // logger reaches the database sink - SerilogExtensions excludes only two
            // property-marked categories and applies no level filter - so the token would be
            // readable from /system/logs by any Permissions.Logs.View holder. Anyone who could
            // read it could confirm an address they do not control. The address alone is enough
            // to answer "did the mail go out?", which is what this line is for.
            _logger.LogInformation("Activation email sent to {Email}.", notification.Email);
        }
        else
        {
            _logger.LogError(
                "Failed to send '{Template}' to {Email}: {Errors}",
                MailTemplates.UserActivation, notification.Email, result.ErrorMessage);
        }
    }
}
