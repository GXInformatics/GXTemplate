using System.Net;
using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Application.Common.Models;
using CleanArchitecture.Blazor.Infrastructure.Configurations;
using CleanArchitecture.Blazor.Infrastructure.Services.Mail;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace CleanArchitecture.Blazor.Infrastructure.UnitTests.Mail;

/// <summary>
/// The two transports: that the sink sends nothing, and that Mailgun reports rather than throws.
/// </summary>
[Collection(TemplateFileCollection.Name)]
public class MailDeliveryTests : IDisposable
{
    private readonly string _sinkDirectory =
        Path.Combine(Path.GetTempPath(), "gx-mail-sink-tests", Guid.NewGuid().ToString("N"));

    private readonly string _templatesDirectory =
        Path.Combine(AppContext.BaseDirectory, MailTemplates.Directory);

    private readonly List<string> _written = [];

    public MailDeliveryTests() => Directory.CreateDirectory(_sinkDirectory);

    public void Dispose()
    {
        foreach (var path in _written)
        {
            try { File.Delete(path); } catch (IOException) { }
        }

        try { Directory.Delete(_sinkDirectory, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private string WriteTemplate(string body)
    {
        var name = "delivery-" + Guid.NewGuid().ToString("N");
        var path = Path.Combine(_templatesDirectory, name + MailTemplates.Extension);
        File.WriteAllText(path, body);
        _written.Add(path);
        return name;
    }

    private static MailTemplateRenderer Renderer()
    {
        var settings = new Mock<IApplicationSettings>();
        settings.SetupGet(s => s.AppName).Returns("Test App");
        settings.SetupGet(s => s.Company).Returns("Test Company");
        settings.SetupGet(s => s.ApplicationUrl).Returns("https://example.test");
        return new MailTemplateRenderer(settings.Object, NullLogger<MailTemplateRenderer>.Instance);
    }

    /// <summary>An HTTP handler that fails the test if anything ever reaches it.</summary>
    private sealed class ForbiddenHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            throw new InvalidOperationException(
                $"The mail sink made an HTTP request to {request.RequestUri}. It must never make one.");
        }
    }

    // ------------------------------------------------------------------ the dev sink

    [Fact]
    public async Task TheSinkWritesAFileAndMakesNoHttpRequest()
    {
        // The ratified guarantee: a developer machine cannot send real mail. Proving it needs a
        // handler that would fail the test on ANY request, not merely the absence of one we looked
        // for - absence of evidence is not what is wanted here.
        var forbidden = new ForbiddenHandler();
        using var client = new HttpClient(forbidden);

        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(client);

        var template = WriteTemplate("<p>Hi {{ user_name }}, from {{ app_name }}.</p>");
        var settings = new MailSettings { SinkPath = _sinkDirectory, DeliveryMode = MailDelivery.Sink };

        var sink = new SinkMailService(settings, Renderer(), NullLogger<SinkMailService>.Instance);

        var result = await sink.SendAsync(
            new MailRecipient("ada@example.test", "Ada Lovelace"), "A subject", template);

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal(0, forbidden.Requests);

        var files = Directory.GetFiles(_sinkDirectory);
        var file = Assert.Single(files);

        var contents = await File.ReadAllTextAsync(file);
        Assert.Contains("Hi Ada Lovelace, from Test App.", contents);
        Assert.Contains("To: ada@example.test", contents);
        Assert.Contains("Subject: A subject", contents);
    }

    [Fact]
    public async Task TheSinkRendersThroughTheSameRendererAsMailgun_SoTokensAreInjectedThere_Too()
    {
        // A sink that rendered differently from the real transport would give false confidence,
        // which is worse than no sink: the developer checks the file, it looks right, and production
        // sends something else.
        var template = WriteTemplate("[{{ user_name }}|{{ app_name }}|{{ company }}|{{ base_url }}]");
        var settings = new MailSettings { SinkPath = _sinkDirectory, DeliveryMode = MailDelivery.Sink };

        var sink = new SinkMailService(settings, Renderer(), NullLogger<SinkMailService>.Instance);

        await sink.SendAsync(new MailRecipient("ada@example.test"), "s", template);

        var contents = await File.ReadAllTextAsync(Directory.GetFiles(_sinkDirectory).Single());
        Assert.Contains("[there|Test App|Test Company|https://example.test]", contents);
    }

    [Fact]
    public async Task TheSinkReportsAMissingTemplateAsAFailure()
    {
        var settings = new MailSettings { SinkPath = _sinkDirectory, DeliveryMode = MailDelivery.Sink };
        var sink = new SinkMailService(settings, Renderer(), NullLogger<SinkMailService>.Instance);

        var result = await sink.SendAsync(
            new MailRecipient("ada@example.test"), "s", "no-such-" + Guid.NewGuid().ToString("N"));

        Assert.False(result.Succeeded);
        Assert.Empty(Directory.GetFiles(_sinkDirectory));
    }

    // ------------------------------------------------------------------ Mailgun reports, never throws

    private sealed class StubHandler(HttpStatusCode status, string body = "") : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("the network is down");
    }

    private MailgunMailService Mailgun(HttpMessageHandler handler, MailSettings? settings = null)
    {
        var client = new HttpClient(handler);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(client);

        return new MailgunMailService(
            factory.Object,
            settings ?? new MailSettings { Domain = "mg.example.test", ApiKey = "key-test" },
            Renderer(),
            NullLogger<MailgunMailService>.Instance);
    }

    [Fact]
    public async Task AnAcceptedSendIsASuccessfulResult()
    {
        var template = WriteTemplate("<p>{{ user_name }}</p>");

        var result = await Mailgun(new StubHandler(HttpStatusCode.OK))
            .SendAsync(new MailRecipient("ada@example.test"), "s", template);

        Assert.True(result.Succeeded, result.ErrorMessage);
    }

    [Fact]
    public async Task ARefusedSendIsAFailedResult_NotAnException()
    {
        var template = WriteTemplate("<p>{{ user_name }}</p>");

        var result = await Mailgun(new StubHandler(HttpStatusCode.Unauthorized, "bad key"))
            .SendAsync(new MailRecipient("ada@example.test"), "s", template);

        Assert.False(result.Succeeded);
        Assert.Contains("401", result.ErrorMessage);
    }

    [Fact]
    public async Task ANetworkFailureIsAFailedResult_NotAnException()
    {
        // A password reset that cannot be sent must not fault the page that triggered it.
        var template = WriteTemplate("<p>{{ user_name }}</p>");

        var result = await Mailgun(new ThrowingHandler())
            .SendAsync(new MailRecipient("ada@example.test"), "s", template);

        Assert.False(result.Succeeded);
        Assert.Contains("the network is down", result.ErrorMessage);
    }

    [Fact]
    public async Task UnconfiguredMailgunFailsWithoutAttemptingARequest()
    {
        var forbidden = new ForbiddenHandler();
        var template = WriteTemplate("<p>{{ user_name }}</p>");

        var result = await Mailgun(forbidden, new MailSettings { Domain = "", ApiKey = "" })
            .SendAsync(new MailRecipient("ada@example.test"), "s", template);

        Assert.False(result.Succeeded);
        Assert.Equal(0, forbidden.Requests);
        Assert.Contains("not configured", result.ErrorMessage);
    }

    // ------------------------------------------------------------------ the composed endpoint

    [Theory]
    [InlineData(MailRegion.US, "https://api.mailgun.net/v3/mg.example.test/messages")]
    [InlineData(MailRegion.EU, "https://api.eu.mailgun.net/v3/mg.example.test/messages")]
    public void TheEndpointIsComposedFromRegionAndDomain(MailRegion region, string expected)
    {
        // Composed, never stored. Earlier GX projects kept the finished URL beside the domain, which
        // lets the two disagree - and mail then goes somewhere nobody configured.
        var settings = new MailSettings { Region = region, Domain = "mg.example.test" };

        Assert.Equal(expected, settings.ApiEndpoint);
    }
}

/// <summary>
/// The <c>Mail:Delivery</c> setting, which is bound as a string on purpose.
/// </summary>
/// <remarks>
/// These exist because a real Development run failed where the tests did not. Binding this as an
/// enum makes <c>"Delivery": ""</c> - the value the shipped appsettings.json carries and the README
/// tells operators to leave - throw out of the options binder before validation can say anything
/// useful: "Failed to convert configuration value '' ... is not a valid value for MailDelivery".
/// Every test had set an explicit "Sink", so none of them ever bound an empty string.
/// </remarks>
public class MailDeliverySettingTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void AnEmptyDeliveryMeansUnresolved_NotInvalid(string? configured)
    {
        var settings = new MailSettings { Delivery = configured! };

        Assert.Null(settings.ParseDelivery());
        Assert.Empty(settings.Validate(new System.ComponentModel.DataAnnotations.ValidationContext(settings)));
    }

    [Theory]
    [InlineData("Sink", MailDelivery.Sink)]
    [InlineData("sink", MailDelivery.Sink)]
    [InlineData("Mailgun", MailDelivery.Mailgun)]
    [InlineData("MAILGUN", MailDelivery.Mailgun)]
    public void AKnownDeliveryParses_CaseInsensitively(string configured, MailDelivery expected)
    {
        Assert.Equal(expected, new MailSettings { Delivery = configured }.ParseDelivery());
    }

    [Fact]
    public void AMisspelledDeliveryIsAValidationError_NamingTheSupportedSet()
    {
        var settings = new MailSettings { Delivery = "Mailgnu" };

        var errors = settings.Validate(new System.ComponentModel.DataAnnotations.ValidationContext(settings)).ToList();

        Assert.Contains(errors, e => e.ErrorMessage!.Contains("Mailgnu") && e.ErrorMessage.Contains("Mailgun"));
    }

    [Fact]
    public void UnspecifiedIsNotAcceptedAsAConfiguredValue()
    {
        // "Unspecified" is an internal state, not something to write in a config file. Parsing it as
        // a real choice would let it silently mean "Mailgun" in production.
        Assert.Null(new MailSettings { Delivery = "Unspecified" }.ParseDelivery());
    }

    [Fact]
    public void AMalformedFromAddressIsAValidationError()
    {
        var settings = new MailSettings { FromAddress = "not-an-address" };

        var errors = settings.Validate(new System.ComponentModel.DataAnnotations.ValidationContext(settings)).ToList();

        Assert.Contains(errors, e => e.ErrorMessage!.Contains("not a valid email address"));
    }
}
