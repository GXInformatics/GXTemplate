// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using CleanArchitecture.Blazor.Infrastructure.Extensions;
using Microsoft.Extensions.Hosting;

namespace CleanArchitecture.Blazor.Infrastructure.Persistence.Logging;

/// <summary>
/// Says out loud, once, at startup, whether the log database is configured and reachable.
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
    /// Runs the check. Never throws.
    /// </summary>
    public static async Task CheckLogDatabaseAsync(this IHost host, CancellationToken cancellationToken = default)
    {
        using var scope = host.Services.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<LogDbContext>>();
        var factory = scope.ServiceProvider.GetRequiredService<ILogDbContextFactory>();

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

            // CanConnectAsync rather than a query: the table may legitimately not exist yet, since
            // the sink creates it on its first write. Reachability is the question here.
            var reachable = db is LogDbContext context &&
                            await context.Database.CanConnectAsync(cancellationToken);

            if (!reachable) logger.LogError(UnreachableMessage);
        }
        catch (Exception ex)
        {
            // Deliberately broad and deliberately swallowed. A malformed connection string, a
            // refused TCP connection and a missing driver all arrive here as different types, and
            // none of them is a reason to refuse to start.
            logger.LogError(ex, UnreachableMessage);
        }
    }
}
