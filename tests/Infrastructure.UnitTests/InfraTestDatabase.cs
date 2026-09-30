using System;
using System.Threading;
using CleanArchitecture.Blazor.TestSupport;
using Npgsql;
using Xunit;

namespace CleanArchitecture.Blazor.Infrastructure.UnitTests;

/// <summary>
/// This assembly's PostgreSQL databases on the server named by <c>GX_TEST_PG</c> (pass 47):
/// <c>gx_test_&lt;project&gt;_infra</c> for the business model, migrated once per run and reset per test, and
/// <c>gx_test_&lt;project&gt;_infra_logs</c> for the log table, created if missing and filled by the tests themselves.
/// Neither is ever dropped.
/// </summary>
/// <remarks>
/// Synchronous on purpose: xUnit builds a test class in its constructor, where these classes used to
/// build an in-memory database. Every class that uses them is in <see cref="PostgresCollection"/>,
/// because xUnit otherwise runs test classes in parallel and they share the databases.
/// </remarks>
public static class InfraTestDatabase
{
    public const string DatabaseName = TestDatabaseNames.Infra;
    public const string LogDatabaseName = TestDatabaseNames.InfraLogs;

    private static readonly Lazy<PostgresTestDatabase> Business =
        new(() => PostgresTestDatabase.FromEnvironment(DatabaseName), LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<PostgresTestDatabase> Logs =
        new(() =>
        {
            var logs = PostgresTestDatabase.FromEnvironment(LogDatabaseName);
            logs.EnsureExistsAsync().GetAwaiter().GetResult();
            return logs;
        }, LazyThreadSafetyMode.ExecutionAndPublication);

    public static PostgresTestDatabase Database => Business.Value;

    /// <summary>The log database's connection string. The database exists by the time this returns.</summary>
    public static string LogConnectionString => Logs.Value.ConnectionString;

    /// <summary>A new, unopened connection to <c>gx_test_&lt;project&gt;_infra</c>, created and migrated if needed.</summary>
    public static NpgsqlConnection NewConnection()
    {
        Database.EnsureReadyAsync().GetAwaiter().GetResult();
        return new NpgsqlConnection(Database.ConnectionString);
    }

    /// <summary>Empties <c>gx_test_&lt;project&gt;_infra</c> and restarts its sequences.</summary>
    public static void Reset() => Database.ResetAsync().GetAwaiter().GetResult();
}

/// <summary>
/// Every test class that touches this assembly's PostgreSQL databases. One collection, run serially,
/// because the databases are shared.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class PostgresCollection
{
    public const string Name = "postgresql";
}
