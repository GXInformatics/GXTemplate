#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CleanArchitecture.Blazor.Application.UnitTests.Configurations;
using FluentAssertions;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.Model;

/// <summary>
/// Owner decision D7 (Pass 60c): every pass plan carries a section "GX conventions touched" that cites
/// <c>docs/gx-conventions.md</c> and says, for each convention the pass affects, whether it conforms or departs; a
/// departure names the owner decision that made it one.
/// </summary>
/// <remarks>
/// <para>
/// Pass 60 retired the <c>core</c> schema without a word about it being a GX convention, because nothing asked the plan
/// to check. This test asks. It reads <c>docs/passes/*-plan.md</c>; a project without that folder skips it.
/// </para>
/// <para>
/// <b>Which plans.</b> When <c>docs/passes/.gx-plan-rules</c> exists, its one line names the first plan file in scope
/// (in GXTemplate: <c>pass60c-gx-conventions-plan.md</c>) and every plan whose file name sorts at or after it is checked.
/// Without it, every plan dated after 2026-11-12 (its first <c>yyyy-mm-dd</c>), or not dated at all, is checked - so a
/// generated project's plans written before the rule existed are not failed after the fact.
/// </para>
/// </remarks>
public class PassPlanRulesTests
{
    public const string Section = "GX conventions touched";
    public const string ConventionsDocument = "docs/gx-conventions.md";
    public const string MarkerFile = ".gx-plan-rules";
    private static readonly DateOnly RuleDate = new(2026, 11, 12);

    private static readonly string[] Verdicts = ["conforms", "departs", "not decided", "no conflict"];

    /// <summary>Everything wrong with one plan's text, one line each; empty when it follows the rule.</summary>
    public static IReadOnlyList<string> ProblemsOf(string plan)
    {
        var lines = plan.Replace("\r\n", "\n").Split('\n');
        var start = Array.FindIndex(lines, l => l.StartsWith('#') && l.Contains(Section, StringComparison.OrdinalIgnoreCase));
        if (start < 0) return [$"has no section \"{Section}\" (owner decision D7)"];

        var end = Array.FindIndex(lines, start + 1, l => Regex.IsMatch(l, "^#{1,2} "));
        var section = lines[(start + 1)..(end < 0 ? lines.Length : end)];
        var problems = new List<string>();
        if (!section.Any(l => l.Contains(ConventionsDocument, StringComparison.Ordinal)) && !lines[start].Contains(ConventionsDocument))
            problems.Add($"its section \"{Section}\" does not cite {ConventionsDocument}");

        var rows = section.Where(l => l.TrimStart().StartsWith('|')).Skip(2).ToList(); // header and separator
        if (rows.Count == 0) problems.Add($"its section \"{Section}\" has no table of conventions");
        foreach (var row in rows)
        {
            var cells = row.Trim().Trim('|').Split('|');
            var verdict = cells[^1].Trim();
            if (!Verdicts.Any(v => verdict.Contains(v, StringComparison.OrdinalIgnoreCase)))
                problems.Add($"row \"{cells[0].Trim()}\" ends in neither Conforms, Departs, Not decided nor No conflict");
            else if (verdict.Contains("departs", StringComparison.OrdinalIgnoreCase)
                     && !Regex.IsMatch(row, @"\bD\d+\b|\bQ-?[A-Z]?\d*\b|owner decision", RegexOptions.IgnoreCase))
                problems.Add($"row \"{cells[0].Trim()}\" departs from a GX convention without naming the owner decision that allows it");
        }
        return problems;
    }

    [Test]
    public void EveryPlanInScope_HasItsGxConventionsSection()
    {
        var passes = Path.Combine(CommittedAppSettingsTests.RepositoryRoot(), "docs", "passes");
        if (!Directory.Exists(passes)) Assert.Ignore("This project keeps no docs/passes; there is no plan to check.");

        var marker = Path.Combine(passes, MarkerFile);
        var first = File.Exists(marker) ? File.ReadAllText(marker).Trim() : null;
        var plans = Directory.EnumerateFiles(passes, "*-plan.md").Where(path => InScope(path, first)).ToList();

        var problems = plans.SelectMany(p => ProblemsOf(File.ReadAllText(p)).Select(problem => $"{Path.GetFileName(p)}: {problem}")).ToList();
        problems.Should().BeEmpty($"every pass plan says, for each GX convention it touches ({ConventionsDocument}), whether it " +
                                  "conforms or departs, and a departure names the owner decision that made it one (owner decision D7)");
    }

    private static bool InScope(string path, string? first)
    {
        if (first is not null) return string.CompareOrdinal(Path.GetFileName(path), first) >= 0;
        var head = string.Join('\n', File.ReadLines(path).Take(15));
        var date = Regex.Match(head, @"\b(20\d\d)-(\d\d)-(\d\d)\b");
        return !date.Success || new DateOnly(int.Parse(date.Groups[1].Value), int.Parse(date.Groups[2].Value), int.Parse(date.Groups[3].Value)) > RuleDate;
    }

    [Test]
    public void APlanWithoutTheSection_OrWithAnUnnamedDeparture_Fails()
    {
        ProblemsOf("# Pass 99 plan\n\n## 1. What is wrong\n").Should().ContainSingle().Which.Should().Contain("has no section");

        const string unnamed = """
            # Pass 99 plan
            ## 0. GX conventions touched
            Against docs/gx-conventions.md.
            | Convention | Effect | Verdict |
            |---|---|---|
            | §1.1 core | Tables move to a module schema | Departs |
            | §2.1 TBL_ | Unchanged | Conforms |
            | §4.1 snake_case | Columns renamed | Maybe |
            ## 1. What is wrong
            """;
        var problems = ProblemsOf(unnamed);
        problems.Should().HaveCount(2);
        problems.Should().Contain(p => p.Contains("§1.1 core") && p.Contains("without naming the owner decision"));
        problems.Should().Contain(p => p.Contains("§4.1 snake_case") && p.Contains("neither"));

        ProblemsOf(unnamed.Replace("| Departs |", "| Departs, by owner decision D9 |").Replace("| Maybe |", "| Conforms |"))
            .Should().BeEmpty();
    }
}
