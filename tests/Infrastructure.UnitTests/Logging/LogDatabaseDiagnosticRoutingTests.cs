using CleanArchitecture.Blazor.Infrastructure.Extensions;
using Serilog.Events;
using Serilog.Parsing;
using Xunit;

namespace CleanArchitecture.Blazor.Infrastructure.UnitTests.Logging;

/// <summary>
/// Which sinks see which marked events. Asserted, because the brief was explicit that this must not
/// be assumed.
/// </summary>
/// <remarks>
/// Two markers, two different answers, and getting either backwards is a silent failure:
/// <list type="bullet">
/// <item>the bootstrap password must never persist, so the file and database sinks drop it and only
/// the console keeps it (Pass 7B);</item>
/// <item>a complaint about the log database must never be routed INTO the log database, or the one
/// message telling an operator their logging is broken is handed to the broken thing to store. It
/// must still reach the file, which is where it will actually be read.</item>
/// </list>
/// These test the predicates the pipeline passes to <c>Filter.ByExcluding</c>, not copies of them.
/// </remarks>
public class LogDatabaseDiagnosticRoutingTests
{
    private static LogEvent EventWith(params string[] propertyNames)
    {
        var e = new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Error,
            exception: null,
            new MessageTemplate("m", []),
            []);

        foreach (var name in propertyNames)
            e.AddPropertyIfAbsent(new LogEventProperty(name, new ScalarValue(true)));

        return e;
    }

    [Fact]
    public void ALogDatabaseDiagnostic_NeverReachesTheDatabaseSink()
    {
        var complaint = EventWith(SerilogExtensions.LogDatabaseDiagnosticProperty);

        Assert.True(SerilogExtensions.IsExcludedFromDatabaseSink(complaint));
    }

    [Fact]
    public void ALogDatabaseDiagnostic_DoesReachTheFileSink()
    {
        // The half that matters most. Excluding it here too would leave the failure with nowhere
        // durable to land, and the console scrolls away.
        var complaint = EventWith(SerilogExtensions.LogDatabaseDiagnosticProperty);

        Assert.False(SerilogExtensions.IsExcludedFromFileSink(complaint));
    }

    [Fact]
    public void TheBootstrapSecret_ReachesNeitherPersistentSink()
    {
        var banner = EventWith(SerilogExtensions.BootstrapSecretProperty);

        Assert.True(SerilogExtensions.IsExcludedFromFileSink(banner));
        Assert.True(SerilogExtensions.IsExcludedFromDatabaseSink(banner));
    }

    [Fact]
    public void AnOrdinaryEvent_ReachesBothPersistentSinks()
    {
        // The paired negative: filters that exclude everything would pass every test above.
        var ordinary = EventWith();

        Assert.False(SerilogExtensions.IsExcludedFromFileSink(ordinary));
        Assert.False(SerilogExtensions.IsExcludedFromDatabaseSink(ordinary));
    }

    [Fact]
    public void AnEventCarryingBothMarkers_IsExcludedFromBoth()
    {
        var both = EventWith(
            SerilogExtensions.BootstrapSecretProperty,
            SerilogExtensions.LogDatabaseDiagnosticProperty);

        Assert.True(SerilogExtensions.IsExcludedFromFileSink(both));
        Assert.True(SerilogExtensions.IsExcludedFromDatabaseSink(both));
    }
}
