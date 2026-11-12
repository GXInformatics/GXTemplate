#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CleanArchitecture.Blazor.Application.UnitTests.Configurations;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using CleanArchitecture.Blazor.Infrastructure.Persistence.Extensions;
using CleanArchitecture.Blazor.Infrastructure.Persistence.Rules;
using CleanArchitecture.Blazor.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.Model;

/// <summary>
/// Pass 60c: two convention checks that need the SOURCE, shipped into every generated project.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description><b>Enums (owner decision Q-H).</b> Every enum a business entity stores is an integer column, so
/// every member has an explicit value. <c>DataModelGuard</c> requires <c>[ExplicitValues]</c> at startup; this test reads
/// each such enum's declaration and fails on a member written without <c>= value</c>, so the attribute cannot lie.</description></item>
/// <item><description><b>Test entities (T11).</b> An entity a test declares must not take a plain business noun as its
/// name: a test model built on the real <c>ApplicationDbContext</c> also holds the application's entities, and a test
/// entity named <c>Warehouse</c> collided with Spare Parts' own (<c>TBL_WAREHOUSE</c>). A test entity's name is at least
/// two words, prefixed with what it tests (<c>GuardProbeWarehouse</c>, <c>TestLedgerRow</c>, <c>SampleWidget</c>).</description></item>
/// </list>
/// </remarks>
public class ConventionSourceTests
{
    private static readonly Regex EnumDeclaration = new(@"\benum\s+(?<name>\w+)\s*(:\s*\w+\s*)?\{(?<body>[^}]*)\}", RegexOptions.Compiled);

    /// <summary>The members of <paramref name="enumName"/> in <paramref name="source"/> that have no explicit value.</summary>
    public static string[] MembersWithoutValues(string source, string enumName)
    {
        var match = EnumDeclaration.Matches(source).FirstOrDefault(m => m.Groups["name"].Value == enumName);
        if (match is null) return [];
        var body = Regex.Replace(match.Groups["body"].Value, @"//[^\n]*|/\*.*?\*/|\[[^\]]*\]", "", RegexOptions.Singleline);
        return body.Split(',').Select(m => m.Trim()).Where(m => m.Length > 0 && !m.Contains('=')).ToArray();
    }

    [Test]
    public void EveryEnumABusinessEntityStores_GivesEveryMemberAnExplicitValue()
    {
        using var context = new ApplicationDbContext(ApplicationModel.NpgsqlOptions("Host=model-only"));
        var enums = context.Model.GetEntityTypes()
            .Where(GxSnakeCaseConvention.IsBusiness)
            .SelectMany(DataModelGuard.EnumsOf).Select(x => x.Enum).Distinct().ToList();

        var sources = Directory.EnumerateFiles(Path.Combine(CommittedAppSettingsTests.RepositoryRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(File.ReadAllText).ToList();

        var problems = enums
            .SelectMany(e => sources.SelectMany(s => MembersWithoutValues(s, e.Name)).Select(member => $"{e.Name}.{member}"))
            .ToList();
        problems.Should().BeEmpty("an enum a business entity stores is an integer column, and its numbers are the contract " +
                                  "(README \"Database naming\"): write every member as Name = value");
    }

    [Test]
    public void TheEnumReader_FindsAMemberWithoutAValue_AndIgnoresCommentsAndAttributes()
    {
        const string source = """
            public enum Good { First = 1, /* note, with a comma */ Second = 2, }
            [ExplicitValues] public enum Bad : int
            {
                Draft = 1,
                [Description("x")] Posted,   // no value
                Reversed = 3
            }
            """;
        MembersWithoutValues(source, "Good").Should().BeEmpty();
        MembersWithoutValues(source, "Bad").Should().Equal("Posted");
    }

    private static readonly Regex EntityDeclaration = new(
        @"\bclass\s+(?<name>[A-Z]\w*)(<[^>]*>)?(\([^)]*\))?\s*:\s*[^{;]*\b(BaseEntity|BaseAuditableEntity|BaseVersionedEntity|BaseLkEntity|BaseVersionedLkEntity|BaseAuditableSoftDeleteEntity|IBusinessEntity)\b",
        RegexOptions.Compiled);

    [Test]
    public void NoTestDeclaresAnEntityNamedWithAPlainWord()
    {
        var tests = Path.Combine(CommittedAppSettingsTests.RepositoryRoot(), "tests");
        var plain = Directory.EnumerateFiles(tests, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .SelectMany(f => EntityDeclaration.Matches(File.ReadAllText(f)).Select(m => (File: Path.GetFileName(f), Name: m.Groups["name"].Value)))
            .Where(x => Regex.IsMatch(x.Name, "^[A-Z][a-z0-9]*$"))
            .Select(x => $"{x.File}: {x.Name}")
            .ToList();

        plain.Should().BeEmpty("a test entity named with one plain word (Warehouse) collides with an application entity of " +
                               "that name once the test model is built on the real context (T11); prefix it with what it tests");
    }
}
