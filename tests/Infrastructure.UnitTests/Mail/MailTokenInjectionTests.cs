using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Application.Common.Models;
using CleanArchitecture.Blazor.Infrastructure.Services.Mail;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace CleanArchitecture.Blazor.Infrastructure.UnitTests.Mail;

/// <summary>
/// The four tokens every template gets whether the caller supplies them or not, and the greeting
/// fallback that stops "Hi ,".
/// </summary>
/// <remarks>
/// MNEFleets lost <c>user_name</c>, <c>app_name</c>, <c>company</c> and <c>base_url</c> across every
/// template at once, because each handler assembled its own model and one of them was written
/// without them. Supplying them centrally means a handler cannot forget; these tests are what say
/// the central supply actually happens, and that it defers to a caller who means something else.
/// </remarks>
[Collection(TemplateFileCollection.Name)]
public class MailTokenInjectionTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "gx-mail-token-tests", Guid.NewGuid().ToString("N"));

    private readonly string _templatesDirectory;

    public MailTokenInjectionTests()
    {
        // The renderer resolves against AppContext.BaseDirectory, so a template written for a test
        // has to live there. Each test writes its own uniquely named file and removes it after.
        _templatesDirectory = Path.Combine(AppContext.BaseDirectory, MailTemplates.Directory);
        System.IO.Directory.CreateDirectory(_templatesDirectory);
        System.IO.Directory.CreateDirectory(_directory);
    }

    private readonly List<string> _written = [];

    private string WriteTemplate(string body)
    {
        var name = "test-" + Guid.NewGuid().ToString("N");
        var path = Path.Combine(_templatesDirectory, name + MailTemplates.Extension);
        File.WriteAllText(path, body);
        _written.Add(path);
        return name;
    }

    public void Dispose()
    {
        foreach (var path in _written)
        {
            try { File.Delete(path); } catch (IOException) { }
        }

        try { System.IO.Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private static MailTemplateRenderer Renderer(
        string appName = "Test App", string company = "Test Company", string baseUrl = "https://example.test")
    {
        var settings = new Mock<IApplicationSettings>();
        settings.SetupGet(s => s.AppName).Returns(appName);
        settings.SetupGet(s => s.Company).Returns(company);
        settings.SetupGet(s => s.ApplicationUrl).Returns(baseUrl);

        return new MailTemplateRenderer(settings.Object, NullLogger<MailTemplateRenderer>.Instance);
    }

    private const string AllFourTokens =
        "[{{ user_name }}|{{ app_name }}|{{ company }}|{{ base_url }}]";

    // ------------------------------------------------------------------ the injection

    [Fact]
    public async Task AHandlerSupplyingNoneOfTheFour_StillRendersAllFour()
    {
        // The MNEFleets failure, inverted into a test: a model with nothing in it must still produce
        // a complete email.
        var template = WriteTemplate(AllFourTokens);

        var result = await Renderer().RenderAsync(
            new MailRecipient("someone@example.test", "Ada Lovelace", "ada"),
            template,
            model: null);

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal("[Ada Lovelace|Test App|Test Company|https://example.test]", result.Data);
    }

    [Fact]
    public async Task AHandlerSupplyingItsOwnTokens_KeepsThemAlongsideTheInjectedOnes()
    {
        var template = WriteTemplate(AllFourTokens + "{{ request_url }}");

        var result = await Renderer().RenderAsync(
            new MailRecipient("someone@example.test", UserName: "ada"),
            template,
            new { RequestUrl = "https://example.test/reset" });

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal(
            "[ada|Test App|Test Company|https://example.test]https://example.test/reset",
            result.Data);
    }

    [Fact]
    public async Task CallerValuesWin_OverInjectedOnes()
    {
        // Caller-wins, not injector-wins. A handler that deliberately sends on behalf of a different
        // brand, or names a different company, must keep what it set - the four are defaults.
        var template = WriteTemplate(AllFourTokens);

        var result = await Renderer().RenderAsync(
            new MailRecipient("someone@example.test", "Ada Lovelace"),
            template,
            new { UserName = "Deliberate Name", AppName = "Deliberate App", Company = "Deliberate Co" });

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal(
            "[Deliberate Name|Deliberate App|Deliberate Co|https://example.test]",
            result.Data);
    }

    // ------------------------------------------------------------------ the greeting fallback

    [Fact]
    public async Task WithADisplayName_TheGreetingUsesIt()
    {
        var template = WriteTemplate("Hi {{ user_name }},");

        var result = await Renderer().RenderAsync(
            new MailRecipient("someone@example.test", "Ada Lovelace", "ada"), template, null);

        Assert.Equal("Hi Ada Lovelace,", result.Data);
    }

    [Fact]
    public async Task WithNoDisplayName_TheGreetingFallsBackToTheUserName()
    {
        var template = WriteTemplate("Hi {{ user_name }},");

        var result = await Renderer().RenderAsync(
            new MailRecipient("someone@example.test", DisplayName: null, UserName: "ada"), template, null);

        Assert.Equal("Hi ada,", result.Data);
    }

    [Fact]
    public async Task WithNeither_TheGreetingIsThere_AndNeverAnEmptyHi()
    {
        // The bug this whole mechanism exists to prevent, asserted in the exact shape it took:
        // "Hi ," in a real person's inbox.
        var template = WriteTemplate("Hi {{ user_name }},");

        var result = await Renderer().RenderAsync(
            new MailRecipient("someone@example.test"), template, null);

        Assert.Equal("Hi there,", result.Data);
        Assert.DoesNotContain("Hi ,", result.Data);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("   ", "   ")]
    [InlineData(null, null)]
    public void WhitespaceNamesAreTreatedAsAbsent(string? displayName, string? userName)
    {
        // Whitespace is the realistic form of "not set": a nullable column that got an empty string,
        // a form field submitted blank. IsNullOrWhiteSpace rather than IsNullOrEmpty is what makes
        // "Hi    ," impossible as well as "Hi ,".
        Assert.Equal("there", new MailRecipient("a@b.test", displayName, userName).Greeting);
    }

    // ------------------------------------------------------------------ the dictionary passthrough

    [Fact]
    public async Task APreparedDictionaryIsPassedThrough_NotReflectedOver()
    {
        // Reflecting over a Dictionary walks the DICTIONARY's own properties - Count, Keys, Values,
        // Comparer - so the model handed to Scriban contains those and none of the caller's tokens.
        // Every real token then renders empty, the email sends, and every value in it is blank.
        var template = WriteTemplate("[{{ request_url }}|{{ count }}]");

        var result = await Renderer().RenderAsync(
            new MailRecipient("someone@example.test", "Ada"),
            template,
            new Dictionary<string, object?> { ["request_url"] = "https://example.test/x" });

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal("[https://example.test/x|]", result.Data);
    }

    [Fact]
    public void ToScribanModel_ConvertsPascalCaseToSnakeCase()
    {
        var model = MailTemplateRenderer.ToScribanModel(new { RequestUrl = "x", Email = "y" });

        Assert.Equal("x", model["request_url"]);
        Assert.Equal("y", model["email"]);
    }

    // ------------------------------------------------------------------ failure is a Result

    [Fact]
    public async Task AMissingTemplateIsAFailedResult_NotAnException()
    {
        var result = await Renderer().RenderAsync(
            new MailRecipient("someone@example.test"), "no-such-template-" + Guid.NewGuid().ToString("N"), null);

        Assert.False(result.Succeeded);
        Assert.Contains("was not found", result.ErrorMessage);
    }

    [Fact]
    public async Task AnUnparseableTemplateIsAFailedResult_NotAnException()
    {
        // An unclosed if - Scriban tolerates a bare unterminated tag, but not a block with no end.
        var template = WriteTemplate("{{ if true }}<p>hello</p>");

        var result = await Renderer().RenderAsync(new MailRecipient("someone@example.test"), template, null);

        Assert.False(result.Succeeded);
        Assert.Contains("failed to parse", result.ErrorMessage);
    }
}
