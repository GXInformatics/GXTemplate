using System;
using System.Collections.Generic;
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
/// Pass 60c (owner decisions D1, D3, D5): on a real database, every module's tables are in core, plural, with
/// snake_case columns and constraint names; the template's tables stay in public, untouched; a first migration
/// creates core and no other schema. Until 1.7.1 this file was <c>ModuleSchemaTests</c> and asserted a schema per
/// module (Pass 60 plan rows 16 and 17).
/// </summary>
[NonParallelizable]
public class CoreSchemaTests
{
    [OneTimeSetUp]
    public async Task Ready() => await RootAsync();

    private static async Task<NpgsqlConnection> OpenAsync(TestModelDbContext db)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
        return connection;
    }

    private static async Task<string?> SchemaOfTableAsync(TestModelDbContext db, string table)
    {
        await using var command = new NpgsqlCommand(
            "SELECT table_schema FROM information_schema.tables WHERE table_name = @t", await OpenAsync(db));
        command.Parameters.AddWithValue("t", table);
        return (string?)await command.ExecuteScalarAsync();
    }

    private static async Task<List<string>> ListAsync(TestModelDbContext db, string sql, string table)
    {
        await using var command = new NpgsqlCommand(sql, await OpenAsync(db));
        command.Parameters.AddWithValue("t", table);
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) values.Add(reader.GetString(0));
        return values;
    }

    [Test]
    public async Task T16_EveryModulesTablesAreInCore_Plural_AndTemplateTablesStayInPublic()
    {
        var (scope, db) = await ContextAsync();
        using (scope)
        await using (db)
        {
            (await SchemaOfTableAsync(db, "TBL_ALPHA_THINGS")).Should().Be("core");
            (await SchemaOfTableAsync(db, "TBL_BETA_THINGS")).Should().Be("core");
            (await SchemaOfTableAsync(db, "TBL_TEST_LEDGER_ROWS")).Should().Be("core");
            (await SchemaOfTableAsync(db, "Branches")).Should().Be("public");
            (await SchemaOfTableAsync(db, "AspNetUsers")).Should().Be("public");
        }
    }

    [Test]
    public async Task P60c_BusinessColumnsAndConstraintsAreSnakeCase_TemplateTablesAreUntouched()
    {
        var (scope, db) = await ContextAsync();
        using (scope)
        await using (db)
        {
            const string columns = "SELECT column_name FROM information_schema.columns WHERE table_name = @t ORDER BY 1";
            (await ListAsync(db, columns, "TBL_ALPHA_THINGS")).Should().Equal("branch_id", "id", "name", "on_hand");
            (await ListAsync(db, columns, "TBL_TEST_LEDGER_ROWS")).Should().OnlyContain(c => c == c.ToLowerInvariant())
                .And.Contain(["id", "ref", "amount"]);
            (await ListAsync(db, columns, "Branches")).Should().Contain(["Id", "Code", "Name", "IsActive"]);

            const string constraints = "SELECT conname FROM pg_constraint k JOIN pg_class c ON c.oid = k.conrelid WHERE c.relname = @t ORDER BY 1";
            (await ListAsync(db, constraints, "TBL_BETA_THINGS")).Should().Equal(
                "fk_tbl_beta_things_tbl_alpha_things_alpha_thing_id", "pk_tbl_beta_things");
            (await ListAsync(db, "SELECT indexname FROM pg_indexes WHERE tablename = @t ORDER BY 1", "TBL_BETA_THINGS"))
                .Should().Equal("ix_tbl_beta_things_alpha_thing_id", "pk_tbl_beta_things");
            (await ListAsync(db, constraints, "Branches")).Should().Contain("PK_Branches");
        }
    }

    [Test]
    public async Task T16_AForeignKeyBetweenModules_IsEnforced_InCore()
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
            postgres.ConstraintName.Should().Be("fk_tbl_beta_things_tbl_alpha_things_alpha_thing_id");
        }
    }

    [Test]
    public async Task T17_AFirstMigration_CreatesCoreAndNoOtherSchema_AndTheHistoryTableStaysInTheDefault()
    {
        var (scope, db) = await ContextAsync();
        using (scope)
        await using (db)
        {
            var differ = db.GetService<IMigrationsModelDiffer>();
            var operations = differ.GetDifferences(null, db.GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>().Model.GetRelationalModel()).ToList();

            operations.OfType<EnsureSchemaOperation>().Select(o => o.Name).Should().Equal("core");

            var alphaTable = operations.OfType<CreateTableOperation>().Single(o => o.Name == "TBL_ALPHA_THINGS");
            alphaTable.Schema.Should().Be("core");
            operations.IndexOf(operations.OfType<EnsureSchemaOperation>().Single())
                .Should().BeLessThan(operations.IndexOf(alphaTable), "the schema is created before its first table");

            var betaForeignKey = operations.OfType<CreateTableOperation>().Single(o => o.Name == "TBL_BETA_THINGS").ForeignKeys.Single();
            (betaForeignKey.PrincipalSchema, betaForeignKey.PrincipalTable).Should().Be(("core", "TBL_ALPHA_THINGS"));
            operations.OfType<CreateTableOperation>().Single(o => o.Name == "Branches").Schema.Should().BeNull();

            var history = db.GetService<IHistoryRepository>();
            history.GetCreateScript().Should().Contain("\"__EFMigrationsHistory\"").And.Contain("\"MigrationId\"").And.NotContain("core.");
        }
    }
}
