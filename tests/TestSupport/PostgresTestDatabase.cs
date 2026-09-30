using System.Text.RegularExpressions;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CleanArchitecture.Blazor.TestSupport;

/// <summary>
/// One test assembly's PostgreSQL database: created if missing, migrated once per run through the
/// real migrations, and emptied before each test. <b>Never dropped.</b>
/// </summary>
/// <remarks>
/// <para>
/// <b>Where the database is.</b> The server comes from one environment variable,
/// <see cref="ServerVariable"/> (<c>GX_TEST_PG</c>): host, port, username and password, and
/// <b>no</b> database. The database name is fixed by each test assembly (<see cref="TestDatabaseNames"/>).
/// No two assemblies ever share one, so assemblies that <c>dotnet test</c> runs side by side cannot
/// empty each other's tables. Every name passes <see cref="TestDatabaseGuard"/>.
/// </para>
/// <para>
/// <b>No fallback and no skip.</b> An unset <c>GX_TEST_PG</c> throws, naming the variable, so every
/// test that needs the database fails with the reason. A run that reported them as skipped would look
/// green in a summary while proving nothing.
/// </para>
/// <para>
/// <b>Why never drop.</b> The server is shared (a developer's local PostgreSQL holds real databases
/// too), and a <c>DROP DATABASE</c> can wait minutes for a checkpoint. A stale migration history fails
/// with the exact command to drop the database by hand instead.
/// </para>
/// <para>
/// <b>Why DELETE and not TRUNCATE.</b> Measured in GX ProjectTracking pass 4a: a <c>TRUNCATE</c> of
/// the schema costs about 58 ms, and a batch of <c>DELETE</c>s about 1 ms. The tables and foreign
/// keys are read from <c>pg_catalog</c> once per run, ordered children-first by
/// <see cref="ResetOrder"/>, and sent as ONE batch per reset. <c>__EFMigrationsHistory</c> is never
/// deleted.
/// </para>
/// <para>
/// <b>Sequences restart too</b>, in the same batch. A <c>DELETE</c> does not rewind identity
/// sequences, so without this a test's generated ids would depend on every test that ran before it,
/// and a test that seeds explicit ids and then inserts a generated row would collide only sometimes.
/// Restarting gives each test the ids a fresh database gives.
/// </para>
/// </remarks>
public sealed class PostgresTestDatabase
{
    /// <summary>The environment variable naming the server (no database).</summary>
    public const string ServerVariable = "GX_TEST_PG";

    /// <summary>EF's history table: a table a reset must keep.</summary>
    public const string MigrationsHistoryTable = "__EFMigrationsHistory";

    /// <summary>
    /// The GX lookup-table prefix. A lookup such as <c>TBL_LK_CHANNEL</c> is seeded by the MIGRATION,
    /// as the history is: schema state, not test data. A reset that emptied it would leave every later
    /// test without the reference rows the application ships with, so a reset keeps it, and its sequence.
    /// </summary>
    public const string LookupTablePrefix = "TBL_LK_";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _ready;
    private string? _resetBatch;

    private PostgresTestDatabase(string databaseName, string connectionString, NpgsqlConnectionStringBuilder server)
    {
        DatabaseName = databaseName;
        ConnectionString = connectionString;
        Server = server;
    }

    /// <summary>The database this assembly owns, for example <c>gx_test_&lt;project&gt;_appint</c>.</summary>
    public string DatabaseName { get; }

    /// <summary>The server from <see cref="ServerVariable"/>, with <see cref="DatabaseName"/> added.</summary>
    public string ConnectionString { get; }

    private NpgsqlConnectionStringBuilder Server { get; }

    /// <summary>The database <paramref name="databaseName"/> on the server named by <c>GX_TEST_PG</c>.</summary>
    public static PostgresTestDatabase FromEnvironment(string databaseName) =>
        Create(databaseName, Environment.GetEnvironmentVariable(ServerVariable));

    /// <summary>
    /// Validates the server connection string and the database name, and connects to nothing.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The server is not given, is not a connection string, names a database, or the database name
    /// is not a throwaway <c>gx_test_</c> one.
    /// </exception>
    public static PostgresTestDatabase Create(string databaseName, string? serverConnectionString)
    {
        if (string.IsNullOrWhiteSpace(serverConnectionString))
        {
            throw new InvalidOperationException(
                $"{ServerVariable} is not set. The database tests need a PostgreSQL server: set {ServerVariable} to a " +
                "connection string with host, port, username and password and NO database, for example " +
                $"Host=localhost;Port=5434;Username=postgres;Password=<your password>. Each test assembly uses its own " +
                $"{TestDatabaseGuard.RequiredPrefix} database on that server (see README, Running the tests).");
        }

        NpgsqlConnectionStringBuilder server;
        try
        {
            server = new NpgsqlConnectionStringBuilder(serverConnectionString);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException($"{ServerVariable} is not a valid PostgreSQL connection string ({ex.Message}).");
        }

        if (!string.IsNullOrEmpty(server.Database))
        {
            throw new InvalidOperationException(
                $"{ServerVariable} names a database ('{server.Database}'). It must name only the server (host, port, " +
                $"username, password): each test assembly chooses its own {TestDatabaseGuard.RequiredPrefix} database, so " +
                "two assemblies can never empty each other's tables. Remove Database= from it.");
        }

        var withDatabase = new NpgsqlConnectionStringBuilder(server.ConnectionString) { Database = databaseName };
        var refusal = TestDatabaseGuard.Refusal("This test assembly", withDatabase.ConnectionString);
        if (refusal is not null)
        {
            throw new InvalidOperationException(refusal);
        }

        return new PostgresTestDatabase(databaseName, withDatabase.ConnectionString, server);
    }

    /// <summary>
    /// Creates the database if it is missing, refuses a stale migration history, applies the
    /// migrations with the application's model, and prepares the reset. Once per instance.
    /// </summary>
    public async Task EnsureReadyAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_ready) return;

            await EnsureExistsAsync(cancellationToken);

            await using (var context = new ApplicationDbContext(ApplicationModel.NpgsqlOptions(ConnectionString)))
            {
                var defined = context.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
                var stale = (await context.Database.GetAppliedMigrationsAsync(cancellationToken))
                    .Where(m => !defined.Contains(m)).ToList();
                if (stale.Count > 0)
                {
                    throw new InvalidOperationException(StaleHistoryMessage(stale));
                }

                // PendingModelChangesWarning is NOT suppressed: it is exactly how a harness migrating
                // with a model other than the application's would announce itself.
                await context.Database.MigrateAsync(cancellationToken);
            }

            _resetBatch = await BuildResetBatchAsync(cancellationToken);
            _ready = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Empties every table except the migration history and the seeded lookups, in one round trip.</summary>
    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        if (!_ready)
        {
            await EnsureReadyAsync(cancellationToken);
        }

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(_resetBatch, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>The ordered DELETE batch a reset sends. Exposed for tests and diagnostics.</summary>
    public string ResetBatch => _resetBatch ?? throw new InvalidOperationException("Call EnsureReadyAsync first.");

    /// <summary>
    /// Creates the database if it is missing, and does nothing else: no migration and no reset. For a
    /// database the application fills itself, such as a log database. Never drops.
    /// </summary>
    public async Task EnsureExistsAsync(CancellationToken cancellationToken = default)
    {
        var maintenance = new NpgsqlConnectionStringBuilder(Server.ConnectionString) { Database = "postgres" };
        await using var connection = new NpgsqlConnection(maintenance.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using (var exists = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @name", connection))
        {
            exists.Parameters.AddWithValue("name", DatabaseName);
            if (await exists.ExecuteScalarAsync(cancellationToken) is not null) return;
        }

        // The name has passed TestDatabaseGuard (gx_test_...), and it is quoted as an identifier anyway.
        await using var create = new NpgsqlCommand($"CREATE DATABASE {QuoteIdentifier(DatabaseName)}", connection);
        try
        {
            await create.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.DuplicateDatabase)
        {
            // Another test assembly's process created it between the check and the CREATE.
        }
    }

    private async Task<string> BuildResetBatchAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);

        var tables = new List<string>();
        await using (var command = new NpgsqlCommand(
            "SELECT format('%I.%I', n.nspname, c.relname) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace " +
            "WHERE c.relkind IN ('r', 'p') AND n.nspname NOT IN ('pg_catalog', 'information_schema') " +
            "AND n.nspname NOT LIKE 'pg\\_%' AND c.relname <> @history AND left(c.relname, length(@lookup)) <> @lookup ORDER BY 1", connection))
        {
            command.Parameters.AddWithValue("history", MigrationsHistoryTable);
            command.Parameters.AddWithValue("lookup", LookupTablePrefix);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) tables.Add(reader.GetString(0));
        }

        var foreignKeys = new List<(string Child, string Parent)>();
        await using (var command = new NpgsqlCommand(
            "SELECT format('%I.%I', cn.nspname, c.relname), format('%I.%I', pn.nspname, p.relname) FROM pg_constraint k " +
            "JOIN pg_class c ON c.oid = k.conrelid JOIN pg_namespace cn ON cn.oid = c.relnamespace " +
            "JOIN pg_class p ON p.oid = k.confrelid JOIN pg_namespace pn ON pn.oid = p.relnamespace " +
            "WHERE k.contype = 'f'", connection))
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) foreignKeys.Add((reader.GetString(0), reader.GetString(1)));
        }

        // A table whose own trigger refuses DELETE (an append-only record, for example) is emptied with
        // TRUNCATE, which fires no row trigger: the one way to clear it, and only a throwaway test
        // database's reset uses it.
        var guarded = new HashSet<string>(StringComparer.Ordinal);
        await using (var command = new NpgsqlCommand(
            "SELECT DISTINCT format('%I.%I', n.nspname, c.relname) FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid " +
            "JOIN pg_namespace n ON n.oid = c.relnamespace WHERE NOT t.tgisinternal AND (t.tgtype & 8) <> 0", connection))
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) guarded.Add(reader.GetString(0));
        }

        return string.Concat(ResetOrder.ChildrenFirst(tables, foreignKeys)
                   .Select(t => guarded.Contains(t) ? $"TRUNCATE {t};" : $"DELETE FROM {t};"))
               + RestartSequences;
    }

    /// <summary>
    /// Rewinds every user sequence (identity columns included) to its start value, so the next
    /// generated id is the one a fresh database would give, except the sequences of the lookup tables
    /// the reset keeps. Their rows survive, and the migration advanced their sequence past them;
    /// rewinding it would make the next insert collide with a seeded id.
    /// </summary>
    private const string RestartSequences =
        "SELECT setval(s.seqrelid, s.seqstart, false) FROM pg_sequence s " +
        "JOIN pg_class c ON c.oid = s.seqrelid JOIN pg_namespace n ON n.oid = c.relnamespace " +
        "WHERE n.nspname NOT IN ('pg_catalog', 'information_schema') " +
        "AND NOT EXISTS (SELECT 1 FROM pg_depend d JOIN pg_class t ON t.oid = d.refobjid " +
        "WHERE d.objid = s.seqrelid AND d.deptype IN ('a', 'i') AND left(t.relname, length('" + LookupTablePrefix + "')) = '" + LookupTablePrefix + "');";

    private string StaleHistoryMessage(IReadOnlyList<string> stale) =>
        $"The test database '{DatabaseName}' has a stale migration history: it records {string.Join(", ", stale)}, " +
        "which this assembly no longer defines (expected after regenerating InitialCreate). The harness never drops " +
        "a database, so drop it by hand and run the suite again; the next run recreates it:" + Environment.NewLine +
        $"  psql -h {Server.Host} -p {Server.Port} -U {Server.Username} -d postgres -c \"DROP DATABASE {PsqlName(DatabaseName)} WITH (FORCE)\"";

    private static string QuoteIdentifier(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    /// <summary>Unquoted when PostgreSQL would fold it to itself anyway; quoted for the shell otherwise.</summary>
    private static string PsqlName(string name) =>
        Regex.IsMatch(name, "^[a-z_][a-z0-9_]*$") ? name : $"\\\"{name.Replace("\"", "\"\"")}\\\"";
}
