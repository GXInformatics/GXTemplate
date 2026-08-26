#nullable enable
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Storage;
using CleanArchitecture.Blazor.Application.Common.Models;
using CleanArchitecture.Blazor.Application.Features.Documents.EventHandlers;
using CleanArchitecture.Blazor.Domain.Common.Enums;
using CleanArchitecture.Blazor.Domain.Entities;
using CleanArchitecture.Blazor.Domain.Events;
using CleanArchitecture.Blazor.Infrastructure.Configurations;
using CleanArchitecture.Blazor.Infrastructure.Services.Storage;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.Features.Documents.EventHandlers;

/// <summary>
/// Deleting a document used to leave its bytes on disk, every time, silently.
///
/// The handler rebuilt a path from the upload type and appended the stored value to it - but the
/// stored value ALREADY carried that prefix, so it looked for Files\Documents\Files\Documents\x.png,
/// found nothing, and logged "File not found for deletion" at Warning. It now deletes by the stored
/// key through IFileStorage, which removes the path reconstruction entirely.
/// </summary>
[TestFixture]
public class DocumentDeletedEventHandlerTests
{
    private string _root = null!;
    private IFileStorage _storage = null!;

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
    public async Task Handle_RemovesTheStoredObject()
    {
        var saved = await _storage.SaveAsync(new FileUploadRequest(
            "invoice.png", UploadType.Document, Encoding.UTF8.GetBytes("bytes")));
        saved.Succeeded.Should().BeTrue(saved.ErrorMessage);

        var document = new Document { Id = 1, StorageKey = saved.Data!.StorageKey, PublicUrl = saved.Data.PublicUrl };
        var handler = new DocumentDeletedEventHandler(NullLogger<DocumentDeletedEventHandler>.Instance, _storage);

        await handler.Handle(new DocumentDeletedEvent(document), CancellationToken.None);

        // Asserted through the abstraction rather than the filesystem, so this is a claim about the
        // stored object rather than about one provider's layout.
        var read = await _storage.ReadAsync(saved.Data.StorageKey);
        read.Succeeded.Should().BeFalse("deleting a document must remove its stored object");
    }

    [Test]
    public async Task Handle_WithNoStorageKey_DoesNothing()
    {
        var handler = new DocumentDeletedEventHandler(NullLogger<DocumentDeletedEventHandler>.Instance, _storage);

        var act = async () => await handler.Handle(
            new DocumentDeletedEvent(new Document { Id = 2, StorageKey = null }), CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Test]
    public async Task Handle_WhenTheObjectIsAlreadyGone_Succeeds()
    {
        // Delete is idempotent, so a document whose bytes were already removed is not an error path.
        var saved = await _storage.SaveAsync(new FileUploadRequest(
            "gone.png", UploadType.Document, Encoding.UTF8.GetBytes("bytes")));
        await _storage.DeleteAsync(saved.Data!.StorageKey);

        var handler = new DocumentDeletedEventHandler(NullLogger<DocumentDeletedEventHandler>.Instance, _storage);

        var act = async () => await handler.Handle(
            new DocumentDeletedEvent(new Document { Id = 3, StorageKey = saved.Data.StorageKey }), CancellationToken.None);

        await act.Should().NotThrowAsync();
    }
}
#nullable restore
