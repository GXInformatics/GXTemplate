#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Bunit;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Caching;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Application.Common.Security;
using CleanArchitecture.Blazor.Application.Features.Tenants.DTOs;
using CleanArchitecture.Blazor.Infrastructure.Configurations;
using CleanArchitecture.Blazor.Server.UI.Components.AppShell;
using CleanArchitecture.Blazor.Server.UI.Services;
using FluentAssertions;
using Mapster;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using MudBlazor.Services;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Server.UI.IntegrationTests;

/// <summary>
/// What the tenant switcher offers, and what it does when there is nothing to offer.
/// </summary>
/// <remarks>
/// The menu used to be gated on <c>Permissions.Users.SwitchTenants</c> alone, so a holder of
/// <c>SwitchToAnyTenant</c> - whose whole capability is switching into tenants they do not belong
/// to - got a DISABLED menu. And its list came from <c>UserProfile.AvailableTenants</c>, a
/// membership-only projection, so even an enabled menu had nothing extra to show them. The escalated
/// permission had no interface at all.
/// <para>
/// Both halves now come from <c>ITenantSwitchService.GetSwitchableTenantsAsync</c>, which derives
/// from the same rule as <c>CanSwitchToTenantAsync</c>. The gate is simply "is that list non-empty",
/// so the ladder is implemented once, in the service, rather than badly restated here.
/// </para>
/// </remarks>
[TestFixture]
public class TenantSelectorComponentTests
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";

    private BunitContext _ctx = null!;

    [TearDown]
    public async Task TearDown() => await _ctx.DisposeAsync();

    private void Arrange(params string[] switchableTenantNames)
    {
        _ctx = new BunitContext();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var services = _ctx.Services;
        services.AddLogging();
        services.AddLocalization();
        services.AddMudServices();

        // The globally-injected services from _Imports.razor. None participates in what is under
        // test; the component simply has to be constructible.
        services.AddSingleton(new TypeAdapterConfig());
        services.AddSingleton(Mock.Of<IValidationService>());
        services.AddSingleton(Mock.Of<IMediator>());
        services.AddSingleton(Mock.Of<IAppCache>());
        services.AddSingleton(Mock.Of<IPermissionService>());
        services.AddSingleton(Mock.Of<IObjectMapper>());
        services.AddScoped<DialogServiceHelper>();
        services.AddScoped<CleanArchitecture.Blazor.Server.UI.Services.Layout.LayoutService>();
        services.AddSingleton(Mock.Of<CleanArchitecture.Blazor.Server.UI.Services.UserPreferences.IUserPreferencesService>());

        var settings = new Mock<IApplicationSettings>();
        settings.SetupGet(x => x.AppName).Returns("GX");
        services.AddSingleton(settings.Object);

        var profile = new Mock<IUserProfileState>();
        profile.SetupGet(x => x.Value).Returns(new UserProfile(
            "u1", "u1", "u1@x.com",
            TenantId: TenantA,
            Tenant: new TenantDto { Id = TenantA, Name = "Tenant A" },
            AvailableTenants: new List<TenantDto>()));
        services.AddSingleton(profile.Object);

        var switchService = new Mock<ITenantSwitchService>();
        switchService.Setup(x => x.GetSwitchableTenantsAsync(It.IsAny<string>()))
            .ReturnsAsync(switchableTenantNames
                .Select(n => new TenantDto { Id = n, Name = n })
                .ToList());
        services.AddSingleton(switchService.Object);
    }

    /// <summary>The selector's own markup — the activator and, when it cannot switch, plain content.</summary>
    private string Render() => _ctx.Render<TenantSelector>().Markup;

    /// <summary>
    /// The selector with its menu opened, and the popover's markup included.
    /// </summary>
    /// <remarks>
    /// <c>MudMenu</c> renders its items into <c>MudPopoverProvider</c> rather than inline, so the
    /// tenant list is not in the component's own markup until the menu is opened and the provider is
    /// present. Same shape as <c>MudDialog</c> handing its body to the dialog instance.
    /// </remarks>
    private string RenderOpened()
    {
        var popovers = _ctx.Render<MudPopoverProvider>();
        var selector = _ctx.Render<TenantSelector>();

        // The activator DIV carries only a keydown handler; the click lives on the chevron button
        // inside it, which is what is wired to the menu's ToggleAsync.
        selector.Find(".mud-menu-activator button").Click();

        return selector.Markup + popovers.Markup;
    }

    // ---- the three principal shapes ----------------------------------------------------------------

    [Test]
    public void ACrossTenantHolder_IsOfferedEveryTenant()
    {
        // The service returns all tenants for a SwitchToAnyTenant holder; the menu shows them.
        // RED before Pass 28: the menu was disabled and listed membership only.
        Arrange(TenantA, TenantB);

        var markup = RenderOpened();

        markup.Should().Contain(TenantA).And.Contain(TenantB);
        HasSwitchMenu(markup).Should().BeTrue();
    }

    [Test]
    public void AMembershipHolder_IsOfferedTheirOwnTenants()
    {
        Arrange(TenantA);

        var markup = RenderOpened();

        markup.Should().Contain(TenantA);
        markup.Should().NotContain(TenantB);
        HasSwitchMenu(markup).Should().BeTrue();
    }

    [Test]
    public void APrincipalWhoCannotSwitch_SeesTheOrganisationNameWithNoMenu()
    {
        // Pass 28 §A.4: the gate removes the ACTION, not the INFORMATION. A disabled menu tells a
        // user they are missing something without telling them what; the organisation name is
        // something everyone needs to see. "Absent, never disabled" - the template's own precedent
        // from Pass 16A's Security tab and Pass 25's deactivation toggle.
        Arrange();

        // Plain Render, not RenderOpened: there is no activator to click, which is itself the point.
        var markup = Render();

        markup.Should().Contain("Tenant A", "the organisation name is information, not a control");
        HasSwitchMenu(markup).Should().BeFalse("there is nothing to switch to");
        markup.Should().NotContain("mud-menu-activator", "the control is absent, not disabled");
    }

    // ---- narrowed, not emptied -----------------------------------------------------------------------

    [Test]
    public void TheMenuShowsEveryTenantTheServiceOffers()
    {
        // A menu that rendered only the current tenant would satisfy "does not show B" in the
        // membership case above while removing a real capability.
        Arrange(TenantA, TenantB);

        var markup = RenderOpened();

        markup.Should().Contain(TenantA);
        markup.Should().Contain(TenantB);
    }

    [Test]
    public void TheSwitcherAsksTheServiceAndNotTheUserProfile()
    {
        // UserProfile.AvailableTenants is empty in every arrangement here, so anything the menu
        // shows can only have come from the service. That is the point: the profile's list is
        // membership-only and could never serve a cross-tenant holder.
        Arrange(TenantA, TenantB);

        RenderOpened().Should().Contain(TenantB, "the profile offers nothing, so this came from the service");
    }

    /// <summary>
    /// Whether the switch menu is present at all.
    /// </summary>
    /// <remarks>
    /// Keyed on the menu's own heading rather than on a class name: the no-switch branch renders the
    /// same organisation name in plain markup, so counting elements would not distinguish them.
    /// </remarks>
    private static bool HasSwitchMenu(string markup) =>
        markup.Contains("Switch Organization", StringComparison.Ordinal);
}
#nullable restore
