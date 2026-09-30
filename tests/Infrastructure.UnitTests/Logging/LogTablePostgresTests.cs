using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Domain.Entities;
using CleanArchitecture.Blazor.Infrastructure.Extensions;
using CleanArchitecture.Blazor.Infrastructure.Persistence.Logging;
using CleanArchitecture.Blazor.Infrastructure.Services.Identity;
using Npgsql;
using Serilog;
using Serilog.Events;
using Xunit;

namespace CleanArchitecture.Blazor.Infrastructure.UnitTests.Logging;

/// <summary>
/// The log table on a real PostgreSQL server (<c>gx_test_&lt;project&gt;_infra_logs</c>): the DDL this application
/// creates it with, and the sink that writes into it.
/// </summary>
/// <remarks>
/// Before pass 47 the only tests that EXECUTED the log DDL, or wrote through a sink and read the row
/// back, ran on a SQLite file database (CO-157). The PostgreSQL statements were only ever inspected as strings. These run them.
/// <para>
/// The database is created if missing and never dropped. The TABLE is dropped at the start of the DDL
/// test, so its first run is a real CREATE and its second a real no-op.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public class LogTablePostgresTests
{
    private static readonly string Table =
        $"\"{LogTableDdl.NpgsqlSchema}\".\"{SerilogExtensions.NpgsqlTableName}\"";

    private static void Exec(NpgsqlConnection connection, string sql)
    {
        using var command = new NpgsqlCommand(sql, connection);
        command.ExecuteNonQuery();
    }

    private static object? Scalar(NpgsqlConnection connection, string sql)
    {
        using var command = new NpgsqlCommand(sql, connection);
        return command.ExecuteScalar();
    }

    private static List<string> Strings(NpgsqlConnection connection, string sql)
    {
        using var command = new NpgsqlCommand(sql, connection);
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read()) values.Add(reader.GetString(0));
        return values;
    }

    private static void RunDdl(NpgsqlConnection connection)
    {
        foreach (var statement in LogTableDdl.Statements(DbProviderKeys.Npgsql)) Exec(connection, statement);
    }

    [Fact]
    public void TheDdlRunsTwice_AndCreatesTheShapeEfReadsAndTheSinkWrites()
    {
        using var connection = new NpgsqlConnection(InfraTestDatabase.LogConnectionString);
        connection.Open();
        Exec(connection, $"DROP TABLE IF EXISTS {Table}");

        // The pre-check has to say "absent" on a database without the table, or nothing creates it.
        Assert.Equal(0L, Convert.ToInt64(Scalar(connection, LogTableDdl.ExistsQuery(DbProviderKeys.Npgsql))));

        // Twice: the second run is the idempotence assertion. A statement without IF NOT EXISTS
        // throws "already exists" here, which is the failure a restarted application would hit.
        RunDdl(connection);
        RunDdl(connection);

        Assert.Equal(1L, Convert.ToInt64(Scalar(connection, LogTableDdl.ExistsQuery(DbProviderKeys.Npgsql))));

        var columns = Strings(connection,
            $"SELECT column_name FROM information_schema.columns WHERE table_schema = '{LogTableDdl.NpgsqlSchema}' " +
            $"AND table_name = '{SerilogExtensions.NpgsqlTableName}' ORDER BY column_name");
        Assert.Equal(LogTableDdl.ColumnNames(DbProviderKeys.Npgsql).OrderBy(c => c, StringComparer.Ordinal), columns);

        // Every property EF reads has its snake_case column, and every column the sink writes exists.
        var expectedByModel = typeof(SystemLog).GetProperties().Select(p => ToSnakeCase(p.Name));
        Assert.Empty(expectedByModel.Except(columns));
        Assert.Empty(SerilogExtensions.BuildNpgsqlColumnWriters().Keys.Except(columns));

        // The types that were each got wrong once: a UTC instant, and a generated key.
        Assert.Equal("timestamp with time zone", Scalar(connection,
            "SELECT data_type FROM information_schema.columns WHERE table_name = 'system_logs' AND column_name = 'time_stamp'"));
        Assert.Equal("YES", Scalar(connection,
            "SELECT is_identity FROM information_schema.columns WHERE table_name = 'system_logs' AND column_name = 'id'"));

        var indexes = Strings(connection,
            $"SELECT indexname FROM pg_indexes WHERE schemaname = '{LogTableDdl.NpgsqlSchema}' " +
            $"AND tablename = '{SerilogExtensions.NpgsqlTableName}' ORDER BY indexname");
        Assert.Equal(["ix_system_logs_level", "ix_system_logs_time_stamp", "pk_system_logs"], indexes);
    }

    [Fact]
    public async Task TheSinkWritesTheAmbientTenant_IntoTenantId()
    {
        await using (var connection = new NpgsqlConnection(InfraTestDatabase.LogConnectionString))
        {
            await connection.OpenAsync();
            RunDdl(connection);
        }

        var marker = "tenant-round-trip-" + Guid.NewGuid().ToString("N");
        var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Enrich.WithUtcTime()
            .Enrich.WithUserInfo()
            .WriteTo.PostgreSQL(
                InfraTestDatabase.LogConnectionString,
                SerilogExtensions.NpgsqlTableName,
                SerilogExtensions.BuildNpgsqlColumnWriters(),
                LogEventLevel.Information,
                needAutoCreateTable: false,
                schemaName: LogTableDdl.NpgsqlSchema,
                useCopy: false,
                batchSizeLimit: 1)
            .CreateLogger();

        IUserContextAccessor accessor = new UserContextAccessor();
        using (accessor.Push(new UserContext("log-user", "logger", TenantId: "tenant-round-trip")))
        {
            logger.Information("{Marker} with a tenant", marker);
        }

        logger.Information("{Marker} without one", marker);

        // Wait for both rows while the logger is alive; disposing first can lose the batch.
        var rows = new List<(string Message, string? TenantId)>();
        for (var attempt = 0; attempt < 100 && rows.Count < 2; attempt++)
        {
            rows = await ReadRowsAsync(marker);
            if (rows.Count < 2) await Task.Delay(100);
        }

        logger.Dispose();

        Assert.Equal(2, rows.Count);
        Assert.Equal("tenant-round-trip", rows.Single(r => r.Message.Contains("with a tenant")).TenantId);
        Assert.Null(rows.Single(r => r.Message.Contains("without one")).TenantId);
    }

    private static async Task<List<(string Message, string? TenantId)>> ReadRowsAsync(string marker)
    {
        await using var connection = new NpgsqlConnection(InfraTestDatabase.LogConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT message, tenant_id FROM {Table} WHERE message LIKE @marker", connection);
        command.Parameters.AddWithValue("marker", "%" + marker + "%");
        await using var reader = await command.ExecuteReaderAsync();

        var rows = new List<(string, string?)>();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
        }

        return rows;
    }

    /// <summary>The name <c>UseSnakeCaseNamingConvention()</c> gives a property (enough for SystemLog's).</summary>
    private static string ToSnakeCase(string name)
    {
        var result = new System.Text.StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c) && i > 0 && (!char.IsUpper(name[i - 1]) ||
                                             (i + 1 < name.Length && !char.IsUpper(name[i + 1]))))
            {
                result.Append('_');
            }

            result.Append(char.ToLowerInvariant(c));
        }

        return result.ToString();
    }
}
