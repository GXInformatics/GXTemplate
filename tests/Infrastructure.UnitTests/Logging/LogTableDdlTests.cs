using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Domain.Entities;
using CleanArchitecture.Blazor.Infrastructure.Extensions;
using CleanArchitecture.Blazor.Infrastructure.Persistence.Logging;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CleanArchitecture.Blazor.Infrastructure.UnitTests.Logging;

/// <summary>
/// The DDL itself: that it names the table the reading side reads, that its guards make it
/// idempotent, and - on SQLite, where a whole database is a file - that it actually runs and
/// produces the shape EF expects.
/// </summary>
[Collection(SqliteFileCollection.Name)]
public class LogTableDdlTests
{
    public static TheoryData<string> Providers =>
        new() { DbProviderKeys.SqLite, DbProviderKeys.SqlServer, DbProviderKeys.Npgsql };

    // ------------------------------------------------------------- naming

    [Fact]
    public void TheDdlNamesTheSameTableTheModelReads_OnSqlite()
    {
        using var db = new LogDbContext(new DbContextOptionsBuilder<LogDbContext>()
            .UseSqlite("Data Source=:memory:").Options);

        Assert.Contains(
            $"\"{LogTableDdl.TableName}\"",
            LogTableDdl.Statements(DbProviderKeys.SqLite)[0]);
        Assert.Equal(LogTableDdl.TableName, db.Model.FindEntityType(typeof(SystemLog))!.GetTableName());
    }

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

    [Theory]
    [InlineData(DbProviderKeys.SqLite)]
    [InlineData(DbProviderKeys.Npgsql)]
    public void TheOtherTwoTakeIfNotExistsDirectly(string provider)
    {
        Assert.Contains("CREATE TABLE IF NOT EXISTS", LogTableDdl.Statements(provider)[0]);
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

    // ------------------------------------------------------------- it actually runs

    [Fact]
    public async Task OnSqlite_TheDdlRuns_IsIdempotent_AndProducesTheShapeEfReads()
    {
        var directory = Path.Combine(Path.GetTempPath(), "gx-ddl-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "logs.db");

        try
        {
            await using (var db = new LogDbContext(new DbContextOptionsBuilder<LogDbContext>()
                             .UseSqlite($"Data Source={path}").Options))
            {
                // Twice, in one go: running the whole thing again is the idempotence assertion. A
                // missing IF NOT EXISTS throws "table SystemLogs already exists" on the second pass.
                for (var pass = 0; pass < 2; pass++)
                foreach (var statement in LogTableDdl.Statements(DbProviderKeys.SqLite))
                {
                    await db.Database.ExecuteSqlRawAsync(statement);
                }

                // The reading side, against a table nothing but this DDL created.
                Assert.Empty(await db.SystemLogs.OrderByDescending(x => x.Id).ToListAsync());
            }

            using var connection = new SqliteConnection($"Data Source={path}");
            connection.Open();

            using var columns = connection.CreateCommand();
            columns.CommandText = "SELECT name FROM pragma_table_info('SystemLogs')";
            var created = new List<string>();
            using (var reader = columns.ExecuteReader())
                while (reader.Read()) created.Add(reader.GetString(0));

            Assert.Equal(
                typeof(SystemLog).GetProperties().Select(p => p.Name).OrderBy(x => x),
                created.OrderBy(x => x));

            // Id must be the auto-generated key: the page pages and orders by it, and no sink writes
            // it. This is Pass 11B's STOP 1 in its SQLite form.
            using var key = connection.CreateCommand();
            key.CommandText = "SELECT pk FROM pragma_table_info('SystemLogs') WHERE name = 'Id'";
            Assert.Equal(1, Convert.ToInt32(key.ExecuteScalar()));

            using var indexes = connection.CreateCommand();
            indexes.CommandText = "SELECT name FROM sqlite_master WHERE type='index' AND tbl_name='SystemLogs'";
            var names = new List<string>();
            using (var reader = indexes.ExecuteReader())
                while (reader.Read()) names.Add(reader.GetString(0));

            Assert.Contains("IX_SystemLogs_Level", names);
            Assert.Contains("IX_SystemLogs_TimeStamp", names);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

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
    public async Task OnSqlite_TheExistenceQueryAnswersFalseThenTrue()
    {
        var directory = Path.Combine(Path.GetTempPath(), "gx-ddl-exists", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "logs.db");

        try
        {
            await using var db = new LogDbContext(new DbContextOptionsBuilder<LogDbContext>()
                .UseSqlite($"Data Source={path}").Options);

            async Task<int> Exists()
            {
                await using var command = db.Database.GetDbConnection().CreateCommand();
                command.CommandText = LogTableDdl.ExistsQuery(DbProviderKeys.SqLite);
                await db.Database.OpenConnectionAsync();
                try { return Convert.ToInt32(await command.ExecuteScalarAsync()); }
                finally { await db.Database.CloseConnectionAsync(); }
            }

            // A brand-new database must answer "no", or nothing would ever create the table.
            Assert.Equal(0, await Exists());

            foreach (var statement in LogTableDdl.Statements(DbProviderKeys.SqLite))
                await db.Database.ExecuteSqlRawAsync(statement);

            Assert.Equal(1, await Exists());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void AnUnsupportedProviderIsRefusedRatherThanSilentlyProducingNothing()
    {
        Assert.Throws<InvalidOperationException>(() => LogTableDdl.Statements("oracle"));
        Assert.Throws<InvalidOperationException>(() => LogTableDdl.ColumnNames("oracle"));
        Assert.Throws<InvalidOperationException>(() => LogTableDdl.ExistsQuery("oracle"));
    }
}
