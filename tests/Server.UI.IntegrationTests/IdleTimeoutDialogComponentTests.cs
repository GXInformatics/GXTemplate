#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using Bunit.TestDoubles;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Caching;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Server.UI.Components.Security;
using CleanArchitecture.Blazor.Server.UI.Services;
using CleanArchitecture.Blazor.Server.UI.Services.Layout;
using CleanArchitecture.Blazor.Server.UI.Services.UserPreferences;
using FluentAssertions;
using Mapster;
using Mediator;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using MudBlazor.Services;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Server.UI.IntegrationTests;

/// <summary>
/// Hosts the monitor alongside MudBlazor's providers, exactly as <c>MainLayout</c> does.
/// </summary>
/// <remarks>
/// <b>Read this before changing these tests.</b> The providers are not decoration. An inline
/// <c>MudDialog</c> is rendered by <see cref="MudDialogProvider"/>, not by the component that
/// declares it - so without the providers in the tree the monitor renders nothing at all, and a test
/// can wrongly conclude the dialog was never shown. It cost Pass 18 an hour.
/// </remarks>
public sealed class IdleMonitorHost : ComponentBase
{
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenComponent<MudPopoverProvider>(0);
        builder.CloseComponent();
        builder.OpenComponent<MudDialogProvider>(1);
        builder.CloseComponent();
        builder.OpenComponent<IdleTimeoutMonitor>(2);
        builder.CloseComponent();
    }
}

/// <summary>
/// The idle warning dialog's close mechanism, observed where it actually happens.
/// </summary>
/// <remarks>
/// The defect these exist for: the monitor used to close the dialog by dropping it from the render
/// tree behind an <c>@if</c>, which does not tell the provider to close anything. The dialog stayed
/// on screen, undismissable by design (<c>BackdropClick</c> and <c>CloseOnEscapeKey</c> are both
/// false), and its overlay swallowed every click - a frozen page, on every close path, whatever the
/// keep-alive returned.
/// <para>
/// <b>Every assertion here is on the HOST's markup, never the monitor's own.</b> That distinction is
/// the whole lesson: during the defect the monitor's own markup was empty - it had "closed" the
/// dialog as far as it was concerned - while the provider went on rendering it. A test that asserted
/// on the component alone would have passed for the entire life of the bug.
/// </para>
/// </remarks>
[TestFixture]
public class IdleTimeoutDialogComponentTests
{
    private const string StayLoggedIn = "Stay Logged In";

    private BunitContext _ctx = null!;
    private BunitJSModuleInterop _module = null!;

    [SetUp]
    public void SetUp()
    {
        _ctx = new BunitContext();

        // MudBlazor's popover provider reaches for JS on render. None of it affects the close
        // decision, so it is answered permissively rather than mocked call by call.
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var services = _ctx.Services;
        services.AddLogging();
        services.AddLocalization();
        services.AddMudServices();
        services.AddScoped<DialogServiceHelper>();
        services.AddSingleton(new TypeAdapterConfig());
        services.AddSingleton(Mock.Of<IApplicationSettings>());
        services.AddSingleton(Mock.Of<IUserProfileState>());
        services.AddSingleton(Mock.Of<IValidationService>());
        services.AddSingleton(Mock.Of<IMediator>());
        services.AddSingleton(Mock.Of<IAppCache>());
        services.AddSingleton(Mock.Of<IPermissionService>());
        services.AddSingleton(Mock.Of<IObjectMapper>());
        services.AddSingleton(Mock.Of<IUserPreferencesService>());
        services.AddScoped<LayoutService>();

        var policy = new Mock<IIdleTimeoutPolicyProvider>();
        policy.SetupGet(p => p.Enabled).Returns(true);
        policy.Setup(p => p.GetEffectiveAsync(
                  It.IsAny<System.Security.Claims.ClaimsPrincipal>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new IdleTimeoutPolicy(true, 1, 15));
        services.AddSingleton(policy.Object);

        _ctx.AddAuthorization().SetAuthorized("someone");

        _module = _ctx.JSInterop.SetupModule("./js/gxIdleTimeout.js");
        _module.SetupVoid("initialize", _ => true).SetVoidResult();
    }

    [TearDown]
    public async Task TearDown() => await _ctx.DisposeAsync();

    private IRenderedComponent<IdleMonitorHost> RenderAndOpenWarning()
    {
        var host = _ctx.Render<IdleMonitorHost>();
        var monitor = host.FindComponent<IdleTimeoutMonitor>().Instance;

        // What the JS tick does a second after the idle window elapses.
        host.InvokeAsync(() => monitor.OnIdleWarning(15)).GetAwaiter().GetResult();

        DialogShown(host).Should().BeTrue("the rest of each test is meaningless if it never opened");
        return host;
    }

    private static bool DialogShown(IRenderedComponent<IdleMonitorHost> host) =>
        host.Markup.Contains(StayLoggedIn, StringComparison.Ordinal);

    private static void ClickStayLoggedIn(IRenderedComponent<IdleMonitorHost> host) =>
        host.FindAll("button")
            .First(b => b.TextContent.Contains(StayLoggedIn, StringComparison.Ordinal))
            .Click();

    [Test]
    public void OnASuccessfulKeepAlive_TheDialogCloses()
    {
        var host = RenderAndOpenWarning();
        _module.SetupVoid("extend").SetVoidResult();

        ClickStayLoggedIn(host);

        DialogShown(host).Should().BeFalse(
            "Stay Logged In must return the page to the user; an undismissable dialog left over a " +
            "live session is a frozen page");
    }

    [Test]
    public void WhenTheModuleCallThrows_NothingEscapesAndTheDialogStillCloses()
    {
        // A JSException awaited in a click handler propagates into the circuit and tears it down -
        // an independent way to freeze the page. The dialog must close and the failure must not
        // reach the circuit.
        var host = RenderAndOpenWarning();
        _module.SetupVoid("extend").SetException(new InvalidOperationException("boom in JS"));

        var click = () => ClickStayLoggedIn(host);

        click.Should().NotThrow("a failed keep-alive is a session question, not a reason to kill the page");
        DialogShown(host).Should().BeFalse();
    }

    [Test]
    public void WhenTheModuleCallNeverSettles_TheDialogStillCloses()
    {
        // The dialog is closed before the module is called and the call is not awaited, so a call
        // that never returns cannot hold the dialog open.
        var host = RenderAndOpenWarning();
        _module.SetupVoid("extend");   // planned, deliberately never completed

        ClickStayLoggedIn(host);

        DialogShown(host).Should().BeFalse(
            "the dialog's fate must not depend on a network round trip that may never finish");
    }

    [Test]
    public void WhenAnotherTabReportsActivity_TheDialogCloses()
    {
        // No click involved. This path had the same defect and is the worse one: an undismissable
        // dialog over a session that is not idle at all, because the user is working in another tab.
        var host = RenderAndOpenWarning();
        var monitor = host.FindComponent<IdleTimeoutMonitor>().Instance;

        host.InvokeAsync(() => monitor.OnActivityResumed()).GetAwaiter().GetResult();

        DialogShown(host).Should().BeFalse();
    }

    [Test]
    public void SignOutNow_AlsoClosesTheDialog()
    {
        // signOut() navigates away, so in a browser the page is leaving anyway - but if the module
        // call fails the user must not be left holding an undismissable dialog.
        var host = RenderAndOpenWarning();
        _module.SetupVoid("signOut").SetException(new InvalidOperationException("boom in JS"));

        var click = () => host.FindAll("button")
            .First(b => b.TextContent.Contains("Sign Out Now", StringComparison.Ordinal))
            .Click();

        click.Should().NotThrow();
        DialogShown(host).Should().BeFalse();
    }
}
