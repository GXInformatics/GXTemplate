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
    /// <b>No sink creates its table.</b> Auto-create is off on all three providers, which is where
    /// this started before Pass 11 and where Pass 11C returned it: Pass 11 briefly made the sinks
    /// responsible for the table in the new log database, and Pass 11B measured that PostgreSQL's
    /// auto-create cannot emit an identity column and SQL Server's runs inside
    /// <c>WebApplicationBuilder.Build()</c>, crashing startup when the log server is unreachable.
    /// <see cref="Persistence.Logging.LogTableDdl"/> owns the schema instead, and the sinks are only
    /// writers again.
    /// <para>
    /// The column configuration below is therefore the INSERT, not the DDL. The two must still
    /// agree, in both directions: a writer naming a column the DDL does not create fails silently at
    /// write time. <c>SinkColumnDriftTests</c> holds the entity, the DDL and each sink's writers to
    /// one another, per provider.
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

            // Also false, and this one must stay false. The shape was never the problem here: with
            // it true the sink creates exactly the right table, all eleven columns including Id,
            // measured against LocalDB in Pass 11B. The problem is WHEN. This sink performs its
            // CREATE TABLE in its CONSTRUCTOR, which Serilog runs during
            // WebApplicationBuilder.Build() - so with the log server unreachable the exception comes
            // out of Build() and the application does not start at all, trading a best-effort
            // logging failure for a total outage. Wrapping in WriteTo.Async does not help: the inner
            // sink is still constructed eagerly.
            //
            // LogTableDdl creates the table instead, after the host is built, where a failure can be
            // reported and survived. That is Pass 11C's resolution of the Pass 11B STOP.
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
    /// This describes the sink's INSERT only. Since Pass 11C the table itself comes from
    /// <see cref="Persistence.Logging.LogTableDdl"/>, and <c>SinkColumnDriftTests</c> holds the two
    /// to each other so a writer can never name a column the DDL does not create.
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
    /// <c>SinkColumnDriftTests</c> checks the configuration the sink
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
            // Reads the UTC property WithUtcTime() adds, NOT the log event's own timestamp.
            //
            // TimestampColumnWriter writes logEvent.Timestamp, a DateTimeOffset, as LOCAL time - and
            // that made PostgreSQL the only provider recording local time into a column everything
            // else reads as UTC. UtcTimestampEnricher enriches in UTC, the MSSQL sink is configured
            // ConvertToUtc = true, SQLite was given storeTimestampInUtc: true in Pass 11B, and
            // SystemLogAdvancedSpecification builds its TODAY and LAST_30_DAYS windows from
            // DateTime.UtcNow. A local-time column read through a UTC filter silently mis-windows by
            // the host's offset: west of Greenwich the page's default view hides the most recent
            // rows, east of it the TODAY view leaks into tomorrow.
            //
            // The enricher already publishes exactly the value needed, so this reads that property
            // instead of re-deriving it. Raw, not ToString, so Npgsql binds the DateTime rather than
            // a rendered string. Pinned for all three providers by SinkTimestampTests.
            //
            // TimestampTz, not Timestamp, since Pass 14B. The column is timestamptz in LogTableDdl
            // and the enricher now publishes Kind=Utc; declaring Timestamp here would have Npgsql
            // reject the bind outright. All three move together or none of them works.
            {
                "time_stamp",
                new SinglePropertyColumnWriter("TimeStamp", PropertyWriteMethod.Raw, NpgsqlDbType.TimestampTz)
            },
            { "exception", new ExceptionColumnWriter(NpgsqlDbType.Text) },

            // Text, not Varchar. These were Varchar while EF's migration owned the DDL and the
            // declared type only affected parameter binding. It stops being cosmetic the moment this
            // sink creates the table: it renders an unsized Varchar as character varying(50), and
            // properties and log_event are serialised JSON documents routinely longer than that.
            // LogTableDdl now creates the column as text to match.
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

            // False, and this one must stay false. This sink's auto-create cannot produce a table
            // LogDbContext can read: its DDL is generated purely from the dictionary above, and a
            // ColumnWriter dictionary has no way to express an identity column, so the table it
            // creates has NO id at all - while SystemLog.Id is the EF key and the SystemLogs page's
            // default sort. It also renders an unsized Varchar as character varying(50) and
            // time_stamp as timestamp WITH time zone, neither of which matches what is read back.
            //
            // LogTableDdl creates the table instead, in PostgreSQL's own words, with a real
            // GENERATED BY DEFAULT AS IDENTITY column. That is Pass 11C's resolution of the Pass 11B
            // STOP.
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
            // False, like the other two, since Pass 11C. This sink's auto-create worked - its DDL
            // is exactly the SystemLog column set - but leaving it on would mean the log table came
            // from the sink on SQLite and from LogTableDdl everywhere else, so a shape defect could
            // only ever be found on two of the three providers. One creator, all three providers.
            needAutoCreateTable: false
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

/// <summary>
/// Publishes the log event's timestamp as a UTC <c>TimeStamp</c> property.
/// </summary>
/// <remarks>
/// Public so SinkTimestampTests can assert the value it produces. The PostgreSQL sink reads this
/// property by name, so what it puts here is what that provider stores.
/// </remarks>
public class UtcTimestampEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory pf)
    {
        // The UTC instant, published with Kind=Utc - which is what it actually is.
        //
        // This REVERTS Pass 11D, which published it as Kind=Unspecified. That was a workaround, and
        // it is worth saying plainly what it was working around rather than leaving the next reader
        // to reconstruct it. The log table's time_stamp column was "timestamp without time zone",
        // because Infrastructure's UseDatabase set Npgsql.EnableLegacyTimestampBehavior and that
        // switch makes "timestamp without time zone" the default mapping for DateTime. Npgsql
        // refuses to bind a Kind=Utc DateTime to that type, so the enricher had to lie about the
        // Kind - and Unspecified was at least the honest Kind for a column of that type. It also
        // meant the sink no longer depended on WHEN some other component happened to set a global
        // AppContext switch, which was the deeper problem.
        //
        // Pass 14B removed the switch. The column is timestamptz now (LogTableDdl), the writer
        // declares NpgsqlDbType.TimestampTz (BuildNpgsqlColumnWriters), and timestamptz accepts
        // Kind=Utc and REJECTS Kind=Unspecified - the exact inverse of the constraint the workaround
        // existed for. So the workaround is not merely unnecessary now, it would break the write.
        // The three change together: DDL, writer, enricher.
        var utc = DateTime.SpecifyKind(logEvent.Timestamp.UtcDateTime, DateTimeKind.Utc);
        logEvent.AddOrUpdateProperty(pf.CreateProperty("TimeStamp", utc));
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
