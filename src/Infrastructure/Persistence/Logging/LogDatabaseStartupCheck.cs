// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using CleanArchitecture.Blazor.Infrastructure.Extensions;
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
