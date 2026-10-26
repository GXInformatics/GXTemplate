#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using FluentAssertions;
using FluentAssertions.Execution;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.Configurations;

/// <summary>
/// The app carries its own ICU, so IANA time zone ids ("Africa/Lagos") resolve on every Windows server, including
/// Windows Server 2019 and older, which ship no ICU. Without it the startup check refuses DefaultTimeZone and IIS
/// answers "500.30 - app failed to start" before the business database is created (FMS staging, 2026-10-26).
/// </summary>
/// <remarks>
/// Asks MSBuild for the evaluated items, as ServerUiPublishTests does, rather than reading Server.UI.csproj's text.
/// The runtime looks for exactly the ICU version the option names, so a package upgraded without the option (or the
/// reverse) fails at startup with "Failed to load app-local ICU" - on the server, not here. This test makes it fail here.
/// </remarks>
[TestFixture]
public class AppLocalIcuTests
{
    private const string Package = "Microsoft.ICU.ICU4C.Runtime";
    private const string Option = "System.Globalization.AppLocalIcu";

    [Test]
    public void TheAppCarriesItsOwnIcuAndAsksForThatVersion()
    {
        var project = Path.Combine(CommittedAppSettingsTests.RepositoryRoot(), "src", "Server.UI", "Server.UI.csproj");
        var items = Evaluate(project);

        var package = items.GetProperty("PackageReference").EnumerateArray()
            .Where(i => string.Equals(i.GetProperty("Identity").GetString(), Package, StringComparison.OrdinalIgnoreCase)).ToList();
        var option = items.TryGetProperty("RuntimeHostConfigurationOption", out var options)
            ? options.EnumerateArray().Where(i => i.GetProperty("Identity").GetString() == Option).ToList()
            : new();

        using var _ = new AssertionScope();
        package.Should().ContainSingle($"Server.UI references {Package}, which publishes the ICU binaries beside the app");
        option.Should().ContainSingle($"Server.UI sets {Option}, which tells .NET to load them instead of the system's");
        if (package.Count == 1 && option.Count == 1)
            option[0].GetProperty("Value").GetString().Should().Be(package[0].GetProperty("Version").GetString(),
                "the runtime loads exactly the ICU version the option names, so it must be the package's");
        items.TryGetProperty("RuntimeHostConfigurationOption", out var all);
        if (all.ValueKind == JsonValueKind.Array)
            all.EnumerateArray().Select(i => i.GetProperty("Identity").GetString())
                .Should().NotContain("System.Globalization.UseNls", "NLS mode would ignore the app's ICU");
    }

    private static JsonElement Evaluate(string project)
    {
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet";
        var start = new ProcessStartInfo(dotnet,
            $"msbuild \"{project}\" -getItem:PackageReference -getItem:RuntimeHostConfigurationOption -nologo")
        {
            WorkingDirectory = Path.GetDirectoryName(project)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var name in start.Environment.Keys
                     .Where(k => k.StartsWith("MSBuild", StringComparison.OrdinalIgnoreCase)).ToList())
            start.Environment.Remove(name);

        using var msbuild = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {dotnet}.");
        var output = msbuild.StandardOutput.ReadToEnd();
        var error = msbuild.StandardError.ReadToEnd();
        msbuild.WaitForExit();

        msbuild.ExitCode.Should().Be(0, $"dotnet msbuild -getItem should evaluate the project. Output: {output} {error}");
        return JsonDocument.Parse(output).RootElement.GetProperty("Items").Clone();
    }
}
#nullable restore
