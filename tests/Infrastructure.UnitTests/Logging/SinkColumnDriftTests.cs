using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Domain.Entities;
using CleanArchitecture.Blazor.Infrastructure.Extensions;
using CleanArchitecture.Blazor.Infrastructure.Persistence.Logging;
using Xunit;

namespace CleanArchitecture.Blazor.Infrastructure.UnitTests.Logging;

/// <summary>
/// The log table's shape is expressed three times. These tests hold the three to each other, per
/// provider.
/// </summary>
/// <remarks>
/// The three expressions are:
/// <list type="number">
/// <item>the <see cref="SystemLog"/> <b>entity</b> - what EF reads back, and therefore what the
/// SystemLogs page can display;</item>
/// <item><see cref="LogTableDdl"/> - what this application actually creates, since Pass 11C took
/// that job away from the sinks;</item>
/// <item>each sink's <b>column configuration</b> - what Serilog writes.</item>
/// </list>
/// Any pair of these can drift, and each drift fails somewhere different and quietly:
/// <list type="bullet">
/// <item>a property with no DDL column ⇒ the page throws on a column that is not there;</item>
/// <item>a property with no sink writer ⇒ the column exists and is always null;</item>
/// <item><b>a sink writer with no DDL column ⇒ every INSERT fails</b>, asynchronously, into SelfLog,
/// while the application looks perfectly healthy. That is precisely the disease Pass 11B found on
/// PostgreSQL, where the sink's own auto-create produced a table with no <c>id</c>; it is worth a
/// test in its own right rather than trusting that it cannot happen again.</item>
/// </list>
/// <para>
/// <b>Containment, not equality.</b> Extra DDL columns are permitted - today there are none, and
/// <c>TheDdlHasNoColumnsNobodyUses</c> says so rather than leaving it implied.
/// </para>
/// <para>
/// <b>What none of this can see.</b> It compares code with code. The DDL creates a table and never
/// alters one, so a log database deployed before a property was added keeps its old columns and no
/// test here will know. Adding a property to <see cref="SystemLog"/> carries a manual ALTER on every
/// deployed log database.
/// </para>
/// </remarks>
public class SinkColumnDriftTests
{
    /// <summary>The properties EF maps, which are exactly the ones a query can ask a database for.</summary>
    private static string[] EntityProperties =>
        typeof(SystemLog).GetProperties().Select(p => p.Name).ToArray();

    /// <summary>
    /// PostgreSQL is addressed by the snake_case names <c>UseSnakeCaseNamingConvention()</c>
    /// produces, so comparisons against it have to be made in the same alphabet.
    /// </summary>
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

    /// <summary>The entity's properties, spelled the way the given provider spells columns.</summary>
    private static string[] EntityColumnsFor(string provider) =>
        provider == DbProviderKeys.Npgsql
            ? EntityProperties.Select(ToSnakeCase).ToArray()
            : EntityProperties;

    /// <summary>
    /// The columns the SQLite sink writes, as a LITERAL list rather than as "whatever the entity
    /// has".
    /// </summary>
    /// <remarks>
    /// It used to be written as <c>EntityProperties</c>, on the grounds that the fork's fixed
    /// statement happened to match the entity - which was true when it was written and made this
    /// provider's three comparisons circular: the sink was defined as the entity, so the entity
    /// could never fail to match the sink. Pass 24 added <c>SystemLog.TenantId</c> and the sink went
    /// on writing ten columns, silently, exactly as the circularity guaranteed it could.
    /// <para>
    /// These are the sink's own words, read out of
    /// <c>Blazor.Serilog.Sinks.SQLite</c>: its INSERT is
    /// <c>VALUES (@timeStamp, @level, @exception, @message, @properties, @messageTemplate,
    /// @logEvent, @userName, @clientIP, @clientAgent)</c>, with <c>Id</c> supplied by the table.
    /// There is no <c>AdditionalColumns</c> or writer dictionary to extend - unlike the other two
    /// sinks, this one's column set is not configurable at all.
    /// </para>
    /// </remarks>
    private static readonly string[] SqliteSinkColumns =
    [
        "Id", "TimeStamp", "Level", "Exception", "Message", "Properties",
        "MessageTemplate", "LogEvent", "UserName", "ClientIP", "ClientAgent"
    ];

    /// <summary>
    /// Entity properties that a given provider's sink is known and accepted not to write.
    /// </summary>
    /// <remarks>
    /// <b>An allow-list, so the gap is stated rather than absent.</b> The column still exists in the
    /// DDL on every provider - it must, or EF's read of <see cref="SystemLog"/> would fail with "no
    /// such column" on that provider - and it is still written by the SQL Server and PostgreSQL
    /// sinks. On SQLite it is simply always null, because that sink cannot be given another column.
    /// <para>
    /// SQLite is the no-server development and test provider; the two providers a GX installation
    /// actually runs on both record the tenant. That is the trade, and it is recorded here rather
    /// than discovered later by somebody wondering why a column is empty.
    /// </para>
    /// </remarks>
    private static string[] SinkCannotWrite(string provider) => provider switch
    {
        DbProviderKeys.SqLite => [nameof(SystemLog.TenantId)],
        _ => []
    };

    /// <summary>What that provider's sink writes.</summary>
    private static string[] SinkColumnsFor(string provider) => provider switch
    {
        DbProviderKeys.SqlServer => SqlServerSinkColumns(),
        DbProviderKeys.Npgsql => SerilogExtensions.BuildNpgsqlColumnWriters().Keys.ToArray(),
        DbProviderKeys.SqLite => SqliteSinkColumns,
        _ => throw new InvalidOperationException(provider)
    };

    private static string[] SqlServerSinkColumns()
    {
        var options = SerilogExtensions.BuildSqlServerColumnOptions();
        return options.Store.Select(s => s.ToString())
            .Concat(options.AdditionalColumns!.Select(c => c.ColumnName!))
            .ToArray();
    }

    public static TheoryData<string> Providers =>
        new() { DbProviderKeys.SqLite, DbProviderKeys.SqlServer, DbProviderKeys.Npgsql };

    // ------------------------------------------------------------- entity -> DDL

    [Theory]
    [MemberData(nameof(Providers))]
    public void EveryPropertyEfReads_HasAColumnInTheDdl(string provider)
    {
        var ddl = LogTableDdl.ColumnNames(provider).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = EntityColumnsFor(provider).Where(p => !ddl.Contains(p)).ToArray();

        Assert.Empty(missing);
    }

    // ------------------------------------------------------------- entity -> sink

    [Theory]
    [MemberData(nameof(Providers))]
    public void EveryPropertyEfReads_HasASinkWriter(string provider)
    {
        var sink = SinkColumnsFor(provider).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Id is the exception, on every provider and by design: the database generates it. No sink
        // writes a key it does not know, and none of them can - the PostgreSQL writer dictionary has
        // no way to express one at all, which is why the DDL had to take the job.
        // The accepted gaps, spelled the way this provider spells columns. Anything OUTSIDE this
        // list that the sink does not write is a defect: the column would exist, be readable, and
        // be permanently null, which is the quietest failure in the whole log pipeline.
        var accepted = SinkCannotWrite(provider)
            .Select(p => provider == DbProviderKeys.Npgsql ? ToSnakeCase(p) : p)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = EntityColumnsFor(provider)
            .Where(p => !p.Equals(nameof(SystemLog.Id), StringComparison.OrdinalIgnoreCase))
            .Where(p => !accepted.Contains(p))
            .Where(p => !sink.Contains(p))
            .ToArray();

        Assert.Empty(missing);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void TheAcceptedSinkGaps_AreRealPropertiesAndReallyUnwritten(string provider)
    {
        // An allow-list that has gone stale is worse than none: it would keep excusing a column the
        // sink had since learned to write, or name a property that no longer exists. Both directions
        // fail here.
        foreach (var property in SinkCannotWrite(provider))
        {
            Assert.Contains(property, EntityProperties);

            var spelled = provider == DbProviderKeys.Npgsql ? ToSnakeCase(property) : property;
            Assert.DoesNotContain(spelled, SinkColumnsFor(provider), StringComparer.OrdinalIgnoreCase);
        }
    }

    // ------------------------------------------------------------- sink -> DDL

    [Theory]
    [MemberData(nameof(Providers))]
    public void EverySinkWriter_HasAColumnInTheDdl(string provider)
    {
        // The direction that fails loudest and reports itself least: a writer naming a column the
        // table does not have makes every INSERT fail into SelfLog while the application appears
        // entirely healthy.
        var ddl = LogTableDdl.ColumnNames(provider).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = SinkColumnsFor(provider).Where(c => !ddl.Contains(c)).ToArray();

        Assert.Empty(missing);
    }

    // ------------------------------------------------------------- the other direction

    [Theory]
    [MemberData(nameof(Providers))]
    public void TheDdlHasNoColumnsNobodyUses(string provider)
    {
        // Extra DDL columns would be permitted - they cost nothing at read time - but there are none,
        // and stating that keeps the three expressions exactly congruent rather than merely
        // compatible. If this ever fails, the extra column wants a comment, not a deletion.
        var known = EntityColumnsFor(provider)
            .Concat(SinkColumnsFor(provider))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var extra = LogTableDdl.ColumnNames(provider).Where(c => !known.Contains(c)).ToArray();

        Assert.Empty(extra);
    }

    // ------------------------------------------------------------- the STOP, now resolved

    [Theory]
    [MemberData(nameof(Providers))]
    public void TheDdlSuppliesTheKeyOnEveryProvider(string provider)
    {
        // Pass 11B's STOP 1 inverted. It used to assert that the PostgreSQL sink could NOT supply an
        // id - written to fail once that was fixed. It is fixed: the DDL supplies the key on all
        // three providers, which is what makes the reading side work at all.
        var ddl = LogTableDdl.ColumnNames(provider);
        var key = provider == DbProviderKeys.Npgsql ? "id" : "Id";

        Assert.Contains(key, ddl, StringComparer.OrdinalIgnoreCase);
    }
}
