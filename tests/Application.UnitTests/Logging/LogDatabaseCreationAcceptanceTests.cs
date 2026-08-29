#nullable enable
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Infrastructure;
using CleanArchitecture.Blazor.Infrastructure.Persistence.Logging;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.Logging;

/// <summary>
/// The log database being brought into existence, against real servers.
/// </summary>
/// <remarks>
/// Pass 15 established the mechanics by probe - that PostgreSQL denies with <c>42501</c>, SQL Server
/// with <c>262</c>, that a race is <c>42P04</c> / <c>1801</c>, and that both catalogue checks run
/// under a login that cannot create anything. These tests are what keep the code that acts on those
/// facts honest, by re-establishing them through the production entry point rather than a probe.
/// <para>
/// <c>Assert.Ignore</c> when the server is not installed, following
/// <c>SinkTimestampAcceptanceTests</c>: a developer without PostgreSQL still gets a green suite, and
/// a machine with one gets the real coverage.
/// </para>
/// <para>
/// The three cases per provider are the ones that matter operationally: the fresh install where the
/// database must appear, the provisioned install where nothing may be attempted, and the hardened
/// install where the attempt is refused and must be explained rather than swallowed.
/// </para>
/// </remarks>
[TestFixture]
public class LogDatabaseCreationAcceptanceTests
{
    private const string PostgresMaintenance =
        "Host=localhost;Port=5433;Database=postgres;Username=postgres;Password=postgres;Timeout=3";

    private static string PostgresDb(string name, string user = "postgres", string password = "postgres") =>
        $"Host=localhost;Port=5433;Database={name};Username={user};Password={password};Timeout=3";

    private const string SqlServerMaster =
        @"Server=(localdb)\mssqllocaldb;Database=master;Trusted_Connection=True;";

    private static string SqlServerDb(string name) =>
        $@"Server=(localdb)\mssqllocaldb;Database={name};Trusted_Connection=True;";

    private static string SqlServerDbAs(string name, string login, string password) =>
        $@"Server=(localdb)\mssqllocaldb;Database={name};User Id={login};Password={password};TrustServerCertificate=True";

    // ------------------------------------------------------------------ PostgreSQL

    [Test]
    public async Task OnPostgres_TheLogDatabaseIsCreatedWhenAbsent_AndNothingIsIssuedWhenPresent()
    {
        if (!CanConnect(() => new NpgsqlConnection(PostgresMaintenance)))
        {
            Assert.Ignore("PostgreSQL is not listening on localhost:5433.");
        }

        var database = "gx_ldb_" + Guid.NewGuid().ToString("N")[..8];
        DropPostgresDatabase(database);

        try
        {
            PostgresDatabaseExists(database).Should().BeFalse("the fixture has not created it");

            // --- first start: absent, so it is created
            var first = await RunStartupCheckAsync(DbProviderKeys.Npgsql, PostgresDb(database));

            PostgresDatabaseExists(database).Should().BeTrue(
                "the application creates its log database, exactly as EF's Migrate() has always created the business one");
            first.Should().Contain(l => l.Level == LogLevel.Information && l.Message.Contains(database),
                "the third startup message names the database it is about to create");
            first.Should().NotContain(l => l.Level >= LogLevel.Error,
                "creating a database the login is allowed to create is not an error");

            // --- second start: present, so NOTHING is attempted and NOTHING is said
            var second = await RunStartupCheckAsync(DbProviderKeys.Npgsql, PostgresDb(database));

            second.Should().BeEmpty(
                "a provisioned deployment must start silently - no maintenance connection, no catalogue " +
                "query, no CREATE, no log line. That silence is what lets a least-privileged login start " +
                "the application on every run after the first");
        }
        finally
        {
            DropPostgresDatabase(database);
        }
    }

    [Test]
    public async Task OnPostgres_ALoginWithoutCreatedb_GetsOneErrorNamingTheGrant_AndTheApplicationContinues()
    {
        if (!CanConnect(() => new NpgsqlConnection(PostgresMaintenance)))
        {
            Assert.Ignore("PostgreSQL is not listening on localhost:5433.");
        }

        var database = "gx_ldb_" + Guid.NewGuid().ToString("N")[..8];
        var role = "gx_ldb_role_" + Guid.NewGuid().ToString("N")[..8];

        ExecPostgres(PostgresMaintenance, $"CREATE ROLE {role} LOGIN PASSWORD 'probe' NOCREATEDB");
        try
        {
            // The hardened production shape: the application's own login may connect, and may not
            // create databases. Pass 15 measured the 42501 this produces.
            var records = await RunStartupCheckAsync(
                DbProviderKeys.Npgsql, PostgresDb(database, role, "probe"));

            PostgresDatabaseExists(database).Should().BeFalse("the role may not create databases");

            var denials = records.Where(l =>
                l.Level == LogLevel.Error && l.Message.Contains("may not create it")).ToList();

            denials.Should().ContainSingle("exactly one error explains the denial, not one per attempt");
            denials[0].Message.Should().Contain(database).And.Contain(role).And.Contain("CREATEDB",
                "the message has to name the database, the login and the exact grant, or the operator " +
                "is left to work out which of the three is wrong");

            // The whole point of the non-fatal posture: nothing threw, so the application would have
            // gone on to serve and audit normally with the SystemLogs page reporting unavailable.
            records.Should().Contain(l => l.Message.Contains("unavailable"),
                "the existing diagnostic still fires afterwards, because the table could not be made either");
        }
        finally
        {
            DropPostgresDatabase(database);
            ExecPostgres(PostgresMaintenance, $"DROP ROLE IF EXISTS {role}");
        }
    }

    [Test]
    public void OnPostgres_ALostRaceIsRecognisedAsAlreadyExisting()
    {
        if (!CanConnect(() => new NpgsqlConnection(PostgresMaintenance)))
        {
            Assert.Ignore("PostgreSQL is not listening on localhost:5433.");
        }

        // A real 42P04 rather than a constructed one. This is what two instances starting together
        // produce when both see "absent" and both issue CREATE - the loser gets this, and gets the
        // outcome it wanted.
        var database = "gx_ldb_" + Guid.NewGuid().ToString("N")[..8];
        ExecPostgres(PostgresMaintenance, LogDatabaseDdl.CreateStatement(DbProviderKeys.Npgsql, database));

        try
        {
            var thrown = Assert.Catch(() =>
                ExecPostgres(PostgresMaintenance, LogDatabaseDdl.CreateStatement(DbProviderKeys.Npgsql, database)))!;

            ((PostgresException)thrown).SqlState.Should().Be("42P04");
            LogDatabaseDdl.IsAlreadyExists(thrown).Should().BeTrue();
            LogDatabaseDdl.IsPermissionDenied(thrown).Should().BeFalse();
        }
        finally
        {
            DropPostgresDatabase(database);
        }
    }

    // ------------------------------------------------------------------ SQL Server

    [Test]
    public async Task OnSqlServer_TheLogDatabaseIsCreatedWhenAbsent_AndNothingIsIssuedWhenPresent()
    {
        if (!CanConnect(() => new SqlConnection(SqlServerMaster)))
        {
            Assert.Ignore("SQL Server LocalDB is not available.");
        }

        var database = "GxLdb" + Guid.NewGuid().ToString("N")[..8];
        DropSqlServerDatabase(database);

        try
        {
            var first = await RunStartupCheckAsync(DbProviderKeys.SqlServer, SqlServerDb(database));

            SqlServerDatabaseExists(database).Should().BeTrue();
            first.Should().Contain(l => l.Level == LogLevel.Information && l.Message.Contains(database));
            first.Should().NotContain(l => l.Level >= LogLevel.Error);

            var second = await RunStartupCheckAsync(DbProviderKeys.SqlServer, SqlServerDb(database));
            second.Should().BeEmpty();
        }
        finally
        {
            DropSqlServerDatabase(database);
        }
    }

    [Test]
    public async Task OnSqlServer_ALoginWithoutDbcreator_GetsOneErrorNamingTheGrant_AndTheApplicationContinues()
    {
        if (!CanConnect(() => new SqlConnection(SqlServerMaster)))
        {
            Assert.Ignore("SQL Server LocalDB is not available.");
        }

        var database = "GxLdb" + Guid.NewGuid().ToString("N")[..8];
        var login = "gx_ldb_" + Guid.NewGuid().ToString("N")[..8];
        const string password = "Pr0be-Pass!";

        ExecSqlServer(SqlServerMaster,
            $"CREATE LOGIN [{login}] WITH PASSWORD = '{password}', CHECK_POLICY = OFF");
        try
        {
            var records = await RunStartupCheckAsync(
                DbProviderKeys.SqlServer, SqlServerDbAs(database, login, password));

            SqlServerDatabaseExists(database).Should().BeFalse();

            var denials = records.Where(l =>
                l.Level == LogLevel.Error && l.Message.Contains("may not create it")).ToList();

            denials.Should().ContainSingle();
            denials[0].Message.Should().Contain(database).And.Contain(login).And.Contain("dbcreator");

            // This is the case ruling 4 exists for: without the messages above, the only diagnostic
            // SQL Server offers for a missing database is 4060, whose text is "The login failed."
            denials[0].Message.Should().Contain("does not exist",
                "the operator must be told the database is missing, not that their password is wrong");
        }
        finally
        {
            DropSqlServerDatabase(database);
            ExecSqlServerQuiet(SqlServerMaster, $"DROP LOGIN [{login}]");
        }
    }

    [Test]
    public void OnSqlServer_ALostRaceIsRecognisedAsAlreadyExisting()
    {
        if (!CanConnect(() => new SqlConnection(SqlServerMaster)))
        {
            Assert.Ignore("SQL Server LocalDB is not available.");
        }

        // The guarded statement this code issues cannot produce 1801, because T-SQL's own
        // IF DB_ID(...) IS NULL suppresses it. The classifier still has to recognise it, so this
        // provokes a real one with the UNguarded form - which is what a future edit dropping the
        // guard would start producing.
        var database = "GxLdb" + Guid.NewGuid().ToString("N")[..8];
        ExecSqlServer(SqlServerMaster, $"CREATE DATABASE [{database}]");

        try
        {
            var thrown = Assert.Catch(() =>
                ExecSqlServer(SqlServerMaster, $"CREATE DATABASE [{database}]"))!;

            ((SqlException)thrown).Number.Should().Be(1801);
            LogDatabaseDdl.IsAlreadyExists(thrown).Should().BeTrue();
            LogDatabaseDdl.IsPermissionDenied(thrown).Should().BeFalse();

            // And the statement the code actually issues is silent about it.
            Assert.DoesNotThrow(() => ExecSqlServer(
                SqlServerMaster, LogDatabaseDdl.CreateStatement(DbProviderKeys.SqlServer, database)));
        }
        finally
        {
            DropSqlServerDatabase(database);
        }
    }

    // ------------------------------------------------------------------ SQLite

    [Test]
    public async Task OnSqlite_AConfiguredPathInAMissingDirectory_NowWorks()
    {
        // Pass 15 measured the failure this closes: SQLite creates the file but never the folder,
        // so this configuration failed with "SQLite Error 14: unable to open database file".
        var root = Path.Combine(Path.GetTempPath(), "gx-ldb-sqlite", Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "nested", "logs.db");

        try
        {
            var records = await RunStartupCheckAsync(DbProviderKeys.SqLite, $"Data Source={target}");

            File.Exists(target).Should().BeTrue("the directory is created, and SQLite then makes the file");
            records.Should().NotContain(l => l.Level >= LogLevel.Error);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // ------------------------------------------------------------------ the harness

    private sealed record LogRecord(LogLevel Level, string Message);

    /// <summary>
    /// Runs the real <c>PrepareLogDatabaseAsync</c> over the real registrations, and returns
    /// everything it logged.
    /// </summary>
    /// <remarks>
    /// Through <c>AddInfrastructure</c> rather than a hand-built context, so the code under test is
    /// reached the way production reaches it - the same <c>ILogDbContextFactory</c>, the same
    /// <c>DatabaseSettings</c>, the same options lambda.
    /// </remarks>
    private static async Task<List<LogRecord>> RunStartupCheckAsync(string provider, string logConnectionString)
    {
        var records = new List<LogRecord>();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DatabaseSettings:DBProvider"] = provider,
                // The business database is never touched by this method; it only has to parse.
                ["DatabaseSettings:ConnectionString"] = logConnectionString,
                ["DatabaseSettings:LogConnectionString"] = logConnectionString,
                ["IdentitySettings:RequireDigit"] = "true",
                ["AppConfigurationSettings:AppName"] = "GX Application",
                ["AppConfigurationSettings:DefaultTimeZone"] = "UTC"
            })
            .Build();

        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IConfiguration>(configuration);
                services.AddLogging(b => b
                    .SetMinimumLevel(LogLevel.Information)
                    .AddProvider(new CapturingProvider(records)));
                services.AddApplication();
                services.AddInfrastructure(configuration);
            })
            .Build();

        await host.PrepareLogDatabaseAsync();
        return records;
    }

    private sealed class CapturingProvider(List<LogRecord> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(sink);
        public void Dispose() { }

        private sealed class CapturingLogger(List<LogRecord> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                // Only what the startup check itself says. EF's own connection and query errors are
                // logged too, and counting them would make "exactly one error" mean nothing.
                var message = formatter(state, exception);
                if (!message.Contains("log database", StringComparison.OrdinalIgnoreCase)) return;

                // The exception's identity comes along, because these messages are deliberately
                // written for operators and say nothing about which error code produced them - so a
                // failing assertion here would otherwise report "unreachable" and leave the reader
                // no way to tell a refused login from a blocked pool from a server that is down.
                for (var e = exception; e is not null; e = e.InnerException)
                {
                    message += $" [{e.GetType().Name}: {e.Message.Split('\n')[0]}]";
                }

                sink.Add(new LogRecord(logLevel, message));
            }
        }
    }

    // ------------------------------------------------------------------ server helpers

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

    private static void ExecPostgres(string connectionString, string sql)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static bool PostgresDatabaseExists(string name)
    {
        using var connection = new NpgsqlConnection(PostgresMaintenance);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM pg_database WHERE datname = '{name}'";
        return Convert.ToInt32(command.ExecuteScalar()) > 0;
    }

    private static void DropPostgresDatabase(string name)
    {
        NpgsqlConnection.ClearAllPools();
        try { ExecPostgres(PostgresMaintenance, $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)"); }
        catch (Exception) { /* a fixture teardown is not a test result */ }
    }

    private static void ExecSqlServer(string connectionString, string sql)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void ExecSqlServerQuiet(string connectionString, string sql)
    {
        try { ExecSqlServer(connectionString, sql); } catch (Exception) { }
    }

    private static bool SqlServerDatabaseExists(string name)
    {
        using var connection = new SqlConnection(SqlServerMaster);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT CASE WHEN DB_ID('{name}') IS NULL THEN 0 ELSE 1 END";
        return Convert.ToInt32(command.ExecuteScalar()) > 0;
    }

    private static void DropSqlServerDatabase(string name)
    {
        SqlConnection.ClearAllPools();
        ExecSqlServerQuiet(SqlServerMaster,
            $"IF DB_ID('{name}') IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]; END");
    }
}
#nullable restore
