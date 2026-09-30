#nullable enable
using System;
using CleanArchitecture.Blazor.TestSupport;
using NUnit.Framework;

// One test at a time, for the whole assembly (pass 47, CO-147). Every host and every component test here
// shares the business database, and a factory resets it before its host boots, so two fixtures running side by side
// would empty each other's installation mid-test. LevelOfParallelism(1) caps the parallel queue at a
// single worker, so even a fixture that later asks for [Parallelizable] still runs alone;
// NonParallelizable states the same intent at assembly level. HarnessTests pins both.
[assembly: NonParallelizable]
[assembly: LevelOfParallelism(1)]

namespace CleanArchitecture.Blazor.Server.UI.IntegrationTests;

/// <summary>
/// The two PostgreSQL databases this assembly owns: <c>gx_test_&lt;project&gt;_ui</c> (business) and
/// <c>gx_test_&lt;project&gt;_ui_logs</c> (logs), on the server named by <c>GX_TEST_PG</c>.
/// </summary>
/// <remarks>
/// There is no file-database fallback. With <c>GX_TEST_PG</c> unset, the first use throws the
/// <see cref="PostgresTestDatabase"/> message naming the variable, and every test that needs a
/// database fails with it. Both names pass <see cref="TestDatabaseGuard"/>.
/// <para>
/// The business database is migrated once per run and reset (one <c>DELETE</c> batch plus a
/// sequence restart) before each host boots. The log database is only created if missing: the
/// application creates its table, and nothing here ever resets or drops it.
/// </para>
/// </remarks>
internal static class UiTestDatabase
{
    public const string BusinessDatabaseName = TestDatabaseNames.Ui;
    public const string LogDatabaseName = TestDatabaseNames.UiLogs;

    private static readonly Lazy<PostgresTestDatabase> BusinessDatabase =
        new(() => PostgresTestDatabase.FromEnvironment(BusinessDatabaseName));

    private static readonly Lazy<PostgresTestDatabase> LogDatabase = new(() =>
    {
        var logs = PostgresTestDatabase.FromEnvironment(LogDatabaseName);
        logs.EnsureExistsAsync().GetAwaiter().GetResult();
        return logs;
    });

    /// <summary>The business database, created and migrated if needed. Throws when GX_TEST_PG is unset.</summary>
    public static PostgresTestDatabase Business
    {
        get
        {
            var business = BusinessDatabase.Value;
            business.EnsureReadyAsync().GetAwaiter().GetResult();
            return business;
        }
    }

    /// <summary>The log database, created if missing. Never migrated, reset or dropped.</summary>
    public static PostgresTestDatabase Logs => LogDatabase.Value;

    /// <summary>Empties the business database, for a component test that seeds its own rows.</summary>
    public static void Reset() => Business.ResetAsync().GetAwaiter().GetResult();
}
#nullable restore
