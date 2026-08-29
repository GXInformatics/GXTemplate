using CleanArchitecture.Blazor.Domain.Entities;
using CleanArchitecture.Blazor.Infrastructure.Extensions;
using Xunit;

namespace CleanArchitecture.Blazor.Infrastructure.UnitTests.Logging;

/// <summary>
/// Every property EF reads off <see cref="SystemLog"/> has a column in each provider's sink
/// configuration.
/// </summary>
/// <remarks>
/// The log database has no EF migration chain: its table is created by the sink, from the very
/// column configuration these tests inspect. So the sink's columns and the entity are two
/// expressions of one shape, and the failure mode is drift between them - someone adds a property to
/// SystemLog, no column writer is added, and the page reads a column that is not there.
/// <para>
/// <b>Containment, not equality.</b> Extra sink columns are harmless and expected: the MSSQL
/// configuration legitimately adds ClientIP, UserName and ClientAgent as additional columns, and the
/// standard-column machinery names things the entity does not model. The direction that matters is
/// entity to sink.
/// </para>
/// <para>
/// <b>What this cannot see.</b> It compares code with code. Auto-create only ever CREATES - no sink
/// alters an existing table - so a log database deployed before a new property was added keeps its
/// old columns and no test here will know. Adding a property to SystemLog carries a manual ALTER on
/// every deployed log database. That is a stated limitation of auto-create, not an oversight.
/// </para>
/// </remarks>
public class SinkColumnDriftTests
{
    /// <summary>The properties EF maps, which are exactly the ones a query can ask a database for.</summary>
    private static IEnumerable<string> EntityProperties =>
        typeof(SystemLog).GetProperties().Select(p => p.Name);

    /// <summary>
    /// PostgreSQL is addressed by the snake_case names <c>UseSnakeCaseNamingConvention()</c>
    /// produces, so the comparison has to be made in the same alphabet.
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

    [Fact]
    public void TheSqlServerSink_HasAColumnForEveryEntityProperty()
    {
        var options = SerilogExtensions.BuildSqlServerColumnOptions();

        var columns = options.Store.Select(s => s.ToString())
            .Concat(options.AdditionalColumns!.Select(c => c.ColumnName!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = EntityProperties.Where(p => !columns.Contains(p)).ToArray();

        Assert.Empty(missing);
    }

    [Fact]
    public void ThePostgresSink_HasAColumnForEveryEntityPropertyExceptTheKey()
    {
        // Id is the documented exception, and it is the Pass 11B STOP: this sink's column-writer
        // dictionary has no way to express an identity column, so it can neither write nor create
        // one. That is why needAutoCreateTable stays false for PostgreSQL - see the comment on
        // WriteToNpgsql and pass11b-report.md §D. When that is resolved, this test should lose its
        // exception rather than keep it.
        var columns = SerilogExtensions.BuildNpgsqlColumnWriters().Keys
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = EntityProperties
            .Where(p => p != nameof(SystemLog.Id))
            .Select(ToSnakeCase)
            .Where(c => !columns.Contains(c))
            .ToArray();

        Assert.Empty(missing);
    }

    [Fact]
    public void ThePostgresSink_StillCannotSupplyTheKey()
    {
        // Stated explicitly so the gap is a recorded fact with a test behind it, not a footnote. If
        // this ever starts failing, the STOP has been resolved and the exception above can go.
        var columns = SerilogExtensions.BuildNpgsqlColumnWriters().Keys
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.DoesNotContain("id", columns);
    }
}
