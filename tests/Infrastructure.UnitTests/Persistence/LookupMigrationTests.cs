using CleanArchitecture.Blazor.Infrastructure.Persistence.Rules;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace CleanArchitecture.Blazor.Infrastructure.UnitTests.Persistence;

/// <summary>Pass 60c: the lookup seeding helpers (GX convention §3.4). The sequence reset runs for real in ResetIsolationTests (G1).</summary>
public class LookupMigrationTests
{
    [Fact]
    public void SeedLookup_InsertsExplicitIds_IntoIdNameSlug_InCore()
    {
        var builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        builder.SeedLookup("TBL_LK_WAREHOUSE_TYPES", (1, "Main"), (2, "In transit"));

        var insert = Assert.IsType<InsertDataOperation>(Assert.Single(builder.Operations));
        Assert.Equal(("core", "TBL_LK_WAREHOUSE_TYPES"), (insert.Schema, insert.Table));
        Assert.Equal(["id", "name", "slug"], insert.Columns);
        Assert.Equal(2, insert.Values[1, 0]);
        Assert.Equal("In transit", insert.Values[1, 1]);
        Assert.Equal("in-transit", insert.Values[1, 2]);
    }

    [Fact]
    public void ResetIdentitySequence_SetsTheSequencePastTheLargestId_OnPostgreSqlOnly()
    {
        var postgres = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        postgres.ResetIdentitySequence("TBL_LK_WAREHOUSE_TYPES");
        var sql = Assert.IsType<SqlOperation>(Assert.Single(postgres.Operations)).Sql;
        Assert.Equal("SELECT setval(pg_get_serial_sequence('\"core\".\"TBL_LK_WAREHOUSE_TYPES\"', 'id'), coalesce(max(\"id\"), 0) + 1, false) " +
                     "FROM \"core\".\"TBL_LK_WAREHOUSE_TYPES\";", sql);

        var sqlite = new MigrationBuilder("Microsoft.EntityFrameworkCore.Sqlite");
        sqlite.ResetIdentitySequence("TBL_LK_WAREHOUSE_TYPES");
        Assert.Empty(sqlite.Operations);
    }

    [Theory]
    [InlineData("Main", "main")]
    [InlineData("In transit", "in-transit")]
    [InlineData("  Spare & Parts / HQ ", "spare-parts-hq")]
    public void Slug_IsLowerCaseKebab(string name, string expected) => Assert.Equal(expected, LookupMigrationExtensions.Slug(name));
}
