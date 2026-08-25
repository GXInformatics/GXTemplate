// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;

namespace CleanArchitecture.Blazor.Application.Features.Documents.EventHandlers;

public class DocumentCreatedEventHandler : INotificationHandler<DocumentCreatedEvent>
{
    private readonly ILogger<DocumentCreatedEventHandler> _logger;
    private readonly IUserContextAccessor _userContextAccessor;

    public DocumentCreatedEventHandler(
        IUserContextAccessor userContextAccessor,
        ILogger<DocumentCreatedEventHandler> logger
    )
    {
        _userContextAccessor = userContextAccessor;
        _logger = logger;
    }

    public ValueTask Handle(DocumentCreatedEvent notification, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Document upload successful. DocumentId: {DocumentId}, User: {@UserName}",
            notification.Item.Id,
            _userContextAccessor.Current?.UserName);

        return ValueTask.CompletedTask;
    }
}
