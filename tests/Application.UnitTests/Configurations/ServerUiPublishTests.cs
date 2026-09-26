#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.Configurations;

/// <summary>
/// appsettings.Development.json holds a developer's local credentials. Gitignoring it keeps it out
/// of the repository; this keeps it out of every publish and MSDeploy package, which a gitignore
/// cannot do.
/// </summary>
[TestFixture]
public class ServerUiPublishTests
{
    /// <summary>
    /// Asks MSBuild what it would do with the file, rather than reading Server.UI.csproj's text: the
    /// Web SDK marks every *.json as publishable content, and only the evaluated item says whether
    /// the project's Update actually overrides that.
    /// </summary>
    /// <remarks>
    /// Evaluates a COPY of the project in a scratch folder that holds both settings files. The real
    /// src/Server.UI may have no appsettings.Development.json (it is gitignored, and a fresh clone
    /// has none), and an Update only applies to an item that exists - so evaluating in place could
    /// see nothing and prove nothing. appsettings.json is evaluated beside it as the control: if the
    /// query stopped seeing content at all, that assertion fails first.
    /// </remarks>
    [Test]
    public void AppSettingsDevelopmentJson_IsNeverPublished()
    {
        var project = Path.Combine(CommittedAppSettingsTests.RepositoryRoot(), "src", "Server.UI", "Server.UI.csproj");
        var scratch = Path.Combine(Path.GetTempPath(), "gx-publish-eval", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            File.Copy(project, Path.Combine(scratch, "Server.UI.csproj"));
            File.WriteAllText(Path.Combine(scratch, "appsettings.json"), "{}");
            File.WriteAllText(Path.Combine(scratch, "appsettings.Development.json"), "{}");

            var content = EvaluateContentItems(Path.Combine(scratch, "Server.UI.csproj"));

            CopyToPublishDirectory(content, "appsettings.json").Should().NotBe("Never",
                "the control: appsettings.json is ordinary publishable content");
            CopyToPublishDirectory(content, "appsettings.Development.json").Should().Be("Never",
                "the local settings file carries this machine's credentials and must never reach a server " +
                "(Server.UI.csproj: <Content Update=\"appsettings.Development.json\" CopyToPublishDirectory=\"Never\" />)");
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    private static string? CopyToPublishDirectory(JsonElement content, string identity)
    {
        var item = content.EnumerateArray()
            .Where(i => string.Equals(i.GetProperty("Identity").GetString(), identity, StringComparison.OrdinalIgnoreCase))
            .ToList();

        item.Should().ContainSingle($"the evaluated project should have exactly one Content item for {identity}");
        return item[0].TryGetProperty("CopyToPublishDirectory", out var value) ? value.GetString() : null;
    }

    private static JsonElement EvaluateContentItems(string project)
    {
        // The dotnet that is running this test, when the runner says which (dotnet test does).
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet";
        var start = new ProcessStartInfo(dotnet, $"msbuild \"{project}\" -getItem:Content -nologo")
        {
            WorkingDirectory = Path.GetDirectoryName(project)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        // A test runner hosted by Visual Studio or by `dotnet test` passes its own MSBuild location
        // down; this evaluation must resolve the SDK the ordinary way, as a command line would.
        foreach (var name in start.Environment.Keys
                     .Where(k => k.StartsWith("MSBuild", StringComparison.OrdinalIgnoreCase)).ToList())
            start.Environment.Remove(name);

        using var msbuild = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {dotnet}.");
        var output = msbuild.StandardOutput.ReadToEnd();
        var error = msbuild.StandardError.ReadToEnd();
        msbuild.WaitForExit();

        msbuild.ExitCode.Should().Be(0, $"dotnet msbuild -getItem:Content should evaluate the project. Output: {output} {error}");
        return JsonDocument.Parse(output).RootElement.GetProperty("Items").GetProperty("Content").Clone();
    }
}
#nullable restore
