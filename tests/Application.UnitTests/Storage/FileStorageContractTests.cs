#nullable enable
using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Storage;
using CleanArchitecture.Blazor.Application.Common.Models;
using CleanArchitecture.Blazor.Domain.Common.Enums;
using FluentAssertions;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.Storage;

/// <summary>
/// The IFileStorage contract, asserted once and run against every provider.
///
/// The point of the abstraction is that a template must not work on only one provider, so the rules
/// that make it usable - a missing key FAILS rather than succeeding empty, delete is idempotent,
/// overwrite:false destroys nothing and RETURNS the key it actually used, and PublicUrl is the same
/// route either way - are pinned here rather than per implementation.
/// </summary>
public abstract class FileStorageContractTests
{
    protected abstract IFileStorage Storage { get; }

    private static FileUploadRequest Request(string fileName, string content, bool overwrite = false, string? folder = null) =>
        new(fileName, UploadType.Document, Encoding.UTF8.GetBytes(content), overwrite, folder);

    [Test]
    public async Task SaveThenRead_ReturnsByteIdenticalContent()
    {
        // Binary, not text: a byte-for-byte assertion should not be satisfiable by an encoding round trip.
        var payload = Enumerable.Range(0, 512).Select(i => (byte)(i % 256)).ToArray();
        var saved = await Storage.SaveAsync(new FileUploadRequest(
            $"{Guid.NewGuid():N}.bin", UploadType.Document, payload));
        saved.Succeeded.Should().BeTrue(saved.ErrorMessage);

        var read = await Storage.ReadAsync(saved.Data!.StorageKey);

        read.Succeeded.Should().BeTrue(read.ErrorMessage);
        read.Data!.Content.Should().Equal(payload);
        read.Data.FileName.Should().Be(saved.Data.FileName);
    }

    [Test]
    public async Task SavedKey_IsTheAgreedShape_AndPublicUrlIsTheStreamingRoute()
    {
        var saved = await Storage.SaveAsync(Request("avatar.png", "x", overwrite: true, folder: "user-1"));

        saved.Succeeded.Should().BeTrue(saved.ErrorMessage);
        saved.Data!.StorageKey.Should().Be("Documents/user-1/avatar.png");
        // Identical under every provider - that is what lets one endpoint serve them all.
        saved.Data.PublicUrl.Should().Be("/files/Documents/user-1/avatar.png");
        saved.Data.FileName.Should().Be("avatar.png");
        saved.Data.Size.Should().Be(1);
    }

    [Test]
    public async Task ReadingAKeyThatDoesNotResolve_Fails_RatherThanSucceedingEmpty()
    {
        var read = await Storage.ReadAsync($"Documents/{Guid.NewGuid():N}.txt");

        // The specific fix for the silent-download defect: an empty success was indistinguishable
        // from an empty file, so a completely broken read looked like an ordinary one.
        read.Succeeded.Should().BeFalse();
        read.Data.Should().BeNull();
        read.ErrorMessage.Should().NotBeNullOrWhiteSpace();
    }

    [Test]
    public async Task Delete_RemovesTheObject_AndIsIdempotent()
    {
        var saved = await Storage.SaveAsync(Request($"{Guid.NewGuid():N}.txt", "delete-me"));
        saved.Succeeded.Should().BeTrue(saved.ErrorMessage);

        var first = await Storage.DeleteAsync(saved.Data!.StorageKey);
        first.Succeeded.Should().BeTrue(first.ErrorMessage);

        // Asserted through the abstraction, not the filesystem, so it means the same under both providers.
        (await Storage.ReadAsync(saved.Data.StorageKey)).Succeeded.Should().BeFalse();

        // Already absent is the state the caller asked for.
        var second = await Storage.DeleteAsync(saved.Data.StorageKey);
        second.Succeeded.Should().BeTrue(second.ErrorMessage);
    }

    [Test]
    public async Task SaveWithoutOverwrite_DerivesANewKey_AndDestroysNothing()
    {
        var name = $"{Guid.NewGuid():N}.txt";

        var first = await Storage.SaveAsync(Request(name, "original"));
        first.Succeeded.Should().BeTrue(first.ErrorMessage);

        var second = await Storage.SaveAsync(Request(name, "second"));
        second.Succeeded.Should().BeTrue(second.ErrorMessage);

        // The promise is "nothing is destroyed", NOT "the key you asked for is the key you get".
        second.Data!.StorageKey.Should().NotBe(first.Data!.StorageKey);
        second.Data.StorageKey.Should().Be($"Documents/{name[..^4]} (1).txt");

        var original = await Storage.ReadAsync(first.Data.StorageKey);
        Encoding.UTF8.GetString(original.Data!.Content).Should().Be("original");

        var derived = await Storage.ReadAsync(second.Data.StorageKey);
        Encoding.UTF8.GetString(derived.Data!.Content).Should().Be("second");
    }

    [Test]
    public async Task SaveWithOverwrite_ReplacesInPlace()
    {
        var name = $"{Guid.NewGuid():N}.txt";

        var first = await Storage.SaveAsync(Request(name, "before", overwrite: true));
        var second = await Storage.SaveAsync(Request(name, "after", overwrite: true));

        second.Data!.StorageKey.Should().Be(first.Data!.StorageKey);
        var read = await Storage.ReadAsync(second.Data.StorageKey);
        Encoding.UTF8.GetString(read.Data!.Content).Should().Be("after");
    }

    [Test]
    public async Task SaveWithNoData_Fails()
    {
        var result = await Storage.SaveAsync(new FileUploadRequest("empty.txt", UploadType.Document, Array.Empty<byte>()));

        result.Succeeded.Should().BeFalse();
    }

    [TestCase("../appsettings.json")]
    [TestCase("Documents/../../appsettings.json")]
    [TestCase("")]
    [TestCase("   ")]
    public async Task KeysThatAreNotKeys_AreRefused(string key)
    {
        // The streaming endpoint hands a raw route value straight to the provider, so traversal has
        // to die here rather than at whatever the path happens to resolve to.
        (await Storage.ReadAsync(key)).Succeeded.Should().BeFalse();
        (await Storage.DeleteAsync(key)).Succeeded.Should().BeFalse();
    }
}
#nullable restore
