#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Caching;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Application.Features.Identity.Notifications.ResetPassword;
using CleanArchitecture.Blazor.Domain.Identity;
using CleanArchitecture.Blazor.Server.UI.Pages.Identity.Forgot;
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
using MudBlazor;
using MudBlazor.Services;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Server.UI.IntegrationTests;

/// <summary>
/// That forgot-password answers the same way for every address.
/// </summary>
/// <remarks>
/// Pass 19 measured three distinguishable outcomes: an unknown address got an Error snackbar and
/// stayed on the page; a known-but-unconfirmed address got a DIFFERENT Error snackbar and stayed on
/// the page; a known confirmed address got no snackbar and NAVIGATED to the confirmation page. Three
/// tells - message, navigation, and a log line written only on success - so an anonymous visitor
/// could sort any address into one of three buckets, one request at a time.
/// <para>
/// Neutralising the message alone would not have been enough, and that is the point of the
/// navigation assertions here: with the snackbar suppressed, the landing URL was still a complete
/// answer.
/// </para>
/// <para>
/// Pass 22 §A then answered the policy question Pass 21 left open: an unconfirmed address MAY reset,
/// and a completed reset confirms it. The behaviour now matches the response, and the only case that
/// receives nothing is an address with no account behind it.
/// </para>
/// <para>
/// The indistinguishability assertions are deliberately unchanged by that. A policy change made
/// BEHIND those responses must not make the responses start differing again, and re-running these
/// untouched across the change is the check that proves it did not.
/// </para>
/// </remarks>
[TestFixture]
public class IdentityEnumerationComponentTests
{
    private const string Confirmed = "confirmed@example.com";
    private const string Unconfirmed = "unconfirmed@example.com";
    private const string Unknown = "nobody@example.invalid";

    private BunitContext _ctx = null!;
    private List<ResetPasswordNotification> _published = null!;

    [SetUp]
    public void SetUp()
    {
        _ctx = new BunitContext();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _published = new List<ResetPasswordNotification>();

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
        services.AddSingleton(Mock.Of<IAppCache>());
        services.AddSingleton(Mock.Of<IPermissionService>());
        services.AddSingleton(Mock.Of<IObjectMapper>());

        var mediator = new Mock<IMediator>();
        mediator.Setup(m => m.Publish(It.IsAny<ResetPasswordNotification>(), It.IsAny<CancellationToken>()))
            .Callback<ResetPasswordNotification, CancellationToken>((n, _) => _published.Add(n))
            .Returns(ValueTask.CompletedTask);
        services.AddSingleton(mediator.Object);

        services.AddScoped(_ => BuildUserManager());
    }

    [TearDown]
    public async Task TearDown() => await _ctx.DisposeAsync();

    /// <summary>One confirmed account, one unconfirmed, and nothing else.</summary>
    private static UserManager<ApplicationUser> BuildUserManager()
    {
        var confirmed = new ApplicationUser
        { Id = "confirmed-id", UserName = "confirmed", Email = Confirmed, EmailConfirmed = true };
        var unconfirmed = new ApplicationUser
        { Id = "unconfirmed-id", UserName = "unconfirmed", Email = Unconfirmed, EmailConfirmed = false };

        var mock = new Mock<UserManager<ApplicationUser>>(
            Mock.Of<IUserStore<ApplicationUser>>(), null!, null!, null!, null!, null!, null!, null!, null!);

        mock.Setup(m => m.FindByEmailAsync(Confirmed)).ReturnsAsync(confirmed);
        mock.Setup(m => m.FindByEmailAsync(Unconfirmed)).ReturnsAsync(unconfirmed);
        mock.Setup(m => m.FindByEmailAsync(Unknown)).ReturnsAsync((ApplicationUser?)null);

        mock.Setup(m => m.IsEmailConfirmedAsync(confirmed)).ReturnsAsync(true);
        mock.Setup(m => m.IsEmailConfirmedAsync(unconfirmed)).ReturnsAsync(false);

        mock.Setup(m => m.GeneratePasswordResetTokenAsync(It.IsAny<ApplicationUser>()))
            .ReturnsAsync("a-real-reset-token");

        return mock.Object;
    }

    private static class LoginUsers
    {
        public const string Ordinary = "ordinary";
        public const string LockedOut = "locked-out";
        public const string Inactive = "inactive";
    }

    /// <summary>
    /// A context for the login page: its own service graph, because each case renders a fresh page
    /// and reads a fresh snackbar.
    /// </summary>
    private static BunitContext NewLoginContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var services = ctx.Services;
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
        services.AddSingleton(Mock.Of<IApplicationDbContextFactory>());
        services.AddSingleton(Mock.Of<IClientInfoAccessor>());

        var ordinary = new ApplicationUser
        { Id = "u1", UserName = LoginUsers.Ordinary, Email = "u1@example.com", IsActive = true };
        var lockedOut = new ApplicationUser
        { Id = "u2", UserName = LoginUsers.LockedOut, Email = "u2@example.com", IsActive = true };
        var inactive = new ApplicationUser
        { Id = "u3", UserName = LoginUsers.Inactive, Email = "u3@example.com", IsActive = false };

        var users = new Mock<UserManager<ApplicationUser>>(
            Mock.Of<IUserStore<ApplicationUser>>(), null!, null!, null!, null!, null!, null!, null!, null!);

        users.Setup(m => m.FindByNameAsync(LoginUsers.Ordinary)).ReturnsAsync(ordinary);
        users.Setup(m => m.FindByNameAsync(LoginUsers.LockedOut)).ReturnsAsync(lockedOut);
        users.Setup(m => m.FindByNameAsync(LoginUsers.Inactive)).ReturnsAsync(inactive);
        users.Setup(m => m.FindByNameAsync(It.Is<string>(n => n == "no-such-person")))
            .ReturnsAsync((ApplicationUser?)null);

        users.Setup(m => m.IsLockedOutAsync(lockedOut)).ReturnsAsync(true);
        users.Setup(m => m.IsLockedOutAsync(ordinary)).ReturnsAsync(false);
        users.Setup(m => m.IsLockedOutAsync(inactive)).ReturnsAsync(false);

        // Nobody in this fixture supplies a correct password: these are the anonymous cases.
        users.Setup(m => m.CheckPasswordAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>()))
            .ReturnsAsync(false);
        users.Setup(m => m.AccessFailedAsync(It.IsAny<ApplicationUser>()))
            .ReturnsAsync(IdentityResult.Success);

        services.AddScoped(_ => users.Object);

        // The external-login panel is a sibling on the page, not part of what is under test, and it
        // resolves a SignInManager this fixture has no reason to build.
        ctx.ComponentFactories.AddStub<CleanArchitecture.Blazor.Server.UI.Pages.Identity.Login.ExternalLoginPicker>();
        return ctx;
    }

    /// <summary>Everything an anonymous caller can observe from one submission.</summary>
    private sealed record Observed(string LandedUrl, string Snackbars, string PageText);

    private Observed Submit(string email)
    {
        var navigation = _ctx.Services.GetRequiredService<NavigationManager>();
        var cut = _ctx.Render<Forgot>();

        // Input, not Change: the field is Immediate="true", so it binds on oninput.
        cut.Find("input").Input(email);
        cut.Find("form").Submit();

        var snackbar = _ctx.Services.GetRequiredService<ISnackbar>();
        var messages = string.Join(" | ",
            snackbar.ShownSnackbars.Select(s => s.Severity + ":" + s.Message).OrderBy(x => x));

        // Visible TEXT, not raw markup: bUnit stamps an incrementing blazor:onsubmit handler id
        // into the markup, so two identical renders in one context never match byte for byte.
        // What an attacker can read is the text.
        return new Observed(navigation.Uri, messages, cut.Find("form").TextContent);
    }

    [Test]
    public void AllThreeCases_AreIndistinguishable()
    {
        var unknown = Submit(Unknown);
        var unconfirmed = Submit(Unconfirmed);
        var confirmed = Submit(Confirmed);

        // Navigation - the tell that survives suppressing the message.
        unknown.LandedUrl.Should().Be(confirmed.LandedUrl, "an unknown address must land where a real one does");
        unconfirmed.LandedUrl.Should().Be(confirmed.LandedUrl, "so must an unconfirmed one");
        confirmed.LandedUrl.Should().EndWith(ForgotPasswordConfirmation.PageUrl);
        confirmed.LandedUrl.Should().NotContain("?", "and it must carry no query string");

        // Message.
        unknown.Snackbars.Should().Be(confirmed.Snackbars);
        unconfirmed.Snackbars.Should().Be(confirmed.Snackbars);
        confirmed.Snackbars.Should().BeEmpty("the page says nothing at all now, to anyone");

        // Page text.
        unknown.PageText.Should().Be(confirmed.PageText);
        unconfirmed.PageText.Should().Be(confirmed.PageText);
    }

    // ---- §D: the login page ---------------------------------------------------------------------

    /// <summary>
    /// Every failure an anonymous caller can provoke at the login page, and the one message they all
    /// produce.
    /// </summary>
    /// <remarks>
    /// The login page was the blunter oracle of the two: it answered "The specified user does not
    /// exist." before checking any password, and reported lockout and inactivity before it too - so
    /// the whole user list could be enumerated at one request per name, with no credential at all.
    /// <para>
    /// The lockout and inactive messages are NOT deleted. They moved behind a correct password,
    /// which is where the person asking has proved the account is theirs. That is why this fixture
    /// gives the locked and inactive users the WRONG password: it is asserting what a stranger
    /// sees, and a stranger does not have the right one.
    /// </para>
    /// </remarks>
    private sealed record LoginCase(string Label, string UserName, string Password);

    [Test]
    public async Task EveryLoginFailure_LooksTheSameToAnAnonymousCaller()
    {
        var messages = new List<string>();

        foreach (var c in new[]
                 {
                     new LoginCase("unknown user", "no-such-person", "whatever-1!"),
                     new LoginCase("wrong password", LoginUsers.Ordinary, "wrong-password-1!"),
                     new LoginCase("locked out", LoginUsers.LockedOut, "wrong-password-1!"),
                     new LoginCase("inactive", LoginUsers.Inactive, "wrong-password-1!")
                 })
        {
            // DisposeAsync, not using/Dispose: MudBlazor registers IAsyncDisposable-only services,
            // and a synchronous dispose of the container throws on them.
            var ctx = NewLoginContext();
            try
            {
                var cut = ctx.Render<CleanArchitecture.Blazor.Server.UI.Pages.Identity.Login.Login>();

                // Change, not Input: the login fields are not Immediate, so they bind on onchange.
                // (The forgot-password field is Immediate and binds on oninput - hence the
                // difference between this and Submit() above.)
                var inputs = cut.FindAll("input");
                inputs[0].Change(c.UserName);
                inputs[1].Change(c.Password);
                cut.Find("form").Submit();

                var snackbar = ctx.Services.GetRequiredService<ISnackbar>();
                messages.Add(c.Label + " => " + string.Join(" | ",
                    snackbar.ShownSnackbars.Select(s => s.Severity + ":" + s.Message)));
            }
            finally
            {
                await ctx.DisposeAsync();
            }
        }

        var answers = messages.Select(m => m[(m.IndexOf("=> ", StringComparison.Ordinal) + 3)..]).Distinct().ToArray();

        answers.Should().ContainSingle(
            "an unknown name, a wrong password, a locked account and an inactive account must be "
            + "indistinguishable to someone who does not hold the password. Observed: "
            + string.Join("   ///   ", messages));
        answers[0].Should().Contain("The username or password is incorrect");
        answers[0].Should().NotContain("does not exist");
        answers[0].Should().NotContain("inactive");
        answers[0].Should().NotContain("locked");
    }

    /// <summary>
    /// Pass 22 §A: every account that exists receives a reset link, confirmed or not; an address
    /// with no account still receives nothing.
    /// </summary>
    /// <remarks>
    /// This is the behavioural counterpart to the indistinguishability tests, and it exists because
    /// they cannot tell the difference: a "fix" that simply stopped sending anything to anybody
    /// would satisfy every assertion above. Before Pass 22 this asserted the opposite for the
    /// unconfirmed case - that it received nothing - which was the behaviour Pass 21 preserved while
    /// it neutralised the response.
    /// </remarks>
    [Test]
    public void EveryExistingAccount_ReceivesAReset_ConfirmedOrNot()
    {
        Submit(Unknown);
        _published.Should().BeEmpty("there is nobody to send to");

        Submit(Unconfirmed);
        _published.Should().ContainSingle(
            "an unconfirmed address may now recover - the link proves mailbox control exactly as a "
            + "confirmation link does, and refusing it left the user no route back (Pass 22 §A)");
        _published[0].Email.Should().Be(Unconfirmed);
        _published[0].RequestUrl.Should().Contain("/account/reset-password").And.Contain("token=");

        _published.Clear();

        Submit(Confirmed);
        _published.Should().ContainSingle("the confirmed flow must still work, unchanged");
        _published[0].RequestUrl.Should().Contain("/account/reset-password").And.Contain("token=");
        _published[0].Email.Should().Be(Confirmed);
    }
}
#nullable restore
