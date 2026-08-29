using System.Text;
using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Infrastructure.Services.Mail;
using Xunit;

namespace CleanArchitecture.Blazor.Infrastructure.UnitTests.Mail;

/// <summary>
/// The startup guard: every template the code can name is present, readable and parseable.
/// </summary>
/// <remarks>
/// Without it, a template lost between build and deployment surfaces on somebody's password reset -
/// and in this application it surfaces almost invisibly, because the send happens inside a
/// notification handler and the notification publisher swallows handler exceptions.
/// </remarks>
[Collection(TemplateFileCollection.Name)]
public class MailTemplateGuardTests : IDisposable
{
    private readonly string _templatesDirectory =
        Path.Combine(AppContext.BaseDirectory, MailTemplates.Directory);

    private readonly List<string> _written = [];
    private readonly List<(string Path, byte[] Content)> _moved = [];

    public void Dispose()
    {
        foreach (var path in _written)
        {
            try { File.Delete(path); } catch (IOException) { }
        }

        // Put back anything a test removed, so the shipped set is intact for every other test.
        foreach (var (path, content) in _moved)
        {
            try { File.WriteAllBytes(path, content); } catch (IOException) { }
        }

        GC.SuppressFinalize(this);
    }

    private string WriteRaw(byte[] content)
    {
        var name = "guard-" + Guid.NewGuid().ToString("N");
        var path = Path.Combine(_templatesDirectory, name + MailTemplates.Extension);
        File.WriteAllBytes(path, content);
        _written.Add(path);
        return name;
    }

    // ------------------------------------------------------------------ the shipped set

    [Fact]
    public void TheShippedTemplateSetPasses()
    {
        // The whole point of the guard, and a real check on the build: if the csproj wildcard stops
        // copying .sbn files to the output directory, this is what says so.
        var problems = MailTemplateGuard.Check();

        Assert.Empty(problems);
    }

    [Fact]
    public void TheShippedSetIsTheThreeTemplatesTheApplicationSends()
    {
        // _authenticatorcode is deliberately absent: Pass 12B deleted it with
        // SendFactorCodeNotification, which had no publisher anywhere.
        Assert.Equal(
            new[] { MailTemplates.RecoveryPassword, MailTemplates.UserActivation, MailTemplates.Welcome }
                .OrderBy(x => x, StringComparer.Ordinal),
            MailTemplates.All);
    }

    [Fact]
    public void DiscoveryDoesNotMistakeTheExtensionOrDirectoryForATemplateName()
    {
        Assert.DoesNotContain(MailTemplates.Extension, MailTemplates.All);
        Assert.DoesNotContain(MailTemplates.Directory, MailTemplates.All);
    }

    // ------------------------------------------------------------------ the three failures

    [Fact]
    public void AMissingTemplateIsReported_NamingIt()
    {
        var absent = "guard-missing-" + Guid.NewGuid().ToString("N");

        var problem = MailTemplateGuard.Check(absent);

        Assert.NotNull(problem);
        Assert.Contains(absent, problem);
        Assert.Contains("missing", problem);
    }

    [Fact]
    public void ADeletedShippedTemplateIsReported()
    {
        // The deploy-time file-loss case, done to a real shipped template and put back afterwards.
        var path = MailTemplateRenderer.PathFor(MailTemplates.Welcome);
        var original = File.ReadAllBytes(path);
        _moved.Add((path, original));
        File.Delete(path);

        var problems = MailTemplateGuard.Check();

        Assert.Contains(problems, p => p.Contains(MailTemplates.Welcome) && p.Contains("missing"));
    }

    [Fact]
    public void AFileThatIsNotValidUtf8IsReported()
    {
        // 0xC3 starts a two-byte sequence and 0x28 cannot continue it. A default UTF8 decode would
        // silently substitute U+FFFD and report nothing; strict decoding is what catches this.
        var template = WriteRaw([0x48, 0x69, 0x20, 0xC3, 0x28, 0x21]);

        var problem = MailTemplateGuard.Check(template);

        Assert.NotNull(problem);
        Assert.Contains("not valid UTF-8", problem);
    }

    [Fact]
    public void AFileAlreadyMangledByALossyReEncodeIsReported()
    {
        // The subtler half, and the one presence checks and strict decoding both miss: this file IS
        // valid UTF-8. It is what you get when a template containing U+2019 was decoded as the wrong
        // codepage, the decoder substituted U+FFFD, and the result was saved back. It exists, it has
        // a plausible length, it decodes cleanly - and it renders replacement characters to every
        // customer. The shipped templates contain U+2019 in "Here's" and "didn't", so this is the
        // realistic accident, not a contrived one.
        var template = WriteRaw(Encoding.UTF8.GetBytes("<p>Here�s your code</p>"));

        var problem = MailTemplateGuard.Check(template);

        Assert.NotNull(problem);
        Assert.Contains("replacement character", problem);
    }

    [Fact]
    public void AnUnparseableTemplateIsReported()
    {
        // "{{ this is not valid scriban" is NOT a parse error - Scriban accepts an unterminated tag and
        // a bare expression chain. An unclosed if IS one, and is the realistic accident: a template
        // edited mid-block and saved.
        var template = WriteRaw(Encoding.UTF8.GetBytes("{{ if true }}<p>hello</p>"));

        var problem = MailTemplateGuard.Check(template);

        Assert.NotNull(problem);
        Assert.Contains("does not parse", problem);
    }

    [Fact]
    public void AWellFormedTemplateWithRealPunctuationPasses()
    {
        // The paired negative for the encoding checks: correctly encoded U+2019 must NOT be
        // reported, or the guard would reject the very templates this application ships.
        var template = WriteRaw(Encoding.UTF8.GetBytes("<p>Here’s your code, {{ user_name }}</p>"));

        Assert.Null(MailTemplateGuard.Check(template));
    }
}
