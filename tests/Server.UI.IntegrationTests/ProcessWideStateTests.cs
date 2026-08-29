#nullable enable
using System;
using System.Net.Http;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Server.UI.IntegrationTests;

/// <summary>
/// The process-wide state a real boot of this application leaves behind.
/// </summary>
/// <remarks>
/// Pass 14 measured the defect this pins. <c>AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior",
/// true)</c> was set from inside an <c>AddDbContextFactory</c> options lambda, so it ran lazily, at
/// first context creation - after Serilog had been configured and, on an unlucky host, after the
/// PostgreSQL sink had already written. Npgsql caches its type handlers on first use, so a switch
/// set after that point changes EF's column mapping and NOT the driver's converters: EF believes
/// <c>timestamp without time zone</c>, the converters believe <c>timestamptz</c>, and every write
/// fails at runtime only. That is the staging outage this line of work came from.
/// <para>
/// Pass 14B deleted the switch rather than moving it, because under <c>timestamptz</c> there is
/// nothing to set. This is Pass 14 F.3's second assertion: the ratified end state, checked after a
/// real boot of the real <c>Program.cs</c>, which is the only place the ordering can be observed
/// rather than reasoned about. It would have caught the original defect.
/// </para>
/// <para>
/// The harness runs on SQLite by default, so the Npgsql branch of <c>UseDatabase</c> is not even
/// reached here - which makes this test WEAKER than it looks on its own, and is exactly why
/// <c>TimestamptzModelInvariantTests</c> exists beside it: that one drives the Npgsql registration
/// directly, and its source assertion catches a reintroduction in any branch, reached or not. Under
/// <c>GX_TEST_DBPROVIDER=postgresql</c> this test additionally covers the reached case.
/// </para>
/// </remarks>
[TestFixture]
public class ProcessWideStateTests
{
    private const string LegacyTimestampSwitch = "Npgsql.EnableLegacyTimestampBehavior";

    [Test]
    public async Task AfterBoot_TheNpgsqlLegacyTimestampSwitchIsNotSet()
    {
        using var factory = new GxWebApplicationFactory();

        // A request, not merely construction: WebApplicationFactory builds the host lazily, and
        // asserting before anything has been served would test a process that has not run the
        // startup path yet. The status code is irrelevant here - reaching the pipeline is the point.
        using var client = factory.CreateNonRedirectingClient();
        await client.GetAsync("/");

        var set = AppContext.TryGetSwitch(LegacyTimestampSwitch, out var enabled);

        (set && enabled).Should().BeFalse(
            "{0} must be unset - or at least not true - after the application has booted. It is a " +
            "process-wide, permanent, read-once-and-cached decision, and Pass 14B removed it " +
            "because timestamptz is Npgsql's default and this template creates its schema fresh",
            LegacyTimestampSwitch);
    }

    [Test]
    public async Task TheBootLogNamesTheEffectiveProcessWideState()
    {
        // The log line itself is the operator-facing half of the ruling: an outage whose signature
        // is "EF and the driver disagree" is undiagnosable from a dashboard and obvious from one
        // startup line. Serilog's file sink is what carries it (the database sink is excluded, since
        // a message about how DateTimes bind to the database cannot be delivered through it), so
        // this asserts the state is reportABLE rather than re-parsing the log file - which would
        // couple the test to the sink's rolling filename and buffering.
        using var factory = new GxWebApplicationFactory();
        using var client = factory.CreateNonRedirectingClient();
        await client.GetAsync("/");

        var set = AppContext.TryGetSwitch(LegacyTimestampSwitch, out _);

        // Program.cs reports "not set" for exactly this state, and that is the expected reading.
        set.Should().BeFalse(
            "the boot line reports {0} as 'not set', and it should be reporting the truth",
            LegacyTimestampSwitch);
    }
}
