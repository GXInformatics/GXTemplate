using System.Text.RegularExpressions;
using CleanArchitecture.Blazor.Infrastructure.Persistence.Extensions;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CleanArchitecture.Blazor.Infrastructure.Persistence.Rules;

/// <summary>
/// Seeding lookups by migration (Pass 60c, GX convention §3.4): explicit ids, and the identity sequence reset in the
/// same migration.
/// </summary>
/// <remarks>
/// <code>migrationBuilder.SeedLookup("TBL_LK_WAREHOUSE_TYPES", (1, "Main"), (2, "In transit"));
/// migrationBuilder.ResetIdentitySequence("TBL_LK_WAREHOUSE_TYPES");</code>
/// <para>
/// <b>The ids are a contract.</b> Code may name a seeded row by its id (MNEFleets compiles constants against them),
/// so a seed writes them explicitly. An explicit insert does not advance an identity sequence, though: without the
/// reset, the first row a user adds collides with id 1. <see cref="ResetIdentitySequence"/> sets the sequence past
/// the largest id, as MNEFleets' <c>core.advance_identity_sequences</c> does. PostgreSQL only; elsewhere it does nothing.
/// </para>
/// </remarks>
public static class LookupMigrationExtensions
{
    /// <summary>
    /// Inserts lookup rows with explicit ids into the lookup's snake_case columns <c>id</c>, <c>name</c> and <c>slug</c>
    /// (the slug is the name in lower-case kebab form). Call <see cref="ResetIdentitySequence"/> after it.
    /// </summary>
    public static MigrationBuilder SeedLookup(this MigrationBuilder migrationBuilder, string table,
        params (int Id, string Name)[] rows) =>
        migrationBuilder.SeedLookup(table, GxNamingConventions.BusinessSchema, rows);

    /// <inheritdoc cref="SeedLookup(MigrationBuilder, string, (int Id, string Name)[])"/>
    public static MigrationBuilder SeedLookup(this MigrationBuilder migrationBuilder, string table, string? schema,
        params (int Id, string Name)[] rows)
    {
        if (rows.Length == 0) return migrationBuilder;
        var values = new object?[rows.Length, 3];
        for (var i = 0; i < rows.Length; i++)
        {
            values[i, 0] = rows[i].Id;
            values[i, 1] = rows[i].Name;
            values[i, 2] = Slug(rows[i].Name);
        }

        migrationBuilder.InsertData(table: table, schema: schema, columns: ["id", "name", "slug"], values: values);
        return migrationBuilder;
    }

    /// <summary>
    /// Sets <paramref name="table"/>'s identity sequence so the next generated id is one more than the largest id in
    /// the table (or 1 when it is empty). PostgreSQL only.
    /// </summary>
    public static MigrationBuilder ResetIdentitySequence(this MigrationBuilder migrationBuilder, string table,
        string? schema = GxNamingConventions.BusinessSchema, string idColumn = "id")
    {
        if (migrationBuilder.ActiveProvider != Transactions.PostingSql.NpgsqlProvider) return migrationBuilder;
        migrationBuilder.Sql(ResetSql(table, schema, idColumn));
        return migrationBuilder;
    }

    /// <summary>The statement <see cref="ResetIdentitySequence"/> runs, for a test or a hand-built schema.</summary>
    public static string ResetSql(string table, string? schema = GxNamingConventions.BusinessSchema, string idColumn = "id")
    {
        var qualified = schema is null ? Quote(table) : Quote(schema) + "." + Quote(table);
        var literal = qualified.Replace("'", "''", StringComparison.Ordinal);
        return $"SELECT setval(pg_get_serial_sequence('{literal}', '{idColumn.Replace("'", "''", StringComparison.Ordinal)}'), " +
               $"coalesce(max({Quote(idColumn)}), 0) + 1, false) FROM {qualified};";
    }

    /// <summary><c>In transit</c> → <c>in-transit</c>.</summary>
    public static string Slug(string name) =>
        Regex.Replace(name.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
