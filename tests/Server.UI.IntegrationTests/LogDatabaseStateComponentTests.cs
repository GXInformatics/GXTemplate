#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using CleanArchitecture.Blazor.Application.Common.ExceptionHandlers;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Caching;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Application.Features.SystemLogs.DTOs;
using CleanArchitecture.Blazor.Application.Features.SystemLogs.Queries.ChatData;
using CleanArchitecture.Blazor.Server.UI.Pages.SystemManagement;
using CleanArchitecture.Blazor.Server.UI.Pages.SystemManagement.Components;
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
/// The three states the log chart distinguishes, observed as rendered components.
/// </summary>
/// <remarks>
/// These cannot be reached over HTTP. The application renders at
/// <c>InteractiveServerRenderMode(prerender: false)</c>, so an HTTP response for
/// <c>/system/logs</c> carries the app shell and nothing else - the components, and every decision
/// they make about the log database, run inside the circuit. That is Pass 10's lesson applied to
/// Pass 11: a page-level state that HTTP cannot see needs a rendered test or it has no test at all.
/// <para>
/// The chart is the component under test rather than the whole SystemLogs page because it is where
/// the distinction first became load-bearing: it sits ABOVE the grid, so before Pass 11B an
/// unreachable log database threw here and took the page down before the grid could explain
/// anything.
/// </para>
/// </remarks>
[TestFixture]
public class LogDatabaseStateComponentTests
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
        services.AddSingleton(Mock.Of<IUserProfileState>());
        services.AddSingleton(Mock.Of<IValidationService>());
        services.AddSingleton(Mock.Of<IAppCache>());
        services.AddSingleton(Mock.Of<IPermissionService>());
        services.AddSingleton(Mock.Of<IObjectMapper>());
        services.AddSingleton(_mediator.Object);
    }

    [TearDown]
    public async Task TearDown() => await _ctx.DisposeAsync();

    /// <summary>Makes the chart's query behave as the given situation would make it behave.</summary>
    private void TheLogDatabase(Func<Exception>? fails = null, List<SystemLogTimeLineDto>? returns = null)
    {
        var setup = _mediator.Setup(m => m.Send(
            It.IsAny<SystemLogsTimeLineChatDataQuery>(), It.IsAny<CancellationToken>()));

        if (fails is not null) setup.ThrowsAsync(fails());
        else setup.Returns(ValueTask.FromResult(returns ?? new List<SystemLogTimeLineDto>()));
    }

    [Test]
    public void WhenNoLogDatabaseIsConfigured_TheChartSaysSo_AndDoesNotThrow()
    {
        // The state that matters most, because it is the one that would otherwise be indistinguishable
        // from a quiet week: nothing has been recorded, and nothing ever was.
        TheLogDatabase(fails: () => new LogDatabaseNotConfiguredException());

        var cut = _ctx.Render<LogsLineCharts>();

        cut.Instance.LogDatabaseState.Should().Be(LogDatabaseState.NotConfigured);
        cut.FindAll("#log-chart-unavailable").Should().ContainSingle();
        cut.Markup.Should().Contain("No log database is configured");
    }

    [Test]
    public void WhenTheLogDatabaseIsUnreachable_TheChartSaysSo_AndDoesNotThrow()
    {
        // Before this pass, this exception escaped OnInitializedAsync and the whole page failed.
        TheLogDatabase(fails: () => new InvalidOperationException("connection refused"));

        var cut = _ctx.Render<LogsLineCharts>();

        cut.Instance.LogDatabaseState.Should().Be(LogDatabaseState.Unavailable);
        cut.FindAll("#log-chart-unavailable").Should().ContainSingle();
        cut.Markup.Should().Contain("The log database is unavailable");
    }

    [Test]
    public void WhenTheLogDatabaseIsSimplyEmpty_TheChartShowsNeitherComplaint()
    {
        // The paired negative, and the reason three states are needed rather than two. An available
        // database with no matching rows must NOT be reported as a problem.
        TheLogDatabase(returns: new List<SystemLogTimeLineDto>());

        var cut = _ctx.Render<LogsLineCharts>();

        cut.Instance.LogDatabaseState.Should().Be(LogDatabaseState.Available);
        cut.FindAll("#log-chart-unavailable").Should().BeEmpty();
        cut.Markup.Should().NotContain("unavailable");
        cut.Markup.Should().NotContain("No log database is configured");
    }

    [Test]
    public void WhenTheLogDatabaseHasRows_TheChartRendersThem()
    {
        TheLogDatabase(returns:
        [
            new SystemLogTimeLineDto { dt = DateTime.UtcNow.Date, total = 3 }
        ]);

        var cut = _ctx.Render<LogsLineCharts>();

        cut.Instance.LogDatabaseState.Should().Be(LogDatabaseState.Available);
        cut.Instance.Data.Should().ContainSingle();
        cut.FindAll("#log-chart-unavailable").Should().BeEmpty();
    }
}
#nullable restore
