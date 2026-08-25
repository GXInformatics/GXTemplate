using CleanArchitecture.Blazor.Domain.Common;
using CleanArchitecture.Blazor.Domain.Common.Entities;
using Mediator;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CleanArchitecture.Blazor.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Interceptor for dispatching domain events when saving changes in the database.
/// <para>
/// Deleted entities are dispatched from <c>SavingChanges</c>, because the change tracker detaches
/// them once the save completes and their events would otherwise be unreachable. Everything else is
/// dispatched from <c>SavedChanges</c>.
/// </para>
/// <para>
/// <b>This interceptor opens no transaction.</b> It used to wrap both dispatch blocks in one, but
/// neither wrapped the save: the base <c>SavingChangesAsync</c>/<c>SavedChangesAsync</c> calls are
/// pass-throughs, so each transaction enclosed only the publishing and committed nothing. They also
/// made this interceptor incompatible with <see cref="AuditableEntityInterceptor"/>, which holds a
/// real transaction across the save - a second <c>BeginTransaction</c> on the same connection throws.
/// </para>
/// <para>
/// It also skips entirely while the audit rows are being written (<see cref="AuditWriteScope"/>), so
/// domain events are published once, from the outer save, after the audit transaction has committed.
/// </para>
/// </summary>
public class DispatchDomainEventsInterceptor : SaveChangesInterceptor
{
    private readonly IMediator _mediator;

    /// <summary>
    /// Initializes a new instance of the <see cref="DispatchDomainEventsInterceptor"/> class.
    /// </summary>
    /// <param name="mediator">The mediator instance used for publishing domain events.</param>
    public DispatchDomainEventsInterceptor(IMediator mediator)
    {
        _mediator = mediator;
    }

    /// <inheritdoc/>
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        var context = eventData.Context;
        if (context is not null && !AuditWriteScope.IsActive(context))
        {
            await DispatchAsync(context, EntityState.Deleted, matches: true, cancellationToken);
        }

        return await base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    /// <inheritdoc/>
    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        var saveResult = await base.SavedChangesAsync(eventData, result, cancellationToken);

        var context = eventData.Context;
        if (context is not null && !AuditWriteScope.IsActive(context))
        {
            await DispatchAsync(context, EntityState.Deleted, matches: false, cancellationToken);
        }

        return saveResult;
    }

    /// <inheritdoc/>
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        var context = eventData.Context;
        if (context is not null && !AuditWriteScope.IsActive(context))
        {
            DispatchAsync(context, EntityState.Deleted, matches: true, CancellationToken.None)
                .GetAwaiter().GetResult();
        }

        return base.SavingChanges(eventData, result);
    }

    /// <inheritdoc/>
    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        var saveResult = base.SavedChanges(eventData, result);

        var context = eventData.Context;
        if (context is not null && !AuditWriteScope.IsActive(context))
        {
            DispatchAsync(context, EntityState.Deleted, matches: false, CancellationToken.None)
                .GetAwaiter().GetResult();
        }

        return saveResult;
    }

    /// <summary>
    /// Publishes and clears the domain events of every tracked <see cref="BaseEntity"/> whose state
    /// does (or does not) equal <paramref name="state"/>.
    /// </summary>
    private async Task DispatchAsync(
        DbContext context, EntityState state, bool matches, CancellationToken cancellationToken)
    {
        var entities = context.ChangeTracker
            .Entries<BaseEntity>()
            .Where(e => e.Entity.DomainEvents.Any() && (e.State == state) == matches)
            .Select(e => e.Entity)
            .ToList();

        if (entities.Count == 0) return;

        var domainEvents = entities.SelectMany(e => e.DomainEvents).ToList();
        entities.ForEach(e => e.ClearDomainEvents());

        foreach (var domainEvent in domainEvents)
        {
            await _mediator.Publish(domainEvent, cancellationToken);
        }
    }
}
