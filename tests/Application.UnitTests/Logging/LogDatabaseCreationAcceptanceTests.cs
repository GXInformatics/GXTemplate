#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Infrastructure;
using CleanArchitecture.Blazor.Infrastructure.Persistence.Logging;
using CleanArchitecture.Blazor.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.Logging;

/// <summary>
/// The log database being brought into existence, against a real PostgreSQL server.
/// </summary>
/// <remarks>
/// Pass 15 established the mechanics by probe: PostgreSQL denies with <c>42501</c>, a lost race is
/// <c>42P04</c>, and the catalogue check runs under a login that cannot create anything. These tests
/// keep the code that acts on those facts honest, by re-establishing them through the production
/// entry point rather than a probe.
/// <para>
/// <b>The server comes from <c>GX_TEST_PG</c> only</b> (pass 47, CO-42; these tests used to hard-code a
/// second server on port 5433, with its credentials, and skip when it was not listening).
/// </para>
/// <para>
/// <b>Two of them are opt-in.</b> The create-when-absent path can only be tested from a database, and
/// a role, that does NOT exist, so each run has to create one and remove it again. Dropping a database
/// on the shared local server waits for a checkpoint, and nothing else in the test suites ever drops
/// one. So these two run only when <see cref="CreateDatabasesVariable"/> is <c>1</c>. Otherwise they
/// are reported as SKIPPED (<c>Assert.Ignore</c>, never a pass). When they run they use
/// <c>gx_test_&lt;project&gt;_ldb_</c> names and drop exactly what they created. The third needs only a database
/// that already exists, so it runs every time against the fixed <c>gx_test_&lt;project&gt;_unit_logs</c>.
/// </para>
/// </remarks>
[TestFixture]
public class LogDatabaseCreationAcceptanceTests
{
    /// <summary>Set to <c>1</c> to run the tests that create, and then drop, databases and roles.</summary>
    public const string CreateDatabasesVariable = "GX_TEST_CREATE_DATABASES";

    /// <summary>This assembly's fixed log database: created if missing, never dropped.</summary>
    private const string LogDatabaseName = TestDatabaseNames.UnitLogs;

    /// <summary>Whether the opt-in value is set. Only exactly <c>1</c> opts in.</summary>
    public static bool OptedIn(string? value) => value == "1";

    /// <summary>
    /// The only databases these tests may drop: the <c>gx_test_&lt;project&gt;_ldb_</c> ones they create themselves.
    /// Never the fixed unit and unit_logs databases, and never anything outside that prefix.
    /// </summary>
    public static bool IsDroppable(string name) => name.StartsWith(DroppablePrefix, StringComparison.Ordinal);

    private const string DroppablePrefix = TestDatabaseNames.ProjectPrefix + "ldb_";

    private static void RequireOptIn()
    {
        if (!OptedIn(Environment.GetEnvironmentVariable(CreateDatabasesVariable)))
        {
            Assert.Ignore($"Creates and drops a database or role on the GX_TEST_PG server; set {CreateDatabasesVariable}=1 to run it.");
        }
    }

    /// <summary>A fresh database name, under <see cref="DroppablePrefix"/>, that does not exist yet.</summary>
    private static string NewDatabaseName() => DroppablePrefix + Guid.NewGuid().ToString("N")[..8];

    // ------------------------------------------------------------------ the opt-in gate

    [TestCase(null, false)]
    [TestCase("", false)]
    [TestCase("0", false)]
    [TestCase("true", false)]
    [TestCase("1", true)]
    public void OnlyTheValueOne_OptsIn(string? value, bool expected)
    {
        OptedIn(value).Should().Be(expected);
    }

    [TestCase(DroppablePrefix + "1a2b3c4d", true)]
    [TestCase(TestDatabaseNames.Unit, false)]
    [TestCase(TestDatabaseNames.UnitLogs, false)]
    [TestCase(TestDatabaseNames.AppInt, false)]
    [TestCase("GXTemplateDatabase", false)]
    [TestCase("gx_test_ldb_1a2b3c4d", false)]
    [TestCase("postgres", false)]
    public void OnlyTheDatabasesTheseTestsCreate_MayBeDropped(string name, bool expected)
    {
        IsDroppable(name).Should().Be(expected);
    }

    // ------------------------------------------------------------------ PostgreSQL (opt-in)

    [Test]
    public async Task OnPostgres_TheLogDatabaseIsCreatedWhenAbsent_AndNothingIsIssuedWhenPresent()
    {
        // The server first: an unset GX_TEST_PG fails here, naming it, and is never reported as skipped.
        var database = PostgresTestDatabase.FromEnvironment(NewDatabaseName());
        RequireOptIn();
        PostgresDatabaseExists(database.DatabaseName).Should().BeFalse("the name is new to this run");

        try
        {
            // --- first start: absent, so it is created
            var first = await RunStartupCheckAsync(database.ConnectionString);

            PostgresDatabaseExists(database.DatabaseName).Should().BeTrue(
                "the application creates its log database, exactly as EF's Migrate() has always created the business one");
            first.Should().Contain(l => l.Level == LogLevel.Information && l.Message.Contains(database.DatabaseName),
                "the third startup message names the database it is about to create");
            first.Should().NotContain(l => l.Level >= LogLevel.Error,
                "creating a database the login is allowed to create is not an error");

            // --- second start: present, so NOTHING is attempted and NOTHING is said
            var second = await RunStartupCheckAsync(database.ConnectionString);

            second.Should().BeEmpty(
                "a provisioned deployment must start silently - no maintenance connection, no catalogue " +
                "query, no CREATE, no log line. That silence is what lets a least-privileged login start " +
                "the application on every run after the first");
        }
        finally
        {
            // This test's run created it (the application did, on the first start).
            DropDatabaseIfExists(database.DatabaseName);
        }
    }

    [Test]
    public async Task OnPostgres_ALoginWithoutCreatedb_GetsOneErrorNamingTheGrant_AndTheApplicationContinues()
    {
        var database = PostgresTestDatabase.FromEnvironment(NewDatabaseName());
        RequireOptIn();
        // Not under the project prefix: a role name is limited to 63 bytes too, and this one needs 17 more.
        var role = "gx_test_ldb_role_" + Guid.NewGuid().ToString("N")[..8];
        // A throwaway secret for a throwaway role, generated per run: no credential in the source.
        var rolePassword = Guid.NewGuid().ToString("N");

        Exec(Maintenance(), $"CREATE ROLE {role} LOGIN PASSWORD '{rolePassword}' NOCREATEDB");
        try
        {
            // The hardened production shape: the application's own login may connect, and may not
            // create databases. Pass 15 measured the 42501 this produces.
            var asRole = new NpgsqlConnectionStringBuilder(database.ConnectionString)
            {
                Username = role,
                Password = rolePassword
            }.ConnectionString;
            var records = await RunStartupCheckAsync(asRole);

            PostgresDatabaseExists(database.DatabaseName).Should().BeFalse("the role may not create databases");

            var denials = records.Where(l =>
                l.Level == LogLevel.Error && l.Message.Contains("may not create it")).ToList();

            denials.Should().ContainSingle("exactly one error explains the denial, not one per attempt");
            denials[0].Message.Should().Contain(database.DatabaseName).And.Contain(role).And.Contain("CREATEDB",
                "the message has to name the database, the login and the exact grant, or the operator " +
                "is left to work out which of the three is wrong");

            // The whole point of the non-fatal posture: nothing threw, so the application would have
            // gone on to serve and audit normally with the SystemLogs page reporting unavailable.
            records.Should().Contain(l => l.Message.Contains("unavailable"),
                "the existing diagnostic still fires afterwards, because the table could not be made either");
        }
        finally
        {
            // Only what this run created: the role, and the database only if the code under test
            // created it despite the missing grant (the name is unique to this run).
            DropDatabaseIfExists(database.DatabaseName);
            NpgsqlConnection.ClearAllPools();
            Exec(Maintenance(), $"DROP ROLE IF EXISTS {role}");
        }
    }

    // ------------------------------------------------------------------ PostgreSQL (always)

    [Test]
    public async Task OnPostgres_ALostRaceIsRecognisedAsAlreadyExisting()
    {
        // A real 42P04 rather than a constructed one. This is what two instances starting together
        // produce when both see "absent" and both issue CREATE: the loser gets this, and gets the
        // outcome it wanted. Issued against a database that already exists, so nothing is created
        // and nothing needs dropping.
        var logs = PostgresTestDatabase.FromEnvironment(LogDatabaseName);
        await logs.EnsureExistsAsync();

        var thrown = Assert.Catch(() =>
            Exec(Maintenance(), LogDatabaseDdl.CreateStatement(DbProviderKeys.Npgsql, logs.DatabaseName)))!;

        ((PostgresException)thrown).SqlState.Should().Be("42P04");
        LogDatabaseDdl.IsAlreadyExists(thrown).Should().BeTrue();
        LogDatabaseDdl.IsPermissionDenied(thrown).Should().BeFalse();
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
    private static async Task<List<LogRecord>> RunStartupCheckAsync(string logConnectionString)
    {
        var records = new List<LogRecord>();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DatabaseSettings:DBProvider"] = DbProviderKeys.Npgsql,
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

    /// <summary>The GX_TEST_PG server's maintenance database.</summary>
    private static string Maintenance() =>
        new NpgsqlConnectionStringBuilder(PostgresTestDatabase.FromEnvironment(LogDatabaseName).ConnectionString)
        {
            Database = "postgres"
        }.ConnectionString;

    private static void Exec(string connectionString, string sql)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static bool PostgresDatabaseExists(string name)
    {
        using var connection = new NpgsqlConnection(Maintenance());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pg_database WHERE datname = @name";
        command.Parameters.AddWithValue("name", name);
        return Convert.ToInt32(command.ExecuteScalar()) > 0;
    }

    /// <summary>
    /// Drops a database this test run created. The only DROP in the test suites, and it runs only
    /// inside the opt-in tests (pass 47, CO-150).
    /// </summary>
    private static void DropDatabaseIfExists(string name)
    {
        if (!IsDroppable(name))
        {
            throw new InvalidOperationException($"Refusing to drop '{name}': only {DroppablePrefix} databases created by these tests.");
        }

        NpgsqlConnection.ClearAllPools();
        if (!PostgresDatabaseExists(name)) return;
        Exec(Maintenance(), $"DROP DATABASE \"{name}\" WITH (FORCE)");
    }
}
#nullable restore
