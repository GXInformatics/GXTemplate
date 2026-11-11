using System.Text.RegularExpressions;

namespace CleanArchitecture.Blazor.TestSupport;

/// <summary>
/// The repository's source files, for the source-scan architecture rules (Pass 60).
/// </summary>
/// <remarks>
/// Anchored on the layout (<c>src/Server.UI/Server.UI.csproj</c>), not on the solution's name, which the
/// template renames; so the scans run unchanged in every generated project, as
/// <c>OutboundAddressTests</c> does.
/// </remarks>
public static class SourceTree
{
    public static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "src", "Server.UI", "Server.UI.csproj"))) return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not find src/Server.UI/Server.UI.csproj above the test assembly.");
    }

    /// <summary>Every <c>*.cs</c> and <c>*.razor</c> file under <c>src</c>, outside <c>bin</c> and <c>obj</c>.</summary>
    public static IReadOnlyList<string> SourceFiles()
    {
        var root = Path.Combine(RepositoryRoot(), "src");
        return Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            .Where(f => !IsBuildOutput(f))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The path relative to the repository root, with forward slashes.</summary>
    public static string Relative(string path) =>
        Path.GetRelativePath(RepositoryRoot(), path).Replace('\\', '/');

    private static bool IsBuildOutput(string path)
    {
        var parts = path.Replace('\\', '/').Split('/');
        return parts.Any(p => p.Equals("bin", StringComparison.OrdinalIgnoreCase) || p.Equals("obj", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>One source-rule finding: file, line and the offending text.</summary>
public sealed record SourceFinding(string File, int Line, string Text)
{
    public override string ToString() => $"{File}:{Line}: {Text.Trim()}";
}

/// <summary>
/// The source-text rules of Pass 60. Each takes source text (so a test can feed it a planted sample) and
/// reports findings; the architecture tests run them over <see cref="SourceTree.SourceFiles"/>.
/// </summary>
public static class SourceRules
{
    // ---- No failure result built from an exception's message (D1) ----------------------------------

    /// <summary>Exceptions whose messages are written for users.</summary>
    public static readonly IReadOnlySet<string> UserMessageExceptions =
        new HashSet<string>(StringComparer.Ordinal) { "NotFoundException", "PostingRejectedException" };

    private static readonly Regex FailureFromMessage = new(
        @"(?:\bFailure(?:Async)?|ResultFailureFactory\.(?:Create|ForRejection)(?:<[^>]+>)?)\s*\((?<args>[^;]*?)\b(?<var>[A-Za-z_]\w*)\.Message\b",
        RegexOptions.Compiled);

    private static readonly Regex CatchClause = new(@"catch\s*\(\s*(?<type>[\w.]+)\s+(?<var>\w+)\s*\)", RegexOptions.Compiled);

    /// <summary>
    /// A failure result (<c>Result.Failure</c>, <c>FailureAsync</c>, <c>ResultFailureFactory.Create</c>) whose
    /// arguments read <c>x.Message</c>, unless <c>x</c> is caught as one of <see cref="UserMessageExceptions"/>.
    /// </summary>
    public static IReadOnlyList<SourceFinding> FailureResultsFromExceptionText(string file, string source)
    {
        var findings = new List<SourceFinding>();
        foreach (Match match in FailureFromMessage.Matches(source))
        {
            var variable = match.Groups["var"].Value;
            var caughtAs = CatchClause.Matches(source[..match.Index])
                .LastOrDefault(c => c.Groups["var"].Value == variable)?.Groups["type"].Value;
            if (caughtAs is not null && UserMessageExceptions.Contains(caughtAs.Split('.').Last())) continue;
            findings.Add(new SourceFinding(file, LineOf(source, match.Index), LineText(source, match.Index)));
        }
        return findings;
    }

    // ---- Rule 4: the branch filter is removed only by AcrossBranchesAsync ----------------------------

    /// <summary>The one file allowed to remove <c>QueryFilters.Branch</c> (pass 59, B5).</summary>
    public const string BranchScopeFile = "src/Application/Common/Branches/BranchQueryScope.cs";

    private static readonly Regex IgnoreFilters = new(@"\.IgnoreQueryFilters\s*\((?<args>[^;]*?)\)\s*(?:[;.)]|$)", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// Architecture rule 4: no bare <c>IgnoreQueryFilters()</c> anywhere (it drops every filter, branch and tenant
    /// alike), and <c>IgnoreQueryFilters</c> naming the branch filter only in <see cref="BranchScopeFile"/>.
    /// A call naming only other filters (the tenant filter's sanctioned scopes) is allowed.
    /// </summary>
    public static IReadOnlyList<SourceFinding> BranchFilterRemovals(string file, string source)
    {
        var code = WithoutComments(source);
        var findings = new List<SourceFinding>();
        foreach (Match match in IgnoreFilters.Matches(code))
        {
            var args = match.Groups["args"].Value.Trim();
            var bare = args.Length == 0;
            var namesBranch = args.Contains("QueryFilters.Branch", StringComparison.Ordinal) || args.Contains("\"Branch\"", StringComparison.Ordinal);
            if (!bare && !(namesBranch && file != BranchScopeFile)) continue;
            findings.Add(new SourceFinding(file, LineOf(code, match.Index), LineText(source, match.Index)));
        }
        return findings;
    }

    // ---- Rule 5: bulk writes and raw SQL only in the listed files -----------------------------------

    /// <summary>
    /// The files allowed to issue bulk writes or raw SQL (plan §3.3 rule 5). A new entry is a reviewed decision.
    /// The migrations projects are allowed wholesale.
    /// </summary>
    public static readonly IReadOnlySet<string> RawSqlFiles = new HashSet<string>(StringComparer.Ordinal)
    {
        "src/Infrastructure/Persistence/Logging/LogDbContext.cs",                 // purge of the log table
        "src/Infrastructure/Persistence/Logging/LogDatabaseStartupCheck.cs",      // log database DDL
        "src/Infrastructure/Persistence/Transactions/PostingSql.cs",              // pass 58 raw-SQL plumbing
        "src/Infrastructure/Persistence/Rules/DataModelGuard.cs",                 // pg_trigger check
        "src/Infrastructure/Persistence/Transactions/InsertIfAbsent.cs"           // pass 60, amendment C4
    };

    private static readonly Regex RawSql = new(
        @"\b(?<call>ExecuteUpdate\w*|ExecuteDelete\w*|ExecuteSql\w*|FromSql\w*|SqlQuery\w*|GetDbConnection|CreateCommand)\s*[(<]",
        RegexOptions.Compiled);

    /// <summary>Architecture rule 5: bulk operations and raw SQL outside <see cref="RawSqlFiles"/> and the migrations.</summary>
    public static IReadOnlyList<SourceFinding> RawSqlOutsideListedFiles(string file, string source)
    {
        if (RawSqlFiles.Contains(file) || file.StartsWith("src/Migrators/", StringComparison.Ordinal)) return [];
        var code = WithoutComments(source);
        return RawSql.Matches(code)
            .Select(m => new SourceFinding(file, LineOf(code, m.Index), LineText(source, m.Index)))
            .ToList();
    }

    /// <summary>
    /// The source with line comments, XML doc comments and block comments blanked out (line breaks kept, so
    /// line numbers still match). Strings are left alone: none of the rules' patterns can occur in one by
    /// accident without also being suspicious.
    /// </summary>
    public static string WithoutComments(string source)
    {
        var block = Regex.Replace(source, @"/\*.*?\*/", m => new string(m.Value.Select(c => c == '\n' ? '\n' : ' ').ToArray()), RegexOptions.Singleline);
        return Regex.Replace(block, @"//[^\n]*", m => new string(' ', m.Value.Length));
    }

    // ---- shared --------------------------------------------------------------------------------------

    public static int LineOf(string source, int index) => source.AsSpan(0, index).Count('\n') + 1;

    public static string LineText(string source, int index)
    {
        var start = source.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
        var end = source.IndexOf('\n', index);
        return source[start..(end < 0 ? source.Length : end)].TrimEnd('\r');
    }
}
