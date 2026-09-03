using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Infrastructure.Extensions;
using CleanArchitecture.Blazor.Infrastructure.Persistence.Logging;
using CleanArchitecture.Blazor.Infrastructure.Services.Identity;
using Microsoft.Data.Sqlite;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.PostgreSQL;
using Serilog.Sinks.PostgreSQL.ColumnWriters;
using Xunit;

namespace CleanArchitecture.Blazor.Infrastructure.UnitTests.Logging;

/// <summary>
/// The log row records which tenant produced the event, and records <c>null</c> when no tenant
/// produced it.
/// </summary>
/// <remarks>
/// <b>Null is a value here, not a gap.</b> Startup, seeding, the bootstrap administrator banner,
/// Hangfire's heartbeats and anything logged after a circuit has gone all run with no ambient user
/// context. Those rows form a third partition - the installation's own events - and any future
/// per-tenant log view has to surface it rather than quietly dropping it.
/// <para>
/// <b>Why the source is the user context and not the HTTP context.</b> The other three enriched
/// values - UserName, ClientIP, ClientAgent - come from <c>IHttpContextAccessor</c> and therefore
/// exist only while a request does. A tenant is knowable wherever the ambient user context has been
/// pushed, which includes Blazor circuits, hub calls and mediator handlers running on continuations
/// long after the request completed. The test below pushes a context with no HTTP request anywhere
/// in sight, which is precisely the case the HTTP accessor could not have served.
/// </para>
/// <para>
/// <b>And why that works at all.</b> Serilog constructs enrichers itself, through a parameterless
/// constructor, and the logger is configured in <c>Program.cs</c> before <c>AddInfrastructure</c>
/// has registered anything - so the enricher cannot resolve a service. It reaches the ambient value
/// the same way it already reaches the request: by newing up an accessor whose state is static and
/// per-call-chain. <c>UserContextAccessor</c> was changed to make that true, and this test is what
/// holds it true.
/// </para>
/// </remarks>
[Collection(SqliteFileCollection.Name)]
public class LogTenantStampingTests : IDisposable
{
    private const string TenantId = "tenant-from-context";

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "gx-log-tenant-tests", Guid.NewGuid().ToString("N"));

    private string DatabasePath => Path.Combine(_directory, "logs.db");

    public LogTenantStampingTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A file the sink still holds is not a test failure.
        }

        GC.SuppressFinalize(this);
    }

    // ------------------------------------------------------- configuration, the two server providers

    [Fact]
    public void TheSqlServerSink_WritesTheTenantColumn_AndAllowsItToBeNull()
    {
        var column = Assert.Single(
            SerilogExtensions.BuildSqlServerColumnOptions().AdditionalColumns!,
            c => c.ColumnName == "TenantId");

        Assert.Equal("TenantId", column.PropertyName);

        // AllowNull is the assertion that matters. Every startup row has no tenant, so a NOT NULL
        // column would make the sink reject the first event the application ever writes - and it
        // would do it asynchronously, into SelfLog, with the application looking healthy.
        Assert.True(column.AllowNull);
    }

    [Fact]
    public void ThePostgresSink_ReadsTheEnrichedTenantProperty()
    {
        var writer = SerilogExtensions.BuildNpgsqlColumnWriters()["tenant_id"];

        var single = Assert.IsType<SinglePropertyColumnWriter>(writer);
        Assert.Equal("TenantId", single.Name);

        // Raw, matching user_name and client_ip. ToString would render a null as a quoted string
        // rather than leaving the column null, which would turn "no tenant" into a tenant named
        // "null" - and the installation partition would stop being distinguishable.
        Assert.Equal(PropertyWriteMethod.Raw, single.WriteMethod);
    }

    // ------------------------------------------------------- SQLite, end to end

    /// <summary>
    /// Writes one event through the real sink into the real DDL, optionally inside an ambient user
    /// context, and waits for the batch to land.
    /// </summary>
    /// <remarks>
    /// Mirrors <c>SinkTimestampTests.WriteOneEventAsync</c>, including <c>batchSize: 1</c> - the
    /// sink flushes when the batch fills or when its own timer fires, and waiting on the timer is
    /// what once took this assembly from 4s to 21s - and including disposing the logger only AFTER
    /// the row has landed, because Dispose halts the background batching thread and can beat the
    /// event into the queue.
    /// </remarks>
    private async Task WriteOneEventAsync(string? tenantId)
    {
        using (var connection = new SqliteConnection($"Data Source={DatabasePath}"))
        {
            connection.Open();
            foreach (var statement in LogTableDdl.Statements(DbProviderKeys.SqLite))
            {
                using var command = connection.CreateCommand();
                command.CommandText = statement;
                command.ExecuteNonQuery();
            }
        }

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

        // The accessor is constructed here, and a DIFFERENT one is constructed inside the enricher.
        // That they agree is the point: the ambient value belongs to the call chain, not to either
        // object. There is no HTTP request anywhere in this test.
        IUserContextAccessor accessor = new UserContextAccessor();
        using (tenantId is null
                   ? null
                   : accessor.Push(new UserContext("log-user", "logger", TenantId: tenantId)))
        {
            logger.Information("a probe row");
        }

        for (var attempt = 0; attempt < 100 && CountRows() == 0; attempt++)
            await Task.Delay(100);

        logger.Dispose();
    }

    private int CountRows()
    {
        if (!File.Exists(DatabasePath)) return 0;
        using var connection = new SqliteConnection($"Data Source={DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM SystemLogs";
        try
        {
            return Convert.ToInt32(command.ExecuteScalar());
        }
        catch (SqliteException)
        {
            return 0;
        }
    }

    private string? ReadTenant()
    {
        using var connection = new SqliteConnection($"Data Source={DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT TenantId FROM SystemLogs LIMIT 1";
        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : (string)value;
    }

    [Fact]
    public async Task OnSqlite_TheRowLands_ButTheTenantColumnStaysNull_BecauseThatSinkCannotWriteIt()
    {
        // Measured, not assumed, and it is the reason SinkColumnDriftTests now names the SQLite
        // sink's columns literally instead of defining them as "whatever the entity has".
        //
        // Blazor.Serilog.Sinks.SQLite writes a FIXED statement -
        //   VALUES (@timeStamp, @level, @exception, @message, @properties, @messageTemplate,
        //           @logEvent, @userName, @clientIP, @clientAgent)
        // - with no AdditionalColumns and no writer dictionary. Unlike the SQL Server and PostgreSQL
        // sinks, its column set is not configurable, so a new column cannot be given to it.
        //
        // The column still exists in the SQLite DDL, and it has to: EF reads SystemLog.TenantId on
        // every provider, and a missing column would fail the read outright rather than return null.
        // So on SQLite the value is permanently null - which is a real limitation, stated here and
        // in the README rather than left for somebody to find in an empty column.
        //
        // SQLite is the no-server development and test provider. The two providers a GX installation
        // runs on both record the tenant, which is asserted above against each sink's own
        // configuration.
        await WriteOneEventAsync(TenantId);

        Assert.Equal(1, CountRows());
        Assert.Null(ReadTenant());
    }

    [Fact]
    public async Task OnSqlite_AnEventWithNoAmbientContext_AlsoLands()
    {
        // The startup case. Nothing here can distinguish it from the case above on this provider -
        // that is the point of the test above - but the row landing at all is worth asserting: an
        // added column that the sink does not know about must not break the INSERT it does issue.
        await WriteOneEventAsync(tenantId: null);

        Assert.Equal(1, CountRows());
        Assert.Null(ReadTenant());
    }

    // ------------------------------------------------------- the enricher, provider-independent

    /// <summary>Captures the enriched events rather than writing them anywhere.</summary>
    private sealed class CapturingSink : Serilog.Core.ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    private static LogEvent EnrichOneEvent(string? tenantId)
    {
        var capture = new CapturingSink();
        using (var logger = new LoggerConfiguration()
                   .MinimumLevel.Verbose()
                   .Enrich.WithUserInfo()
                   .WriteTo.Sink(capture)
                   .CreateLogger())
        {
            IUserContextAccessor accessor = new UserContextAccessor();
            using (tenantId is null
                       ? null
                       : accessor.Push(new UserContext("log-user", "logger", TenantId: tenantId)))
            {
                logger.Information("a probe row");
            }
        }

        return Assert.Single(capture.Events);
    }

    [Fact]
    public void TheEnricherPublishesTheAmbientTenant_WithNoHttpRequestInSight()
    {
        // The mechanism itself, independent of any sink or provider: this is what the SQL Server and
        // PostgreSQL writers asserted above go on to read.
        //
        // There is no HTTP context anywhere in this test, which is exactly the case
        // IHttpContextAccessor could not have served - a mediator handler on a continuation, a hub
        // call, a Blazor circuit. The accessor pushed here and the one the enricher constructs for
        // itself are different objects that agree, because the value belongs to the call chain.
        var logEvent = EnrichOneEvent(TenantId);

        var value = Assert.IsType<ScalarValue>(logEvent.Properties["TenantId"]);
        Assert.Equal(TenantId, value.Value);
    }

    [Fact]
    public void TheEnricherPublishesANullTenant_WhenThereIsNoAmbientContext()
    {
        // Startup, seeding, Hangfire heartbeats. The property is present and null rather than
        // absent, so the sinks bind a null instead of failing on a missing property.
        var logEvent = EnrichOneEvent(tenantId: null);

        var value = Assert.IsType<ScalarValue>(logEvent.Properties["TenantId"]);
        Assert.Null(value.Value);
    }
}
