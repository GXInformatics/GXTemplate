#nullable enable
using System.Linq;
using System.Threading.Tasks;
using Bunit;
using Bunit.TestDoubles;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Caching;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Domain.Identity;
using CleanArchitecture.Blazor.Infrastructure.Configurations;
using CleanArchitecture.Blazor.Server.UI.Pages.Identity.Users;
using CleanArchitecture.Blazor.Server.UI.Pages.Identity.Users.Components;
using CleanArchitecture.Blazor.Server.UI.Services;
using CleanArchitecture.Blazor.Server.UI.Services.Layout;
using CleanArchitecture.Blazor.Server.UI.Services.UserPreferences;
using FluentAssertions;
using Mapster;
using Mediator;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor.Services;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Server.UI.IntegrationTests;

/// <summary>
/// Whether the profile page offers a Security tab at all.
/// </summary>
/// <remarks>
/// This can only be seen by rendering. The page answers 200 either way - the app renders at
/// <c>InteractiveServerRenderMode(prerender: false)</c>, so an HTTP response carries the shell and
/// none of the tabs - which is exactly how Pass 16A found an **empty** Security tab shipping while
/// every HTTP test stayed green.
/// <para>
/// The tab is absent, not disabled, in both off states: the feature switched off entirely, and user
/// overrides switched off. A greyed-out or empty tab invites a support call asking what belongs in
/// it, and neither state is something the user can act on.
/// </para>
/// </remarks>
[TestFixture]
public class ProfileSecurityTabComponentTests
{
    private BunitContext _ctx = null!;

    [TearDown]
    public async Task TearDown() => await _ctx.DisposeAsync();

    private void Arrange(bool enabled, bool allowUserOverride)
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
        services.AddSingleton(Mock.Of<IUserStore<ApplicationUser>>());
        services.AddIdentityCore<ApplicationUser>();

        // The real settings object, so the page reads the same shape production does.
        services.AddSingleton<IIdleTimeoutSettings>(new IdleTimeoutSettings
        {
            Enabled = enabled,
            AllowUserOverride = allowUserOverride
        });
        services.AddSingleton(Mock.Of<IIdleTimeoutPolicyProvider>());

        // The tab CONTENTS are not under test and drag in the whole profile stack; stubbing them
        // keeps this about which panels the page declares.
        _ctx.ComponentFactories.AddStub<ProfileInformationTab>();
        _ctx.ComponentFactories.AddStub<ChangePasswordTab>();
        _ctx.ComponentFactories.AddStub<OrgChartTab>();
        _ctx.ComponentFactories.AddStub<SecurityTab>();
    }

    private static bool HasSecurityTab(IRenderedComponent<Profile> page) =>
        page.Markup.Contains("Security", System.StringComparison.Ordinal);

    [Test]
    public void WhenEnabledAndOverridesAllowed_TheSecurityTabIsOffered()
    {
        Arrange(enabled: true, allowUserOverride: true);

        var page = _ctx.Render<Profile>();

        HasSecurityTab(page).Should().BeTrue("the tab is the only place a user can shorten their own window");
    }

    [Test]
    public void WhenTheFeatureIsDisabled_TheSecurityTabIsAbsent()
    {
        Arrange(enabled: false, allowUserOverride: true);

        var page = _ctx.Render<Profile>();

        HasSecurityTab(page).Should().BeFalse("an empty tab is worse than no tab");
        page.FindComponents<Stub<SecurityTab>>().Should().BeEmpty();
    }

    [Test]
    public void WhenUserOverridesAreDisallowed_TheSecurityTabIsAbsent()
    {
        // AllowUserOverride: false is a decision that this is not the user's to set. A tab that
        // renders nothing says the opposite - that there should be something there.
        Arrange(enabled: true, allowUserOverride: false);

        var page = _ctx.Render<Profile>();

        HasSecurityTab(page).Should().BeFalse();
        page.FindComponents<Stub<SecurityTab>>().Should().BeEmpty();
    }

    [Test]
    public void TheOtherTabs_AreUnaffectedInEveryState()
    {
        // The blast radius: gating one panel must not drop the others. Asserted on the tab HEADERS,
        // because MudTabs renders only the active panel's content - the reason the first attempt at
        // this test failed looking for a stub that was never going to be in the DOM.
        foreach (var (enabled, allowOverride) in new[] { (true, true), (false, true), (true, false) })
        {
            Arrange(enabled, allowOverride);
            var markup = _ctx.Render<Profile>().Markup;

            markup.Should().Contain("Change Password", $"enabled={enabled} allowOverride={allowOverride}");
            markup.Should().Contain("Org Chart", $"enabled={enabled} allowOverride={allowOverride}");

            _ctx.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
