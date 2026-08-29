#nullable enable
using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using CleanArchitecture.Blazor.Application.Common.ExceptionHandlers;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Caching;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Application.Common.Models;
using CleanArchitecture.Blazor.Application.Common.Security;
using CleanArchitecture.Blazor.Application.Features.SystemLogs.DTOs;
using CleanArchitecture.Blazor.Application.Features.SystemLogs.Queries.ChatData;
using CleanArchitecture.Blazor.Application.Features.SystemLogs.Queries.PaginationQuery;
using CleanArchitecture.Blazor.Server.UI.Pages.SystemManagement;
using CleanArchitecture.Blazor.Server.UI.Services;
using CleanArchitecture.Blazor.Server.UI.Services.Layout;
using CleanArchitecture.Blazor.Server.UI.Services.UserPreferences;
using FluentAssertions;
using Mapster;
using Mediator;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor.Services;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Server.UI.IntegrationTests;

/// <summary>
/// The same three states, on the GRID rather than the chart.
/// </summary>
/// <remarks>
/// <c>LogDatabaseStateComponentTests</c> covers <c>LogsLineCharts</c>. The SystemLogs page has a
/// SECOND path to the log database - <c>ServerReload</c>, feeding the data grid - with its own
/// duplicated try/catch and its own copy of the three-state decision, and until Pass 15B nothing
/// rendered it. Pass 11B built both halves; only one was pinned, and an asymmetry like that is how a
/// state quietly stops being handled on one of two nearly identical code paths.
/// <para>
/// The grid is what a viewer actually reads the logs from, so its failure text matters more than the
/// chart's: the chart going quiet is a missing picture, the grid going quiet says "no logs found" to
/// someone whose logs were never being recorded.
/// </para>
/// </remarks>
[TestFixture]
public class SystemLogsPageStateComponentTests
{
    private BunitContext _ctx = null!;
    private Mock<IMediator> _mediator = null!;

    [SetUp]
    public void SetUp()
    {
        _ctx = new BunitContext();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _mediator = new Mock<IMediator>();

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
        services.AddSingleton(_mediator.Object);

        // The page reads a profile for the query's principal-scoped window, and its access rights
        // for the Purge button. Neither is what these tests are about; both must be present or the
        // page cannot render at all.
        var profile = new Mock<IUserProfileState>();
        profile.SetupGet(p => p.Value).Returns(new UserProfile(
            UserId: "u", UserName: "u", Email: "u@example.com", TimeZoneId: "UTC"));
        services.AddSingleton(profile.Object);

        var permissions = new Mock<IPermissionService>();
        permissions.Setup(p => p.GetAccessRightsAsync<LogsAccessRights>())
            .ReturnsAsync(new LogsAccessRights());
        services.AddSingleton(permissions.Object);

        _ctx.AddAuthorization().SetAuthorized("probe");

        // The chart sits above the grid on this page and issues its own query. It is not under test
        // here, so it is given an empty, healthy answer - leaving the grid's own outcome as the only
        // thing that varies between these tests.
        _mediator.Setup(m => m.Send(It.IsAny<SystemLogsTimeLineChatDataQuery>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.FromResult(new List<SystemLogTimeLineDto>()));
    }

    [TearDown]
    public async Task TearDown() => await _ctx.DisposeAsync();

    /// <summary>Makes the GRID's query behave as the given situation would make it behave.</summary>
    private void TheGridQuery(Func<Exception>? fails = null, bool asynchronously = false, int rows = 0)
    {
        var setup = _mediator.Setup(m => m.Send(
            It.IsAny<SystemLogsWithPaginationQuery>(), It.IsAny<CancellationToken>()));

        if (fails is null)
        {
            var items = new List<SystemLogDto>();
            for (var i = 0; i < rows; i++) items.Add(new SystemLogDto { Id = i + 1, Level = "Information", Message = $"row {i}" });
            setup.Returns(ValueTask.FromResult(new PaginatedData<SystemLogDto>(items, items.Count, 1, 15)));
            return;
        }

        // Asynchronous by default here, unlike the chart fixture's historical default: a real
        // ServerReload awaits a database, and Pass 15 showed that a synchronous throw is the one
        // shape that hides timing bugs in the assertions.
        if (asynchronously)
        {
            setup.Returns(async (SystemLogsWithPaginationQuery _, CancellationToken _) =>
            {
                await Task.Yield();
                throw fails();
            });
        }
        else
        {
            setup.ThrowsAsync(fails());
        }
    }

    private IRenderedComponent<SystemLogs> RenderThePage() => _ctx.Render<SystemLogs>();

    /// <summary>Waits for the grid to have finished its server load and rendered the outcome.</summary>
    private static void WaitForTheGrid(IRenderedComponent<SystemLogs> cut, string expected) =>
        cut.WaitForAssertion(
            () => cut.Markup.Should().Contain(expected),
            TimeSpan.FromSeconds(10));

    [Test]
    public void WhenNoLogDatabaseIsConfigured_TheGridSaysSo_RatherThanShowingNoLogsFound()
    {
        // The failure this page most needs to avoid, and the reason NoRecordsContent has three
        // branches: an empty grid saying "No System Logs Found" to someone whose log database was
        // never configured reports a quiet week when nothing was ever being recorded.
        TheGridQuery(fails: () => new LogDatabaseNotConfiguredException(), asynchronously: true);

        var cut = RenderThePage();
        WaitForTheGrid(cut, "No log database is configured");

        cut.Instance.LogDatabaseState.Should().Be(LogDatabaseState.NotConfigured);
        cut.Markup.Should().NotContain("No System Logs Found");
    }

    [Test]
    public void WhenTheLogDatabaseIsUnreachable_TheGridSaysSo_AndOffersRetry()
    {
        // The state a missing or unreachable log database produces - including the one Pass 15B now
        // creates its way out of, when the login is not allowed to.
        TheGridQuery(fails: () => new InvalidOperationException("connection refused"), asynchronously: true);

        var cut = RenderThePage();
        WaitForTheGrid(cut, "The log database is unavailable");

        cut.Instance.LogDatabaseState.Should().Be(LogDatabaseState.Unavailable);
        cut.FindAll("#log-database-retry").Should().ContainSingle(
            "an unreachable database is the one state a viewer can usefully retry from");
        cut.Markup.Should().NotContain("No System Logs Found");
    }

    [Test]
    public void WhenTheFailureArrivesSynchronously_TheGridStillSaysSo()
    {
        // The paired timing case. Both shapes must reach the same rendered outcome, or the coverage
        // depends on how the mock happens to be written rather than on what the page does.
        TheGridQuery(fails: () => new InvalidOperationException("connection refused"), asynchronously: false);

        var cut = RenderThePage();
        WaitForTheGrid(cut, "The log database is unavailable");

        cut.Instance.LogDatabaseState.Should().Be(LogDatabaseState.Unavailable);
    }

    [Test]
    public void WhenTheLogDatabaseIsAvailableButEmpty_TheGridSaysNoLogsFound()
    {
        // The paired negative, and the whole reason three states exist rather than two. An available
        // database with nothing in it must NOT be reported as a problem.
        TheGridQuery(rows: 0);

        var cut = RenderThePage();
        WaitForTheGrid(cut, "No System Logs Found");

        cut.Instance.LogDatabaseState.Should().Be(LogDatabaseState.Available);
        cut.Markup.Should().NotContain("The log database is unavailable");
        cut.Markup.Should().NotContain("No log database is configured");
    }

    [Test]
    public void WhenTheLogDatabaseHasRows_TheGridShowsThem()
    {
        TheGridQuery(rows: 2);

        var cut = RenderThePage();
        WaitForTheGrid(cut, "row 0");

        cut.Instance.LogDatabaseState.Should().Be(LogDatabaseState.Available);
        cut.Markup.Should().Contain("row 1");
        cut.Markup.Should().NotContain("No System Logs Found");
    }
}
#nullable restore
