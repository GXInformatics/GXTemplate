using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace CleanArchitecture.Blazor.Infrastructure.Persistence.Rules;

/// <summary>
/// Database triggers for append-only (ledger) tables (Pass 59, owner decision 6): defence in depth beneath
/// the save rules and the bulk-command guard.
/// </summary>
/// <remarks>
/// <b>Call it in the migration that creates each <c>IAppendOnly</c> table</b>, after <c>CreateTable</c>:
/// <code>migrationBuilder.CreateAppendOnlyTrigger("TBL_STOCK_MOVEMENTS");   // core, the business schema (Pass 60c)</code>
/// The row trigger refuses <c>UPDATE</c> and <c>DELETE</c>, and the statement trigger refuses
/// <c>TRUNCATE</c>, from every path: the application, a raw connection, psql. <c>DataModelGuard</c> checks
/// at startup that every append-only table has both, enabled, so a forgotten call fails loudly.
/// <para>
/// A deliberate correction then needs someone with the right to disable the triggers, which is the point.
/// PostgreSQL only: on another provider the calls do nothing.
/// </para>
/// </remarks>
public static class AppendOnlyMigrationExtensions
{
    private const string Function = "gx_refuse_append_only_change";

    public static MigrationBuilder CreateAppendOnlyTrigger(this MigrationBuilder migrationBuilder, string table, string? schema = Extensions.GxNamingConventions.BusinessSchema)
    {
        if (!migrationBuilder.IsNpgsql()) return migrationBuilder;
        var target = Qualified(table, schema);

        migrationBuilder.Sql(
            $"""
             CREATE OR REPLACE FUNCTION {Function}() RETURNS trigger LANGUAGE plpgsql AS $gx$
             BEGIN
                 RAISE EXCEPTION 'Table %.% is append-only: % is not allowed. Post a reversing entry instead.',
                     TG_TABLE_SCHEMA, TG_TABLE_NAME, TG_OP
                     USING ERRCODE = 'restrict_violation';
             END
             $gx$;
             """);
        migrationBuilder.Sql(
            $"CREATE TRIGGER {DataModelGuard.RowTrigger} BEFORE UPDATE OR DELETE ON {target} " +
            $"FOR EACH ROW EXECUTE FUNCTION {Function}();");
        migrationBuilder.Sql(
            $"CREATE TRIGGER {DataModelGuard.TruncateTrigger} BEFORE TRUNCATE ON {target} " +
            $"FOR EACH STATEMENT EXECUTE FUNCTION {Function}();");
        return migrationBuilder;
    }

    /// <summary>Removes the triggers (for a migration's <c>Down</c>). The shared function stays.</summary>
    public static MigrationBuilder DropAppendOnlyTrigger(this MigrationBuilder migrationBuilder, string table, string? schema = Extensions.GxNamingConventions.BusinessSchema)
    {
        if (!migrationBuilder.IsNpgsql()) return migrationBuilder;
        var target = Qualified(table, schema);
        migrationBuilder.Sql($"DROP TRIGGER IF EXISTS {DataModelGuard.RowTrigger} ON {target};");
        migrationBuilder.Sql($"DROP TRIGGER IF EXISTS {DataModelGuard.TruncateTrigger} ON {target};");
        return migrationBuilder;
    }

    /// <summary>The SQL the helper runs, for a test or a hand-built schema (EnsureCreated has no migrations).</summary>
    public static IReadOnlyList<string> CreateSql(string table, string? schema = Extensions.GxNamingConventions.BusinessSchema)
    {
        var builder = new MigrationBuilder(Transactions.PostingSql.NpgsqlProvider);
        builder.CreateAppendOnlyTrigger(table, schema);
        return builder.Operations.OfType<SqlOperation>().Select(o => o.Sql).ToList();
    }

    private static bool IsNpgsql(this MigrationBuilder migrationBuilder) =>
        migrationBuilder.ActiveProvider == Transactions.PostingSql.NpgsqlProvider;

    private static string Qualified(string table, string? schema) =>
        schema is null ? Quote(table) : Quote(schema) + "." + Quote(table);

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
