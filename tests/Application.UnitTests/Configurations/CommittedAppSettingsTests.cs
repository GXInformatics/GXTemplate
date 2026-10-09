#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.Configurations;

/// <summary>
/// The GX configuration layout: committed appsettings files hold structure and non-secret settings
/// only. Secrets and server-specific values come from the gitignored appsettings.Development.json
/// locally, and from the server's web.config in deployment.
/// </summary>
[TestFixture]
public class CommittedAppSettingsTests
{
    private const string DevelopmentFile = "src/Server.UI/appsettings.Development.json";

    /// <summary>The committed files, relative to src/Server.UI.</summary>
    private static readonly string[] CommittedFiles =
        ["appsettings.json", "appsettings.Staging.json", "appsettings.Production.json"];

    /// <summary>
    /// A key whose last segment names a credential. It matches by name rather than by list, so a
    /// secret-bearing key added later is covered without anyone remembering to add it here.
    /// </summary>
    private static readonly Regex SecretBearing = new(
        "(ConnectionString|ApiKey|LicenseKey|Password|Secret|Token|AccountKey|SasKey)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The keys a server's web.config has to supply. They must stay present in appsettings.json:
    /// "never delete a key" is what makes the committed file a complete list of what to set.
    /// </summary>
    private static readonly string[] RequiredStructure =
    [
        "DatabaseSettings:ConnectionString",
        "DatabaseSettings:LogConnectionString",
        "Authentication:Microsoft:ClientId",
        "Authentication:Microsoft:ClientSecret",
        "Authentication:Google:ClientId",
        "Authentication:Google:ClientSecret",
        "Mail:Domain",
        "Mail:FromAddress",
        "Mail:ApiKey",
        "Storage:ConnectionString",
        "AppConfigurationSettings:ApplicationUrl"
    ];

    private static string ServerUiDirectory() => Path.Combine(RepositoryRoot(), "src", "Server.UI");

    /// <summary>
    /// The folder holding src/Server.UI, found by walking up from the test assembly. Anchored on the
    /// layout rather than on the solution file's name, which the template renames.
    /// </summary>
    internal static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(typeof(CommittedAppSettingsTests).Assembly.Location)!);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "src", "Server.UI", "Server.UI.csproj"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find src/Server.UI/Server.UI.csproj above the test assembly.");
    }

    private static IConfigurationRoot Load(string file) =>
        new ConfigurationBuilder().AddJsonFile(Path.Combine(ServerUiDirectory(), file), optional: false).Build();

    [TestCaseSource(nameof(CommittedFiles))]
    public void EverySecretBearingKey_IsEmpty(string file)
    {
        var filled = Load(file).AsEnumerable()
            .Where(pair => SecretBearing.IsMatch(pair.Key) && !string.IsNullOrEmpty(pair.Value))
            .Select(pair => pair.Key)
            .ToList();

        filled.Should().BeEmpty(
            $"{file} is committed: secrets belong in appsettings.Development.json locally and in the server's web.config");
    }

    [Test]
    public void AppSettingsJson_KeepsEveryKeyAServerMustSupply_AndLeavesItEmpty()
    {
        var configuration = Load("appsettings.json");

        foreach (var key in RequiredStructure)
        {
            configuration.GetSection(key).Value.Should().NotBeNull($"'{key}' must stay in appsettings.json");
            configuration[key].Should().BeEmpty($"'{key}' is supplied per environment, not committed");
        }
    }

    [Test]
    public void AppSettingsDevelopmentJson_IsIgnoredByGit()
    {
        var root = RepositoryRoot();

        // Asked of git itself, so this covers every way a rule could stop matching: a deleted line,
        // a negation added later, or a rename. In a repository it also fails for a file that is
        // already tracked, because git does not apply ignore rules to tracked files.
        if (Git(root, "rev-parse --is-inside-work-tree").ExitCode == 0)
        {
            var (exitCode, error) = Git(root, $"check-ignore --quiet {DevelopmentFile}");
            exitCode.Should().Be(0,
                "appsettings.Development.json holds local credentials and must be gitignored " +
                $"(git check-ignore exit code {exitCode}; 1 means not ignored){(error.Length > 0 ? ": " + error : "")}");
            return;
        }

        // Not a repository yet: a project straight out of `dotnet new`, before `git init`. Its
        // .gitignore is what the first commit will obey, so ask git about that file in a scratch
        // repository holding nothing else.
        var scratch = Path.Combine(Path.GetTempPath(), "gx-gitignore-check", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            Git(scratch, "init --quiet").ExitCode.Should().Be(0, "a scratch repository is needed to evaluate .gitignore");
            File.Copy(Path.Combine(root, ".gitignore"), Path.Combine(scratch, ".gitignore"));

            var (exitCode, error) = Git(scratch, $"check-ignore --quiet {DevelopmentFile}");
            exitCode.Should().Be(0,
                "appsettings.Development.json holds local credentials and this project's .gitignore must ignore it " +
                $"(git check-ignore exit code {exitCode}; 1 means not ignored){(error.Length > 0 ? ": " + error : "")}");
        }
        finally
        {
            DeleteScratch(scratch);
        }
    }

    /// <summary>
    /// Runs git with the user's global excludes file switched off, so the answer is this project's
    /// .gitignore and not a rule that happens to exist on this machine only.
    /// </summary>
    private static (int ExitCode, string Error) Git(string workingDirectory, string arguments)
    {
        var noGlobalExcludes = Path.Combine(Path.GetTempPath(), "gx-no-global-excludes-" + Guid.NewGuid().ToString("N"));
        var start = new ProcessStartInfo("git", $"-c core.excludesFile=\"{noGlobalExcludes}\" {arguments}")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var git = Process.Start(start) ?? throw new InvalidOperationException("Could not start git.");
        git.StandardOutput.ReadToEnd();
        var error = git.StandardError.ReadToEnd();
        git.WaitForExit();
        return (git.ExitCode, error.Trim());
    }

    private static void DeleteScratch(string directory)
    {
        // git marks its object files read-only, which Directory.Delete refuses on Windows.
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(directory, recursive: true);
    }
}
#nullable restore
