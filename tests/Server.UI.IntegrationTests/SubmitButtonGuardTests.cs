#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Server.UI.IntegrationTests;

/// <summary>
/// Pass 52: every button that submits a form shows its progress through <c>GxSubmitButton</c>
/// (<c>src/Server.UI/Components/Common/GxSubmitButton.razor</c>). This reads every <c>.razor</c> file under <c>src</c> and
/// fails on a <c>MudButton</c> with <c>ButtonType.Submit</c>, on a hand-written <c>&lt;button type="submit"&gt;</c>, and on
/// any <c>MudLoadingButton</c> (removed in pass 52; GxSubmitButton replaced it).
/// </summary>
/// <remarks>
/// A tag is read whole, across lines, up to its closing <c>&gt;</c>, so an attribute on its fourth line counts.
/// It is a source scan, not a render: it cannot see a submit button built in C#, and it does not judge buttons that
/// do server work without submitting (dialog Save buttons, row actions). Those are listed in the pass 52 report.
/// </remarks>
[TestFixture]
public class SubmitButtonGuardTests
{
    /// <summary>
    /// Exceptions, by path relative to <c>src</c> (forward slashes). Each entry needs a comment saying why the button may
    /// not show progress. Empty: there is no exception today.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> AllowList = new Dictionary<string, string>
    {
        // ["Server.UI/Pages/Example.razor"] = "why this submit button may stay a plain MudButton",
    };

    private static readonly Regex MudButtonTag = new(@"<MudButton\b[^>]*>", RegexOptions.Singleline);
    private static readonly Regex SubmitType = new(@"ButtonType\s*=\s*""?@?\(?\s*(MudBlazor\.)?ButtonType\.Submit", RegexOptions.None);
    private static readonly Regex RawSubmitButton = new(@"<button\b[^>]*\btype\s*=\s*[""']submit[""'][^>]*>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
    private static readonly Regex LoadingButton = new(@"<MudLoadingButton\b", RegexOptions.None);

    [Test]
    public void NoRazorFile_HasASubmitButtonThatIsNotAGxSubmitButton_OrAMudLoadingButton()
    {
        var src = Path.Combine(RepositoryRoot(), "src");
        var offences = new List<string>();

        foreach (var file in Directory.EnumerateFiles(src, "*.razor", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(src, file).Replace('\\', '/');
            if (relative.Split('/').Any(part => part is "bin" or "obj") || AllowList.ContainsKey(relative)) continue;

            var text = File.ReadAllText(file);
            foreach (Match tag in MudButtonTag.Matches(text))
            {
                if (SubmitType.IsMatch(tag.Value)) offences.Add($"{relative}:{LineOf(text, tag.Index)} MudButton with ButtonType.Submit");
            }
            foreach (Match tag in RawSubmitButton.Matches(text))
            {
                offences.Add($"{relative}:{LineOf(text, tag.Index)} <button type=\"submit\">");
            }
            foreach (Match tag in LoadingButton.Matches(text))
            {
                offences.Add($"{relative}:{LineOf(text, tag.Index)} MudLoadingButton");
            }
        }

        offences.Should().BeEmpty(
            "a submit button shows progress through GxSubmitButton (pass 52); add a commented AllowList entry only for a real exception");
    }

    [Test]
    public void TheScan_SeesThePlaces_ItIsMeantToCatch()
    {
        // The regexes, on the three shapes they exist for - so a scan that silently matched nothing could not pass above.
        var multiLineTag = MudButtonTag.Matches("<MudButton Variant=\"Variant.Filled\"\n    ButtonType=\"ButtonType.Submit\"\n    FullWidth=\"true\">").Single().Value;
        SubmitType.IsMatch(multiLineTag).Should().BeTrue("the attribute is on the tag's second line");
        RawSubmitButton.IsMatch("<button type=\"submit\" class=\"x\">Go</button>").Should().BeTrue();
        LoadingButton.IsMatch("<MudLoadingButton Loading=\"@_saving\">").Should().BeTrue();
        SubmitType.IsMatch("<MudButton ButtonType=\"@ButtonType\">").Should().BeFalse("GxSubmitButton's own MudButton passes its parameter through");
    }

    private static int LineOf(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CleanArchitecture.Blazor.slnx"))) return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not find CleanArchitecture.Blazor.slnx above the test assembly.");
    }
}
#nullable restore
