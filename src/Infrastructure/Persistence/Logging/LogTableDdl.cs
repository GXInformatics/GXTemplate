// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Infrastructure.Extensions;

namespace CleanArchitecture.Blazor.Infrastructure.Persistence.Logging;

/// <summary>
/// The log table's schema, as DDL this application issues itself.
/// </summary>
/// <remarks>
/// Pass 11 gave the log database its own home and made the sink's auto-create responsible for the
/// table there. Pass 11B measured that decision against all three providers and it failed on two,
/// for two unrelated reasons:
/// <list type="number">
/// <item><b>PostgreSQL could not produce a usable table.</b> That sink's DDL is generated entirely
/// from its <c>ColumnWriter</c> dictionary, and a dictionary of writers has no way to express an
/// identity column - so the table it created had no <c>id</c> at all, while <c>SystemLog.Id</c> is
/// the EF key and the SystemLogs page's default sort.</item>
/// <item><b>SQL Server could, but at an unacceptable price.</b> That sink issues its CREATE TABLE
/// in its constructor, which Serilog runs inside <c>WebApplicationBuilder.Build()</c>; with the log
/// server unreachable the exception came out of Build() and the whole application failed to start -
/// exactly inverting the rule that a broken log database must never stop the business application
/// serving.</item>
/// </list>
/// Owning the DDL here fixes both. It runs after the host is built, so a failure can be reported and
/// survived rather than thrown out of startup, and it can say <c>IDENTITY</c> in the provider's own
/// words.
/// <para>
/// The shapes below reproduce what EF's migrations created before Pass 11 moved the table out - the
/// arrangement both sinks were already writing into successfully - so this is a relocation of a
/// known-good schema, not a new one. <c>LogTableDdlTests</c> and <c>SinkColumnDriftTests</c> hold it
/// to the entity and to both sinks.
/// </para>
/// <para>
/// <b>There is still no migration chain.</b> This creates a table and never alters one. A log
/// database deployed before a property was added to <see cref="SystemLog"/> keeps its old columns,
/// and the guards below will not touch it: adding a property carries a manual ALTER on every
/// deployed log database. That limitation is unchanged from Pass 11B and is stated in the README.
/// </para>
/// </remarks>
public static class LogTableDdl
{
    /// <summary>The table's name on SQLite and SQL Server.</summary>
    public const string TableName = "SystemLogs";

    /// <summary>The SQL Server schema the table lives in.</summary>
    public const string SqlServerSchema = "dbo";

    /// <summary>The PostgreSQL schema the table lives in.</summary>
    public const string NpgsqlSchema = "public";

    /// <summary>
    /// The columns, per provider, in order, as (name, type-and-constraints) pairs.
    /// </summary>
    /// <remarks>
    /// Held as data rather than as a SQL string so that the drift test can compare this column set
    /// against the entity and against each sink's own configuration without parsing SQL. The
    /// statements below are rendered from it, so what the test inspects is what gets executed.
    /// </remarks>
    private static readonly (string Name, string Definition)[] SqliteColumns =
    [
        ("Id",              "INTEGER NOT NULL CONSTRAINT \"PK_SystemLogs\" PRIMARY KEY AUTOINCREMENT"),
        ("Message",         "TEXT NULL"),
        ("MessageTemplate", "TEXT NULL"),
        ("Level",           "TEXT NOT NULL"),
        ("TimeStamp",       "TEXT NOT NULL"),
        ("Exception",       "TEXT NULL"),
        ("UserName",        "TEXT NULL"),

        // Nullable, and null is meaningful: startup, seeding and background events have no tenant.
        // See SystemLog.TenantId.
        ("TenantId",        "TEXT NULL"),

        ("ClientIP",        "TEXT NULL"),
        ("ClientAgent",     "TEXT NULL"),
        ("Properties",      "TEXT NULL"),
        ("LogEvent",        "TEXT NULL")
    ];

    private static readonly (string Name, string Definition)[] SqlServerColumns =
    [
        ("Id",              "int IDENTITY(1,1) NOT NULL"),
        ("Message",         "nvarchar(max) NULL"),
        ("MessageTemplate", "nvarchar(max) NULL"),
        ("Level",           "nvarchar(450) NOT NULL"),
        ("TimeStamp",       "datetime2 NOT NULL"),
        ("Exception",       "nvarchar(max) NULL"),

        // Unbounded rather than the nvarchar(450) EF's migration used. The sink declares UserName
        // and ClientAgent with no length, so it writes them unbounded; a User-Agent header longer
        // than the column is a truncation error on a log write, which is a silly way to lose a log.
        ("UserName",        "nvarchar(max) NULL"),

        // 450, matching ClientIP rather than the unbounded columns: a tenant id is a GUID string
        // written by this application, not a value some client can make arbitrarily long.
        ("TenantId",        "nvarchar(450) NULL"),

        ("ClientIP",        "nvarchar(450) NULL"),
        ("ClientAgent",     "nvarchar(max) NULL"),

        ("Properties",      "nvarchar(max) NULL"),
        ("LogEvent",        "nvarchar(max) NULL")
    ];

    /// <remarks>
    /// snake_case, because <c>UseDatabase</c> applies <c>UseSnakeCaseNamingConvention()</c> for this
    /// provider and the sink writes these exact names. <c>time_stamp</c> is
    /// <c>timestamp with time zone</c> - <c>timestamptz</c> - since Pass 14B, and all three sides
    /// had to move at once: EF maps <c>DateTime</c> to <c>timestamptz</c> now that
    /// <c>Npgsql.EnableLegacyTimestampBehavior</c> is gone, <c>BuildNpgsqlColumnWriters</c> declares
    /// <c>NpgsqlDbType.TimestampTz</c>, and <c>UtcTimestampEnricher</c> publishes <c>Kind=Utc</c>.
    /// A timestamptz column ACCEPTS Kind=Utc and REJECTS Kind=Unspecified, which is the exact
    /// inverse of what this column used to be; leaving any one of the three behind is a runtime
    /// bind failure on every log write. <c>SinkTimestampAcceptanceTests</c> writes through the real
    /// sink into this DDL, which is the only check that covers all three together.
    /// </remarks>
    private static readonly (string Name, string Definition)[] NpgsqlColumns =
    [
        ("id",               "integer GENERATED BY DEFAULT AS IDENTITY"),
        ("message",          "text NULL"),
        ("message_template", "text NULL"),
        ("level",            "character varying(450) NOT NULL"),
        ("time_stamp",       "timestamp with time zone NOT NULL"),
        ("exception",        "text NULL"),

        // text rather than EF's character varying(450), for the same reason as SQL Server above:
        // Pass 11B set these three writers to NpgsqlDbType.Text, so the sink writes them unbounded.
        ("user_name",        "text NULL"),

        // snake_case, like every column here - UseSnakeCaseNamingConvention turns SystemLog.TenantId
        // into tenant_id, and the writer below is keyed by the same name.
        ("tenant_id",        "text NULL"),

        ("client_ip",        "text NULL"),
        ("client_agent",     "text NULL"),

        ("properties",       "text NULL"),
        ("log_event",        "text NULL")
    ];

    /// <summary>The column names this DDL creates, for the configured provider.</summary>
    public static IReadOnlyList<string> ColumnNames(string dbProvider) =>
        ColumnsFor(dbProvider).Select(c => c.Name).ToArray();

    private static (string Name, string Definition)[] ColumnsFor(string dbProvider) =>
        dbProvider.ToLowerInvariant() switch
        {
            DbProviderKeys.SqLite => SqliteColumns,
            DbProviderKeys.SqlServer => SqlServerColumns,
            DbProviderKeys.Npgsql => NpgsqlColumns,
            _ => throw new InvalidOperationException($"DB Provider {dbProvider} is not supported.")
        };

    /// <summary>
    /// A read-only catalogue query returning 1 if the log table already exists and 0 if it does not.
    /// </summary>
    /// <remarks>
    /// Asked before any DDL, and the reason is a privilege one rather than an efficiency one.
    /// <b>PostgreSQL evaluates CREATE permission on the schema before it evaluates IF NOT EXISTS</b>,
    /// so <c>CREATE TABLE IF NOT EXISTS</c> raises <c>42501 permission denied for schema public</c>
    /// for a login without CREATE even when the table is right there and the statement would have
    /// done nothing. Measured, not assumed: an ordinary log-writer role holding only
    /// SELECT/INSERT/DELETE produced exactly that error on every start.
    /// <para>
    /// Without this check the guarded DDL would still be safe, but it would print a spurious startup
    /// error forever on precisely the least-privileged - that is, best-configured - deployments.
    /// These queries read the system catalogue, which needs no privilege beyond connecting.
    /// </para>
    /// </remarks>
    public static string ExistsQuery(string dbProvider) =>
        dbProvider.ToLowerInvariant() switch
        {
            DbProviderKeys.SqLite =>
                $"SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '{TableName}'",

            DbProviderKeys.SqlServer =>
                $"""
                 SELECT COUNT(*) FROM sys.tables t
                 JOIN sys.schemas s ON s.schema_id = t.schema_id
                 WHERE s.name = '{SqlServerSchema}' AND t.name = '{TableName}'
                 """,

            DbProviderKeys.Npgsql =>
                $"""
                 SELECT COUNT(*) FROM pg_tables
                 WHERE schemaname = '{NpgsqlSchema}' AND tablename = '{SerilogExtensions.NpgsqlTableName}'
                 """,

            _ => throw new InvalidOperationException($"DB Provider {dbProvider} is not supported.")
        };

    /// <summary>
    /// The statements to execute, in order, to bring the log table into existence.
    /// </summary>
    /// <remarks>
    /// Every statement is guarded, so running them against an already-provisioned log database
    /// issues no DDL at all. That is what lets an ordinary production login - one holding only
    /// INSERT, SELECT and DELETE - start the application normally on every run after the first.
    /// <para>
    /// The guards are written in each provider's own dialect rather than a shared one, because
    /// <b>T-SQL has no CREATE TABLE IF NOT EXISTS</b>. SQLite and PostgreSQL take the clause
    /// directly; SQL Server needs an explicit <c>sys.tables</c> / <c>sys.indexes</c> test.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> Statements(string dbProvider) =>
        dbProvider.ToLowerInvariant() switch
        {
            DbProviderKeys.SqLite => SqliteStatements(),
            DbProviderKeys.SqlServer => SqlServerStatements(),
            DbProviderKeys.Npgsql => NpgsqlStatements(),
            _ => throw new InvalidOperationException($"DB Provider {dbProvider} is not supported.")
        };

    private static string Columns((string Name, string Definition)[] columns, string quoteOpen, string quoteClose) =>
        string.Join(",\n", columns.Select(c => $"    {quoteOpen}{c.Name}{quoteClose} {c.Definition}"));

    private static IReadOnlyList<string> SqliteStatements() =>
    [
        $"""
         CREATE TABLE IF NOT EXISTS "{TableName}" (
         {Columns(SqliteColumns, "\"", "\"")}
         );
         """,

        // The indexes SystemLogConfiguration declares. No migration will create them now, and the
        // SystemLogs page filters by Level and orders by TimeStamp on every page load.
        $"""CREATE INDEX IF NOT EXISTS "IX_{TableName}_Level" ON "{TableName}" ("Level");""",
        $"""CREATE INDEX IF NOT EXISTS "IX_{TableName}_TimeStamp" ON "{TableName}" ("TimeStamp");"""
    ];

    private static IReadOnlyList<string> SqlServerStatements() =>
    [
        $"""
         IF NOT EXISTS (SELECT 1 FROM sys.tables t
                        JOIN sys.schemas s ON s.schema_id = t.schema_id
                        WHERE s.name = '{SqlServerSchema}' AND t.name = '{TableName}')
         BEGIN
             CREATE TABLE [{SqlServerSchema}].[{TableName}] (
         {Columns(SqlServerColumns, "[", "]")},
                 CONSTRAINT [PK_{TableName}] PRIMARY KEY ([Id])
             );
         END
         """,

        $"""
         IF NOT EXISTS (SELECT 1 FROM sys.indexes
                        WHERE name = 'IX_{TableName}_Level'
                          AND object_id = OBJECT_ID('{SqlServerSchema}.{TableName}'))
             CREATE INDEX [IX_{TableName}_Level] ON [{SqlServerSchema}].[{TableName}] ([Level]);
         """,

        $"""
         IF NOT EXISTS (SELECT 1 FROM sys.indexes
                        WHERE name = 'IX_{TableName}_TimeStamp'
                          AND object_id = OBJECT_ID('{SqlServerSchema}.{TableName}'))
             CREATE INDEX [IX_{TableName}_TimeStamp] ON [{SqlServerSchema}].[{TableName}] ([TimeStamp]);
         """
    ];

    private static IReadOnlyList<string> NpgsqlStatements() =>
    [
        $"""
         CREATE TABLE IF NOT EXISTS "{NpgsqlSchema}"."{SerilogExtensions.NpgsqlTableName}" (
         {Columns(NpgsqlColumns, "\"", "\"")},
             CONSTRAINT "pk_{SerilogExtensions.NpgsqlTableName}" PRIMARY KEY ("id")
         );
         """,

        $"""CREATE INDEX IF NOT EXISTS "ix_{SerilogExtensions.NpgsqlTableName}_level" ON "{NpgsqlSchema}"."{SerilogExtensions.NpgsqlTableName}" ("level");""",
        $"""CREATE INDEX IF NOT EXISTS "ix_{SerilogExtensions.NpgsqlTableName}_time_stamp" ON "{NpgsqlSchema}"."{SerilogExtensions.NpgsqlTableName}" ("time_stamp");"""
    ];
}
