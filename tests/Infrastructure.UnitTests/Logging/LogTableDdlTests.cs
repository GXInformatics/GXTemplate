using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Domain.Entities;
using CleanArchitecture.Blazor.Infrastructure.Extensions;
using CleanArchitecture.Blazor.Infrastructure.Persistence.Logging;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CleanArchitecture.Blazor.Infrastructure.UnitTests.Logging;

/// <summary>
/// The DDL itself: that it names the table the reading side reads, that its guards make it
/// idempotent, and that each provider's statements create the columns and indexes the model reads.
/// </summary>
public class LogTableDdlTests
{
    public static TheoryData<string> Providers =>
        new()
        {
            DbProviderKeys.SqLite,
#if (UseSqlServer)
            DbProviderKeys.SqlServer,
#endif
#if (UsePostgreSql)
            DbProviderKeys.Npgsql,
#endif
        };

    // ------------------------------------------------------------- naming

#if (UseSqlServer)
    [Fact]
    public void TheDdlNamesTheSameTableTheModelReads_OnSqlServer()
    {
        using var db = new LogDbContext(new DbContextOptionsBuilder<LogDbContext>()
            .UseSqlServer("Server=none;Database=none;").Options);

        Assert.Contains(
            $"[{LogTableDdl.SqlServerSchema}].[{LogTableDdl.TableName}]",
            LogTableDdl.Statements(DbProviderKeys.SqlServer)[0]);
        Assert.Equal(LogTableDdl.TableName, db.Model.FindEntityType(typeof(SystemLog))!.GetTableName());
    }
#endif

#if (UsePostgreSql)
    [Fact]
    public void TheDdlNamesTheSameTableTheModelReadsAndTheSinkWrites_OnPostgres()
    {
        // Three-way on the name as well as on the columns: the snake_case convention, the sink's
        // hard-coded table name, and this DDL all have to land on system_logs.
        using var db = new LogDbContext(new DbContextOptionsBuilder<LogDbContext>()
            .UseNpgsql("Host=none;Database=none;")
            .UseSnakeCaseNamingConvention().Options);

        Assert.Contains(
            $"\"{LogTableDdl.NpgsqlSchema}\".\"{SerilogExtensions.NpgsqlTableName}\"",
            LogTableDdl.Statements(DbProviderKeys.Npgsql)[0]);
        Assert.Equal(
            SerilogExtensions.NpgsqlTableName,
            db.Model.FindEntityType(typeof(SystemLog))!.GetTableName());
    }
#endif

    // ------------------------------------------------------------- idempotence, by dialect

    [Theory]
    [MemberData(nameof(Providers))]
    public void EveryStatementIsGuarded(string provider)
    {
        // Idempotence is what lets a production login holding only INSERT/SELECT/DELETE start the
        // application on every run after the first: nothing is issued, so nothing is denied.
        foreach (var statement in LogTableDdl.Statements(provider))
        {
            var guarded = statement.Contains("IF NOT EXISTS", StringComparison.OrdinalIgnoreCase);
            Assert.True(guarded, $"unguarded statement for {provider}:\n{statement}");
        }
    }

#if (UseSqlServer)
    [Fact]
    public void TheSqlServerGuardsUseTSqlsOwnForm_BecauseTSqlHasNoCreateTableIfNotExists()
    {
        // The trap this test exists for: "CREATE TABLE IF NOT EXISTS" is valid in SQLite and
        // PostgreSQL and a syntax error in T-SQL. SQL Server has to test sys.tables / sys.indexes.
        var statements = LogTableDdl.Statements(DbProviderKeys.SqlServer);

        Assert.DoesNotContain(statements, s =>
            s.Contains("CREATE TABLE IF NOT EXISTS", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("CREATE INDEX IF NOT EXISTS", StringComparison.OrdinalIgnoreCase));

        Assert.Contains("sys.tables", statements[0]);
        Assert.Contains(statements, s => s.Contains("sys.indexes"));
    }
#endif

    [Theory]
    [InlineData(DbProviderKeys.SqLite)]
#if (UsePostgreSql)
    [InlineData(DbProviderKeys.Npgsql)]
#endif
    public void TheOtherTwoTakeIfNotExistsDirectly(string provider)
    {
        Assert.Contains("CREATE TABLE IF NOT EXISTS", LogTableDdl.Statements(provider)[0]);
    }

    // ------------------------------------------------------------- column types that must agree

#if (UsePostgreSql)
    [Fact]
    public void ThePostgresTimestampColumnIsTimestamptz_BecauseTheWriterAndTheEnricherSayItIs()
    {
        // Pass 14B moved three things at once: this DDL to timestamptz, the sink's writer to
        // NpgsqlDbType.TimestampTz, and UtcTimestampEnricher back to Kind=Utc. There was no
        // expectation on the column TYPE here before - only on the column NAME - so any one of the
        // three could have been reverted and only a live PostgreSQL run would have noticed.
        //
        // A timestamptz column ACCEPTS Kind=Utc and REJECTS Kind=Unspecified; "timestamp without
        // time zone" does the exact opposite. The two are not interchangeable and there is no value
        // that satisfies both, so this is a genuine either/or rather than a stylistic preference.
        var create = LogTableDdl.Statements(DbProviderKeys.Npgsql)[0];

        Assert.Contains("timestamp with time zone", create, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("timestamp without time zone", create, StringComparison.OrdinalIgnoreCase);
    }
#endif

    [Fact]
    public void TheOtherTwoProvidersTimestampColumnsAreUnchangedByTheTimestamptzWork()
    {
        // The legacy switch was Npgsql-only and set only in the Npgsql branch, so MSSQL and SQLite
        // were already in the target state and nothing about them moved in Pass 14B. Stated as an
        // assertion rather than assumed, because "unchanged" is exactly the kind of claim that goes
        // stale silently.
#if (UseSqlServer)
        Assert.Contains("datetime2", LogTableDdl.Statements(DbProviderKeys.SqlServer)[0]);
#endif
        Assert.Contains("TEXT NOT NULL", LogTableDdl.Statements(DbProviderKeys.SqLite)[0]);
    }

    // ------------------------------------------------------------- indexes

    [Theory]
    [MemberData(nameof(Providers))]
    public void TheIndexesSystemLogConfigurationDeclares_AreCreated(string provider)
    {
        // No migration will create these now, and the SystemLogs page filters by Level and orders by
        // TimeStamp on every page load.
        var all = string.Join("\n", LogTableDdl.Statements(provider));

        Assert.Contains("level", all, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("time_stamp".Replace("_", ""), all.Replace("_", ""), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, LogTableDdl.Statements(provider).Count(s =>
            s.Contains("CREATE INDEX", StringComparison.OrdinalIgnoreCase)));
    }

    // It actually runs, twice, and produces the shape EF reads: LogTablePostgresTests, on a real
    // server (pass 47, CO-157). The SQLite file versions that were here are gone with SQLite.

    // ------------------------------------------------------------- the existence pre-check

    [Theory]
    [MemberData(nameof(Providers))]
    public void TheExistenceQueryReadsOnlyTheCatalogue(string provider)
    {
        // It has to be answerable by a login holding no privilege beyond connecting, because that is
        // exactly the login it exists to protect: PostgreSQL refuses CREATE TABLE IF NOT EXISTS for
        // want of CREATE on the schema even when the table is already there, so the guard alone
        // would print a startup error forever on the best-configured deployments.
        var query = LogTableDdl.ExistsQuery(provider);

        Assert.StartsWith("SELECT COUNT(*)", query.TrimStart(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CREATE", query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("INSERT", query, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnUnsupportedProviderIsRefusedRatherThanSilentlyProducingNothing()
    {
        Assert.Throws<InvalidOperationException>(() => LogTableDdl.Statements("oracle"));
        Assert.Throws<InvalidOperationException>(() => LogTableDdl.ColumnNames("oracle"));
        Assert.Throws<InvalidOperationException>(() => LogTableDdl.ExistsQuery("oracle"));
    }
}
