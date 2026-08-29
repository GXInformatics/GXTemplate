#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using CleanArchitecture.Blazor.Infrastructure.Persistence.Logging;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
    private async Task<bool> WaitForTheMarkerAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<ILogDbContextFactory>();

        for (var attempt = 0; attempt < 60; attempt++)
        {
            await using (var db = await factory.CreateAsync())
            {
                if (await db.SystemLogs.AnyAsync(x => x.Message!.Contains(Marker))) return true;
            }

            await Task.Delay(250);
        }

        return false;
    }

    [OneTimeTearDown]
    public void StopTheApplication() => _factory.Dispose();

    private static List<string> TableNames(string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
        using var reader = command.ExecuteReader();

        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }

    // ------------------------------------------------------- the central claim

    [Test]
    public void TheBusinessDatabase_HasNoSystemLogsTable()
    {
        // This is what Pass 11 is for. If this table exists in the business database then log volume
        // is still growing inside the backup this pass set out to keep small, and every other piece
        // of evidence is beside the point.
        var tables = TableNames(_factory.BusinessConnectionString);

        tables.Should().NotBeEmpty("the business migration must have run");
        tables.Should().NotContain("SystemLogs",
            "logs moved to their own database; the business schema must no longer carry the table");
    }

    [Test]
    public void TheBusinessDatabase_StillHasItsAuditTrail()
    {
        // The scope boundary. Pass 5's audit trail stays where it was, in the business database, in
        // the same transaction as the change it records.
        TableNames(_factory.BusinessConnectionString).Should().Contain("AuditTrails");
    }

    [Test]
    public void TheLogDatabase_HasTheSystemLogsTable_CreatedByTheSink()
    {
        // Nothing migrates the log database - it has no migration chain at all - so the presence of
        // this table is entirely the sink's doing.
        TableNames(_factory.LogConnectionString).Should().Contain("SystemLogs");
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

        using var connection = new SqliteConnection(_factory.BusinessConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND lower(name) LIKE '%systemlog%'";

        Convert.ToInt32(command.ExecuteScalar()).Should().Be(0,
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
