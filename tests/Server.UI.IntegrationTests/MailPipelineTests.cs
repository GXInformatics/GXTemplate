#nullable enable
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Application.Common.ExceptionHandlers;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Application.Common.Models;
using CleanArchitecture.Blazor.Application.Features.Identity.Notifications.SendMail;
using CleanArchitecture.Blazor.Infrastructure.Configurations;
using FluentAssertions;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Server.UI.IntegrationTests;

/// <summary>
/// The mail pipeline in the running application: what it sends, how many times, and what it refuses
/// to serve.
/// </summary>
[TestFixture]
public class MailPipelineTests
{
    private GxWebApplicationFactory _factory = null!;

    [OneTimeSetUp]
    public async Task StartTheApplication()
    {
        _factory = new GxWebApplicationFactory();
        using var client = _factory.CreateNonRedirectingClient();
        await client.GetAsync("/");
    }

    [OneTimeTearDown]
    public void StopTheApplication() => _factory.Dispose();

    private int SinkFileCount() =>
        Directory.Exists(_factory.MailRoot) ? Directory.GetFiles(_factory.MailRoot).Length : 0;

    // ---------------------------------------------------------- the templates are not served

    /// <summary>
    /// Every path a mail template could plausibly be reachable at, if the Web SDK had picked the
    /// files up as static web assets.
    /// </summary>
    private static readonly string[] PlausibleStaticPaths =
    [
        "/Resources/EmailTemplates/welcome.sbn",
        "/resources/emailtemplates/welcome.sbn",
        "/Resources/EmailTemplates/recovery-password.sbn",
        "/Resources/EmailTemplates/user-activation.sbn",
        "/welcome.sbn",
        "/_content/CleanArchitecture.Blazor.Infrastructure/Resources/EmailTemplates/welcome.sbn"
    ];

    [TestCaseSource(nameof(PlausibleStaticPaths))]
    public async Task AMailTemplateIsNotServedOverHttp(string path)
    {
        // The reason <Content Remove> is in the csproj. A Content item under a Web SDK project can
        // become a static web asset, and the failure mode is not subtle: every email template - the
        // wording, the layout, the token names - published at a URL for anyone to fetch. Checking
        // the publish output for a wwwroot copy proves it is not THERE; this proves it is not
        // SERVED, which is the thing actually cared about.
        using var client = _factory.CreateNonRedirectingClient();

        var response = await client.GetAsync(path);

        response.StatusCode.Should().NotBe(HttpStatusCode.OK, $"{path} must not serve a mail template");

        if (response.Content.Headers.ContentLength > 0)
        {
            var body = await response.Content.ReadAsStringAsync();
            body.Should().NotContain("{{ user_name }}", $"{path} returned template source");
        }
    }

    // ---------------------------------------------------------- the administrator path sends once

    [Test]
    public async Task TheAdministratorMailCommandSendsExactlyOnce()
    {
        // Ruling 2's no-double-send requirement. The two Users.razor buttons used to
        // Mediator.Publish a notification; they now Mediator.Send this command instead - REPLACING
        // the publish, not adding to it. If the publish had been left in place alongside, this would
        // find two rendered messages rather than one.
        var before = SinkFileCount();

        using var scope = _factory.Services.CreateScope();

        // The HANDLER, not Mediator.Send. The command carries Permissions.Users.Edit and
        // AuthorizationBehaviour denies it outright when no principal is in context - correctly, and
        // asserted separately below. Going through the pipeline here would test deny-by-default a
        // second time instead of testing how many messages one command produces.
        var handler = scope.ServiceProvider.GetRequiredService<SendIdentityMailCommandHandler>();

        var result = await handler.Handle(new SendIdentityMailCommand
        {
            Kind = IdentityMailKind.Activation,
            Email = "double-send-probe@example.test",
            UserName = "probe",
            DisplayName = "Probe User",
            CallbackUrl = "https://example.test/confirm"
        }, CancellationToken.None);

        result.Succeeded.Should().BeTrue(result.ErrorMessage);
        SinkFileCount().Should().Be(before + 1, "one command must produce exactly one message");

        var written = Directory.GetFiles(_factory.MailRoot)
            .OrderByDescending(File.GetCreationTimeUtc)
            .First();
        var contents = await File.ReadAllTextAsync(written);

        contents.Should().Contain("double-send-probe@example.test");
        contents.Should().Contain("Hi Probe User,", "the display name is preferred over the user name");
        contents.Should().Contain("https://example.test/confirm");
    }

    [Test]
    public async Task TheAdministratorMailCommandIsGatedLikeEveryOtherRequest()
    {
        // Moving these two sites off the notification publisher put them onto the request pipeline,
        // which means they are now subject to deny-by-default - a gain, not a cost, and worth
        // asserting rather than assuming. An unauthenticated caller is refused.
        using var scope = _factory.Services.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var send = async () => await mediator.Send(new SendIdentityMailCommand
        {
            Kind = IdentityMailKind.PasswordReset,
            Email = "denied@example.test",
            UserName = "denied",
            CallbackUrl = "https://example.test/reset"
        });

        // Awaited: an un-awaited ThrowAsync assertion never runs and the test passes regardless.
        await send.Should().ThrowAsync<ForbiddenAccessException>();
    }

    [Test]
    public async Task AFailedSendIsReportedRatherThanClaimedAsSuccess()
    {
        // The dishonest snackbar, inverted. A template that does not exist stands in for any send
        // failure; what matters is that the command returns a failed Result for the page to act on,
        // instead of the silence the notification publisher used to provide.
        using var scope = _factory.Services.CreateScope();
        var mail = scope.ServiceProvider.GetRequiredService<IMailService>();

        var result = await mail.SendAsync(
            new MailRecipient("nobody@example.test"), "subject", "no-such-template-at-all");

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("was not found");
    }

    // ---------------------------------------------------------- the sink is what is running

    [Test]
    public void TheHarnessRunsOnTheSink_SoNoTestCanSendRealMail()
    {
        using var scope = _factory.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<MailSettings>().DeliveryMode
            .Should().Be(MailDelivery.Sink);
        scope.ServiceProvider.GetRequiredService<IMailService>()
            .Should().BeOfType<CleanArchitecture.Blazor.Infrastructure.Services.Mail.SinkMailService>();
    }

    [Test]
    public void TheShippedTemplatesReachTheRunningApplicationsOutput()
    {
        // The csproj wildcard, checked where it matters: the templates have to travel from
        // Infrastructure to whatever project actually hosts the application.
        foreach (var template in MailTemplates.All)
        {
            File.Exists(CleanArchitecture.Blazor.Infrastructure.Services.Mail.MailTemplateRenderer.PathFor(template))
                .Should().BeTrue($"{template} must be in the output directory");
        }
    }
}
#nullable restore
