#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Bunit;
using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Caching;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Application.Common.Models;
using CleanArchitecture.Blazor.Application.Common.Security;
using CleanArchitecture.Blazor.Infrastructure.Configurations;
using CleanArchitecture.Blazor.Server.UI.Pages.Identity.Roles.Components;
using CleanArchitecture.Blazor.Server.UI.Services;
using CleanArchitecture.Blazor.Server.UI.Services.UserPreferences;
using CleanArchitecture.Blazor.Server.UI.Services.Layout;
using FluentAssertions;
using Mapster;
using Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor.Services;
using NUnit.Framework;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;

namespace CleanArchitecture.Blazor.Server.UI.IntegrationTests;

/// <summary>
/// The installation-wide warning on the three <c>Logs.*</c> rights actually reaches the screen an
/// administrator grants them on.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only rendering can see this, and the path is longer than it looks.</b> The
/// <c>[Description]</c> on each constant does NOT become <c>PermissionModel.Description</c> - that
/// field carries the module's class-level attribute and renders as the group header.
/// <c>PermissionQueryService</c> puts the per-constant attribute into <c>HelpText</c>, and
/// <c>PermissionsDrawer</c> renders <c>HelpText</c> under the permission's name. A pass that changed
/// the attribute and assumed it surfaced would have been half right and would not have known which
/// half.
/// </para>
/// <para>
/// <b>The models are built from the real attributes by the same reflection the service uses</b>,
/// rather than hand-written strings, so this test cannot pass against wording the application does
/// not actually carry.
/// </para>
/// </remarks>
[TestFixture]
public class LogPermissionDescriptionComponentTests
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
        // _Imports.razor property-injects this set into EVERY component, so they all have to
        // resolve even though the drawer uses none of them - it takes its input as a parameter.
        services.AddSingleton(new TypeAdapterConfig());
        services.AddSingleton(Mock.Of<IObjectMapper>());
        services.AddSingleton(Mock.Of<IApplicationSettings>());
        services.AddSingleton(Mock.Of<IValidationService>());
        services.AddSingleton(Mock.Of<IAppCache>());
        services.AddSingleton(Mock.Of<IPermissionService>());
        services.AddSingleton(Mock.Of<IMediator>());
        services.AddSingleton(Mock.Of<IAuthorizationService>());
        services.AddScoped<DialogServiceHelper>();

        var profileState = new Mock<IUserProfileState>();
        profileState.SetupGet(x => x.Value).Returns(UserProfile.Empty);
        services.AddSingleton(profileState.Object);
    }

    [TearDown]
    public async Task TearDown() => await _ctx.DisposeAsync();

    /// <summary>
    /// Builds the drawer's input exactly as <c>PermissionQueryService.BuildRolePermissionModels</c>
    /// does for one module: <c>Group</c> and <c>Description</c> from the class, <c>Name</c> and
    /// <c>HelpText</c> from each constant.
    /// </summary>
    private static IList<PermissionModel> LogPermissionModels()
    {
        var module = typeof(Permissions.Logs);
        var moduleDescription =
            module.GetCustomAttributes<DescriptionAttribute>().FirstOrDefault()?.Description ?? string.Empty;
        var groupName =
            module.GetCustomAttributes<DisplayNameAttribute>().FirstOrDefault()?.DisplayName ?? module.Name;

        return module
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.FieldType == typeof(string))
            .Select(f => new PermissionModel
            {
                RoleId = "role-under-edit",
                ClaimType = ApplicationClaimTypes.Permission,
                ClaimValue = (string)f.GetValue(null)!,
                Group = groupName,
                Name = f.Name,
                HelpText = f.GetCustomAttributes<DescriptionAttribute>().FirstOrDefault()?.Description
                           ?? string.Empty,
                Description = moduleDescription,
                Assigned = false
            })
            .ToList();
    }

    private IRenderedComponent<PermissionsDrawer> RenderDrawer() =>
        _ctx.Render<PermissionsDrawer>(p => p
            .Add(x => x.Permissions, LogPermissionModels())
            .Add(x => x.Open, true));

    [Test]
    public void TheDrawerRendersThreeLogRights()
    {
        // Guards the harness itself: if the drawer stopped rendering help text at all, every
        // assertion below would fail for a reason that has nothing to do with the wording.
        var models = LogPermissionModels();

        models.Should().HaveCount(3, "View, Search and Purge - Logs.Export was deleted in Pass 11C");
        models.Should().OnlyContain(m => !string.IsNullOrEmpty(m.HelpText));
    }

    [Test]
    public void ViewSaysItSpansEveryTenant()
    {
        var markup = RenderDrawer().Markup;

        markup.Should().Contain("every tenant's activity",
            "this is the sentence an administrator reads while deciding to grant Logs.View");
    }

    [Test]
    public void PurgeSaysItErasesEveryTenantsHistory()
    {
        RenderDrawer().Markup.Should().Contain("permanently erasing the entire system log");
    }

    [Test]
    public void SearchSaysItCrossesTenants()
    {
        RenderDrawer().Markup.Should().Contain("across every tenant");
    }

    [Test]
    public void TheGroupHeaderCarriesTheScopeToo()
    {
        // A separate string on a separate path - PermissionModel.Description - shown above the three
        // checkboxes, so a reader skimming groups meets it before any help text.
        RenderDrawer().Markup.Should().Contain("all of them installation-wide");
    }

    [Test]
    public void NothingInTheDrawerPromisesATenantScopedLog()
    {
        // The inverse control: the failure mode of rewording is an affirmative false statement,
        // which is worse than the vague original it replaced.
        var markup = RenderDrawer().Markup;

        markup.Should().NotContainAny("your tenant", "your organisation", "own organisation");
    }
}
