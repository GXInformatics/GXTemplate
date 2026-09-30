#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Infrastructure.Configurations;
using CleanArchitecture.Blazor.Infrastructure.Extensions;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using CleanArchitecture.Blazor.Infrastructure.Persistence.Logging;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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

    /// <summary>
    /// The PostgreSQL sink's batch period: the application registers the sink without a
    /// <c>period</c> (<c>SerilogExtensions.WriteToNpgsql</c>), so the package default applies.
    /// </summary>
    private static readonly TimeSpan SinkPeriod = Serilog.LoggerConfigurationPostgreSqlExtensions.DefaultPeriod;

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
    /// The budget is derived from the sink's own period rather than picked, and that is not
    /// fussiness. It was once a flat 15 seconds, shorter than another sink's 20-second batch period,
    /// so on that sink this test gave up before it was due to write and reported "the message never
    /// arrived" for a message that was merely still in the batch - the most misleading failure a
    /// round-trip test can produce.
    /// <para>
    /// Half as long again as <see cref="SinkPeriod"/> (PostgreSQL's is the only sink the tests run), so a
    /// tuning change to that period moves this with it instead of silently eating the margin.
    /// </para>
    /// </remarks>
    private async Task<bool> WaitForTheMarkerAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<ILogDbContextFactory>();

        var interval = TimeSpan.FromMilliseconds(250);
        var attempts = (int)Math.Ceiling(SinkPeriod * 1.5 / interval);

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

    /// <summary>Every table in the PostgreSQL database the connection string points at.</summary>
    /// <remarks>
    /// This is deliberately raw ADO.NET against the catalogue rather than anything EF offers,
    /// because the claim under test is about what is IN the database - not about what a model
    /// believes is in it. Asking EF would be asking the same source that produced the schema whether
    /// it produced the schema. PostgreSQL only since pass 47, when the harness lost its database files.
    /// </remarks>
    private static List<string> TableNames(string connectionString)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();

        // Excluding the two system schemas rather than filtering to 'public': the business database
        // keeps Identity's tables and the snake_cased ones side by side, and pinning a schema name
        // here would quietly stop finding them if either ever moved.
        using var command = new NpgsqlCommand(
            """
            SELECT table_name FROM information_schema.tables
            WHERE table_type = 'BASE TABLE'
              AND table_schema NOT IN ('pg_catalog', 'information_schema')
            """, connection);

        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }

    private List<string> BusinessTableNames() => TableNames(_factory.BusinessConnectionString);

    private List<string> LogTableNames() => TableNames(_factory.LogConnectionString);

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
    public void TheBusinessAndLogSettings_NameTwoDifferentDatabases()
    {
        // Every other test here is only evidence of separation if the host was actually given two
        // databases. Pointed at one, "no log table in the business database" would be checking the
        // log database's own table list against itself. Read from the settings the host bound, not
        // from the harness, because that is what the application connects with (pass 47, CO-159).
        var settings = _factory.Services.GetRequiredService<IOptions<DatabaseSettings>>().Value;
        var business = new NpgsqlConnectionStringBuilder(settings.ConnectionString).Database;
        var logs = new NpgsqlConnectionStringBuilder(settings.LogConnectionString).Database;

        business.Should().NotBeNullOrEmpty();
        logs.Should().NotBeNullOrEmpty();
        logs.Should().NotBe(business, "the log database must be a different database from the business one");
    }

    [Test]
    public void TheBusinessDatabase_HasNoSystemLogsTable()
    {
        // This is what Pass 11 is for. If this table exists in the business database then log volume
        // is still growing inside the backup this pass set out to keep small, and every other piece
        // of evidence is beside the point.
        var tables = BusinessTableNames();

        tables.Should().NotBeEmpty("the business migration must have run");
        Has(tables, SerilogExtensions.NpgsqlTableName).Should().BeFalse(
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
        Has(LogTableNames(), SerilogExtensions.NpgsqlTableName).Should().BeTrue();
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
        // is caught too.
        var suspicious = BusinessTableNames()
            .Where(t => Normalise(t).Contains(Normalise(SerilogExtensions.NpgsqlTableName).TrimEnd('s')))
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
