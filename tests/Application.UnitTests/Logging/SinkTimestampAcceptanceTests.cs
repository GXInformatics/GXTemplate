#nullable enable
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Infrastructure.Extensions;
using CleanArchitecture.Blazor.Infrastructure.Persistence.Logging;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Npgsql;
using NUnit.Framework;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace CleanArchitecture.Blazor.Application.UnitTests.Logging;

/// <summary>
/// The UTC timestamp rule, written and read back through a real SQL Server and a real PostgreSQL.
/// </summary>
/// <remarks>
/// <c>Infrastructure.UnitTests/Logging/SinkTimestampTests</c> pins each provider's CONFIGURATION and
/// runs the full round trip on SQLite, which needs no server. These are the same round trip for the
/// two providers that do, and they skip when the server is absent - the idiom
/// <c>AzureBlobFileStorageTests</c> already uses for Azurite.
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
    private const string SqlServerMaster =
        @"Server=(localdb)\mssqllocaldb;Database=master;Trusted_Connection=True;";

    private static string SqlServerLogDatabase(string name) =>
        $@"Server=(localdb)\mssqllocaldb;Database={name};Trusted_Connection=True;";

    private const string PostgresMaintenance =
        "Host=localhost;Port=5433;Database=postgres;Username=postgres;Password=postgres;Timeout=3";

    private static string PostgresLogDatabase(string name) =>
        $"Host=localhost;Port=5433;Database={name};Username=postgres;Password=postgres";

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

    // ------------------------------------------------------------------ SQL Server

    [Test]
    public async Task TheSqlServerSink_RecordsTimestampsInUtc()
    {
        if (!CanConnect(() => new SqlConnection(SqlServerMaster)))
        {
            Assert.Ignore("SQL Server LocalDB is not available; the MSSQL sink is not exercised on this machine.");
        }

        var database = "GxSinkTs_" + Guid.NewGuid().ToString("N")[..8];
        using (var master = new SqlConnection(SqlServerMaster))
        {
            master.Open();
            using var create = master.CreateCommand();
            create.CommandText = $"CREATE DATABASE [{database}]";
            create.ExecuteNonQuery();
        }

        try
        {
            using (var target = new SqlConnection(SqlServerLogDatabase(database)))
            {
                target.Open();
                RunDdl(target, DbProviderKeys.SqlServer);
            }

            var before = DateTime.UtcNow.AddMinutes(-1);

            var logger = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .Enrich.WithUtcTime()
                .WriteTo.MSSqlServer(
                    SqlServerLogDatabase(database),
                    new Serilog.Sinks.MSSqlServer.MSSqlServerSinkOptions
                    {
                        TableName = LogTableDdl.TableName,
                        SchemaName = LogTableDdl.SqlServerSchema,
                        AutoCreateSqlDatabase = false,
                        AutoCreateSqlTable = false,
                        BatchPostingLimit = 1,
                        BatchPeriod = TimeSpan.FromMilliseconds(200)
                    },
                    columnOptions: SerilogExtensions.BuildSqlServerColumnOptions())
                .CreateLogger();

            var stored = await WriteAndReadBackAsync(logger, () =>
            {
                using var connection = new SqlConnection(SqlServerLogDatabase(database));
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT TOP 1 [TimeStamp] FROM [{LogTableDdl.SqlServerSchema}].[{LogTableDdl.TableName}]";
                return command.ExecuteScalar() as DateTime?;
            });

            stored.Should().NotBeNull("the sink must have written a row into the table the DDL created");
            AssertStoredInUtc(stored!.Value, before);
        }
        finally
        {
            using var master = new SqlConnection(SqlServerMaster);
            master.Open();
            using var drop = master.CreateCommand();
            drop.CommandText =
                $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}];";
            drop.ExecuteNonQuery();
        }
    }

    // ------------------------------------------------------------------ PostgreSQL

    [Test]
    public async Task ThePostgresSink_RecordsTimestampsInUtc()
    {
        if (!CanConnect(() => new NpgsqlConnection(PostgresMaintenance)))
        {
            Assert.Ignore("PostgreSQL is not listening on localhost:5433; the Npgsql sink is not exercised on this machine.");
        }

        var database = "gx_sink_ts_" + Guid.NewGuid().ToString("N")[..8];
        using (var maintenance = new NpgsqlConnection(PostgresMaintenance))
        {
            maintenance.Open();
            using var create = maintenance.CreateCommand();
            create.CommandText = $"CREATE DATABASE \"{database}\"";
            create.ExecuteNonQuery();
        }

        try
        {
            using (var target = new NpgsqlConnection(PostgresLogDatabase(database)))
            {
                target.Open();
                RunDdl(target, DbProviderKeys.Npgsql);
            }

            var before = DateTime.UtcNow.AddMinutes(-1);

            var logger = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .Enrich.WithUtcTime()
                .WriteTo.PostgreSQL(
                    PostgresLogDatabase(database),
                    SerilogExtensions.NpgsqlTableName,
                    SerilogExtensions.BuildNpgsqlColumnWriters(),
                    LogEventLevel.Information,
                    needAutoCreateTable: false,
                    schemaName: LogTableDdl.NpgsqlSchema,
                    useCopy: false)
                .CreateLogger();

            var stored = await WriteAndReadBackAsync(logger, () =>
            {
                using var connection = new NpgsqlConnection(PostgresLogDatabase(database));
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText =
                    $"SELECT time_stamp FROM \"{LogTableDdl.NpgsqlSchema}\".\"{SerilogExtensions.NpgsqlTableName}\" LIMIT 1";
                return command.ExecuteScalar() as DateTime?;
            });

            stored.Should().NotBeNull("the sink must have written a row into the table the DDL created");

            // The Pass 11D regression, caught where it actually shows: TimestampColumnWriter would
            // have stored the host's local time here and this window would reject it.
            AssertStoredInUtc(stored!.Value, before);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            using var maintenance = new NpgsqlConnection(PostgresMaintenance);
            maintenance.Open();
            using var drop = maintenance.CreateCommand();
            drop.CommandText = $"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)";
            drop.ExecuteNonQuery();
        }
    }

    private static bool CanConnect(Func<DbConnection> factory)
    {
        try
        {
            using var connection = factory();
            connection.Open();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
#nullable restore
