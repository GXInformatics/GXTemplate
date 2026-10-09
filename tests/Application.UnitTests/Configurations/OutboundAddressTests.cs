#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.Configurations;

/// <summary>
/// No address the server could call is written into src/ unless it is on <see cref="Allowed"/>,
/// with the reason it is there.
/// </summary>
/// <remarks>
/// The upstream starter shipped a Serilog sink to its author's own Seq server
/// (https://seq.blazorserver.com) at Verbose, with no setting that turned it off, and seeded every
/// account with a Gravatar picture. Both were hard-coded addresses in C#, and nothing failed when
/// they arrived. This test is what fails now: a new address anywhere in the C#, appsettings,
/// web.config or project files under src/ is red until somebody adds it to the list on purpose.
/// <para>
/// Addresses that belong to an installation - the database, the storage account, the mail domain,
/// the external-login credentials - come from configuration and are never written here, so they
/// never appear in this list. Pages and scripts (.razor, .js, .css) are not scanned: they run in
/// the visitor's browser, not on the server.
/// </para>
/// </remarks>
[TestFixture]
public class OutboundAddressTests
{
    /// <summary>
    /// The only hard-coded addresses allowed under src/, each pinned to the file it lives in. An
    /// address is the scheme and host (and port, if any), lower-cased; the path is not part of it.
    /// </summary>
    private static readonly AllowedAddress[] Allowed =
    [
        new("src/Infrastructure/Configurations/MailSettings.cs", "https://api.mailgun.net",
            "Mailgun's API host for US-region domains: the mail transport. Called only when mail " +
            "delivery resolves to Mailgun, which needs Mail:Domain and Mail__ApiKey to be set."),
        new("src/Infrastructure/Configurations/MailSettings.cs", "https://api.eu.mailgun.net",
            "Mailgun's API host for EU-region domains (Mail:Region = EU). Same conditions as above."),
        new("src/Server.UI/DependencyInjection.cs", "https://aka.ms",
            "A documentation link in the stock HSTS comment. Never fetched."),
    ];

    /// <summary>
    /// The developer's own gitignored settings file. It is local to one machine, is never
    /// committed and never deployed (a server's values come from its web.config), and may well
    /// hold a localhost URL.
    /// </summary>
    private const string LocalSettingsFile = "appsettings.Development.json";

    private static readonly Regex Address = new(
        @"\b(?:https?|wss?|ftp)://[^\s""'<>`{}()\[\]\\,;|/?#]*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AppSettings = new(
        @"^appsettings(\..+)?\.json$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex WebConfig = new(
        @"^web(\..+)?\.config$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [Test]
    public void EveryHardCodedAddressInSrc_IsOnTheAllowedList()
    {
        var unlisted = Scan()
            .Where(found => !Allowed.Any(allowed => allowed.File == found.File && allowed.Address == found.Address))
            .Select(found => $"{found.File}:{found.Line} {found.Address}")
            .ToList();

        unlisted.Should().BeEmpty(
            "nothing in src/ may call out to an address nobody chose. If this one is meant, add it " +
            "to OutboundAddressTests.Allowed with the reason the server needs it");
    }

    [Test]
    public void EveryAllowedAddress_IsStillInItsFile()
    {
        var found = Scan().Select(f => (f.File, f.Address)).ToHashSet();

        Allowed.Where(allowed => !found.Contains((allowed.File, allowed.Address)))
            .Select(allowed => $"{allowed.File} {allowed.Address}")
            .Should().BeEmpty("an entry for an address that is gone would quietly allow its return; remove it");
    }

    [Test]
    public void TheScan_ReadsTheCSharpTheSettingsAndTheProjectFiles()
    {
        var files = ScannedFiles().Select(Relative).ToList();

        files.Should().Contain("src/Infrastructure/Configurations/MailSettings.cs");
        files.Should().Contain("src/Server.UI/appsettings.json");
        files.Should().Contain("src/Server.UI/appsettings.Production.json");
        files.Should().Contain("src/Infrastructure/Infrastructure.csproj");
        files.Should().NotContain(file => file.Contains("/bin/") || file.Contains("/obj/"));
    }

    private static IEnumerable<FoundAddress> Scan()
    {
        foreach (var path in ScannedFiles())
        {
            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (Match match in Address.Matches(lines[i]))
                    yield return new FoundAddress(Relative(path), i + 1, match.Value.ToLowerInvariant());
            }
        }
    }

    private static IEnumerable<string> ScannedFiles()
    {
        var src = Path.Combine(CommittedAppSettingsTests.RepositoryRoot(), "src");
        return Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path))
            .Where(path =>
            {
                var name = Path.GetFileName(path);
                return name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                       || name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                       || WebConfig.IsMatch(name)
                       || (AppSettings.IsMatch(name) && !name.Equals(LocalSettingsFile, StringComparison.OrdinalIgnoreCase));
            });
    }

    private static bool IsBuildOutput(string path) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment.Equals("bin", StringComparison.OrdinalIgnoreCase)
                            || segment.Equals("obj", StringComparison.OrdinalIgnoreCase));

    private static string Relative(string path) =>
        Path.GetRelativePath(CommittedAppSettingsTests.RepositoryRoot(), path).Replace('\\', '/');

    private sealed record AllowedAddress(string File, string Address, string Reason);

    private sealed record FoundAddress(string File, int Line, string Address);
}
#nullable restore
