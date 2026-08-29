using CleanArchitecture.Blazor.Domain.Entities;
using Microsoft.Data.Sqlite;
using Serilog;
using Serilog.Events;
using Xunit;

namespace CleanArchitecture.Blazor.Infrastructure.UnitTests.Logging;

/// <summary>
/// The SQLite sink, exercised for real against a throwaway file: what it creates, and in which time
/// zone it records.
/// </summary>
/// <remarks>
/// SQLite is the one provider whose log table can be produced and inspected in a unit test - it is a
/// file - so these assertions are made against the sink's actual behaviour rather than against a
/// reading of its parameters.
/// <para>
/// The timestamp half of this exists because <c>storeTimestampInUtc</c> defaults to <c>false</c>,
/// and nothing else in the system works in local time: <c>UtcTimestampEnricher</c> enriches in UTC,
/// the MSSQL sink is configured <c>ConvertToUtc = true</c>, and
/// <c>SystemLogAdvancedSpecification</c> builds its TODAY and LAST_30_DAYS windows from
/// <c>DateTime.UtcNow</c>. A local-time column read through a UTC filter hides recent rows from the
/// page's default view for anyone west of Greenwich - quietly, and only for some users.
/// </para>
/// </remarks>
public class SqliteSinkTimestampTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "gx-sqlite-sink-tests", Guid.NewGuid().ToString("N"));

    private string DatabasePath => Path.Combine(_directory, "logs.db");

    public SqliteSinkTimestampTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
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

    /// <summary>
    /// Writes one event through the sink exactly as <c>SerilogExtensions.WriteToSqLite</c> configures
    /// it, and waits for the batch to land.
    /// </summary>
    private async Task WriteOneEventAsync()
    {
        var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.SQLite(
                DatabasePath,
                "SystemLogs",
                LogEventLevel.Information,
                storeTimestampInUtc: true,
                needAutoCreateTable: true)
            .CreateLogger();

        logger.Information("a probe row");
        logger.Dispose();

        // The sink batches on a timer; the flush is what Dispose triggers.
        for (var attempt = 0; attempt < 40 && CountRows() == 0; attempt++)
            await Task.Delay(100);
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
    public async Task TheSinkCreatesTheTable_WithAColumnForEveryEntityProperty()
    {
        // The auto-create half of the Pass 11 design, measured rather than assumed: with no EF
        // migration chain for the log database, this is the only thing that creates the table the
        // SystemLogs page reads.
        await WriteOneEventAsync();

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
