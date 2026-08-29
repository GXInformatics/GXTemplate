namespace CleanArchitecture.Blazor.Application.Features.Identity.Notifications.SendWelcome;

public record SendWelcomeNotification(string LoginUrl, string Email, string UserName, string? DisplayName = null)
    : INotification;

public class SendWelcomeNotificationHandler : INotificationHandler<SendWelcomeNotification>
{
    private readonly IStringLocalizer<SendWelcomeNotificationHandler> _localizer;
    private readonly ILogger<SendWelcomeNotificationHandler> _logger;
    private readonly IMailService _mailService;
    private readonly IApplicationSettings _settings;

    public SendWelcomeNotificationHandler(
        IStringLocalizer<SendWelcomeNotificationHandler> localizer,
        ILogger<SendWelcomeNotificationHandler> logger,
        IMailService mailService,
        IApplicationSettings settings)
    {
        _localizer = localizer;
        _logger = logger;
        _mailService = mailService;
        _settings = settings;
    }

    public async ValueTask Handle(SendWelcomeNotification notification, CancellationToken cancellationToken)
    {
        // IApplicationSettings survives here only for the SUBJECT line, which is not a template
        // token and so is not covered by the central injection. The body's app_name, company,
        // base_url and user_name all arrive from MailTemplateRenderer.
        var subject = string.Format(_localizer["Welcome to {0}"], _settings.AppName);

        var result = await _mailService.SendAsync(
            new MailRecipient(notification.Email, notification.DisplayName, notification.UserName),
            subject,
            MailTemplates.Welcome,
            new { LoginUrl = notification.LoginUrl, Email = notification.Email },
            cancellationToken);

        // Last place a failure can be observed: the publisher swallows handler exceptions.
        if (result.Succeeded)
        {
            _logger.LogInformation("Welcome email sent to {Email}.", notification.Email);
        }
        else
        {
            _logger.LogError(
                "Failed to send '{Template}' to {Email}: {Errors}",
                MailTemplates.Welcome, notification.Email, result.ErrorMessage);
        }
    }
}
