using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Application.Common.PublishStrategies;
using CleanArchitecture.Blazor.Infrastructure.Extensions;
using CleanArchitecture.Blazor.Infrastructure.Services.Identity;
using Mediator;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;
using Serilog.Events;
using Xunit;

namespace CleanArchitecture.Blazor.Infrastructure.UnitTests.Logging;

/// <summary>
/// A log event written by a NOTIFICATION HANDLER carries the tenant of the scope that published the
/// notification, not the tenant the publisher happened to be constructed in.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the end of the chain Pass 39 repaired.</b> Roughly twenty-five logging call sites sit
/// inside notification handlers or the services they call, and every one of them was labelled with
/// the tenant ambient when <c>ChannelBasedNoWaitPublisher</c> was first resolved in its scope rather
/// than the tenant that raised the event. <c>UserInfoEnricher</c> reads
/// <c>IUserContextAccessor.Current?.TenantId</c>, so the fix and the log row are one question.
/// </para>
/// <para>
/// <b>Asserted on the enriched event, with no database</b> (pass 47). This used to write through the
/// file-database sink and read the <c>Properties</c> JSON, because that sink cannot write the tenant column.
/// What is under test is which tenant the publisher and enricher attach, and a database cannot change
/// that. That the PostgreSQL sink then writes the property into <c>tenant_id</c> is asserted
/// end to end by <c>LogTablePostgresTests</c>.
/// </para>
/// </remarks>
public class PublishedNotificationLogTenantTests
{
    private sealed record Ping : INotification;

    /// <summary>Logs from inside the handler, exactly as the six shipped handlers do.</summary>
    private sealed class LoggingHandler : INotificationHandler<Ping>
    {
        private readonly Serilog.ILogger _logger;
        public TaskCompletionSource<bool> Logged { get; } = new();
        public LoggingHandler(Serilog.ILogger logger) => _logger = logger;

        public ValueTask Handle(Ping notification, CancellationToken ct)
        {
            _logger.Information("a row written by a notification handler");
            Logged.SetResult(true);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Captures the enriched events rather than writing them anywhere.</summary>
    private sealed class CapturingSink : Serilog.Core.ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];
        public void Emit(LogEvent logEvent) { lock (Events) Events.Add(logEvent); }
    }

    [Fact]
    public async Task AnEventWrittenByAHandlerCarriesThePublishingTenant_NotTheConstructingOne()
    {
        var capture = new CapturingSink();
        var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Enrich.WithUserInfo()
            .WriteTo.Sink(capture)
            .CreateLogger();

        IUserContextAccessor accessor = new UserContextAccessor();
        var handler = new LoggingHandler(logger);

        // Constructed under tenant-A, exactly as a publisher first resolved inside one circuit is.
        ChannelBasedNoWaitPublisher publisher;
        using (accessor.Push(new UserContext("u-a", "u-a", TenantId: "tenant-A")))
        {
            publisher = new ChannelBasedNoWaitPublisher(
                NullLogger<ChannelBasedNoWaitPublisher>.Instance);
        }

        // Published under tenant-B: this is the event that actually happened, and the row must say so.
        using (accessor.Push(new UserContext("u-b", "u-b", TenantId: "tenant-B")))
        {
            await publisher.Publish(
                new NotificationHandlers<Ping>(new INotificationHandler<Ping>[] { handler }, false),
                new Ping(), CancellationToken.None);
        }

        await handler.Logged.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await publisher.DisposeAsync();
        logger.Dispose();

        var logEvent = Assert.Single(capture.Events);
        var tenant = Assert.IsType<ScalarValue>(logEvent.Properties["TenantId"]);
        Assert.Equal("tenant-B", tenant.Value);
    }
}
