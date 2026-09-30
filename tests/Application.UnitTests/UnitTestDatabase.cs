#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.TestSupport;
using Npgsql;

namespace CleanArchitecture.Blazor.Application.UnitTests;

/// <summary>
/// This assembly's PostgreSQL database, <c>gx_test_&lt;project&gt;_unit</c>, on the server named by <c>GX_TEST_PG</c>.
/// </summary>
/// <remarks>
/// <para>
/// It replaces the in-memory database each class used to build in its <c>[SetUp]</c> (pass 47,
/// CO-148). A class keeps its shape: it takes a connection from <see cref="NewConnection"/> where it
/// used to open an in-memory connection, and calls <see cref="ResetAsync"/> where it used to call
/// <c>EnsureCreatedAsync</c>. The reset empties every table and restarts every sequence, so each test
/// starts from what a fresh in-memory database gave it.
/// </para>
/// <para>
/// Lazy, and not an NUnit <c>[SetUpFixture]</c>: most of this assembly needs no database, and a
/// missing <c>GX_TEST_PG</c> must fail the database tests (with a message naming the variable) without
/// failing the pure ones. NUnit runs this assembly serially, which the shared database requires.
/// </para>
/// </remarks>
public static class UnitTestDatabase
{
    public const string DatabaseName = TestDatabaseNames.Unit;

    private static readonly Lazy<PostgresTestDatabase> Instance =
        new(() => PostgresTestDatabase.FromEnvironment(DatabaseName), LazyThreadSafetyMode.ExecutionAndPublication);

    public static PostgresTestDatabase Database => Instance.Value;

    public static string ConnectionString => Database.ConnectionString;

    /// <summary>
    /// A new, unopened connection to <c>gx_test_&lt;project&gt;_unit</c>, which exists and is migrated by the time this
    /// returns. Several classes open their connection before they reset, so on a machine's first run
    /// the database must not depend on the reset having created it.
    /// </summary>
    public static NpgsqlConnection NewConnection()
    {
        Database.EnsureReadyAsync().GetAwaiter().GetResult();
        return new NpgsqlConnection(ConnectionString);
    }

    /// <summary>Creates and migrates the database on first use, then empties it.</summary>
    public static Task ResetAsync() => Database.ResetAsync();
}
#nullable restore
