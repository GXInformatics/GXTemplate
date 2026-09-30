#nullable enable
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Infrastructure.Extensions;
using CleanArchitecture.Blazor.Infrastructure.Persistence.Logging;
using FluentAssertions;
using Npgsql;
using CleanArchitecture.Blazor.TestSupport;
using NUnit.Framework;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace CleanArchitecture.Blazor.Application.UnitTests.Logging;

/// <summary>
/// The UTC timestamp rule, written and read back through a real PostgreSQL server.
/// </summary>
/// <remarks>
/// <c>Infrastructure.UnitTests/Logging/SinkTimestampTests</c> pins the sink's CONFIGURATION.
/// This is the round trip itself, on the server named by <c>GX_TEST_PG</c>, into this assembly's own
/// log database <c>gx_test_&lt;project&gt;_unit_logs</c>: created if missing, never dropped (pass 47). Without
/// <c>GX_TEST_PG</c> it fails naming the variable; it does not skip.
/// <para>
/// They matter because configuration and behaviour are not the same claim. <c>ConvertToUtc = true</c>
/// and a writer named <c>TimeStamp</c> are both statements about intent; only writing a row and
/// reading it back says what the database actually holds. PostgreSQL is the provider that had this
/// wrong until Pass 11D, and it had a perfectly reasonable-looking configuration throughout.
/// </para>
/// <para>
/// The assertion is deliberately a WINDOW around <c>DateTime.UtcNow</c> rather than an exact value.
/// On a host in any zone more than a minute from UTC - the developer machines this template is built
/// on - a local-time write falls outside it, which is the whole point. On a UTC build agent the test
/// cannot distinguish the two and simply passes; the configuration assertions elsewhere are what
/// cover that case, and this is stated rather than pretended otherwise.
/// </para>
/// </remarks>
[TestFixture]
public class SinkTimestampAcceptanceTests
{
    /// <summary>This assembly's log database, on the GX_TEST_PG server.</summary>
    private const string LogDatabaseName = TestDatabaseNames.UnitLogs;

    /// <summary>The window a UTC write lands in and a local write (in a non-UTC zone) does not.</summary>
    private static void AssertStoredInUtc(DateTime stored, DateTime before)
    {
        stored.Should().BeOnOrAfter(before)
            .And.BeOnOrBefore(DateTime.UtcNow.AddMinutes(1),
                "the sink must record UTC; a local-time write on a non-UTC host falls outside this window");
    }

    /// <summary>
    /// Waits for the batched row while the logger is still alive, then disposes it. Disposing first
    /// races the sink's background queue and loses the event - see the note in
    /// <c>SinkTimestampTests.WriteOneEventAsync</c>.
    /// </summary>
    private static async Task<DateTime?> WriteAndReadBackAsync(
        Logger logger, Func<DateTime?> read)
    {
        logger.Information("a probe row");

        DateTime? stored = null;
        for (var attempt = 0; attempt < 100 && stored is null; attempt++)
        {
            stored = read();
            if (stored is null) await Task.Delay(100);
        }

        logger.Dispose();
        return stored;
    }

    private static void RunDdl(DbConnection connection, string provider)
    {
        foreach (var statement in LogTableDdl.Statements(provider))
        {
            using var command = connection.CreateCommand();
            command.CommandText = statement;
            command.ExecuteNonQuery();
        }
    }

    // ------------------------------------------------------------------ PostgreSQL

    [Test]
    public async Task ThePostgresSink_RecordsTimestampsInUtc()
    {
        var logs = PostgresTestDatabase.FromEnvironment(LogDatabaseName);
        await logs.EnsureExistsAsync();
        var table = $"\"{LogTableDdl.NpgsqlSchema}\".\"{SerilogExtensions.NpgsqlTableName}\"";

        await using (var target = new NpgsqlConnection(logs.ConnectionString))
        {
            await target.OpenAsync();
            // The DDL is idempotent (IF NOT EXISTS), so the fixed database is prepared on every run.
            RunDdl(target, DbProviderKeys.Npgsql);

            // Emptied rather than dropped: the row read back below must be this run's.
            await using var empty = target.CreateCommand();
            empty.CommandText = $"DELETE FROM {table}";
            await empty.ExecuteNonQueryAsync();
        }

        var before = DateTime.UtcNow.AddMinutes(-1);

        var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Enrich.WithUtcTime()
            .WriteTo.PostgreSQL(
                logs.ConnectionString,
                SerilogExtensions.NpgsqlTableName,
                SerilogExtensions.BuildNpgsqlColumnWriters(),
                LogEventLevel.Information,
                needAutoCreateTable: false,
                schemaName: LogTableDdl.NpgsqlSchema,
                useCopy: false)
            .CreateLogger();

        var stored = await WriteAndReadBackAsync(logger, () =>
        {
            using var connection = new NpgsqlConnection(logs.ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT time_stamp FROM {table} LIMIT 1";
            return command.ExecuteScalar() as DateTime?;
        });

        stored.Should().NotBeNull("the sink must have written a row into the table the DDL created");

        // The Pass 11D regression, caught where it actually shows: TimestampColumnWriter would
        // have stored the host's local time here and this window would reject it.
        AssertStoredInUtc(stored!.Value, before);
    }
}
#nullable restore
