// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace CleanArchitecture.Blazor.Application.Features.Documents.EventHandlers;

public class DocumentDeletedEventHandler : INotificationHandler<DocumentDeletedEvent>
{
    private readonly ILogger<DocumentDeletedEventHandler> _logger;
    private readonly IFileStorage _fileStorage;

    public DocumentDeletedEventHandler(ILogger<DocumentDeletedEventHandler> logger, IFileStorage fileStorage)
    {
        _logger = logger;
        _fileStorage = fileStorage;
    }

    public async ValueTask Handle(DocumentDeletedEvent notification, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(notification.Item.StorageKey))
        {
            _logger.LogWarning("The document storage key is null or empty, skipping file deletion.");
            return;
        }

        // Delete by the STORED key. The previous implementation rebuilt a path from the upload type
        // and appended the stored value to it, which already contained that same prefix - so it
        // looked for Files\Documents\Files\Documents\x.png, never found it, and logged a warning
        // instead of deleting anything. Every deleted document left its bytes behind.
        var result = await _fileStorage.DeleteAsync(notification.Item.StorageKey, cancellationToken);
        if (result.Succeeded)
        {
            _logger.LogInformation("File deleted successfully: {StorageKey}", notification.Item.StorageKey);
        }
        else
        {
            _logger.LogError("Failed to delete file {StorageKey}: {Error}",
                notification.Item.StorageKey, result.ErrorMessage);
        }
    }
}
