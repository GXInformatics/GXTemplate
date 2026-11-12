using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Domain.Entities;
using CleanArchitecture.Blazor.Domain.Identity;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using CleanArchitecture.Blazor.Infrastructure.Persistence.Rules;
using CleanArchitecture.Blazor.TestSupport;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NUnit.Framework;

// Under CleanArchitecture.Blazor.Application.IntegrationTests, so the [SetUpFixture] has already
// created, migrated and prepared the database before these run.
namespace CleanArchitecture.Blazor.Application.IntegrationTests.Harness;

using static Testing;

/// <summary>
/// The shared test database behaves like a fresh one for every test: that is the whole contract
/// the suites rely on once they stop getting a new database per test.
/// </summary>
[TestFixture]
public class ResetIsolationTests
{
    private const string Marker = "reset-isolation-marker";

    // Only the reset, not TestBase's reset-and-sign-in: if the reset stopped working, this fixture
    // must fail on its own assertion below, not on the harness user already existing.
    //
    // And as NOBODY (CO-164): the ambient user is static, so without this the fixture inherits whoever
    // the previous test signed in - a user the reset has just deleted, whose cached context would then
    // stamp the audit trail with a dangling UserId.
    [SetUp]
    public Task ResetOnly()
    {
        UseUser(null);
        return Database.ResetAsync();
    }

    [Test, Order(1)]
    public async Task TheFirstTest_LeavesARowBehind()
    {
        await AddAsync(new PicklistSet { Name = Picklist.Brand, Value = Marker, Text = Marker });

        (await CountAsync<PicklistSet>()).Should().Be(1);
    }

    [Test, Order(2)]
    public async Task TheNextTest_StartsWithoutIt()
    {
        // The reset ran in [SetUp]; the previous test's row must be gone.
        (await CountAsync<PicklistSet>()).Should().Be(0);
    }

    [Test, Order(3)]
    public async Task AReset_RestartsGeneratedIds()
    {
        // Without the restart, the id after a reset depends on every test that ran before, and a test
        // seeding explicit ids then inserting a generated row collides only sometimes.
        var first = new PicklistSet { Name = Picklist.Brand, Value = "first", Text = "t" };
        await AddAsync(first);

        await Database.ResetAsync();
        var afterReset = new PicklistSet { Name = Picklist.Brand, Value = "after-reset", Text = "t" };
        await AddAsync(afterReset);

        first.Id.Should().Be(1, "the [SetUp] reset restarted the sequence");
        afterReset.Id.Should().Be(1, "so did this one");
    }

    [Test, Order(4)]
    public async Task TheAmbientUser_IsNobody_AfterTheReset()
    {
        // Arranged here rather than inherited from whichever fixture ran before: a signed-in user
        // whose context the loader has cached, exactly what a previous test leaves behind.
        await RunAsDefaultUserAsync();
        using (var scope = CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<IUserContextAccessor>().Current.Should().NotBeNull();
        }

        // What this fixture's [SetUp] does. The reset deletes that user, but its cached context would
        // still be served; only clearing the ambient user stops it stamping a dangling author.
        await ResetOnly();

        var row = new PicklistSet { Name = Picklist.Brand, Value = "anonymous", Text = "t" };
        await AddAsync(row);

        row.CreatedById.Should().BeNull("the reset deleted that user, so a row written now has no author");
    }
}

[TestFixture]
public class ResetOrderAgainstTheDatabaseTests : TestBase
{
    [Test]
    public async Task AResetOverAParentAndItsChild_DoesNotThrow_AndEmptiesBoth()
    {
        // AspNetUsers.TenantId references Tenants, so deleting Tenants first fails.
        var tenant = new Tenant { Name = "reset-order-parent", Description = "parent" };
        await AddAsync(tenant);
        await AddAsync(new ApplicationUser
        {
            UserName = "reset-order-child", Email = "reset-order-child@example.com", TenantId = tenant.Id
        });

        var reset = () => Database.ResetAsync();

        await reset.Should().NotThrowAsync();
        (await CountAsync<Tenant>()).Should().Be(0);
        (await CountAsync<ApplicationUser>()).Should().Be(0);
    }
}

[TestFixture]
public class MigrationModelTests
{
    [Test]
    public async Task TheDatabaseWasMigratedWithTheApplicationsModel_AndNothingIsPending()
    {
        await using var context = new ApplicationDbContext(ApplicationModel.NpgsqlOptions(Database.ConnectionString));

        context.Database.HasPendingModelChanges().Should().BeFalse(
            "the database is migrated with the application's model, which matches the migrations");
        (await context.Database.GetPendingMigrationsAsync()).Should().BeEmpty();

        // The table only the application's Identity options map: proof the model was not a bare one.
        await using var connection = new NpgsqlConnection(Database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM information_schema.tables WHERE table_name = 'AspNetUserPasskeys'", connection);
        ((long)(await command.ExecuteScalarAsync())!).Should().Be(1);
    }

    [Test]
    public void TheResetNeverTouchesTheMigrationHistory()
    {
        Database.ResetBatch.Should().NotContain(PostgresTestDatabase.MigrationsHistoryTable);
    }

    [Test]
    public async Task TheReset_KeepsLookupTables_AndEmptiesATableWhoseTriggersRefuseDeleteAndTruncate()
    {
        // No template table is a TBL_LK_ lookup or refuses DELETE by trigger yet, so this builds one of
        // each (tables, never a database), reads a fresh reset batch that sees them, and removes them.
        await using var connection = new NpgsqlConnection(Database.ConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection,
            """
            CREATE TABLE IF NOT EXISTS public."TBL_LK_HARNESS_PROBE" ("Id" int GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY, "Name" text);
            CREATE TABLE IF NOT EXISTS public.harness_guarded_probe (id int);
            CREATE OR REPLACE FUNCTION public.harness_refuse_delete() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'append only'; END $$;
            DROP TRIGGER IF EXISTS harness_refuse_delete ON public.harness_guarded_probe;
            CREATE TRIGGER harness_refuse_delete BEFORE DELETE ON public.harness_guarded_probe FOR EACH ROW EXECUTE FUNCTION public.harness_refuse_delete();
            DROP TRIGGER IF EXISTS harness_refuse_truncate ON public.harness_guarded_probe;
            CREATE TRIGGER harness_refuse_truncate BEFORE TRUNCATE ON public.harness_guarded_probe FOR EACH STATEMENT EXECUTE FUNCTION public.harness_refuse_delete();
            INSERT INTO public."TBL_LK_HARNESS_PROBE" ("Name") VALUES ('seeded by a migration');
            INSERT INTO public.harness_guarded_probe VALUES (1);
            """);
        try
        {
            var fresh = PostgresTestDatabase.FromEnvironment(Database.DatabaseName);
            await fresh.EnsureReadyAsync();

            // (G1) A lookup is never emptied with the business tables: only the restore block names it, and only to put
            // back its migrated rows when a test wrote it.
            fresh.ResetBatch.Split("DO $gx$")[0].Should().NotContain("TBL_LK_HARNESS_PROBE");
            fresh.ResetBatch
                // Pass 59: an append-only table refuses TRUNCATE too, so the reset disables its user
                // triggers around a DELETE instead.
                .Should().Contain("ALTER TABLE public.harness_guarded_probe DISABLE TRIGGER USER; DELETE FROM public.harness_guarded_probe; " +
                             "ALTER TABLE public.harness_guarded_probe ENABLE TRIGGER USER;")
                .And.NotContain("TRUNCATE public.harness_guarded_probe");

            await fresh.ResetAsync();

            (await ScalarAsync(connection, "SELECT count(*) FROM public.\"TBL_LK_HARNESS_PROBE\"")).Should().Be(1);
            (await ScalarAsync(connection, "SELECT count(*) FROM public.harness_guarded_probe")).Should().Be(0);

            // The lookup's sequence was not rewound, so the next generated id does not collide with the seeded row.
            var insert = () => ExecuteAsync(connection, "INSERT INTO public.\"TBL_LK_HARNESS_PROBE\" (\"Name\") VALUES ('added later')");
            await insert.Should().NotThrowAsync();
        }
        finally
        {
            await ExecuteAsync(connection,
                "DROP TABLE IF EXISTS public.\"TBL_LK_HARNESS_PROBE\"; DROP TABLE IF EXISTS public.harness_guarded_probe; " +
                "DROP FUNCTION IF EXISTS public.harness_refuse_delete();");
        }
    }

    // ---- (G1) Pass 60c: a reset restores each lookup to its migrated state ------------------------

    /// <summary>
    /// Two stand-in lookups, seeded the way a migration seeds one (explicit ids, then the identity sequence reset with
    /// the template's own <c>ResetIdentitySequence</c> SQL), then a fresh harness instance that snapshots them.
    /// </summary>
    private static async Task<PostgresTestDatabase> SeededLookupsAsync(NpgsqlConnection connection)
    {
        await ExecuteAsync(connection,
            """
            DROP TABLE IF EXISTS public."TBL_LK_G1_PROBES"; DROP TABLE IF EXISTS public."TBL_LK_G1_UNTOUCHED";
            CREATE TABLE public."TBL_LK_G1_PROBES" (id int GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY, name text NOT NULL, slug text);
            CREATE TABLE public."TBL_LK_G1_UNTOUCHED" (id int GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY, name text NOT NULL, slug text);
            INSERT INTO public."TBL_LK_G1_PROBES" (id, name, slug) VALUES (1, 'Main', 'main'), (2, 'In transit', 'in-transit');
            INSERT INTO public."TBL_LK_G1_UNTOUCHED" (id, name, slug) VALUES (1, 'Only', 'only');
            """);
        await ExecuteAsync(connection, LookupMigrationExtensions.ResetSql("TBL_LK_G1_PROBES", "public"));
        await ExecuteAsync(connection, LookupMigrationExtensions.ResetSql("TBL_LK_G1_UNTOUCHED", "public"));

        var fresh = PostgresTestDatabase.FromEnvironment(Database.DatabaseName);
        await fresh.EnsureReadyAsync();
        return fresh;
    }

    private static async Task DropLookupsAsync(NpgsqlConnection connection) =>
        await ExecuteAsync(connection, "DROP TABLE IF EXISTS public.\"TBL_LK_G1_PROBES\"; DROP TABLE IF EXISTS public.\"TBL_LK_G1_UNTOUCHED\";");

    private static async Task<string> RowsAsync(NpgsqlConnection connection, string table)
    {
        await using var command = new NpgsqlCommand($"SELECT string_agg(id || '=' || name, ',' ORDER BY id) FROM public.\"{table}\"", connection);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    [Test]
    public async Task G1a_ARowATestInsertsIntoALookup_IsGoneAfterTheReset()
    {
        await using var connection = new NpgsqlConnection(Database.ConnectionString);
        await connection.OpenAsync();
        try
        {
            var fresh = await SeededLookupsAsync(connection);
            await ExecuteAsync(connection, "INSERT INTO public.\"TBL_LK_G1_PROBES\" (name) VALUES ('added by a test')");
            (await RowsAsync(connection, "TBL_LK_G1_PROBES")).Should().Be("1=Main,2=In transit,3=added by a test");

            await fresh.ResetAsync();

            (await RowsAsync(connection, "TBL_LK_G1_PROBES")).Should().Be("1=Main,2=In transit");
        }
        finally
        {
            await DropLookupsAsync(connection);
        }
    }

    [Test]
    public async Task G1b_ASeededRowATestRenamedOrDeleted_IsBack_WithItsOriginalId()
    {
        await using var connection = new NpgsqlConnection(Database.ConnectionString);
        await connection.OpenAsync();
        try
        {
            var fresh = await SeededLookupsAsync(connection);
            await ExecuteAsync(connection, "UPDATE public.\"TBL_LK_G1_PROBES\" SET name = 'Renamed' WHERE id = 1");
            await ExecuteAsync(connection, "DELETE FROM public.\"TBL_LK_G1_PROBES\" WHERE id = 2");
            (await RowsAsync(connection, "TBL_LK_G1_PROBES")).Should().Be("1=Renamed");

            await fresh.ResetAsync();

            (await RowsAsync(connection, "TBL_LK_G1_PROBES")).Should().Be("1=Main,2=In transit");
        }
        finally
        {
            await DropLookupsAsync(connection);
        }
    }

    [Test]
    public async Task G1c_AfterTheReset_TheSequenceContinuesWhereTheMigrationLeftIt()
    {
        await using var connection = new NpgsqlConnection(Database.ConnectionString);
        await connection.OpenAsync();
        try
        {
            var fresh = await SeededLookupsAsync(connection);

            // A test uses up ids 3 and 4. Without the restore the next would be 5; rewound to its start, 1 (a collision).
            await ExecuteAsync(connection, "INSERT INTO public.\"TBL_LK_G1_PROBES\" (name) VALUES ('three'), ('four')");
            await fresh.ResetAsync();

            await ExecuteAsync(connection, "INSERT INTO public.\"TBL_LK_G1_PROBES\" (name) VALUES ('next')");
            (await RowsAsync(connection, "TBL_LK_G1_PROBES")).Should().Be("1=Main,2=In transit,3=next");
            await fresh.ResetAsync();
        }
        finally
        {
            await DropLookupsAsync(connection);
        }
    }

    [Test]
    public async Task G1d_ALookupNoTestTouched_IsNotRewritten_AndTheDirtySetIsEmptyAfterwards()
    {
        await using var connection = new NpgsqlConnection(Database.ConnectionString);
        await connection.OpenAsync();
        try
        {
            var fresh = await SeededLookupsAsync(connection);
            var before = await ScalarAsync(connection, "SELECT xmin::text::bigint FROM public.\"TBL_LK_G1_UNTOUCHED\" WHERE id = 1");

            await ExecuteAsync(connection, "INSERT INTO public.\"TBL_LK_G1_PROBES\" (name) VALUES ('written')");
            (await ScalarAsync(connection, $"SELECT count(*) FROM {PostgresTestDatabase.SeedSchema}.dirty")).Should().Be(1);
            await fresh.ResetAsync();

            (await ScalarAsync(connection, "SELECT xmin::text::bigint FROM public.\"TBL_LK_G1_UNTOUCHED\" WHERE id = 1"))
                .Should().Be(before, "a lookup no test wrote is left as it is");
            (await ScalarAsync(connection, $"SELECT count(*) FROM {PostgresTestDatabase.SeedSchema}.dirty")).Should().Be(0);

            // A reset with nothing written does nothing to the lookups at all.
            var probeBefore = await ScalarAsync(connection, "SELECT xmin::text::bigint FROM public.\"TBL_LK_G1_PROBES\" WHERE id = 1");
            await fresh.ResetAsync();
            (await ScalarAsync(connection, "SELECT xmin::text::bigint FROM public.\"TBL_LK_G1_PROBES\" WHERE id = 1")).Should().Be(probeBefore);
        }
        finally
        {
            await DropLookupsAsync(connection);
        }
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
