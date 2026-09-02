#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using Bunit;
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
/// That the self-service profile page hands out no staff directory.
/// </summary>
/// <remarks>
/// Pass 19 found <c>/user/profile</c> rendering an Org Chart tab whose component loaded EVERY user
/// in the installation - <c>UserManager.Users.Include(UserRoles).ThenInclude(Role).Include(Superior)</c>,
/// no permission check and no tenant filter - and projected display name, roles, tenant, profile
/// picture, <b>email and phone number</b> into a chart that paints the email on every node
/// (<c>wwwroot/js/orgchart.js:125,176-178</c>). The page carries no <c>[Authorize(Policy = …)]</c>,
/// only the route fallback policy, so a self-registered Basic account - whose entire grant is
/// Documents.View and Documents.Download - obtained the complete directory of every tenant by
/// visiting its own profile. Two defects at once: a permission gap and a tenant-isolation break.
/// <para>
/// Pass 21 removed the tab rather than gating or filtering it. An org chart of the whole
/// organisation is not "your profile"; if one is wanted later it belongs on a page of its own with
/// its own gate, where the question of who may see it has to be answered explicitly.
/// </para>
/// <para>
/// These assert on RENDERED output and on the assembly's own types, because that is the only place
/// this is visible: the application renders at <c>InteractiveServerRenderMode(prerender: false)</c>,
/// so an HTTP response carries the shell and none of the tabs - an HTTP test would have passed
/// against the broken page.
/// </para>
/// </remarks>
[TestFixture]
public class ProfileDirectoryExposureComponentTests
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

        // The idle-timeout feature fully on, so the page declares the most tabs it ever can. If an
        // Org Chart tab can appear at all, it appears under this arrangement.
        services.AddSingleton<IIdleTimeoutSettings>(new IdleTimeoutSettings
        {
            Enabled = true,
            AllowUserOverride = true
        });
        services.AddSingleton(Mock.Of<IIdleTimeoutPolicyProvider>());

        // Tab CONTENTS are not under test; stubbing keeps this about which panels the page declares.
        _ctx.ComponentFactories.AddStub<ProfileInformationTab>();
        _ctx.ComponentFactories.AddStub<ChangePasswordTab>();
        _ctx.ComponentFactories.AddStub<SecurityTab>();
    }

    [TearDown]
    public async Task TearDown() => await _ctx.DisposeAsync();

    [Test]
    public void TheProfilePage_OffersNoOrgChartTab()
    {
        var page = _ctx.Render<Profile>();

        page.Markup.Should().NotContain("Org Chart",
            "the profile page must not offer a view of other people at all - it is reachable by "
            + "every authenticated user, including a self-registered account with no permissions");
    }

    /// <summary>
    /// The durable half: the components are gone, not merely unreferenced. This is what stops the
    /// tab being re-added by restoring one line to Profile.razor.
    /// </summary>
    [TestCase("OrgChartTab", "the component that loaded every user in every tenant")]
    [TestCase("OrgChart", "the JS interop wrapper that shipped the directory to the browser")]
    public void TheOrgChartComponents_AreGoneFromServerUi(string typeName, string why)
    {
        var found = typeof(Profile).Assembly.GetTypes()
            .Where(t => t.Name == typeName)
            .Select(t => t.FullName)
            .ToArray();

        found.Should().BeEmpty($"{typeName} was removed in Pass 21 - {why}");
    }

    [Test]
    public void TheOrgItemProjection_IsGone()
    {
        // OrgItem is the shape that carried Email and PhoneNumber to the browser. It lived in the
        // Application layer, so its absence is asserted against that assembly rather than Server.UI.
        var found = typeof(IApplicationSettings).Assembly.GetTypes()
            .Where(t => t.Name == "OrgItem")
            .Select(t => t.FullName)
            .ToArray();

        found.Should().BeEmpty("OrgItem carried Email and PhoneNumber for every user in the installation");
    }
}
#nullable restore
