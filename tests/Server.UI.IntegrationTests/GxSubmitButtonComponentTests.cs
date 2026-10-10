#nullable enable
using System;
using System.Threading.Tasks;
using Bunit;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Caching;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Application.Common.Security;
using CleanArchitecture.Blazor.Server.UI.Components.Common;
using CleanArchitecture.Blazor.Server.UI.Services;
using FluentAssertions;
using Mapster;
using Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor.Services;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Server.UI.IntegrationTests;

/// <summary>
/// Pass 52: <see cref="GxSubmitButton"/>. At rest it is a plain submit button that carries the marker
/// <c>wwwroot/js/gx-busy-button.js</c> looks for, with its spinner already in the markup and hidden. On the circuit it is
/// busy while its action runs, and stays busy when the action has started a navigation.
/// Pass 52: busy is the busy class (its own colour at reduced opacity), never the disabled attribute that MudBlazor
/// draws grey, and a second click while busy does not run the work again.
/// </summary>
/// <remarks>
/// What happens in the browser (the script, the sign-in post, Enter in the password field) is the pass's recorded
/// browser control, not this fixture: bUnit renders markup and runs no script.
/// </remarks>
[TestFixture]
public class GxSubmitButtonComponentTests
{
    private BunitContext _ctx = null!;

    [SetUp]
    public void SetUp()
    {
        _ctx = new BunitContext();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        // What Server.UI's _Imports.razor injects into every component.
        var services = _ctx.Services;
        services.AddLogging();
        services.AddLocalization();
        services.AddMudServices();
        services.AddSingleton(Mock.Of<IUserProfileState>());
        services.AddSingleton(Mock.Of<IApplicationSettings>());
        services.AddSingleton(Mock.Of<IAuthorizationService>());
        services.AddSingleton(Mock.Of<IValidationService>());
        services.AddSingleton(Mock.Of<IMediator>());
        services.AddScoped<DialogServiceHelper>();
        services.AddSingleton(Mock.Of<IAppCache>());
        services.AddSingleton(Mock.Of<IPermissionService>());
        services.AddSingleton(Mock.Of<IObjectMapper>());
        services.AddSingleton(new TypeAdapterConfig());
    }

    [TearDown]
    public async Task TearDown() => await _ctx.DisposeAsync();

    private IRenderedComponent<GxSubmitButton> RenderButton() =>
        _ctx.Render<GxSubmitButton>(p => p
            .Add(x => x.Label, "Sign in")
            .Add(x => x.BusyLabel, "Signing in…"));

    [Test]
    public void AtRest_ItIsASubmitButton_CarryingTheBusyMarker_WithItsSpinnerPresentAndHidden()
    {
        var cut = RenderButton();

        var button = cut.Find("button");
        button.GetAttribute("type").Should().Be("submit");
        button.HasAttribute("data-gx-busy-button").Should().BeTrue("the script finds the button by this marker");
        button.GetAttribute("data-gx-busy-label").Should().Be("Signing in…", "the script swaps this in without C#");
        button.HasAttribute("disabled").Should().BeFalse();
        button.HasAttribute("aria-busy").Should().BeFalse();
        cut.Find("button .gx-busy-text").TextContent.Should().Be("Sign in");

        var spinner = cut.Find("button .gx-busy-spinner");
        spinner.HasAttribute("hidden").Should().BeTrue("the spinner shows only while busy");
        spinner.QuerySelector(".mud-progress-circular.mud-progress-indeterminate")
            .Should().NotBeNull("the spinner is an indeterminate MudProgressCircular, in the markup from the start");
    }

    [Test]
    public void WithButtonTypeButton_ItIsNotASubmitButton()
    {
        var cut = _ctx.Render<GxSubmitButton>(p => p
            .Add(x => x.Label, "Save")
            .Add(x => x.BusyLabel, "Saving…")
            .Add(x => x.ButtonType, MudBlazor.ButtonType.Button));

        cut.Find("button").GetAttribute("type").Should().Be("button", "a dialog's Save outside a form must not submit one");
    }

    [Test]
    public async Task WhileItsActionRuns_ItCarriesTheBusyClass_NotTheDisabledStyle_AndAfterwardsItIsNormalAgain()
    {
        var cut = RenderButton();
        var work = new TaskCompletionSource();

        var run = cut.InvokeAsync(() => cut.Instance.RunAsync(() => work.Task));

        cut.WaitForAssertion(() => cut.Find("button").ClassList.Should().Contain(GxSubmitButton.BusyClass));
        AssertBusy(cut);

        work.SetResult();
        await run;

        cut.WaitForAssertion(() => cut.Find("button").ClassList.Should().NotContain(GxSubmitButton.BusyClass));
        cut.Find("button").HasAttribute("disabled").Should().BeFalse();
        cut.Find("button").HasAttribute("aria-busy").Should().BeFalse();
        cut.Find("button").HasAttribute("aria-disabled").Should().BeFalse();
        cut.Find("button .gx-busy-spinner").HasAttribute("hidden").Should().BeTrue();
        cut.Find("button .gx-busy-text").TextContent.Should().Be("Sign in");
    }

    [Test]
    public async Task WhenTheActionStartsANavigation_ItStaysBusyAfterTheActionReturns()
    {
        var cut = RenderButton();

        await cut.InvokeAsync(() => cut.Instance.RunAsync(() =>
        {
            cut.Instance.HoldUntilNavigation();
            return Task.CompletedTask;
        }));

        cut.Instance.Busy.Should().BeTrue("the page is leaving, so the button must not come back before it goes");
        AssertBusy(cut);
    }

    [Test]
    public async Task ASecondClickWhileBusy_RunsTheActionOnce()
    {
        var runs = 0;
        var work = new TaskCompletionSource();
        var cut = _ctx.Render<GxSubmitButton>(p => p
            .Add(x => x.Label, "Save")
            .Add(x => x.BusyLabel, "Saving…")
            .Add(x => x.ButtonType, MudBlazor.ButtonType.Button)
            .Add(x => x.Action, () => { runs++; return work.Task; }));

        var first = cut.Find("button").ClickAsync(new());
        cut.WaitForAssertion(() => cut.Find("button").ClassList.Should().Contain(GxSubmitButton.BusyClass));
        // The busy class stops the pointer in a browser; a keyboard press, or a click already on its way, still reaches
        // the handler, and RunAsync must refuse it. Not awaited before the work ends: without the guard each of these runs
        // the action and waits on the same unfinished work, and awaiting them here would hang the test instead of
        // failing it (pass 52, mutation M2).
        var second = cut.Find("button").ClickAsync(new());
        var third = cut.Find("button").ClickAsync(new());

        work.SetResult();
        await Task.WhenAll(first, second, third).WaitAsync(TimeSpan.FromSeconds(10));

        runs.Should().Be(1, "the work runs once however often the busy button is pressed");
    }

    [Test]
    public async Task WhenItsActionEnds_ThePageThatSuppliedIt_ShowsItsNewState()
    {
        // The page's state changes in the action (a dialog's form opens, a member is added); the page must re-render, as
        // it did when the work was a MudButton's OnClick.
        var host = _ctx.Render<ActionHost>();
        host.Find("[data-test=count]").TextContent.Should().Be("0");

        await host.Find("button").ClickAsync(new());

        host.WaitForAssertion(() => host.Find("[data-test=count]").TextContent.Should().Be("1",
            "the component that supplies Action re-renders when the work is done"));
    }

    /// <summary>A page that owns some state and changes it in the button's Action.</summary>
    private sealed class ActionHost : Microsoft.AspNetCore.Components.ComponentBase
    {
        private int _count;

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "span");
            builder.AddAttribute(1, "data-test", "count");
            builder.AddContent(2, _count);
            builder.CloseElement();
            builder.OpenComponent<GxSubmitButton>(3);
            builder.AddAttribute(4, nameof(GxSubmitButton.Label), "Add");
            builder.AddAttribute(5, nameof(GxSubmitButton.BusyLabel), "Adding…");
            builder.AddAttribute(6, nameof(GxSubmitButton.ButtonType), MudBlazor.ButtonType.Button);
            builder.AddAttribute(7, nameof(GxSubmitButton.Action),
                Microsoft.AspNetCore.Components.EventCallback.Factory.Create(this, async () => { await Task.Yield(); _count++; }));
            builder.CloseComponent();
        }
    }

    private static void AssertBusy(IRenderedComponent<GxSubmitButton> cut)
    {
        var button = cut.Find("button");
        button.ClassList.Should().Contain(GxSubmitButton.BusyClass, "a busy button is drawn by the busy class");
        button.HasAttribute("disabled").Should().BeFalse(
            "a busy button keeps its own colour; the disabled attribute is what MudBlazor draws grey");
        button.GetAttribute("aria-disabled").Should().Be("true", "assistive technology is told it cannot be pressed");
        button.GetAttribute("aria-busy").Should().Be("true");
        cut.Find("button .gx-busy-spinner").HasAttribute("hidden").Should().BeFalse();
        cut.Find("button .gx-busy-text").TextContent.Should().Be("Signing in…");
    }
}
#nullable restore
