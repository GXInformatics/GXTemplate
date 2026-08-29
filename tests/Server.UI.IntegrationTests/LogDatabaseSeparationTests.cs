#nullable enable
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Infrastructure.Extensions;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using CleanArchitecture.Blazor.Infrastructure.Persistence.Logging;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Server.UI.IntegrationTests;

/// <summary>
/// The separation of the two databases, observed in the running application rather than in a model
/// built for the occasion.
/// </summary>
/// <remarks>
/// The unit tests assert the two EF models are partitioned. These assert what actually happens when
/// the real <c>Program.cs</c> boots with the real registrations: that the migration puts no log table
/// in the business database, that Serilog's sink puts log rows in the other one, and that the log
/// context is registered without the audit interceptor.
/// <para>
/// Pass 10's lesson applies in reverse here - HTTP status codes cannot see any of this, so these
/// tests reach into the two databases directly.
/// </para>
/// </remarks>
[TestFixture]
public class LogDatabaseSeparationTests
{
    private GxWebApplicationFactory _factory = null!;

    /// <summary>A message distinctive enough to find among whatever else the application logged.</summary>
    private static readonly string Marker = "gx-log-roundtrip-" + Guid.NewGuid().ToString("N");

    [OneTimeSetUp]
    public async Task StartTheApplication()
    {
        // The harness quiets Serilog to Warning so the other fixtures can assert on status codes
        // without wading through log output. This fixture is about log rows, so it asks for them.
        _factory = new GxWebApplicationFactory(extraConfiguration: new Dictionary<string, string?>
        {
            ["Serilog:MinimumLevel:Default"] = "Information"
        });

        // Booting is what runs the migration and the seeding. Any request forces the host to build.
        using var client = _factory.CreateNonRedirectingClient();
        await client.GetAsync("/");

        // Then log through the application's own ILogger, which is the whole round trip under test:
        // ILogger -> Serilog -> the database sink -> the log database -> back through LogDbContext.
        _factory.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Gx.RoundTrip")
            .LogInformation("{Marker}", Marker);
    }

    /// <summary>
    /// Waits for the marker row to arrive. The sink batches behind Serilog.Sinks.Async, so the write
    /// is not synchronous with the log call and polling is the honest way to observe it.
    /// </summary>
    /// <remarks>
    /// The budget is derived from <see cref="SerilogExtensions.SqlServerBatchPeriod"/> rather than
    /// picked, and that is not fussiness. It was a flat 60 x 250ms = 15 seconds, which is comfortable
    /// for SQLite and PostgreSQL and **shorter than the SQL Server sink's own 20-second BatchPeriod**
    /// - so under <c>GX_TEST_DBPROVIDER=mssql</c> this test failed by giving up before the sink was
    /// due to write. It reported "the message never arrived" for a message that was merely still in
    /// the batch, which is the most misleading failure a round-trip test can produce.
    /// <para>
    /// Half as long again as the slowest configured period, so a tuning change to that period moves
    /// this with it instead of silently eating the margin.
    /// </para>
    /// </remarks>
    private async Task<bool> WaitForTheMarkerAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<ILogDbContextFactory>();

        var interval = TimeSpan.FromMilliseconds(250);
        var attempts = (int)Math.Ceiling(SerilogExtensions.SqlServerBatchPeriod * 1.5 / interval);

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            await using (var db = await factory.CreateAsync())
            {
                if (await db.SystemLogs.AnyAsync(x => x.Message!.Contains(Marker))) return true;
            }

            await Task.Delay(interval);
        }

        return false;
    }

    [OneTimeTearDown]
    public void StopTheApplication() => _factory.Dispose();

    /// <summary>
    /// Every table in the database the connection string points at, for whichever provider the
    /// harness is running against.
    /// </summary>
    /// <remarks>
    /// This is deliberately raw ADO.NET against each provider's own catalogue rather than anything
    /// EF offers, because the claim under test is about what is IN the database - not about what a
    /// model believes is in it. Asking EF would be asking the same source that produced the schema
    /// whether it produced the schema.
    /// <para>
    /// Provider-aware since Pass 14B's anomaly 1. It previously opened every connection string with
    /// <see cref="SqliteConnection"/>, which is correct for the harness's default and throws
    /// "Connection string keyword 'host' is not supported" under
    /// <c>GX_TEST_DBPROVIDER=postgresql</c> - so the four tests that most directly assert Pass 11's
    /// central claim were the four that could not run on the provider the template ships pointed at.
    /// </para>
    /// <para>
    /// An unrecognised provider throws rather than returning nothing: an empty list would make
    /// <c>NotContain</c> pass and <c>Contain</c> fail, which is a confusing way to be told the
    /// harness does not understand its own configuration.
    /// </para>
    /// </remarks>
    private static List<string> TableNames(string provider, string connectionString)
    {
        using DbConnection connection = provider.ToLowerInvariant() switch
        {
            DbProviderKeys.SqLite => new SqliteConnection(connectionString),
            DbProviderKeys.Npgsql => new NpgsqlConnection(connectionString),
            DbProviderKeys.SqlServer => new SqlConnection(connectionString),
            _ => throw new NotSupportedException(
                $"LogDatabaseSeparationTests cannot inspect the schema of a '{provider}' database. " +
                "Add its catalogue query here rather than letting these tests silently stop asserting.")
        };

        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = provider.ToLowerInvariant() switch
        {
            DbProviderKeys.SqLite => "SELECT name FROM sqlite_master WHERE type='table'",

            // Excluding the two system schemas rather than filtering to 'public': the business
            // database keeps Identity's tables and the snake_cased ones side by side, and pinning a
            // schema name here would quietly stop finding them if either ever moved.
            DbProviderKeys.Npgsql =>
                """
                SELECT table_name FROM information_schema.tables
                WHERE table_type = 'BASE TABLE'
                  AND table_schema NOT IN ('pg_catalog', 'information_schema')
                """,

            // sys.tables is already scoped to user tables in the connected database.
            DbProviderKeys.SqlServer => "SELECT name FROM sys.tables",

            _ => throw new NotSupportedException(provider)
        };

        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }

    private List<string> BusinessTableNames() => TableNames(_factory.DbProvider, _factory.BusinessConnectionString);

    private List<string> LogTableNames() => TableNames(_factory.DbProvider, _factory.LogConnectionString);

    /// <summary>
    /// Compares two table names ignoring case and underscores.
    /// </summary>
    /// <remarks>
    /// <c>UseSnakeCaseNamingConvention()</c> applies on PostgreSQL and nowhere else, so the same
    /// table is <c>SystemLogs</c> on two providers and <c>system_logs</c> on the third - and
    /// <c>AuditTrails</c> is <c>audit_trails</c>. These tests are about which tables EXIST in which
    /// database, not about how they are spelled, so the spelling is normalised away rather than
    /// branched on. The same idiom is already used in <c>LogTableDdlTests</c>.
    /// </remarks>
    private static string Normalise(string tableName) => tableName.Replace("_", "").ToLowerInvariant();

    private static bool Has(IEnumerable<string> tables, string name) =>
        tables.Any(t => Normalise(t) == Normalise(name));

    // ------------------------------------------------------- the central claim

    [Test]
    public void TheBusinessDatabase_HasNoSystemLogsTable()
    {
        // This is what Pass 11 is for. If this table exists in the business database then log volume
        // is still growing inside the backup this pass set out to keep small, and every other piece
        // of evidence is beside the point.
        var tables = BusinessTableNames();

        tables.Should().NotBeEmpty("the business migration must have run");
        Has(tables, LogTableDdl.TableName).Should().BeFalse(
            "logs moved to their own database; the business schema must no longer carry the table " +
            $"(under any spelling - the tables found were: {string.Join(", ", tables)})");
    }

    [Test]
    public void TheBusinessDatabase_StillHasItsAuditTrail()
    {
        // The scope boundary. Pass 5's audit trail stays where it was, in the business database, in
        // the same transaction as the change it records.
        Has(BusinessTableNames(), "AuditTrails").Should().BeTrue();
    }

    [Test]
    public void TheLogDatabase_HasTheSystemLogsTable_CreatedByTheApplication()
    {
        // Nothing migrates the log database - it has no migration chain at all - and since Pass 11C
        // no sink creates it either. Its presence is entirely LogTableDdl's doing, run from
        // LogDatabaseStartupCheck before the business database is even touched.
        Has(LogTableNames(), LogTableDdl.TableName).Should().BeTrue();
    }

    [Test]
    public async Task ALoggedMessage_LandsInTheLogDatabase_AndIsReadableThroughTheLogContext()
    {
        // Query-level evidence that the writing side and the reading side agree about the shape of a
        // table neither EF nor a migration created - the claim that made sink auto-create acceptable
        // in the first place.
        (await WaitForTheMarkerAsync()).Should().BeTrue(
            "a message logged through ILogger must reach the log database and be readable back");

        using var scope = _factory.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<ILogDbContextFactory>();
        factory.IsConfigured.Should().BeTrue();

        await using var db = await factory.CreateAsync();
        var row = await db.SystemLogs.Where(x => x.Message!.Contains(Marker)).SingleAsync();

        row.Id.Should().BeGreaterThan(0, "Id is the key the page pages and orders by");
        row.Level.Should().NotBeNullOrWhiteSpace();
        row.TimeStamp.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(5),
            "the row is stamped in UTC, which is the alphabet the page's date filters read in");
    }

    [Test]
    public async Task ThatSameMessage_IsNowhereInTheBusinessDatabase()
    {
        // The negative half of the central claim. It is not enough that the business database has no
        // SystemLogs table at boot; the log traffic must genuinely be going somewhere else.
        (await WaitForTheMarkerAsync()).Should().BeTrue();

        // Deliberately looser than an equality check, as it always has been: anything whose name
        // merely RESEMBLES a log table counts, so a "SystemLogs_backup" or a half-finished rename
        // is caught too. It goes through the shared catalogue query now instead of carrying its own
        // copy of SQLite's, which is what made this the second of the two dialect-bound tests.
        var suspicious = BusinessTableNames()
            .Where(t => Normalise(t).Contains(Normalise(LogTableDdl.TableName).TrimEnd('s')))
            .ToList();

        suspicious.Should().BeEmpty(
            "no table resembling a log table should exist in the business database");
    }

    // ------------------------------------------------------- the registration

    private static IEnumerable<IInterceptor> InterceptorsOn(DbContext context) =>
        context.GetService<IDbContextOptions>()
            .FindExtension<CoreOptionsExtension>()?.Interceptors
        ?? Enumerable.Empty<IInterceptor>();

    [Test]
    public void TheLogContext_IsRegisteredWithNoSaveChangesInterceptor()
    {
        // AuditableEntityInterceptor opens a transaction in SavingChanges and holds it across the
        // save (Pass 5). Attaching it to a context that never saves, over a database with no
        // AuditTrails table, could only ever do harm - so its absence here is deliberate, and this
        // test is what stops it being reinstated by someone copying the business registration.
        using var scope = _factory.Services.CreateScope();
        using var context = scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<LogDbContext>>().CreateDbContext();

        InterceptorsOn(context).OfType<ISaveChangesInterceptor>().Should().BeEmpty();
    }

    [Test]
    public void TheBusinessContext_StillCarriesItsInterceptors()
    {
        // The paired positive. Without it, the assertion above would pass just as well if
        // interceptors had been dropped from both contexts.
        using var scope = _factory.Services.CreateScope();
        using var context = scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext();

        InterceptorsOn(context).OfType<ISaveChangesInterceptor>().Should().NotBeEmpty();
    }

    [Test]
    public void TheLogContext_TracksNothing()
    {
        using var scope = _factory.Services.CreateScope();
        using var context = scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<LogDbContext>>().CreateDbContext();

        context.ChangeTracker.QueryTrackingBehavior.Should().Be(QueryTrackingBehavior.NoTracking);
    }
}
#nullable restore
