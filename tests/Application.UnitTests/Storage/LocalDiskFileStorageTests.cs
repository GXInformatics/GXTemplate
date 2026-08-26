#nullable enable
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Storage;
using CleanArchitecture.Blazor.Application.Common.Models;
using CleanArchitecture.Blazor.Domain.Common.Enums;
using CleanArchitecture.Blazor.Infrastructure.Configurations;
using CleanArchitecture.Blazor.Infrastructure.Services.Storage;
using FluentAssertions;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.Storage;

[TestFixture]
public class LocalDiskFileStorageTests : FileStorageContractTests
{
    private string _root = null!;
    private LocalDiskFileStorage _storage = null!;

    protected override IFileStorage Storage => _storage;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "gx-storage-tests", Guid.NewGuid().ToString("N"));
        _storage = new LocalDiskFileStorage(new StorageSettings { RootPath = _root });
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Test]
    public async Task TheKeyIsAPathUnderTheRoot_WithForwardSlashesInTheKeyItself()
    {
        var saved = await _storage.SaveAsync(new FileUploadRequest(
            "photo.jpg", UploadType.ProfilePicture, Encoding.UTF8.GetBytes("x"), overwrite: true, folder: "user-7"));

        saved.Data!.StorageKey.Should().Be("ProfilePictures/user-7/photo.jpg");
        // The old provider returned this as a Windows path (backslashes, no leading slash) and then
        // rendered it into an img src, where it could never resolve.
        saved.Data.StorageKey.Should().NotContain("\\");
        saved.Data.PublicUrl.Should().StartWith("/files/");

        File.Exists(Path.Combine(_root, "ProfilePictures", "user-7", "photo.jpg")).Should().BeTrue();
    }

    [Test]
    public async Task ATraversalKey_NeverReachesTheFilesystem()
    {
        var outside = Path.Combine(Path.GetDirectoryName(_root)!, $"outside-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(outside, "secret");
        try
        {
            var read = await _storage.ReadAsync($"../{Path.GetFileName(outside)}");

            read.Succeeded.Should().BeFalse();
            File.Exists(outside).Should().BeTrue("a refused read must not have deleted or moved anything");
        }
        finally
        {
            File.Delete(outside);
        }
    }
}
#nullable restore
