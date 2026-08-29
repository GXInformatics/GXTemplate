#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.Common.Extensions;

/// <summary>
/// Every <c>GetDateRange</c> branch returns both ends in the INPUT's <see cref="DateTimeKind"/>.
/// </summary>
/// <remarks>
/// Pass 14 measured the defect this pins: eight of sixteen branches returned
/// <c>Kind=Unspecified</c> for a <c>Kind=Utc</c> input, because the month and year paths call
/// <c>new DateTime(y, m, d)</c> - which is always Unspecified - while the day and week paths derive
/// from the input and preserve it. Nothing failed, because every call site passes only TODAY and
/// LAST_30_DAYS. The first "This month" filter anyone added would have thrown at runtime, on
/// PostgreSQL only, because Npgsql refuses to bind an Unspecified DateTime to a timestamptz column.
/// <para>
/// <b>The keyword list is read from the source file, not written out here</b>, and that is the
/// point of the test rather than an affectation. A hand-copied list stops covering the surface the
/// moment somebody adds a seventeenth branch, and stops silently - which is exactly how eight
/// broken branches survived. Reading the <c>case</c> labels means a new branch is exercised by this
/// test on the day it is written, whether or not its author knew the test existed.
/// </para>
/// <para>
/// The dynamic branches (LAST_n_DAYS, NEXT_n_DAYS, LAST_n_MONTHS, NEXT_n_MONTHS) have no literal
/// label to read, so they are listed explicitly below and <see cref="TheDynamicBranchesStillExist"/>
/// asserts the four patterns are still in the source that produced them.
/// </para>
/// </remarks>
[TestFixture]
public class GetDateRangeKindTests
{
    /// <summary>An arbitrary but fixed instant, mid-month and mid-year so no branch lands on a boundary.</summary>
    private static readonly DateTime Utc = new(2026, 8, 29, 13, 45, 17, DateTimeKind.Utc);

    /// <summary>The four keyword families the switch matches by pattern rather than by literal.</summary>
    private static readonly string[] DynamicKeywords =
        ["LAST_30_DAYS", "NEXT_7_DAYS", "LAST_3_MONTHS", "NEXT_2_MONTHS"];

    public static IEnumerable<string> AllKeywords() => LiteralKeywordsFromSource().Concat(DynamicKeywords);

    [TestCaseSource(nameof(AllKeywords))]
    public void EveryBranch_ReturnsBothEndsInTheInputsKind(string keyword)
    {
        var (start, end) = Utc.GetDateRange(keyword, TimeSpan.FromHours(1));

        start.Kind.Should().Be(DateTimeKind.Utc,
            "{0} returns Start as a query parameter, and a timestamptz column rejects any Kind but Utc", keyword);
        end.Kind.Should().Be(DateTimeKind.Utc,
            "{0} returns End as a query parameter, and a timestamptz column rejects any Kind but Utc", keyword);
    }

    [Test]
    public void TheKindFollowsTheInput_RatherThanBeingHardCodedToUtc()
    {
        // SpecifyKind at the exit relabels to dateTime.Kind, so a Local input must come back Local.
        // Hard-coding Utc would pass the test above and quietly mislabel a local-time caller's range
        // as UTC - a wrong answer instead of a rejected one, which is worse.
        var local = new DateTime(2026, 8, 29, 13, 45, 17, DateTimeKind.Local);

        var (start, end) = local.GetDateRange("THIS_MONTH");

        start.Kind.Should().Be(DateTimeKind.Local);
        end.Kind.Should().Be(DateTimeKind.Local);
    }

    [Test]
    public void TheKindIsRelabelled_NotConverted()
    {
        // The whole fix rests on SpecifyKind not shifting the clock. THIS_MONTH's start is the first
        // of the input's month at midnight; if anything converted instead of relabelling, this would
        // move by the host's offset.
        var (start, _) = Utc.GetDateRange("THIS_MONTH");

        start.Should().Be(new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Test]
    public void TheMonthHelpers_PreserveKindWhenCalledDirectly()
    {
        // EndOfMonth and StartOfMonth are public, so a caller can reach them without passing through
        // GetDateRange's exit. They construct with new DateTime(...), which defaults to Unspecified.
        Utc.StartOfMonth().Kind.Should().Be(DateTimeKind.Utc);
        Utc.EndOfMonth().Kind.Should().Be(DateTimeKind.Utc);
    }

    [Test]
    public void TheSourceScanFoundTheBranchesItIsSupposedTo()
    {
        // If the regex or the file location ever stops working, every TestCaseSource case would
        // vanish and this suite would pass by testing nothing. Pass 14 counted twelve literal
        // branches; fewer than that means the scan broke, not that the branches did.
        LiteralKeywordsFromSource().Should().HaveCountGreaterThanOrEqualTo(12,
            "the source scan must be finding the switch case labels; if it returns almost nothing, " +
            "the file moved or the regex stopped matching and this suite is silently vacuous");
    }

    [Test]
    public void TheDynamicBranchesStillExist()
    {
        // The four pattern branches carry no literal label for the scan to find, so they are listed
        // by hand above. This is what stops that hand-written list from drifting away from the code.
        var source = ReadSource();

        foreach (var pattern in new[] { "\"LAST_\"", "\"NEXT_\"", "\"_DAYS\"", "\"_MONTHS\"" })
        {
            source.Should().Contain(pattern,
                "the dynamic keyword family using {0} is enumerated by hand in this test", pattern);
        }
    }

    [Test]
    public void NothingElseReturnsARangeWithoutPassingThroughTheKindGuarantee()
    {
        // The guarantee lives at GetDateRange's single exit rather than in each branch, which is
        // what makes a future branch correct by construction. The way to defeat that is not a new
        // branch - it is a SECOND public entry point that returns a range without re-specifying.
        var entryPoints = typeof(DateTimeExtensions)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.ReturnType == typeof((DateTime Start, DateTime End)))
            .Select(m => m.Name)
            .ToArray();

        entryPoints.Should().BeEquivalentTo(new[] { nameof(DateTimeExtensions.GetDateRange) },
            "GetDateRange is the only public way to obtain a range, so its exit is the only place " +
            "the Kind guarantee has to be applied");
    }

    // ------------------------------------------------------------------ the scan

    private static IReadOnlyList<string> LiteralKeywordsFromSource() =>
        Regex.Matches(ReadSource(), "case\\s+\"([A-Z0-9_]+)\"\\s*:")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToArray();

    private static string ReadSource() => File.ReadAllText(SourcePath());

    /// <summary>
    /// Walks up from the test assembly until the extension's source file appears beneath it.
    /// </summary>
    /// <remarks>
    /// Anchored on the file's own path under <c>src/</c> rather than on a solution file or a
    /// namespace, because both of those are renamed when the template is generated and the folder
    /// layout is not. A generated project therefore runs this test against its own copy.
    /// </remarks>
    private static string SourcePath()
    {
        const string relative = "src/Application/Common/Extensions/DateTimeExtensions.cs";

        var directory = new DirectoryInfo(Path.GetDirectoryName(typeof(GetDateRangeKindTests).Assembly.Location)!);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            "Could not find " + relative + " above " + typeof(GetDateRangeKindTests).Assembly.Location + ". " +
            "This test reads the switch case labels from the source so that a newly added branch " +
            "is covered automatically; it fails rather than silently testing nothing.");
    }
}
