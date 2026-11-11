using System;
using System.Linq;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using NUnit.Framework;
using static CleanArchitecture.Blazor.Application.IntegrationTests.DataFoundations.ModelHarness;

namespace CleanArchitecture.Blazor.Application.IntegrationTests.DataFoundations;

/// <summary>
/// Pass 60 plan §5 rows 16 and 17, on the test model's two sample modules (Alpha in <c>tsa</c>, Beta in
/// <c>tsb</c>): a generated project's tables land in their module schemas, and its first migration creates
/// them.
/// </summary>
[NonParallelizable]
public class ModuleSchemaTests
{
    [OneTimeSetUp]
    public async Task Ready() => await RootAsync();

    private static async Task<string?> SchemaOfTableAsync(TestModelDbContext db, string table)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT table_schema FROM information_schema.tables WHERE table_name = @t", connection);
        command.Parameters.AddWithValue("t", table);
        return (string?)await command.ExecuteScalarAsync();
    }

    [Test]
    public async Task T16_ModuleTablesLandInTheirSchemas_AndTemplateTablesStayInPublic()
    {
        var (scope, db) = await ContextAsync();
        using (scope)
        await using (db)
        {
            (await SchemaOfTableAsync(db, "TBL_ALPHA_THING")).Should().Be("tsa");
            (await SchemaOfTableAsync(db, "TBL_BETA_THING")).Should().Be("tsb");
            (await SchemaOfTableAsync(db, "TBL_TEST_LEDGER_ROW")).Should().Be("tdf");
            (await SchemaOfTableAsync(db, "Branches")).Should().Be("public");
            (await SchemaOfTableAsync(db, "AspNetUsers")).Should().Be("public");
        }
    }

    [Test]
    public async Task T16_AForeignKeyAcrossModuleSchemas_IsEnforced()
    {
        var branch = await CreateBranchAsync(); // outside the system push: the audit row needs a real user
        using var _ = Accessor.Push(ModelHarness.System);
        var (scope, db) = await ContextAsync();
        using (scope)
        await using (db)
        {
            var alpha = new AlphaThing { BranchId = branch, Name = "alpha" };
            db.Set<AlphaThing>().Add(alpha);
            await db.SaveChangesAsync();

            db.Set<BetaThing>().Add(new BetaThing { AlphaThingId = alpha.Id, Note = "refers to an existing row" });
            await db.SaveChangesAsync();

            db.Set<BetaThing>().Add(new BetaThing { AlphaThingId = int.MaxValue, Note = "refers to nothing" });
            var act = () => db.SaveChangesAsync();
            var thrown = (await act.Should().ThrowAsync<DbUpdateException>()).Which;
            var postgres = thrown.GetBaseException().Should().BeOfType<PostgresException>().Which;
            postgres.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
            postgres.ConstraintName.Should().Contain("TBL_ALPHA_THING");
        }
    }

    [Test]
    public async Task T17_AFirstMigration_CreatesEachModuleSchema_AndTheHistoryTableStaysInTheDefault()
    {
        var (scope, db) = await ContextAsync();
        using (scope)
        await using (db)
        {
            var differ = db.GetService<IMigrationsModelDiffer>();
            var operations = differ.GetDifferences(null, db.GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>().Model.GetRelationalModel()).ToList();

            var schemas = operations.OfType<EnsureSchemaOperation>().Select(o => o.Name).ToList();
            schemas.Should().Contain(["tsa", "tsb", "tdf"]);
            schemas.Should().NotContain("core");

            var alphaTable = operations.OfType<CreateTableOperation>().Single(o => o.Name == "TBL_ALPHA_THING");
            alphaTable.Schema.Should().Be("tsa");
            operations.IndexOf(operations.OfType<EnsureSchemaOperation>().Single(o => o.Name == "tsa"))
                .Should().BeLessThan(operations.IndexOf(alphaTable), "the schema is created before its first table");

            var betaForeignKey = operations.OfType<CreateTableOperation>().Single(o => o.Name == "TBL_BETA_THING").ForeignKeys.Single();
            (betaForeignKey.PrincipalSchema, betaForeignKey.PrincipalTable).Should().Be(("tsa", "TBL_ALPHA_THING"));

            var history = db.GetService<IHistoryRepository>();
            history.GetCreateScript().Should().Contain("\"__EFMigrationsHistory\"").And.NotContain("tsa.").And.NotContain("tsb.");
        }
    }
}
