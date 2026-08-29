using System.Collections.ObjectModel;
using System.Data;
using CleanArchitecture.Blazor.Infrastructure.Configurations;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using NpgsqlTypes;
using Serilog;
using Serilog.Configuration;
using Serilog.Core;
using Serilog.Events;
using Serilog.Sinks.MSSqlServer;
using Serilog.Sinks.PostgreSQL;
using Serilog.Sinks.PostgreSQL.ColumnWriters;
using ColumnOptions = Serilog.Sinks.MSSqlServer.ColumnOptions;
using CleanArchitecture.Blazor.Application.Common.Constants;

namespace CleanArchitecture.Blazor.Infrastructure.Extensions;

public static class SerilogExtensions
{
    public static void RegisterSerilog(this WebApplicationBuilder builder)
    {
        Serilog.Debugging.SelfLog.Enable(msg => Console.WriteLine(msg));
        builder.Host.UseSerilog((context, configuration) =>
            configuration.ReadFrom.Configuration(context.Configuration)
                .MinimumLevel.Override("Microsoft", LogEventLevel.Error)
                .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Error)
                .MinimumLevel.Override("MudBlazor", LogEventLevel.Information)
                .MinimumLevel.Override("Serilog", LogEventLevel.Information)
                .MinimumLevel.Override("Microsoft.EntityFrameworkCore.AddOrUpdate", LogEventLevel.Error)
                .MinimumLevel.Override("Hangfire.BackgroundJobServer", LogEventLevel.Error)
                .MinimumLevel.Override("Hangfire.InMemory.InMemoryStorage", LogEventLevel.Error)
                .MinimumLevel.Override("Hangfire.Server.BackgroundServerProcess", LogEventLevel.Error)
                .MinimumLevel.Override("Hangfire.Server.ServerHeartbeatProcess", LogEventLevel.Error)
                .MinimumLevel.Override("Hangfire.Processing.BackgroundExecution", LogEventLevel.Error)
                .MinimumLevel.Override("ZiggyCreatures.Caching.Fusion.FusionCache", LogEventLevel.Error)
                .Enrich.FromLogContext()
                .Enrich.WithUtcTime()
                .Enrich.WithUserInfo()
                // The file sink persists; the bootstrap banner must not. See BootstrapSecretProperty.
                .WriteTo.Logger(lc => lc
                    .Filter.ByExcluding(IsExcludedFromFileSink)
                    .WriteTo.Async(wt => wt.File("./log/log-.txt", rollingInterval: RollingInterval.Day)))
                .WriteTo.Async(wt =>
                    wt.Console(
                        outputTemplate:
                        "[{Timestamp:HH:mm:ss} {Level:u3} {ClientIp}] {Message:lj}{NewLine}{Exception}"))
                .ApplyConfigPreferences(context.Configuration)
        );
    }

    /// <summary>
    /// Marks a log event as carrying a secret that must never leave the console - today, the one-off
    /// administrator password written at bootstrap.
    /// <para>
    /// The marker is a property rather than a substring of the message on purpose: matching on
    /// wording would silently stop filtering the first time someone rephrases the banner, and a
    /// filter that fails open is worse than none. The producing side sets it via
    /// <c>ILogger.BeginScope</c>, which Serilog surfaces as an event property because
    /// <c>Enrich.FromLogContext()</c> is configured above.
    /// </para>
    /// </summary>
    public const string BootstrapSecretProperty = "BootstrapSecret";

    /// <summary>
    /// Marks a log event as a complaint ABOUT the log database, which therefore must not be routed
    /// INTO the log database.
    /// </summary>
    /// <remarks>
    /// Without this, the startup check's "cannot reach the log database" error is handed to a sink
    /// whose whole job is writing to the database that cannot be reached. It would be dropped
    /// silently - a loud failure wearing a silent one's clothes, and the single most likely way for
    /// this arrangement to fail unnoticed. Console and file carry these events; the database sink
    /// never sees them, and <c>LogDatabaseDiagnosticRoutingTests</c> asserts that rather than
    /// assuming it.
    /// <para>
    /// A property rather than a message substring, for the same reason as
    /// <see cref="BootstrapSecretProperty"/>: matching on wording fails open the first time somebody
    /// rewords the message.
    /// </para>
    /// </remarks>
    public const string LogDatabaseDiagnosticProperty = "LogDatabaseDiagnostic";

    private static bool CarriesBootstrapSecret(LogEvent logEvent) =>
        logEvent.Properties.ContainsKey(BootstrapSecretProperty);

    private static bool IsLogDatabaseDiagnostic(LogEvent logEvent) =>
        logEvent.Properties.ContainsKey(LogDatabaseDiagnosticProperty);

    /// <summary>
    /// Events the FILE sink drops. The file persists, so the bootstrap password must not reach it -
    /// but a complaint about the log database must, because the file is where it will be read.
    /// </summary>
    public static bool IsExcludedFromFileSink(LogEvent logEvent) => CarriesBootstrapSecret(logEvent);

    /// <summary>
    /// Events the DATABASE sink drops: the bootstrap password, and anything reporting on the health
    /// of the very database this sink writes to.
    /// </summary>
    public static bool IsExcludedFromDatabaseSink(LogEvent logEvent) =>
        CarriesBootstrapSecret(logEvent) || IsLogDatabaseDiagnostic(logEvent);

    private static void ApplyConfigPreferences(this LoggerConfiguration serilogConfig, IConfiguration configuration)
    {
        // The database sink writes to a table the SystemLogs page reads back, so it outlives the
        // console and drops the banner. The console sink above is deliberately the only one that
        // keeps it.
        serilogConfig.WriteTo.Logger(lc =>
        {
            lc.Filter.ByExcluding(IsExcludedFromDatabaseSink);
            WriteToDatabase(lc, configuration);
        });
    }

    /// <summary>
    /// Configures the database sink, which writes to the SEPARATE log database named by
    /// <see cref="DatabaseSettings.LogConnectionString"/> - never to the business database.
    /// </summary>
    /// <remarks>
    /// <b>The sink now creates its own table.</b> Until Pass 11 every provider was told not to
    /// (<c>AutoCreateSqlTable = false</c>, <c>needAutoCreateTable: false</c>) because EF's migration
    /// created it in the business database and the sink was only a writer. The log database has no
    /// migration chain and no EF-owned schema, so that ownership moves to the sink.
    /// <para>
    /// This does not make the table's shape unknown to the reading side. Each provider's table is
    /// created from the very column configuration written below - the same <c>ColumnOptions</c> and
    /// <c>ColumnWriter</c> dictionaries that already had to agree with the <c>SystemLog</c> entity
    /// for the sink's INSERT to work. The shape is the entity, expressed twice; the risk is drift
    /// between the two expressions, and drift is what <c>SinkColumnDriftTests</c> pins.
    /// </para>
    /// <para>
    /// <b>Auto-create only ever CREATES. It never ALTERS.</b> A log table that predates a new
    /// property on <c>SystemLog</c> keeps its old columns, and no sink will widen it; the drift test
    /// compares code against code and cannot see a deployed schema. Adding a property to the entity
    /// therefore carries a manual ALTER on every deployed log database. This is a stated limitation
    /// of choosing auto-create over a second migration chain, recorded in the README.
    /// </para>
    /// <para>
    /// An absent connection string configures NO database sink - and specifically does not fall back
    /// to <see cref="DatabaseSettings.ConnectionString"/>, which would put the log table straight
    /// back into the business database in the one configuration nobody would think to check.
    /// <c>LogDatabaseStartupCheck</c> is what says so out loud.
    /// </para>
    /// </remarks>
    private static void WriteToDatabase(LoggerConfiguration serilogConfig, IConfiguration configuration)
    {
        var dbProvider =
            configuration.GetValue<string>($"{nameof(DatabaseSettings)}:{nameof(DatabaseSettings.DBProvider)}");

        // The LOG connection string. Both databases share DBProvider by construction - they are
        // properties of one settings object, so a mismatch cannot be expressed.
        var connectionString =
            configuration.GetValue<string>($"{nameof(DatabaseSettings)}:{nameof(DatabaseSettings.LogConnectionString)}");

        switch (dbProvider)
        {
            case DbProviderKeys.SqlServer:
                WriteToSqlServer(serilogConfig, connectionString);
                break;
            case DbProviderKeys.Npgsql:
                WriteToNpgsql(serilogConfig, connectionString);
                break;
            case DbProviderKeys.SqLite:
                WriteToSqLite(serilogConfig, connectionString);
                break;
        }
    }

   

    private static void WriteToSqlServer(LoggerConfiguration serilogConfig, string? connectionString)
    {
        if (string.IsNullOrEmpty(connectionString)) return;

        MSSqlServerSinkOptions sinkOpts = new()
        {
            TableName = "SystemLogs",
            SchemaName = "dbo",

            // Deliberately still false. The sink CAN create the database, but enabling it would
            // require the application's SQL login to hold CREATE DATABASE rights in production - a
            // privilege it otherwise never needs - and it would make the operational story
            // provider-dependent for no gain, since the PostgreSQL sink cannot do the same. Creating
            // the log database is a one-time setup step, documented in the README, that PostgreSQL
            // deployments must perform regardless.
            AutoCreateSqlDatabase = false,

            // ALSO still false, against the ratified Pass 11 design, and for a harder reason than
            // the PostgreSQL one.
            //
            // The shape is not the problem here: with this true the sink creates exactly the right
            // table, all eleven columns including Id, measured against LocalDB. The problem is WHEN.
            // This sink performs its CREATE TABLE in its constructor, which Serilog runs during
            // WebApplicationBuilder.Build() - so if the log database is unreachable at startup the
            // exception comes out of Build(), and the application does not start at all.
            //
            // That trades a best-effort logging failure for a hard outage of the whole application,
            // which is the exact inversion of the ratified rule that a missing or broken log
            // database must never stop the business application serving. Wrapping in WriteTo.Async
            // does not help: the inner sink is still constructed eagerly.
            //
            // SQLite is unaffected because that sink creates its table lazily, on first write.
            // This is a Pass 11B STOP awaiting a decision - see pass11b-report.md §D.
            AutoCreateSqlTable = false,

            BatchPostingLimit = 100,
            BatchPeriod = new TimeSpan(0, 0, 20),

        };

        serilogConfig.WriteTo.Async(wt => wt.MSSqlServer(
            connectionString,
            sinkOpts,
            columnOptions: BuildSqlServerColumnOptions()
        ));
    }

    /// <summary>
    /// The SQL Server sink's column set. Extracted from <see cref="WriteToSqlServer"/> so that
    /// <c>SinkColumnDriftTests</c> checks the configuration the sink actually uses rather than a
    /// copy of it that could quietly diverge.
    /// </summary>
    /// <remarks>
    /// With <c>AutoCreateSqlTable = true</c> this is now the DDL as well as the INSERT: the table
    /// the SystemLogs page reads is created from exactly these columns.
    /// </remarks>
    public static ColumnOptions BuildSqlServerColumnOptions()
    {
        ColumnOptions columnOpts = new()
        {
            Store = new Collection<StandardColumn>
            {
                StandardColumn.Id,
                StandardColumn.TimeStamp,
                StandardColumn.Level,
                StandardColumn.LogEvent,
                StandardColumn.Exception,
                StandardColumn.Message,
                StandardColumn.MessageTemplate,
                StandardColumn.Properties
            },
            AdditionalColumns = new Collection<SqlColumn>
            {
                new()
                {
                    ColumnName = "ClientIP", PropertyName = "ClientIP",AllowNull=true, DataType = SqlDbType.NVarChar, DataLength = 64
                },
                new()
                {
                    ColumnName = "UserName", PropertyName = "UserName",AllowNull=true, DataType = SqlDbType.NVarChar
                },
                new()
                {
                    ColumnName = "ClientAgent", PropertyName = "ClientAgent",AllowNull=true, DataType = SqlDbType.NVarChar
                }
            },
            TimeStamp = { ConvertToUtc = true, ColumnName = "TimeStamp" },
            LogEvent = { DataLength = -1 }
        };
        columnOpts.PrimaryKey = columnOpts.Id;
        columnOpts.TimeStamp.NonClusteredIndex = true;
        return columnOpts;
    }

    /// <summary>The log table's name on PostgreSQL, where the snake_case convention applies.</summary>
    public const string NpgsqlTableName = "system_logs";

    /// <summary>
    /// The PostgreSQL sink's column writers. Extracted from <see cref="WriteToNpgsql"/> so that
    /// <c>SinkColumnDriftTests</c> and <c>PostgresSinkDdlTests</c> check the configuration the sink
    /// actually uses rather than a copy of it.
    /// </summary>
    /// <remarks>
    /// The keys are the snake_case column names <c>LogDbContext</c>'s
    /// <c>UseSnakeCaseNamingConvention()</c> produces for the <see cref="SystemLog"/> entity.
    /// </remarks>
    public static IDictionary<string, ColumnWriterBase> BuildNpgsqlColumnWriters() =>
        new Dictionary<string, ColumnWriterBase>
        {
            { "message", new RenderedMessageColumnWriter(NpgsqlDbType.Text) },
            { "message_template", new MessageTemplateColumnWriter(NpgsqlDbType.Text) },
            { "level", new LevelColumnWriter(true, NpgsqlDbType.Varchar) },
            { "time_stamp", new TimestampColumnWriter(NpgsqlDbType.Timestamp) },
            { "exception", new ExceptionColumnWriter(NpgsqlDbType.Text) },

            // Text, not Varchar. These were Varchar while EF's migration owned the DDL and the
            // declared type only affected parameter binding. It stops being cosmetic the moment this
            // sink creates the table: it renders an unsized Varchar as character varying(50), and
            // properties and log_event are serialised JSON documents routinely longer than that.
            // PostgresSinkDdlTests pins the rendered DDL.
            { "properties", new PropertiesColumnWriter(NpgsqlDbType.Text) },
            { "log_event", new LogEventSerializedColumnWriter(NpgsqlDbType.Text) },
            { "user_name", new SinglePropertyColumnWriter("UserName", PropertyWriteMethod.Raw, NpgsqlDbType.Text) },
            { "client_ip", new SinglePropertyColumnWriter("ClientIP", PropertyWriteMethod.Raw, NpgsqlDbType.Text) },
            {
                "client_agent",
                new SinglePropertyColumnWriter("ClientAgent", PropertyWriteMethod.ToString, NpgsqlDbType.Text)
            }
        };

    private static void WriteToNpgsql(LoggerConfiguration serilogConfig, string? connectionString)
    {
        if (string.IsNullOrEmpty(connectionString)) return;

        serilogConfig.WriteTo.Async(wt => wt.PostgreSQL(
            connectionString,
            NpgsqlTableName,
            BuildNpgsqlColumnWriters(),
            LogEventLevel.Information,

            // STILL FALSE, against the ratified Pass 11 design, because this sink's auto-create
            // cannot produce a table LogDbContext can read. Its DDL is generated purely from the
            // dictionary above, and a ColumnWriter dictionary has no way to express an identity
            // column - so the table it creates has NO id at all, while SystemLog.Id is the EF key
            // and the SystemLogs page orders by it by default.
            //
            // MSSQL and SQLite are unaffected: both were measured to auto-create the full column
            // set including Id. This is a Pass 11B STOP awaiting a decision - see pass11b-report.md
            // §D. Until it is resolved a PostgreSQL deployment has no log table, and the startup
            // check and the SystemLogs page both report the log database unusable rather than
            // pretending otherwise.
            needAutoCreateTable: false,
            schemaName: "public",
            useCopy: false
        ));
    }


    /// <summary>
    /// Mirrors the MSSQL and PostgreSQL sinks: the sink writes into the separate log database, and
    /// now creates the SystemLogs table there, because the log database has no EF migration chain.
    /// Pass 7-2 §H extracted this fork's DDL and found it is exactly the <see cref="SystemLog"/>
    /// column set, so the created table is the shape <c>LogDbContext</c> reads.
    /// </summary>
    private static void WriteToSqLite(LoggerConfiguration serilogConfig, string? connectionString)
    {
        if (string.IsNullOrEmpty(connectionString)) return;

        // The sink takes a file path, not a connection string. Reading it back through the builder
        // resolves whatever form the configured log connection string takes to the one file.
        var sqlPath = new SqliteConnectionStringBuilder(connectionString).DataSource;
        if (string.IsNullOrEmpty(sqlPath)) return;

        const string tableName = "SystemLogs";
        serilogConfig.WriteTo.Async(wt => wt.SQLite(
            sqlPath,
            tableName,
            LogEventLevel.Information,
            // storeTimestampInUtc defaults to FALSE, which was leaving this provider - alone among
            // the three - writing local timestamps into a column everything else reads as UTC:
            // UtcTimestampEnricher enriches in UTC, the MSSQL sink is told ConvertToUtc = true, and
            // SystemLogAdvancedSpecification builds its TODAY and LAST_30_DAYS windows from
            // DateTime.UtcNow. West of Greenwich that silently hid recent rows from the page's
            // default view. Pinned by SqliteSinkTimestampTests.
            storeTimestampInUtc: true,
            // The log database has no EF migration chain; the sink owns this table.
            needAutoCreateTable: true
        ));
    }


    public static LoggerConfiguration WithUtcTime(this LoggerEnrichmentConfiguration enrichmentConfiguration)
    {
        
        return enrichmentConfiguration.With<UtcTimestampEnricher>();
    }
    public static LoggerConfiguration WithUserInfo(this LoggerEnrichmentConfiguration enrichmentConfiguration)
    {
        return enrichmentConfiguration.With<UserInfoEnricher>();
    }
}

internal class UtcTimestampEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory pf)
    {
        logEvent.AddOrUpdateProperty(pf.CreateProperty("TimeStamp", logEvent.Timestamp.UtcDateTime));
    }
}
internal class UserInfoEnricher : ILogEventEnricher
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    public UserInfoEnricher() : this(new HttpContextAccessor())
    {
    }
    //Dependency injection can be used to retrieve any service required to get a user or any data.
    //Here, I easily get data from HTTPContext
    public UserInfoEnricher(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var userName = _httpContextAccessor.HttpContext?.User?.Identity?.Name ?? "";
        var headers = _httpContextAccessor.HttpContext?.Request?.Headers;
        var clientIp = headers != null && headers.ContainsKey("X-Forwarded-For")
        ? headers["X-Forwarded-For"].ToString().Split(',').First().Trim()
        : _httpContextAccessor.HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "";
        var clientAgent = headers != null && headers.ContainsKey("User-Agent")
            ? headers["User-Agent"].ToString()
            : "";

        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("UserName", userName));
        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("ClientIP", clientIp));
        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("ClientAgent", clientAgent));
    }
}
