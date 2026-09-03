#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Caching;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Application.Common.Models;
using CleanArchitecture.Blazor.Application.Common.Security;
using CleanArchitecture.Blazor.Application.Features.AuditTrails.DTOs;
using CleanArchitecture.Blazor.Application.Features.AuditTrails.Queries.PaginationQuery;
using CleanArchitecture.Blazor.Infrastructure.Configurations;
using CleanArchitecture.Blazor.Server.UI.Pages.SystemManagement;
using CleanArchitecture.Blazor.Server.UI.Services;
using CleanArchitecture.Blazor.Server.UI.Services.Layout;
using CleanArchitecture.Blazor.Server.UI.Services.UserPreferences;
using FluentAssertions;
using Mapster;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor.Services;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Server.UI.IntegrationTests;

/// <summary>
/// Whether the audit-trail search controls are reachable without
/// <c>Permissions.AuditTrails.Search</c>.
/// </summary>
/// <remarks>
/// <b>Only rendering can see this.</b> The permission was granted to the administrator, listed in the
/// role editor as a revocable right, and checked by nothing - while the page had already loaded
/// <c>AuditTrailsAccessRights</c> and never read it. The app renders at
/// <c>InteractiveServerRenderMode(prerender: false)</c>, so an HTTP response carries the shell and
/// none of the toolbar.
/// <para>
/// The <b>list-view</b> selector beside it is deliberately NOT gated: choosing "My change histories"
/// or a date window is part of viewing the trail, not of searching it. That is why these tests key
/// on the audit-type placeholder rather than counting selects - the page renders two, and only one
/// of them is behind the permission.
/// </para>
/// </remarks>
[TestFixture]
public class SearchPermissionComponentTests
{
    private BunitContext _ctx = null!;

    [TearDown]
    public async Task TearDown() => await _ctx.DisposeAsync();

    private void Arrange(bool canSearch)
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
        services.AddSingleton(Mock.Of<IValidationService>());
        services.AddSingleton(Mock.Of<IAppCache>());
        services.AddSingleton(Mock.Of<IObjectMapper>());

        // The profile the page hands to its query. UserProfile.Empty is enough - nothing here
        // asserts on the rows.
        var profileState = new Mock<IUserProfileState>();
        profileState.SetupGet(x => x.Value).Returns(UserProfile.Empty);
        services.AddSingleton(profileState.Object);

        // An empty page of results, so the grid's ServerData completes rather than throwing.
        var mediator = new Mock<IMediator>();
        mediator.Setup(x => x.Send(It.IsAny<AuditTrailsWithPaginationQuery>(), It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<PaginatedData<AuditTrailDto>>(
                new PaginatedData<AuditTrailDto>(Array.Empty<AuditTrailDto>(), 0, 1, 10)));
        services.AddSingleton(mediator.Object);

        // The one thing under test.
        var permissions = new Mock<IPermissionService>();
        permissions.Setup(x => x.GetAccessRightsAsync<AuditTrailsAccessRights>())
            .ReturnsAsync(new AuditTrailsAccessRights { View = true, Search = canSearch });
        services.AddSingleton(permissions.Object);
    }

    /// <summary>
    /// The audit-type filter's placeholder, which renders only inside the gated block.
    /// </summary>
    private const string GatedControlMarker = "Search by audit type";

    [Test]
    public void WithoutTheSearchPermission_TheSearchControlsAreAbsent()
    {
        // RED before Pass 26: the controls rendered for every holder of AuditTrails.View.
        Arrange(canSearch: false);

        var page = _ctx.Render<AuditTrails>();

        page.Markup.Should().NotContain(GatedControlMarker,
            "a holder of AuditTrails.View alone must not reach the search controls");
    }

    [Test]
    public void WithTheSearchPermission_TheSearchControlsArePresent()
    {
        Arrange(canSearch: true);

        var page = _ctx.Render<AuditTrails>();

        page.Markup.Should().Contain(GatedControlMarker,
            "the permission is what the capability is named after");
    }

    [Test]
    public void TheRestOfThePageIsUnaffectedEitherWay()
    {
        // The blast radius. Gating the search box must not take the grid, the refresh button or the
        // list-view selector with it - the page still has to show the audit trail, which is what
        // AuditTrails.View grants.
        foreach (var canSearch in new[] { true, false })
        {
            Arrange(canSearch);
            var markup = _ctx.Render<AuditTrails>().Markup;

            markup.Should().Contain("mud-table", $"the grid must render with Search={canSearch}");
            markup.Should().Contain("Refresh", $"the refresh action must survive with Search={canSearch}");

            _ctx.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        // Re-arm so TearDown has a live context to dispose.
        Arrange(canSearch: true);
    }
}
#nullable restore
