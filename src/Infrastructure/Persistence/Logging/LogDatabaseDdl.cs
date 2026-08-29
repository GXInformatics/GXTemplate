// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Data.Common;
using CleanArchitecture.Blazor.Application.Common.Constants;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace CleanArchitecture.Blazor.Infrastructure.Persistence.Logging;

/// <summary>
/// The log DATABASE, as opposed to the log table: how to ask whether it exists, how to create it,
/// and how to reach a server on which it does not yet exist.
/// </summary>
/// <remarks>
/// A sibling of <see cref="LogTableDdl"/> rather than a member of it, and the separation is
/// load-bearing rather than tidy. <c>LogTableDdlTests.EveryStatementIsGuarded</c> asserts that every
/// statement <see cref="LogTableDdl.Statements"/> produces carries <c>IF NOT EXISTS</c>, which is
/// what lets a least-privileged production login start the application on every run after the first.
/// <b>No <c>CREATE DATABASE</c> can satisfy that contract</b> - PostgreSQL answers
/// <c>42601 syntax error at or near "NOT"</c> and T-SQL answers <c>156 incorrect syntax near the
/// keyword 'IF'</c>, both measured in Pass 15. Putting these statements in the same member would
/// therefore have forced that assertion to be weakened, taking the table guarantees down with it.
/// They live here instead, with their own guard contract stated below.
/// <para>
/// <b>The guard contract here.</b> Idempotence comes from the catalogue pre-check
/// (<see cref="ExistsCommandText"/>), not from the statement. On SQL Server the create statement is
/// additionally self-guarded, because T-SQL offers <c>IF DB_ID(...) IS NULL</c> - the same idiom
/// <see cref="LogTableDdl"/> uses for <c>sys.tables</c>. On PostgreSQL no guard exists at any price,
/// so a race between two instances starting together can still raise <c>42P04</c>; the caller treats
/// that as success. See <see cref="IsAlreadyExists"/>.
/// </para>
/// <para>
/// <b>Why the pre-check matters more than efficiency.</b> Both existence queries were proved in
/// Pass 15 to run under a login that cannot create databases at all - a PostgreSQL role with
/// <c>NOCREATEDB</c> and a SQL Server login without <c>dbcreator</c>. That is what lets a correctly
/// provisioned production deployment, where the database was created by an administrator in advance,
/// find it present and attempt nothing: the create path is dead code there, and the elevated grant
/// never comes up.
/// </para>
/// <para>
/// <b>No transaction.</b> <c>CREATE DATABASE</c> cannot run inside one - PostgreSQL <c>25001</c>,
/// SQL Server <c>226</c> - so the caller issues it on a dedicated connection with no ambient
/// transaction, which is also why this is raw ADO.NET rather than anything routed through EF.
/// </para>
/// </remarks>
public static class LogDatabaseDdl
{
    /// <summary>The parameter every <see cref="ExistsCommandText"/> binds the database name to.</summary>
    /// <remarks>
    /// A parameter rather than an interpolated literal. The database name reaches this class from
    /// configuration, which - unlike the wizard symbol, sanitised by <c>template.json</c> since
    /// Pass 13 - nothing sanitises. The existence check is the half of this class that CAN be
    /// parameterised, so it is; see <see cref="QuoteIdentifier"/> for the half that cannot.
    /// </remarks>
    public const string NameParameter = "@name";

    /// <summary>
    /// Whether this provider has a database that must be brought into existence separately.
    /// </summary>
    /// <remarks>
    /// False for SQLite alone: a SQLite database is a file, and <c>Microsoft.Data.Sqlite</c> creates
    /// it on <c>Open()</c>. Verified in Pass 15 rather than assumed - which also turned up the one
    /// thing SQLite does NOT do, handled by <see cref="EnsureParentDirectoryExists"/>.
    /// </remarks>
    public static bool RequiresExplicitCreation(string dbProvider) =>
        dbProvider.ToLowerInvariant() switch
        {
            DbProviderKeys.SqLite => false,
            DbProviderKeys.SqlServer => true,
            DbProviderKeys.Npgsql => true,
            _ => throw new InvalidOperationException($"DB Provider {dbProvider} is not supported.")
        };

    /// <summary>The grant a login needs before it may create a database on this provider.</summary>
    public static string RequiredGrant(string dbProvider) =>
        dbProvider.ToLowerInvariant() switch
        {
            DbProviderKeys.SqlServer => "the dbcreator server role (or CREATE DATABASE permission in master)",
            DbProviderKeys.Npgsql => "the CREATEDB attribute (ALTER ROLE ... CREATEDB)",
            _ => throw new InvalidOperationException($"DB Provider {dbProvider} is not supported.")
        };

    /// <summary>The name of the database the configured log connection string points at.</summary>
    public static string DatabaseName(string dbProvider, string connectionString) =>
        dbProvider.ToLowerInvariant() switch
        {
            DbProviderKeys.Npgsql => new NpgsqlConnectionStringBuilder(connectionString).Database ?? "",
            DbProviderKeys.SqlServer => new SqlConnectionStringBuilder(connectionString).InitialCatalog,
            DbProviderKeys.SqLite => new SqliteConnectionStringBuilder(connectionString).DataSource,
            _ => throw new InvalidOperationException($"DB Provider {dbProvider} is not supported.")
        };

    /// <summary>
    /// The maintenance database every server provider guarantees exists, so that a connection can be
    /// made to a server on which the log database does not yet exist.
    /// </summary>
    public static string MaintenanceDatabase(string dbProvider) =>
        dbProvider.ToLowerInvariant() switch
        {
            DbProviderKeys.Npgsql => "postgres",
            DbProviderKeys.SqlServer => "master",
            _ => throw new InvalidOperationException($"DB Provider {dbProvider} is not supported.")
        };

    /// <summary>
    /// A connection to the maintenance database, derived from the configured log connection string
    /// by swapping the database and preserving everything else.
    /// </summary>
    /// <remarks>
    /// Built through the provider's own connection-string builder rather than by string surgery, so
    /// host, port, credentials, TLS settings, timeouts and every other keyword survive exactly as
    /// configured.
    /// <para>
    /// <b>It reuses the log login's credentials.</b> That is worth stating because it is the one
    /// assumption in this design a hardened deployment may refuse: a login granted CONNECT on the
    /// log database only, and not on <c>postgres</c> / <c>master</c>, cannot open this connection at
    /// all. That failure is neither "the log database is unreachable" nor "permission to create was
    /// denied", and the caller reports it as its own thing - a message about the log database when
    /// the log database is fine would send an operator to the wrong server.
    /// </para>
    /// </remarks>
    public static DbConnection CreateMaintenanceConnection(string dbProvider, string connectionString)
    {
        switch (dbProvider.ToLowerInvariant())
        {
            case DbProviderKeys.Npgsql:
                var npgsql = new NpgsqlConnectionStringBuilder(connectionString)
                {
                    Database = MaintenanceDatabase(dbProvider)
                };
                return new NpgsqlConnection(npgsql.ConnectionString);

            case DbProviderKeys.SqlServer:
                var sqlServer = new SqlConnectionStringBuilder(connectionString)
                {
                    InitialCatalog = MaintenanceDatabase(dbProvider)
                };
                return new SqlConnection(sqlServer.ConnectionString);

            default:
                throw new InvalidOperationException($"DB Provider {dbProvider} is not supported.");
        }
    }

    /// <summary>
    /// A read-only catalogue query returning 1 if the log database already exists and 0 if it does
    /// not. Bind the name to <see cref="NameParameter"/>.
    /// </summary>
    /// <remarks>
    /// Runs on the MAINTENANCE connection, not on the log database - the whole point is to ask the
    /// question when the answer may be "no", at which moment the log database cannot be connected
    /// to. Both forms need no privilege beyond connecting, proved in Pass 15 against a
    /// <c>NOCREATEDB</c> role and a login without <c>dbcreator</c>.
    /// </remarks>
    public static string ExistsCommandText(string dbProvider) =>
        dbProvider.ToLowerInvariant() switch
        {
            DbProviderKeys.Npgsql =>
                $"SELECT COUNT(*) FROM pg_database WHERE datname = {NameParameter}",

            DbProviderKeys.SqlServer =>
                $"SELECT CASE WHEN DB_ID({NameParameter}) IS NULL THEN 0 ELSE 1 END",

            _ => throw new InvalidOperationException($"DB Provider {dbProvider} is not supported.")
        };

    /// <summary>The statement that brings the log database into existence.</summary>
    /// <remarks>
    /// The identifier is interpolated because <b>no provider lets a database name be a parameter in
    /// DDL</b> - which is exactly why <see cref="QuoteIdentifier"/> exists and is applied here
    /// rather than left to the caller.
    /// <para>
    /// PostgreSQL gets a bare <c>CREATE DATABASE</c>: there is no guarded form (<c>42601</c>), so
    /// the pre-check is the guard and a lost race raises <c>42P04</c>. SQL Server gets its own
    /// guarded form as well as the pre-check, because T-SQL has one and it costs nothing.
    /// </para>
    /// </remarks>
    public static string CreateStatement(string dbProvider, string databaseName) =>
        dbProvider.ToLowerInvariant() switch
        {
            DbProviderKeys.Npgsql =>
                $"CREATE DATABASE {QuoteIdentifier(dbProvider, databaseName)}",

            DbProviderKeys.SqlServer =>
                $"IF DB_ID({QuoteLiteral(databaseName)}) IS NULL " +
                $"CREATE DATABASE {QuoteIdentifier(dbProvider, databaseName)}",

            _ => throw new InvalidOperationException($"DB Provider {dbProvider} is not supported.")
        };

    /// <summary>
    /// Quotes a database name for use as an identifier in DDL, in the provider's own form.
    /// </summary>
    /// <remarks>
    /// <b>This is the layer that has to be safe.</b> The wizard's <c>DatabaseName</c> symbol is
    /// sanitised by <c>template.json</c>'s regex generator - anything outside letters, digits and
    /// underscore is stripped - but a generated project's <c>appsettings.json</c> and its
    /// <c>DatabaseSettings__LogConnectionString</c> environment variable are not sanitised by
    /// anything, and this class reads its name from there.
    /// <para>
    /// Two defences, deliberately belt and braces. First, the quote character is DOUBLED, which is
    /// the escape both dialects define and is complete on its own: a name containing <c>"</c> or
    /// <c>]</c> is rendered harmless rather than rejected. Second, control characters are refused
    /// outright - not because doubling fails to handle them, but because no legitimate database name
    /// contains a NUL or a newline, and a name that does is a misconfiguration worth failing loudly
    /// on rather than quietly creating.
    /// </para>
    /// <para>
    /// The throw is safe to perform here: every call site is inside the startup check's non-fatal
    /// try, so a rejected name is reported and survived like any other log-database failure.
    /// </para>
    /// </remarks>
    public static string QuoteIdentifier(string dbProvider, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException(
                "The log connection string names no database, so there is nothing to create.");
        }

        if (name.Any(char.IsControl))
        {
            throw new InvalidOperationException(
                "The log database name contains a control character. Check " +
                "DatabaseSettings:LogConnectionString - this is a malformed configuration value, " +
                "not a name any server would accept.");
        }

        return dbProvider.ToLowerInvariant() switch
        {
            DbProviderKeys.Npgsql => $"\"{name.Replace("\"", "\"\"")}\"",
            DbProviderKeys.SqlServer => $"[{name.Replace("]", "]]")}]",
            _ => throw new InvalidOperationException($"DB Provider {dbProvider} is not supported.")
        };
    }

    /// <summary>
    /// Quotes a database name as a T-SQL string literal, for the <c>DB_ID(...)</c> guard, where it
    /// is a value rather than an identifier and single quotes are the escape.
    /// </summary>
    public static string QuoteLiteral(string name) => $"N'{name.Replace("'", "''")}'";

    /// <summary>
    /// Whether the failure is "the database already exists" - which, arriving after the pre-check
    /// said it did not, means another instance created it in between.
    /// </summary>
    /// <remarks>
    /// Treated as success by the caller, deliberately. The pre-check and the create are two
    /// statements on one connection and nothing makes them atomic, so two application instances
    /// starting together can both see "absent" and both attempt it. The loser of that race has
    /// exactly the outcome it wanted - the database exists - and reporting an error would make a
    /// correct outcome look like a fault on every scaled deployment's startup.
    /// </remarks>
    public static bool IsAlreadyExists(Exception exception) => exception switch
    {
        PostgresException { SqlState: "42P04" } => true,   // duplicate_database
        SqlException { Number: 1801 } => true,             // Database '...' already exists
        _ => false
    };

    /// <summary>
    /// Whether the failure is "this login may not create databases" - the case that must be loud,
    /// specific and non-fatal.
    /// </summary>
    /// <remarks>
    /// Both codes measured in Pass 15 against a real restricted login: PostgreSQL <c>42501
    /// permission denied to create database</c> for a role with <c>NOCREATEDB</c>, SQL Server
    /// <c>262 CREATE DATABASE permission denied in database 'master'</c> for a login without
    /// <c>dbcreator</c>.
    /// </remarks>
    public static bool IsPermissionDenied(Exception exception) => exception switch
    {
        PostgresException { SqlState: "42501" } => true,
        SqlException { Number: 262 } => true,
        _ => false
    };

    /// <summary>
    /// Discards any pooled connections for the log connection string.
    /// </summary>
    /// <remarks>
    /// <b>Called immediately after the database is created, and the pass that added it did not
    /// expect to need it.</b> Reaching the creation path means a connection to the log database has
    /// just failed, and ADO.NET pools remember that.
    /// <para>
    /// SQL Server is the one that actually bites: <c>Microsoft.Data.SqlClient</c> applies a
    /// connection-pool blocking period after a login failure, so for several seconds afterwards it
    /// fails new attempts on that connection string <i>without contacting the server</i>. The
    /// database would be created and the log table would then fail to be created against it - a
    /// first run that leaves the log database existing but empty, fixed only by restarting.
    /// <c>LogDatabaseCreationAcceptanceTests</c> caught exactly that.
    /// </para>
    /// <para>
    /// <b>ClearAllPools rather than the targeted ClearPool, and that is a measurement rather than a
    /// preference.</b> The targeted form was tried first and is provably sufficient in isolation - a
    /// standalone probe that fails a connection, creates the database and then calls
    /// <c>SqlConnection.ClearPool</c> over the same string connects immediately, where without it
    /// the wait is a measured 5.0 seconds. It was NOT sufficient here, because the failed attempt
    /// this has to undo was made by EF's own connection rather than by one built from this string,
    /// and those do not land in the same pool group. Clearing everything is unambiguous.
    /// </para>
    /// <para>
    /// Safe at this point in startup, which is the only reason so blunt an instrument is acceptable:
    /// <c>PrepareLogDatabaseAsync</c> runs before the business database is migrated or seeded and
    /// long before the application serves, so there is no pooled connection anywhere worth keeping.
    /// Done for PostgreSQL as well, where the delay was never observed, because an asymmetry here
    /// would be a worse thing for the next reader to explain than one extra call.
    /// </para>
    /// </remarks>
    public static void ClearPool(string dbProvider)
    {
        switch (dbProvider.ToLowerInvariant())
        {
            case DbProviderKeys.Npgsql:
                NpgsqlConnection.ClearAllPools();
                break;

            case DbProviderKeys.SqlServer:
                SqlConnection.ClearAllPools();
                break;
        }
    }

    /// <summary>
    /// Creates the directory a SQLite log database file will live in, if it is not already there.
    /// </summary>
    /// <remarks>
    /// The one thing SQLite's own file creation does not do. <c>Microsoft.Data.Sqlite</c> creates the
    /// FILE on <c>Open()</c> but not the FOLDER, so a configured path like
    /// <c>Data Source=./data/logs.db</c> fails with <c>SQLite Error 14: 'unable to open database
    /// file'</c> when <c>./data</c> does not exist - measured in Pass 15. That error names neither
    /// the path nor the reason, which makes it a poor thing to hand an operator when the fix is one
    /// <c>mkdir</c>.
    /// <para>
    /// A no-op for every other provider, and for an in-memory or empty data source.
    /// </para>
    /// </remarks>
    public static void EnsureParentDirectoryExists(string dbProvider, string connectionString)
    {
        if (!string.Equals(dbProvider, DbProviderKeys.SqLite, StringComparison.OrdinalIgnoreCase)) return;

        var dataSource = new SqliteConnectionStringBuilder(connectionString).DataSource;
        if (string.IsNullOrWhiteSpace(dataSource)) return;
        if (dataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase)) return;

        var directory = Path.GetDirectoryName(Path.GetFullPath(dataSource));
        if (string.IsNullOrEmpty(directory) || Directory.Exists(directory)) return;

        Directory.CreateDirectory(directory);
    }
}
