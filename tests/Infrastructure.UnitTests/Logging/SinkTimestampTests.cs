using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Domain.Entities;
using CleanArchitecture.Blazor.Infrastructure.Extensions;
using CleanArchitecture.Blazor.Infrastructure.Persistence.Logging;
using Microsoft.Data.Sqlite;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Sinks.PostgreSQL;
using Serilog.Sinks.PostgreSQL.ColumnWriters;
using Xunit;

namespace CleanArchitecture.Blazor.Infrastructure.UnitTests.Logging;

/// <summary>
/// Every sink records the log timestamp in UTC.
/// </summary>
/// <remarks>
/// This is one rule with three different spellings, and each provider has now got it wrong at least
/// once:
/// <list type="bullet">
/// <item><b>SQLite</b> - <c>storeTimestampInUtc</c> defaults to <c>false</c> and was not being
/// passed (fixed in Pass 11B);</item>
/// <item><b>PostgreSQL</b> - <c>TimestampColumnWriter</c> writes the event's own timestamp as LOCAL
/// time (fixed in Pass 11D);</item>
/// <item><b>SQL Server</b> - correct, via <c>ConvertToUtc = true</c>, and pinned here so it stays
/// that way.</item>
/// </list>
/// Nothing else in the system works in local time: <c>UtcTimestampEnricher</c> enriches in UTC, and
/// <c>SystemLogAdvancedSpecification</c> builds its TODAY and LAST_30_DAYS windows from
/// <c>DateTime.UtcNow</c>. A local-time column read through a UTC filter mis-windows by the host's
/// offset - quietly, and only for viewers in some time zones, which is what let it survive three
/// passes.
/// <para>
/// The configuration assertions run everywhere and would each have caught their own regression. The
/// live write-and-read-back below is SQLite only, because a database that is a file needs no server;
/// the same round trip for SQL Server and PostgreSQL lives in
/// <c>Application.UnitTests/Logging/SinkTimestampAcceptanceTests</c>, which skips when the server is
/// absent.
/// </para>
/// </remarks>
[Collection(SqliteFileCollection.Name)]
public class SinkTimestampTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "gx-sink-timestamp-tests", Guid.NewGuid().ToString("N"));

    private string DatabasePath => Path.Combine(_directory, "logs.db");

    public SinkTimestampTests() => Directory.CreateDirectory(_directory);

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

    // ------------------------------------------------------- the configuration, all three providers

    [Fact]
    public void ThePostgresSink_ReadsTheUtcEnrichedProperty_NotTheEventsOwnTimestamp()
    {
        // The Pass 11D regression, pinned at its exact cause. TimestampColumnWriter would compile,
        // run, and write the right-looking value in the wrong time zone; only the writer's identity
        // distinguishes it.
        var writer = SerilogExtensions.BuildNpgsqlColumnWriters()["time_stamp"];

        var single = Assert.IsType<SinglePropertyColumnWriter>(writer);
        Assert.Equal("TimeStamp", single.Name);
        Assert.Equal(PropertyWriteMethod.Raw, single.WriteMethod);
    }

    [Fact]
    public void ThePropertyThePostgresSinkReads_IsTheOneTheEnricherWritesInUtc()
    {
        // The other half of the pairing: the writer above names a property, and this is what proves
        // the property it names is produced, and produced in UTC. If the enricher stopped adding it,
        // the column would go null rather than wrong - a different failure, equally silent.
        var logEvent = new LogEvent(
            new DateTimeOffset(2026, 8, 29, 11, 30, 0, TimeSpan.FromHours(1)),
            LogEventLevel.Information,
            exception: null,
            new MessageTemplate("m", []),
            []);

        new UtcTimestampEnricher().Enrich(logEvent, new PropertyFactory());

        var value = Assert.IsType<ScalarValue>(logEvent.Properties["TimeStamp"]);
        // The UTC instant, with Kind=Unspecified so Npgsql will bind it to a "timestamp without
        // time zone" column without depending on a global legacy switch.
        Assert.Equal(new DateTime(2026, 8, 29, 10, 30, 0, DateTimeKind.Unspecified), value.Value);
        Assert.Equal(DateTimeKind.Unspecified, ((DateTime)value.Value!).Kind);
    }

    /// <summary>The smallest thing that satisfies the enricher's signature.</summary>
    private sealed class PropertyFactory : ILogEventPropertyFactory
    {
        public LogEventProperty CreateProperty(string name, object? value, bool destructureObjects = false) =>
            new(name, new ScalarValue(value));
    }

    [Fact]
    public void TheSqlServerSink_ConvertsItsTimestampToUtc()
    {
        var options = SerilogExtensions.BuildSqlServerColumnOptions();

        Assert.True(options.TimeStamp.ConvertToUtc);
        Assert.Equal("TimeStamp", options.TimeStamp.ColumnName);
    }

    // ------------------------------------------------------- SQLite, end to end

    /// <summary>
    /// Writes one event exactly as <c>SerilogExtensions.WriteToSqLite</c> configures the sink -
    /// including <c>needAutoCreateTable: false</c>, because since Pass 11C the table comes from
    /// <see cref="LogTableDdl"/> and not from the sink - and waits for the batch to land.
    /// </summary>
    private async Task WriteOneEventAsync()
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
            .WriteTo.SQLite(
                DatabasePath,
                "SystemLogs",
                LogEventLevel.Information,
                storeTimestampInUtc: true,
                needAutoCreateTable: false)
            .CreateLogger();

        logger.Information("a probe row");

        // Wait for the row while the logger is still ALIVE, and dispose only afterwards.
        //
        // Disposing straight after writing looks tidier and is a race the test loses often enough to
        // matter: this sink queues events to a background batching thread, and Dispose halts that
        // thread. Serilog's SelfLog shows the losing case plainly - "Halting sink... The collection
        // argument is empty and has been marked as complete with regards to additions" - where the
        // halt beats the event into the queue and the row is simply never written. Letting the
        // sink's own timer flush, then disposing, removes the race instead of making it rarer.
        //
        // This is a property of a short-lived logger in a test. A running application's logger lives
        // as long as the process, so nothing here indicates a production defect.
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

    [Fact]
    public async Task TheSinkWritesIntoTheTableTheApplicationCreated()
    {
        // Production's arrangement end to end on the one provider that needs no server: LogTableDdl
        // creates the table, the sink writes into it, and the two agree about every column.
        await WriteOneEventAsync();

        Assert.Equal(1, CountRows());

        using var connection = new SqliteConnection($"Data Source={DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info('SystemLogs')";
        using var reader = command.ExecuteReader();

        var columns = new List<string>();
        while (reader.Read()) columns.Add(reader.GetString(0));

        var missing = typeof(SystemLog).GetProperties()
            .Select(p => p.Name)
            .Where(p => !columns.Contains(p, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        Assert.NotEmpty(columns);
        Assert.Empty(missing);
    }

    [Fact]
    public async Task TheSinkRecordsTimestampsInUtc()
    {
        var before = DateTime.UtcNow.AddMinutes(-1);

        await WriteOneEventAsync();

        using var connection = new SqliteConnection($"Data Source={DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT TimeStamp FROM SystemLogs LIMIT 1";
        var stored = DateTime.Parse(
            (string)command.ExecuteScalar()!,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);

        var after = DateTime.UtcNow.AddMinutes(1);

        // A local-time write in any zone more than a minute from UTC falls outside this window, which
        // is the whole point: on a UTC build agent this test can only fail for a real reason, and on
        // a developer machine in a non-UTC zone it fails loudly the moment the flag is lost.
        Assert.InRange(stored, before, after);
    }
}
