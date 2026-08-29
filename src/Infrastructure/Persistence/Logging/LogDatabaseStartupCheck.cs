// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Data.Common;
using CleanArchitecture.Blazor.Infrastructure.Extensions;
using Microsoft.Data.SqlClient;
using Npgsql;
using Microsoft.Extensions.Hosting;

namespace CleanArchitecture.Blazor.Infrastructure.Persistence.Logging;

/// <summary>
/// Brings the log table into existence, and says out loud, once, at startup, whether the log
/// database is configured and reachable.
/// </summary>
/// <remarks>
/// Logging is best-effort: a missing or broken log database must not stop the application starting,
/// serving or auditing, and nothing here throws. But best-effort must not mean silent. Without this
/// check the two failure modes are close to invisible - Serilog's sinks fail asynchronously into
/// <c>SelfLog</c>, and a SystemLogs page showing no rows looks exactly like a quiet week.
/// <para>
/// This deliberately does NOT participate in the fail-fast posture that
/// <c>DatabaseSettings.Validate</c> plus <c>ValidateOnStart</c> give the BUSINESS database, where a
/// missing connection string is a startup failure. The two databases have different criticality and
/// the code says so in two different places.
/// </para>
/// </remarks>
public static class LogDatabaseStartupCheck
{
    /// <summary>
    /// Emitted when no log connection string is configured. The application runs; nothing is
    /// recorded to a database.
    /// </summary>
    public const string NotConfiguredMessage =
        "No log database is configured: DatabaseSettings:LogConnectionString is empty. The application " +
        "will run and audit normally, but no log rows will be written to a database and the SystemLogs " +
        "page will have nothing to read. Logs are still written to the console and to ./log/log-*.txt.";

    /// <summary>
    /// Emitted when a log connection string is configured but the database cannot be reached.
    /// </summary>
    public const string UnreachableMessage =
        "The log database is configured but unreachable. The application will run and audit normally, " +
        "but log rows will not be written to it and the SystemLogs page will report it unavailable.";

    /// <summary>
    /// Emitted when the log database answered but the log table could not be created - almost
    /// always a login without CREATE TABLE on a database whose table has not been made yet.
    /// </summary>
    public const string TableCreationFailedMessage =
        "The log database is reachable but the log table could not be created. The application will " +
        "run and audit normally, but log rows will not be written to it and the SystemLogs page will " +
        "report it unavailable. Create the table by hand, or grant the application's login CREATE TABLE " +
        "for one start.";

    /// <summary>
    /// Emitted when the log database is absent and about to be created. Informational: this is the
    /// normal first run, not a fault.
    /// </summary>
    /// <remarks>
    /// The third message, added in Pass 15B, and it earns its place mostly on SQL Server. There, a
    /// connection to a database that does not exist fails with <c>SqlException 4060</c>, whose text
    /// is <i>"Cannot open database ... requested by the login. The login failed."</i> - so the one
    /// diagnostic the operator would otherwise get for a missing database sends them to check
    /// credentials for a problem that has nothing to do with credentials. Naming the database and
    /// what is about to happen to it removes that dead end.
    /// </remarks>
    public const string DatabaseMissingMessage =
        "The log database {Database} does not exist on this server; creating it now. This is the " +
        "normal first run. To keep the application's login unprivileged, create the database in " +
        "advance instead - it is then found on every start and nothing is issued.";

    /// <summary>
    /// Emitted when the log database is absent and the application's login may not create it.
    /// </summary>
    /// <remarks>
    /// Each named placeholder appears exactly once, here and in every message below.
    /// <c>ILogger</c>'s templates are positional under their names, so a name repeated for
    /// readability silently consumes a second argument - which CA2017 catches, and which would
    /// otherwise render the tail of the message wrong precisely when an operator is relying on it.
    /// </remarks>
    public const string DatabaseCreationDeniedMessage =
        "The log database {Database} does not exist and the login {Login} may not create it: " +
        "{Grant} is required. The application will run and audit normally, but log rows will not be " +
        "written to a database and the SystemLogs page will report it unavailable. Prefer creating " +
        "the database once as an administrator - no elevated grant is then needed on any start - " +
        "rather than granting that privilege to the application's own login.";

    /// <summary>
    /// Emitted when the maintenance database could not be opened, so the log database could not be
    /// checked for or created.
    /// </summary>
    /// <remarks>
    /// Deliberately worded as being about the MAINTENANCE database, because it is. Creating a
    /// database requires a connection to some other database on the same server, and this one is
    /// derived from the log connection string - same host, same login, different database. A
    /// deployment that grants its log login CONNECT on the log database only will refuse it, and
    /// reporting that as "the log database is unreachable" would send an operator to look at a
    /// database that is very likely fine.
    /// <para>
    /// It fires only when the server answered and refused. If the server did not answer at all,
    /// nothing is said here and the ordinary <see cref="UnreachableMessage"/> below is the correct
    /// and sufficient diagnosis.
    /// </para>
    /// </remarks>
    public const string MaintenanceDatabaseRefusedMessage =
        "The log database {Database} could not be checked or created: the server refused login " +
        "{Login} a connection to its maintenance database {MaintenanceDatabase}. The log database " +
        "itself may be perfectly healthy - this is about the connection used to CREATE it. If it " +
        "already exists, this message is harmless; granting that login CONNECT on the maintenance " +
        "database removes it.";

    /// <summary>
    /// Prepares the log database and reports on it. Never throws.
    /// </summary>
    public static async Task PrepareLogDatabaseAsync(this IHost host, CancellationToken cancellationToken = default)
    {
        using var scope = host.Services.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<LogDbContext>>();
        var factory = scope.ServiceProvider.GetRequiredService<ILogDbContextFactory>();
        var databaseSettings = scope.ServiceProvider.GetRequiredService<DatabaseSettings>();

        // Every message this method emits is marked as a log-database diagnostic, and the database
        // sink excludes marked events. Routing a complaint about an unreachable log database into
        // the log database would drop it silently - the one outcome that would make this check
        // worse than useless. Console and file carry it; LogDatabaseDiagnosticRoutingTests asserts
        // the marker survives to the point the sinks filter on.
        using var scopeState = logger.BeginScope(new Dictionary<string, object>
        {
            [SerilogExtensions.LogDatabaseDiagnosticProperty] = true
        });

        if (!factory.IsConfigured)
        {
            logger.LogWarning(NotConfiguredMessage);
            return;
        }

        try
        {
            // SQLite creates the FILE on Open() but never the FOLDER, so a configured path under a
            // directory that does not exist fails with "SQLite Error 14: unable to open database
            // file" - a message that names neither the path nor the reason. A no-op on every other
            // provider.
            LogDatabaseDdl.EnsureParentDirectoryExists(
                databaseSettings.DBProvider, databaseSettings.LogConnectionString);

            // The DATABASE, before the table. Gated on the log database being unreachable, and that
            // gate is the whole reason a least-privileged deployment stays silent: where the
            // database already exists and the login can open it, nothing below runs at all - no
            // maintenance connection, no catalogue query, no CREATE, no log line. It is the same
            // reasoning as the table's catalogue pre-check, one level up.
            if (LogDatabaseDdl.RequiresExplicitCreation(databaseSettings.DBProvider)
                && !await IsReachableAsync(factory, cancellationToken))
            {
                await EnsureLogDatabaseExistsAsync(logger, databaseSettings, cancellationToken);
            }

            await using var db = await factory.CreateAsync(cancellationToken);
            var context = (LogDbContext)db;

            // The DDL is attempted FIRST, with no reachability check in front of it, and that
            // ordering is deliberate rather than lazy. On SQLite the log database is a file that
            // does not exist until something opens it, so CanConnectAsync answers "no" for a
            // perfectly healthy configuration - gating the DDL on it meant SQLite never got a log
            // table at all. Doing the work and diagnosing only on failure avoids having to predict,
            // per provider, what "reachable" means for a database that is about to be brought into
            // existence.
            //
            // Ask the catalogue first, and issue nothing at all if the table is already there. The
            // statements are individually guarded too, but a guard is not enough on PostgreSQL: it
            // checks CREATE permission on the schema BEFORE it evaluates IF NOT EXISTS, so a
            // least-privileged log-writer login gets "permission denied for schema public" from a
            // statement that would have done nothing. Skipping the DDL outright is what lets a
            // production deployment hold only INSERT/SELECT/DELETE and start silently every time.
            if (await LogTableExistsAsync(context, databaseSettings.DBProvider, cancellationToken)) return;

            foreach (var statement in LogTableDdl.Statements(databaseSettings.DBProvider))
            {
                await context.Database.ExecuteSqlRawAsync(statement, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            // Deliberately broad and deliberately swallowed. A malformed connection string, a
            // refused TCP connection, a missing driver and a login without CREATE TABLE all arrive
            // here as different types, and none of them is a reason to refuse to start. Before
            // Pass 11C the SQL Server sink threw the equivalent of this out of
            // WebApplicationBuilder.Build() instead, and the application did not start at all.
            //
            // Which message to use is decided after the fact, by asking whether the database
            // answers: "I cannot reach the log database" and "I reached it and was not allowed to
            // create the table" call for completely different things from an operator.
            logger.LogError(ex, await IsReachableAsync(factory, cancellationToken)
                ? TableCreationFailedMessage
                : UnreachableMessage);
        }
    }

    /// <summary>
    /// Brings the log database into existence when it is absent and the login is allowed to.
    /// </summary>
    /// <remarks>
    /// Never throws: every outcome it understands is logged and returned from, and the caller
    /// carries on to the table check regardless. When creation did not happen the table check fails
    /// next, the ordinary diagnostic fires, and the SystemLogs page reports the log database
    /// unavailable - which is the correct end state for every failure here.
    /// <para>
    /// This is symmetric with the business database, which EF's <c>Migrate()</c> has always created
    /// when absent, in every environment. The log database being the one that needed a manual step
    /// was an asymmetry nobody chose; it just fell out of the log database having no migration
    /// chain (Pass 11 §A.3).
    /// </para>
    /// </remarks>
    private static async Task EnsureLogDatabaseExistsAsync(
        ILogger logger, DatabaseSettings databaseSettings, CancellationToken cancellationToken)
    {
        var provider = databaseSettings.DBProvider;
        var connectionString = databaseSettings.LogConnectionString;

        var database = LogDatabaseDdl.DatabaseName(provider, connectionString);
        var maintenance = LogDatabaseDdl.MaintenanceDatabase(provider);

        await using var connection = LogDatabaseDdl.CreateMaintenanceConnection(provider, connectionString);
        var login = LoginOf(connection);

        // Opening the maintenance connection is its own step with its own failure, because its
        // failure means something quite different from every other failure in this file.
        try
        {
            await connection.OpenAsync(cancellationToken);
        }
        catch (Exception ex) when (ServerAnswered(ex))
        {
            logger.LogError(ex, MaintenanceDatabaseRefusedMessage, database, login, maintenance);
            return;
        }
        // Any other failure to open - the server did not answer at all - is deliberately NOT caught
        // here. It means the whole server is down, not that this connection was refused, and the
        // caller's existing "log database is unreachable" diagnostic says that correctly. Letting it
        // propagate keeps one outage from producing two contradictory explanations.

        await using var exists = connection.CreateCommand();
        exists.CommandText = LogDatabaseDdl.ExistsCommandText(provider);
        var parameter = exists.CreateParameter();
        parameter.ParameterName = LogDatabaseDdl.NameParameter;
        parameter.Value = database;
        exists.Parameters.Add(parameter);

        if (Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken)) > 0)
        {
            // It exists, yet the log database could not be opened - so this is not a missing
            // database at all. Most often a login without CONNECT on it, or a wrong password.
            // Nothing to create; the caller's diagnostic covers it.
            return;
        }

        logger.LogInformation(DatabaseMissingMessage, database);

        try
        {
            await using var create = connection.CreateCommand();
            create.CommandText = LogDatabaseDdl.CreateStatement(provider, database);
            await create.ExecuteNonQueryAsync(cancellationToken);

            // The caller connects to the log database next, and the attempt that brought us here
            // just failed against it. SqlClient blocks a connection string for several seconds after
            // a login failure without asking the server again, so without this the database is
            // created and the table immediately fails to be - see LogDatabaseDdl.ClearPool.
            LogDatabaseDdl.ClearPool(provider);
        }
        catch (Exception ex) when (LogDatabaseDdl.IsAlreadyExists(ex))
        {
            // Another instance created it between the check above and the statement above. The
            // pre-check and the CREATE are two statements with nothing making them atomic, so two
            // instances starting together can both see "absent". The loser of that race got exactly
            // the outcome it wanted, and reporting an error would make a correct result look like a
            // fault on every scaled deployment's startup.
        }
        catch (Exception ex) when (LogDatabaseDdl.IsPermissionDenied(ex))
        {
            logger.LogError(ex, DatabaseCreationDeniedMessage,
                database, login, LogDatabaseDdl.RequiredGrant(provider));
        }
    }

    /// <summary>
    /// Whether the server answered and refused, as opposed to not answering at all.
    /// </summary>
    /// <remarks>
    /// The distinction decides which of two very different messages an operator gets, so it is drawn
    /// on the provider's own evidence rather than on the exception text. A PostgreSQL error response
    /// arrives as <see cref="PostgresException"/>; a transport failure arrives as its plainer base
    /// type. SQL Server reports both as <see cref="SqlException"/> and distinguishes them by number,
    /// where 53 / 40 / -2 / 258 are the connection-level ones.
    /// </remarks>
    private static bool ServerAnswered(Exception exception) => exception switch
    {
        PostgresException => true,
        SqlException sql => sql.Number is not (53 or 40 or -2 or 258),
        _ => false
    };

    /// <summary>The login the connection authenticates as, for a message that has to name it.</summary>
    private static string LoginOf(DbConnection connection) => connection switch
    {
        NpgsqlConnection => new NpgsqlConnectionStringBuilder(connection.ConnectionString).Username
                            ?? "(integrated security)",
        SqlConnection => new SqlConnectionStringBuilder(connection.ConnectionString) is { IntegratedSecurity: false } b
                            ? b.UserID
                            : "(integrated security)",
        _ => "(unknown)"
    };

    /// <summary>
    /// Whether the log table is already there, asked of the system catalogue so it needs no
    /// privilege beyond connecting.
    /// </summary>
    private static async Task<bool> LogTableExistsAsync(
        LogDbContext context, string dbProvider, CancellationToken cancellationToken)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = LogTableDdl.ExistsQuery(dbProvider);

        await context.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) > 0;
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }

    /// <summary>
    /// Whether the log database answers at all. Used only to choose the wording of a failure that
    /// has already happened, so it swallows its own errors: a probe that throws while diagnosing a
    /// throw tells nobody anything.
    /// </summary>
    private static async Task<bool> IsReachableAsync(
        ILogDbContextFactory factory, CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await factory.CreateAsync(cancellationToken);
            return await ((LogDbContext)db).Database.CanConnectAsync(cancellationToken);
        }
        catch
        {
            return false;
        }
    }
}
