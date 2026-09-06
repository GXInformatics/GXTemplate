using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Application.Common.PublishStrategies;
using CleanArchitecture.Blazor.Infrastructure.Extensions;
using CleanArchitecture.Blazor.Infrastructure.Persistence.Logging;
using CleanArchitecture.Blazor.Infrastructure.Services.Identity;
using Mediator;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;
using Serilog.Events;
using Xunit;

namespace CleanArchitecture.Blazor.Infrastructure.UnitTests.Logging;

/// <summary>
/// A log row written by a NOTIFICATION HANDLER carries the tenant of the scope that published the
/// notification - sampled from a real log database, not reasoned from call sites.
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
/// <b>Why the assertion is on <c>Properties</c> rather than the <c>TenantId</c> column.</b> Pass 34
/// established that the SQLite sink is a third-party package with a fixed INSERT and cannot write
/// that column at all - it is permanently null on this provider. It does write <c>Properties</c>,
/// and the enriched <c>TenantId</c> appears there as JSON, which is exactly what Pass 34 sampled
/// (<c>"TenantId":null</c> on every row it captured). So this asserts the same value from the same
/// real database, through the one column this provider can carry it in.
/// </para>
/// <para>
/// A run against SQL Server or PostgreSQL would populate the dedicated column too, but neither can
/// produce a tenanted notification row without a live Blazor circuit - the ambient context exists
/// only inside a hub invocation (Pass 34 §2.4) - which is why the publisher is driven directly here.
/// </para>
/// </remarks>
[Collection(SqliteFileCollection.Name)]
public class PublishedNotificationLogTenantTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "gx-publisher-log-tests", Guid.NewGuid().ToString("N"));

    private string DatabasePath => Path.Combine(_directory, "logs.db");

    public PublishedNotificationLogTenantTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A sink thread may still hold the file; the temp directory is disposable either way.
        }
    }

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

    private void CreateTable()
    {
        using var connection = new SqliteConnection($"Data Source={DatabasePath}");
        connection.Open();
        foreach (var statement in LogTableDdl.Statements(DbProviderKeys.SqLite))
        {
            using var command = connection.CreateCommand();
            command.CommandText = statement;
            command.ExecuteNonQuery();
        }
    }

    private int CountRows()
    {
        if (!File.Exists(DatabasePath)) return 0;
        using var connection = new SqliteConnection($"Data Source={DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM SystemLogs";
        try { return Convert.ToInt32(command.ExecuteScalar()); }
        catch (SqliteException) { return 0; }
    }

    private string ReadProperties()
    {
        using var connection = new SqliteConnection($"Data Source={DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Properties FROM SystemLogs LIMIT 1";
        return command.ExecuteScalar() as string ?? string.Empty;
    }

    [Fact]
    public async Task ARowWrittenByAHandlerCarriesThePublishingTenant_NotTheConstructingOne()
    {
        CreateTable();

        var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Enrich.WithUserInfo()
            .WriteTo.SQLite(
                DatabasePath,
                "SystemLogs",
                LogEventLevel.Information,
                storeTimestampInUtc: true,
                batchSize: 1,
                needAutoCreateTable: false)
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

        for (var attempt = 0; attempt < 100 && CountRows() == 0; attempt++)
        {
            await Task.Delay(100);
        }

        logger.Dispose();

        Assert.Equal(1, CountRows());

        var properties = ReadProperties();
        Assert.Contains("\"TenantId\":\"tenant-B\"", properties);
        Assert.DoesNotContain("tenant-A", properties);
    }
}
