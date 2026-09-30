using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Domain.Entities;
using CleanArchitecture.Blazor.Infrastructure.Extensions;
using CleanArchitecture.Blazor.Infrastructure.Persistence.Logging;
#if (UsePostgreSql)
using NpgsqlTypes;
#endif
using Serilog;
using Serilog.Core;
using Serilog.Events;
#if (UsePostgreSql)
using Serilog.Sinks.PostgreSQL;
using Serilog.Sinks.PostgreSQL.ColumnWriters;
#endif
using Xunit;

namespace CleanArchitecture.Blazor.Infrastructure.UnitTests.Logging;

/// <summary>
/// Every sink records the log timestamp in UTC.
/// </summary>
/// <remarks>
/// This is one rule with three different spellings, and each provider has now got it wrong at least
/// once:
/// <list type="bullet">
/// <item><b>SQLite</b> - <c>storeTimestampInUtc</c> defaults to <c>false</c> and was not being
/// passed (fixed in Pass 11B);</item>
/// <item><b>PostgreSQL</b> - <c>TimestampColumnWriter</c> writes the event's own timestamp as LOCAL
/// time (fixed in Pass 11D);</item>
/// <item><b>SQL Server</b> - correct, via <c>ConvertToUtc = true</c>, and pinned here so it stays
/// that way.</item>
/// </list>
/// Nothing else in the system works in local time: <c>UtcTimestampEnricher</c> enriches in UTC, and
/// <c>SystemLogAdvancedSpecification</c> builds its TODAY and LAST_30_DAYS windows from
/// <c>DateTime.UtcNow</c>. A local-time column read through a UTC filter mis-windows by the host's
/// offset - quietly, and only for viewers in some time zones, which is what let it survive three
/// passes.
/// <para>
/// The configuration assertions here would each have caught their own regression. The live
/// write-and-read-back is <c>Application.UnitTests/Logging/SinkTimestampAcceptanceTests</c>, on the
/// PostgreSQL server named by <c>GX_TEST_PG</c>; the SQLite file round trip that was here went with
/// SQLite in pass 47.
/// </para>
/// </remarks>
public class SinkTimestampTests
{
    // ------------------------------------------------------- the configuration, all three providers

#if (UsePostgreSql)
    [Fact]
    public void ThePostgresSink_ReadsTheUtcEnrichedProperty_NotTheEventsOwnTimestamp()
    {
        // The Pass 11D regression, pinned at its exact cause. TimestampColumnWriter would compile,
        // run, and write the right-looking value in the wrong time zone; only the writer's identity
        // distinguishes it.
        var writer = SerilogExtensions.BuildNpgsqlColumnWriters()["time_stamp"];

        var single = Assert.IsType<SinglePropertyColumnWriter>(writer);
        Assert.Equal("TimeStamp", single.Name);
        Assert.Equal(PropertyWriteMethod.Raw, single.WriteMethod);

        // TimestampTz, not Timestamp. This is the half of Pass 14B that lives on the driver side:
        // LogTableDdl declares time_stamp as timestamptz and the enricher publishes Kind=Utc, and
        // Npgsql binds by the DECLARED type, so a writer still saying Timestamp rejects the value.
        Assert.Equal(NpgsqlDbType.TimestampTz, single.DbType);
    }

#endif
    [Fact]
    public void ThePropertyThePostgresSinkReads_IsTheOneTheEnricherWritesInUtc()
    {
        // The other half of the pairing: the writer above names a property, and this is what proves
        // the property it names is produced, and produced in UTC. If the enricher stopped adding it,
        // the column would go null rather than wrong - a different failure, equally silent.
        var logEvent = new LogEvent(
            new DateTimeOffset(2026, 8, 29, 11, 30, 0, TimeSpan.FromHours(1)),
            LogEventLevel.Information,
            exception: null,
            new MessageTemplate("m", []),
            []);

        new UtcTimestampEnricher().Enrich(logEvent, new PropertyFactory());

        var value = Assert.IsType<ScalarValue>(logEvent.Properties["TimeStamp"]);
        // The UTC instant, with Kind=Utc. Pass 14B flipped this from Unspecified, and the flip is
        // the assertion: timestamptz ACCEPTS Kind=Utc and REJECTS Kind=Unspecified, which is the
        // exact inverse of the "timestamp without time zone" column Pass 11D was writing into. If
        // the enricher reverts to Unspecified while LogTableDdl still says timestamptz, every
        // PostgreSQL log write fails at bind time - and this test fails first.
        Assert.Equal(new DateTime(2026, 8, 29, 10, 30, 0, DateTimeKind.Utc), value.Value);
        Assert.Equal(DateTimeKind.Utc, ((DateTime)value.Value!).Kind);
    }

    /// <summary>The smallest thing that satisfies the enricher's signature.</summary>
    private sealed class PropertyFactory : ILogEventPropertyFactory
    {
        public LogEventProperty CreateProperty(string name, object? value, bool destructureObjects = false) =>
            new(name, new ScalarValue(value));
    }

#if (UseSqlServer)
    [Fact]
    public void TheSqlServerSink_ConvertsItsTimestampToUtc()
    {
        var options = SerilogExtensions.BuildSqlServerColumnOptions();

        Assert.True(options.TimeStamp.ConvertToUtc);
        Assert.Equal("TimeStamp", options.TimeStamp.ColumnName);
    }

#endif
}
