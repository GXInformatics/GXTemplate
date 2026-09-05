#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Bunit;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Caching;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Application.Common.Models;
using CleanArchitecture.Blazor.Application.Common.Security;
using CleanArchitecture.Blazor.Application.Features.Tenants.DTOs;
using CleanArchitecture.Blazor.Infrastructure.Configurations;
using CleanArchitecture.Blazor.Server.UI.Components.AppShell;
using CleanArchitecture.Blazor.Server.UI.Models.NavigationMenu;
using CleanArchitecture.Blazor.Server.UI.Services;
using CleanArchitecture.Blazor.Server.UI.Services.Layout;
using CleanArchitecture.Blazor.Server.UI.Services.Navigation;
using CleanArchitecture.Blazor.Server.UI.Services.UserPreferences;
using FluentAssertions;
using Mapster;
using Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor.Services;
using NUnit.Framework;
using ConstantRoles = CleanArchitecture.Blazor.Application.Common.Constants.Roles;

namespace CleanArchitecture.Blazor.Server.UI.IntegrationTests;

/// <summary>
/// The navigation gate on the System menu, and on the Logs entry in particular.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pass 34 §2.3 reported the Logs entry as ungated "unlike its neighbours". That was wrong, and
/// this fixture is what establishes it.</b> The menu's gate is by ROLE NAME, applied at three levels
/// - section, item and sub-item - and the whole <c>MANAGEMENT</c> section carries
/// <c>Roles = [Admin]</c>. No sub-item anywhere in <c>MenuService</c> carries a gate of its own, so
/// Logs is gated exactly as Users, Roles, Picklist, Security Settings, Audit Trails and Jobs are.
/// Adding an item-level gate to Logs alone would have introduced the inconsistency the finding
/// described, not removed it.
/// </para>
/// <para>
/// <b>Menu by role, page by permission - and the gap is real but cosmetic.</b> Every page under this
/// section is gated by <c>[Authorize(Policy = Permissions.*)]</c> while the menu is gated by role
/// membership, so the two can disagree. That matters directly for the README's instruction to build
/// a customer-administrator role WITHOUT <c>Logs.*</c>: done by copying <c>Admin</c>, the user keeps
/// the role name, still sees the Logs link, and is refused at the page. The refusal is the boundary;
/// the link is a papercut. <see cref="ARoleHolderWhoLacksTheLogPermissionStillSeesTheLink"/> pins
/// that behaviour so it is a known consequence rather than a surprise.
/// </para>
/// <para>
/// <b>Circuit-level, necessarily.</b> The application renders at
/// <c>InteractiveServerRenderMode(prerender: false)</c>, so an HTTP response carries the shell and no
/// menu at all - an HTTP test could not see this either way.
/// </para>
/// </remarks>
[TestFixture]
public class SystemMenuGateComponentTests
{
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

        // The real menu definition - the thing under test. Nothing here is a stand-in for it.
        // MenuService's constructor takes IIdleTimeoutSettings because it DROPS the Security
        // Settings entry when the feature is off (that route 404s), so the flag is left on: the
        // gate under test is the section's role, and a missing entry for an unrelated reason would
        // muddy TheWholeManagementSectionMovesTogether.
        services.AddSingleton<IMenuService, MenuService>();
        services.AddSingleton(EnabledIdleTimeout());

        services.AddSingleton(new TypeAdapterConfig());
        services.AddSingleton(Mock.Of<IObjectMapper>());
        services.AddSingleton(Mock.Of<IValidationService>());
        services.AddSingleton(Mock.Of<IAppCache>());
        services.AddSingleton(Mock.Of<IPermissionService>());
        services.AddSingleton(Mock.Of<IMediator>());
        services.AddSingleton(Mock.Of<IAuthorizationService>());
        services.AddScoped<DialogServiceHelper>();
        services.AddScoped<LayoutService>();
        services.AddSingleton(Mock.Of<IUserPreferencesService>());

        var settings = new Mock<IApplicationSettings>();
        settings.SetupGet(x => x.AppName).Returns("GX");
        settings.SetupGet(x => x.Copyright).Returns("(c)");
        settings.SetupGet(x => x.Version).Returns("1.0.0");
        services.AddSingleton(settings.Object);

        var profile = new Mock<IUserProfileState>();
        profile.SetupGet(x => x.Value).Returns(UserProfile.Empty);
        services.AddSingleton(profile.Object);

        var switchService = new Mock<ITenantSwitchService>();
        switchService
            .Setup(x => x.GetSwitchableTenantsAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<TenantDto>());
        services.AddSingleton(switchService.Object);
    }

    [TearDown]
    public async Task TearDown() => await _ctx.DisposeAsync();

    private static IIdleTimeoutSettings EnabledIdleTimeout()
    {
        var idle = new Mock<IIdleTimeoutSettings>();
        idle.SetupGet(x => x.Enabled).Returns(true);
        return idle.Object;
    }

    private IRenderedComponent<NavigationMenu> RenderMenu(params string[] roles) =>
        _ctx.Render<NavigationMenu>(p => p
            .Add(x => x.DrawerOpen, true)
            .Add(x => x.Roles, roles));

    // ---- the gate ------------------------------------------------------------------------------

    [Test]
    public void WithoutTheAdminRole_TheLogsEntryIsNotRendered()
    {
        var markup = RenderMenu().Markup;

        markup.Should().NotContain("/system/logs",
            "the MANAGEMENT section is gated on the Admin role and Logs sits inside it");
    }

    [Test]
    public void WithTheAdminRole_TheLogsEntryIsRendered()
    {
        // Narrowed, not emptied: the negative above must not be satisfied by a menu that renders
        // nothing at all.
        RenderMenu(ConstantRoles.Admin).Markup.Should().Contain("/system/logs");
    }

    [Test]
    public void TheWholeManagementSectionMovesTogether()
    {
        // Logs is gated identically to its neighbours - which is the correction to Pass 34 §2.3.
        // If a future change gates one of them individually, this is where it shows up.
        var withoutAdmin = RenderMenu().Markup;
        var withAdmin = RenderMenu(ConstantRoles.Admin).Markup;

        foreach (var href in new[]
                 {
                     "/system/tenants", "/identity/users", "/identity/roles",
                     "/system/picklistset", "/system/security-settings",
                     "/system/audittrails", "/system/logs"
                 })
        {
            withoutAdmin.Should().NotContain(href);
            withAdmin.Should().Contain(href);
        }
    }

    [Test]
    public void AnUnrelatedRoleDoesNotOpenTheSection()
    {
        RenderMenu(ConstantRoles.Basic).Markup.Should().NotContain("/system/logs");
    }

    // ---- the idiom -----------------------------------------------------------------------------

    [Test]
    public void NoMenuEntryCarriesAGateOfItsOwn()
    {
        // The idiom, asserted rather than described: exactly one gate exists in the whole menu, at
        // the MANAGEMENT section. A pass that "fixed" the Logs entry by giving it its own Roles
        // array would fail here, which is the point - the fix would have been the inconsistency.
        var sections = new MenuService(EnabledIdleTimeout()).Features.ToList();

        sections.SelectMany(s => s.SectionItems ?? new List<MenuSectionItemModel>())
            .Should().OnlyContain(i => i.Roles == null, "section items inherit the section's gate");

        sections.SelectMany(s => s.SectionItems ?? new List<MenuSectionItemModel>())
            .SelectMany(i => i.MenuItems ?? new List<MenuSectionSubItemModel>())
            .Should().OnlyContain(m => m.Roles == null, "sub-items inherit it too");

        sections.Count(s => s.Roles is { Length: > 0 })
            .Should().Be(1, "only MANAGEMENT is gated, and it is gated on a role");
    }

    [Test]
    public void ARoleHolderWhoLacksTheLogPermissionStillSeesTheLink()
    {
        // The documented consequence of the README's instruction. The menu cannot express
        // "has Logs.View" - MenuSectionSubItemModel has a Roles array and no permission field - so a
        // customer administrator built by copying Admin and dropping the three log rights keeps the
        // link and meets the refusal at the page. Asserted so that it is a known cost of the
        // recommendation rather than a defect somebody rediscovers.
        //
        // IPermissionService is a mock returning nothing here: the menu never consults it, which is
        // exactly the point being recorded.
        RenderMenu(ConstantRoles.Admin).Markup.Should().Contain("/system/logs");
    }
}
