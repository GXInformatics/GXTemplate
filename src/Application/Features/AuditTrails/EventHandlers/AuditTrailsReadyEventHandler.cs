// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.


namespace CleanArchitecture.Blazor.Application.Features.AuditTrails.EventHandlers;

/// <summary>
/// Persists audit trails asynchronously; failures are logged but not propagated.
/// </summary>
public class AuditTrailsReadyEventHandler : INotificationHandler<AuditTrailsReadyEvent>
{
    private readonly IApplicationDbContextFactory _dbContextFactory;
    private readonly ILogger<AuditTrailsReadyEventHandler> _logger;

    public AuditTrailsReadyEventHandler(IApplicationDbContextFactory dbContextFactory, ILogger<AuditTrailsReadyEventHandler> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    public async ValueTask Handle(AuditTrailsReadyEvent notification, CancellationToken cancellationToken)
    {
        if (notification.AuditTrails.Count == 0)
            return;

        // This handler is already invoked off the request path: ChannelBasedNoWaitPublisher queues
        // it and drains the queue on its own background task. Awaiting the write here therefore
        // costs the caller nothing, and it is what makes completing that channel mean the rows are
        // persisted - an extra Task.Run would return on scheduling and drop the rows at shutdown.
        try
        {
            // Create new scope to avoid disposed service provider issues
            await using var db = await _dbContextFactory.CreateAsync(CancellationToken.None);
            await db.AuditTrails.AddRangeAsync(notification.AuditTrails, CancellationToken.None);
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist {Count} audit trails asynchronously", notification.AuditTrails.Count);
        }
    }
}
