#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Caching;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Application.Common.Security;
using CleanArchitecture.Blazor.Server.UI.Components.Theming;
using CleanArchitecture.Blazor.Server.UI.Layouts;
using CleanArchitecture.Blazor.Server.UI.Services;
using CleanArchitecture.Blazor.Server.UI.Services.Layout;
using CleanArchitecture.Blazor.Server.UI.Services.UserPreferences;
using FluentAssertions;
using Mapster;
using Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;
using MudBlazor;
using MudBlazor.Extensions;
using MudBlazor.Services;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Server.UI.IntegrationTests;

/// <summary>
/// Pass 52: the app bar's light/dark toggle, which replaced the theme drawer.
/// </summary>
/// <remarks>
/// Each test renders the real <see cref="MainLayout"/> (which resolves the theme and hosts
/// <see cref="MudThemeProvider"/>) around the real <see cref="ThemeToggle"/>, over the real
/// <see cref="UserPreferencesService"/> and <see cref="ProtectedLocalStorage"/>. Only the browser is simulated: its
/// localStorage by <see cref="FakeBrowserStorage"/>, and its <c>prefers-color-scheme</c> by answering MudBlazor's
/// <c>mudThemeProvider.isDarkMode</c>. One <see cref="Browser"/> shared by two contexts is one browser seen by two
/// visits.
/// </remarks>
[TestFixture]
public class ThemeToggleComponentTests
{
    private const string StoreKey = "userPreferences";
    private const string Toggle = "button[data-test=\"theme-toggle\"]";

    private readonly List<BunitContext> _contexts = [];

    [TearDown]
    public async Task TearDown()
    {
        foreach (var ctx in _contexts) await ctx.DisposeAsync();
        _contexts.Clear();
    }

    [Test]
    public void ClickingTheToggle_FlipsTheMode_AndSwapsTheIconTooltipAndLabel_AndWritesTheChoice()
    {
        var browser = new Browser();
        var cut = Visit(browser, deviceIsDark: false);

        ShouldBeLight(cut);

        cut.Find(Toggle).Click();

        ShouldBeDark(cut);
        browser.Read().Should().NotBeNull("the choice is written to the preference store")
            .And.BeEquivalentTo(new UserPreference { IsDarkMode = true });

        cut.Find(Toggle).Click();

        ShouldBeLight(cut);
        browser.Read()!.IsDarkMode.Should().BeFalse("the second click is saved too");
    }

    [Test]
    public void TheChoice_IsReadBack_ByAFreshRender()
    {
        var browser = new Browser();
        Visit(browser, deviceIsDark: false).Find(Toggle).Click();

        // A new circuit in the same browser: nothing survives but the store.
        var again = Visit(browser, deviceIsDark: false);

        ShouldBeDark(again);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void WithNoSavedChoice_TheInitialMode_FollowsTheDevice(bool deviceIsDark)
    {
        var cut = Visit(new Browser(), deviceIsDark);

        if (deviceIsDark) ShouldBeDark(cut); else ShouldBeLight(cut);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void ASavedChoice_OutranksTheDevice(bool savedDark)
    {
        var browser = new Browser();
        browser.Write(new UserPreference { IsDarkMode = savedDark });

        var cut = Visit(browser, deviceIsDark: !savedDark);

        if (savedDark) ShouldBeDark(cut); else ShouldBeLight(cut);
    }

    /// <summary>
    /// What the old drawer saved: every field it had, with its mode (0 System, 1 Light, 2 Dark) and an
    /// <c>isDarkMode</c> that disagrees with the expected result, so reading the wrong field fails.
    /// </summary>
    [TestCase(0, true, false, false, TestName = "AnOldDrawerPreference_System_FollowsTheDevice_Light")]
    [TestCase(0, false, true, true, TestName = "AnOldDrawerPreference_System_FollowsTheDevice_Dark")]
    [TestCase(1, true, true, false, TestName = "AnOldDrawerPreference_Light_IsLight")]
    [TestCase(2, false, false, true, TestName = "AnOldDrawerPreference_Dark_IsDark")]
    public void AnOldDrawerPreference_LoadsHarmlessly(int mode, bool storedIsDarkMode, bool deviceIsDark, bool expectDark)
    {
        var browser = new Browser();
        browser.Write(new
        {
            IsDarkMode = storedIsDarkMode,
            RightToLeft = true,
            PrimaryColor = "#7c3aed",
            DarkPrimaryColor = "#8b5cf6",
            BorderRadius = 20.0,
            DefaultFontSize = 18.0,
            DarkLightTheme = mode,
            PrimaryDarken = "#6d28d9",
            PrimaryLighten = "#8b5cf6",
            PrimaryContrastText = "#ffffff"
        });

        var cut = Visit(browser, deviceIsDark);

        if (expectDark) ShouldBeDark(cut); else ShouldBeLight(cut);
        cut.FindComponent<MudThemeProvider>().Instance.Theme!.LayoutProperties.DefaultBorderRadius
            .Should().Be("4px", "the old drawer's radius is ignored; the theme is fixed");
    }

    [Test]
    public void NothingIsRendered_UntilTheThemeIsKnown()
    {
        var ctx = NewContext(new Browser());
        var device = ctx.JSInterop.Setup<bool>("mudThemeProvider.isDarkMode");

        var cut = RenderLayout(ctx);

        cut.FindAll(Toggle).Should().BeEmpty("nothing is painted before the theme is known, so nothing is painted wrong");
        cut.FindComponents<MudThemeProvider>().Should().BeEmpty();

        device.SetResult(true);

        cut.WaitForAssertion(() => ShouldBeDark(cut));
    }

    private static void ShouldBeDark(IRenderedComponent<MainLayout> cut) => ShouldBe(cut, dark: true);

    private static void ShouldBeLight(IRenderedComponent<MainLayout> cut) => ShouldBe(cut, dark: false);

    private static void ShouldBe(IRenderedComponent<MainLayout> cut, bool dark)
    {
        var label = dark ? "Switch to light theme" : "Switch to dark theme";

        cut.FindComponent<MudThemeProvider>().Instance.GetState(x => x.IsDarkMode).Should().Be(dark);
        cut.Find(Toggle).GetAttribute("aria-label").Should().Be(label);
        cut.FindComponent<ThemeToggle>().FindComponent<MudTooltip>().Instance.Text.Should().Be(label);
        cut.FindComponent<ThemeToggle>().FindComponent<MudIconButton>().Instance.Icon
            .Should().Be(dark ? Icons.Material.Filled.LightMode : Icons.Material.Filled.DarkMode,
                "the icon shows what a click will do: a sun in the dark theme, a moon in the light");
    }

    /// <summary>A visit: a new circuit in <paramref name="browser"/>, on a device that prefers dark or light.</summary>
    private IRenderedComponent<MainLayout> Visit(Browser browser, bool deviceIsDark)
    {
        var ctx = NewContext(browser);
        ctx.JSInterop.Setup<bool>("mudThemeProvider.isDarkMode").SetResult(deviceIsDark);
        return RenderLayout(ctx);
    }

    private static IRenderedComponent<MainLayout> RenderLayout(BunitContext ctx) =>
        ctx.Render<MainLayout>(p => p.Add(x => x.Body, (RenderFragment)(b =>
        {
            b.OpenComponent<ThemeToggle>(0);
            b.CloseComponent();
        })));

    private BunitContext NewContext(Browser browser)
    {
        var ctx = new BunitContext();
        _contexts.Add(ctx);
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var services = ctx.Services;
        services.AddLogging();
        services.AddLocalization();
        services.AddMudServices();

        // The theme's own chain, all real but the browser.
        services.AddScoped(_ => browser.Storage());
        services.AddScoped<IUserPreferencesService, UserPreferencesService>();
        services.AddScoped<LayoutService>();

        // The root _Imports.razor injects these into every component.
        services.AddSingleton(Mock.Of<IUserProfileState>());
        services.AddSingleton(Mock.Of<IApplicationSettings>());
        services.AddSingleton(Mock.Of<IAuthorizationService>());
        services.AddSingleton(Mock.Of<IValidationService>());
        services.AddSingleton(Mock.Of<IMediator>());
        services.AddSingleton(Mock.Of<IAppCache>());
        services.AddSingleton(Mock.Of<IPermissionService>());
        services.AddSingleton(Mock.Of<IObjectMapper>());
        services.AddSingleton(new TypeAdapterConfig());
        services.AddScoped<DialogServiceHelper>();
        return ctx;
    }

    /// <summary>One browser: its localStorage and the data-protection keys that the server protects it with.</summary>
    private sealed class Browser
    {
        private readonly FakeBrowserStorage _localStorage = new();
        private readonly IDataProtectionProvider _keys = new EphemeralDataProtectionProvider();

        public ProtectedLocalStorage Storage() => new(_localStorage, _keys);

        public void Write(object value) => Storage().SetAsync(StoreKey, value).AsTask().GetAwaiter().GetResult();

        public UserPreference? Read()
        {
            var result = Storage().GetAsync<UserPreference>(StoreKey).AsTask().GetAwaiter().GetResult();
            return result.Success ? result.Value : null;
        }
    }

    /// <summary>A browser's localStorage, as <see cref="ProtectedLocalStorage"/> reaches it.</summary>
    private sealed class FakeBrowserStorage : IJSRuntime
    {
        private readonly Dictionary<string, string> _items = new();

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            var key = (string)args![0]!;
            switch (identifier)
            {
                case "localStorage.setItem":
                    _items[key] = (string)args[1]!;
                    return ValueTask.FromResult(default(TValue)!);
                case "localStorage.getItem":
                    return ValueTask.FromResult((TValue)(object?)_items.GetValueOrDefault(key)!);
                case "localStorage.removeItem":
                    _items.Remove(key);
                    return ValueTask.FromResult(default(TValue)!);
                default:
                    throw new NotSupportedException(identifier);
            }
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);
    }
}
#nullable restore
