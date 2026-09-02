#nullable enable
using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Caching;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Application.Features.Identity.Notifications.ResetPassword;
using CleanArchitecture.Blazor.Domain.Identity;
using CleanArchitecture.Blazor.Server.UI.Pages.Identity.Forgot;
using CleanArchitecture.Blazor.Server.UI.Pages.Identity.Register;
using CleanArchitecture.Blazor.Server.UI.Services;
using CleanArchitecture.Blazor.Server.UI.Services.Layout;
using CleanArchitecture.Blazor.Server.UI.Services.UserPreferences;
using FluentAssertions;
using Mapster;
using Mediator;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor.Services;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Server.UI.IntegrationTests;

/// <summary>
/// That the password-reset token, and the email-confirmation token beside it, never reach the
/// browser.
/// </summary>
/// <remarks>
/// Pass 19 reproduced the defect end to end: an unauthenticated visitor submitted a known address
/// at <c>/account/forgot-password</c> and was handed a WORKING reset link for that account, on
/// screen and in the address bar, because <c>Forgot.razor</c> appended the callback URL to its
/// navigation and <c>ForgotPasswordConfirmation.razor</c> rendered whatever arrived in that query
/// parameter as a button's <c>Href</c>. Full account takeover of any confirmed account, including
/// the administrator, with no mailbox access. The same unvalidated <c>Href</c> was an open
/// redirect, and <c>RegisterConfirmation.razor</c> carried the identical shape for the email
/// confirmation link.
/// <para>
/// <b>These are component tests, and that is load-bearing rather than a matter of taste.</b> The
/// application renders at <c>InteractiveServerRenderMode(prerender: false)</c> (see
/// <c>App.razor</c>), so an HTTP response carries the shell and none of the component tree. Pass 19
/// confirmed this by hand: <c>curl</c> of the confirmation page with a hostile parameter returned
/// 200 with the URL nowhere in the body, while a real browser rendered the button. An HTTP-level
/// test asserting "the response contains no such anchor" would therefore have passed against the
/// broken code, which is worth less than no test at all. Only rendering sees this.
/// </para>
/// <para>
/// The reflection tests are the durable half. Markup assertions prove the button is not rendered
/// today; the absence of a <c>[SupplyParameterFromQuery]</c> property whose value could reach an
/// <c>href</c> is what stops the block being re-added tomorrow.
/// </para>
/// </remarks>
[TestFixture]
public class ResetLinkDisclosureComponentTests
{
    /// <summary>An absolute, off-site URL. Covers the open redirect as well as the token leak.</summary>
    private const string HostileUrl = "https://evil.example/phish";

    /// <summary>The shape of the real leak: a same-origin reset URL carrying a live token.</summary>
    private const string TokenBearingUrl =
        "https://localhost/account/reset-password?userId=17cb7855-2b4f-4adf-82e2-9a85a7ca1cf0&token=Q2ZESjhEZWFtU3RQ";

    private BunitContext _ctx = null!;

    [SetUp]
    public void SetUp()
    {
        _ctx = new BunitContext();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var services = _ctx.Services;
        services.AddLogging();
        services.AddLocalization();
        services.AddMudServices();

        services.AddSingleton(Mock.Of<IUserPreferencesService>());
        services.AddScoped<LayoutService>();
        services.AddScoped<DialogServiceHelper>();
        services.AddSingleton(new TypeAdapterConfig());

        services.AddSingleton(Mock.Of<IApplicationSettings>());
        services.AddSingleton(Mock.Of<IUserProfileState>());
        services.AddSingleton(Mock.Of<IValidationService>());
        services.AddSingleton(Mock.Of<IMediator>());
        services.AddSingleton(Mock.Of<IAppCache>());
        services.AddSingleton(Mock.Of<IPermissionService>());
        services.AddSingleton(Mock.Of<IObjectMapper>());

        // RegisterConfirmation is an OwningComponentBase and resolves a UserManager from its own
        // scope during OnInitializedAsync.
        services.AddScoped(_ => NullUserManager());
    }

    [TearDown]
    public async Task TearDown() => await _ctx.DisposeAsync();

    /// <summary>
    /// A UserManager that finds nobody. Enough for the confirmation pages, which only look a user
    /// up to decide whether to show an error.
    /// </summary>
    private static UserManager<ApplicationUser> NullUserManager()
    {
        var mock = new Mock<UserManager<ApplicationUser>>(
            Mock.Of<IUserStore<ApplicationUser>>(), null!, null!, null!, null!, null!, null!, null!, null!);
        mock.Setup(m => m.FindByEmailAsync(It.IsAny<string>())).ReturnsAsync((ApplicationUser?)null);
        return mock.Object;
    }

    private void NavigateTo(string relativeUrl) =>
        _ctx.Services.GetRequiredService<NavigationManager>()
            .NavigateTo(relativeUrl);

    /// <summary>Every href and src the rendered component emits.</summary>
    private static string[] LinkTargets(IRenderedComponent<IComponent> cut) =>
        cut.FindAll("a, area, link, iframe, form, img, script")
            .SelectMany(e => new[] { e.GetAttribute("href"), e.GetAttribute("src"), e.GetAttribute("action") })
            .Where(v => !string.IsNullOrEmpty(v))
            .Select(v => v!)
            .ToArray();

    // ---- ForgotPasswordConfirmation -------------------------------------------------------------

    [Test]
    public void ForgotPasswordConfirmation_RendersNoOffSiteLink_WhenGivenAHostileQueryParameter()
    {
        NavigateTo($"/account/forgotpasswordconfirmation?ResetPasswordLink={Uri.EscapeDataString(HostileUrl)}");

        var cut = _ctx.Render<ForgotPasswordConfirmation>();

        LinkTargets(cut).Should().NotContain(t => t.Contains("evil.example"),
            "a query parameter must never become a link target - that is an open redirect on the "
            + "application's own origin, under its own branding");
        cut.Markup.Should().NotContain("evil.example");
    }

    [Test]
    public void ForgotPasswordConfirmation_RendersNoResetToken_WhenGivenOneInTheQueryString()
    {
        NavigateTo($"/account/forgotpasswordconfirmation?ResetPasswordLink={Uri.EscapeDataString(TokenBearingUrl)}");

        var cut = _ctx.Render<ForgotPasswordConfirmation>();

        cut.Markup.Should().NotContain("token=",
            "this is the account-takeover path: a reset token rendered to whoever asked for it");
        cut.Markup.Should().NotContain("reset-password?userId=");
    }

    [Test]
    public void ForgotPasswordConfirmation_SaysTheSameThingToEveryone()
    {
        NavigateTo($"/account/forgotpasswordconfirmation?ResetPasswordLink={Uri.EscapeDataString(HostileUrl)}");

        var withParameter = _ctx.Render<ForgotPasswordConfirmation>().Markup;

        NavigateTo("/account/forgotpasswordconfirmation");
        var withoutParameter = _ctx.Render<ForgotPasswordConfirmation>().Markup;

        withParameter.Should().Be(withoutParameter,
            "the page must not be steerable from the query string at all");
    }

    // ---- RegisterConfirmation -------------------------------------------------------------------

    [Test]
    public void RegisterConfirmation_RendersNoOffSiteLink_WhenGivenAHostileQueryParameter()
    {
        NavigateTo("/account/registerconfirmation?email=someone@example.com"
                   + $"&EmailConfirmationLink={Uri.EscapeDataString(HostileUrl)}");

        var cut = _ctx.Render<RegisterConfirmation>();

        LinkTargets(cut).Should().NotContain(t => t.Contains("evil.example"));
        cut.Markup.Should().NotContain("evil.example");
    }

    [Test]
    public void RegisterConfirmation_RendersNoConfirmationToken_WhenGivenOneInTheQueryString()
    {
        var tokenUrl = "https://localhost/account/confirmemail?userId=abc&code=Q2ZESjhEZWFt";
        NavigateTo("/account/registerconfirmation?email=someone@example.com"
                   + $"&EmailConfirmationLink={Uri.EscapeDataString(tokenUrl)}");

        var cut = _ctx.Render<RegisterConfirmation>();

        cut.Markup.Should().NotContain("code=",
            "anyone could otherwise confirm an address they do not control");
        cut.Markup.Should().NotContain("confirmemail?userId=");
    }

    // ---- The durable half: the parameters are gone, not merely unrendered -----------------------

    // ---- The flow still works ------------------------------------------------------------------

    /// <summary>
    /// The token is still generated and still published to the mail handler; it just stops being
    /// put in front of the browser.
    /// </summary>
    /// <remarks>
    /// Without this, "the token is not in the URL" would also pass if the flow had silently stopped
    /// sending anything at all - which is the same defect wearing a fix's clothes. The catalogue
    /// this pass came from names three checks in a sibling project that passed against broken code
    /// for exactly this reason.
    /// </remarks>
    [Test]
    public void Forgot_StillPublishesTheResetNotification_ButNavigatesWithNoQueryString()
    {
        const string email = "administrator@localhost";

        var user = new ApplicationUser { Id = "17cb7855", UserName = "Administrator", Email = email };
        var userManager = new Mock<UserManager<ApplicationUser>>(
            Mock.Of<IUserStore<ApplicationUser>>(), null!, null!, null!, null!, null!, null!, null!, null!);
        userManager.Setup(m => m.FindByEmailAsync(email)).ReturnsAsync(user);
        userManager.Setup(m => m.IsEmailConfirmedAsync(user)).ReturnsAsync(true);
        userManager.Setup(m => m.GeneratePasswordResetTokenAsync(user)).ReturnsAsync("a-real-reset-token");

        ResetPasswordNotification? published = null;
        var mediator = new Mock<IMediator>();
        mediator.Setup(m => m.Publish(It.IsAny<ResetPasswordNotification>(), It.IsAny<CancellationToken>()))
            .Callback<ResetPasswordNotification, CancellationToken>((n, _) => published = n)
            .Returns(ValueTask.CompletedTask);

        // A container of its own: this test needs a UserManager that finds somebody, and an
        // IMediator it can look inside.
        _ctx.Services.AddScoped(_ => userManager.Object);
        _ctx.Services.AddSingleton(mediator.Object);

        var navigation = _ctx.Services.GetRequiredService<NavigationManager>();
        var cut = _ctx.Render<Forgot>();

        // Input, not Change: the field is Immediate="true", so it binds on oninput.
        cut.Find("input").Input(email);
        cut.Find("form").Submit();

        published.Should().NotBeNull("the reset email must still be sent - that is the whole flow");
        published!.RequestUrl.Should().Contain("/account/reset-password")
            .And.Contain("token=", "the mail handler still needs the real callback URL");
        published.Email.Should().Be(email);

        navigation.Uri.Should().EndWith(ForgotPasswordConfirmation.PageUrl,
            "the landing URL must carry no query string at all");
        navigation.Uri.Should().NotContain("?", "no query parameters, not merely no token");
    }

    // ---- The durable half: the parameters are gone, not merely unrendered -----------------------

    /// <summary>
    /// No component may bind a query-supplied value into a link target. This is the assertion that
    /// survives somebody re-adding the block the markup tests currently cover.
    /// </summary>
    [TestCase(typeof(ForgotPasswordConfirmation), "ResetPasswordLink")]
    [TestCase(typeof(RegisterConfirmation), "EmailConfirmationLink")]
    public void TheLinkCarryingQueryParameter_NoLongerExists(Type component, string parameterName)
    {
        var property = component.GetProperty(parameterName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        property.Should().BeNull(
            $"{component.Name}.{parameterName} existed only to be rendered as an href, and a reset "
            + "or confirmation token does not belong in a URL in ANY environment - URLs reach "
            + "browser history, Referer headers, proxy logs, screenshots and shoulders");
    }
}
#nullable restore
