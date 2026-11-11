using CleanArchitecture.Blazor.Domain.Common;
using CleanArchitecture.Blazor.Domain.Common.Entities;
using CleanArchitecture.Blazor.Infrastructure.Persistence.Transactions;
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
/// <b>Inside a unit of work it publishes nothing</b> (Pass 58). The same two hooks hand the events to
/// the unit of work instead, which dispatches posting events inside the transaction and publishes the
/// rest after the commit. Outside one it behaves exactly as before - except that it refuses, before
/// anything is written, to save an entity carrying a <see cref="PostingEvent"/>: that event exists to
/// be handled inside a transaction, and there is none.
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
            if (UnitOfWorkScope.Find(context) is { } unitOfWork)
            {
                Buffer(unitOfWork, context, EntityState.Deleted, matches: true);
            }
            else
            {
                ThrowIfPostingEventsOutsideUnitOfWork(context);
                await DispatchAsync(context, EntityState.Deleted, matches: true, cancellationToken);
            }
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
            if (UnitOfWorkScope.Find(context) is { } unitOfWork)
            {
                Buffer(unitOfWork, context, EntityState.Deleted, matches: false);
            }
            else
            {
                await DispatchAsync(context, EntityState.Deleted, matches: false, cancellationToken);
            }
        }

        return saveResult;
    }

    /// <inheritdoc/>
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        var context = eventData.Context;
        if (context is not null && !AuditWriteScope.IsActive(context))
        {
            if (UnitOfWorkScope.Find(context) is { } unitOfWork)
            {
                Buffer(unitOfWork, context, EntityState.Deleted, matches: true);
            }
            else
            {
                ThrowIfPostingEventsOutsideUnitOfWork(context);
                DispatchAsync(context, EntityState.Deleted, matches: true, CancellationToken.None)
                    .GetAwaiter().GetResult();
            }
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
            if (UnitOfWorkScope.Find(context) is { } unitOfWork)
            {
                Buffer(unitOfWork, context, EntityState.Deleted, matches: false);
            }
            else
            {
                DispatchAsync(context, EntityState.Deleted, matches: false, CancellationToken.None)
                    .GetAwaiter().GetResult();
            }
        }

        return saveResult;
    }

    /// <summary>
    /// Hands the events of the same entities <see cref="DispatchAsync"/> would publish to the unit of
    /// work, and clears them from the entities (Pass 58).
    /// </summary>
    private static void Buffer(UnitOfWorkSession unitOfWork, DbContext context, EntityState state, bool matches)
    {
        var entities = EntitiesWithEvents(context, state, matches);
        if (entities.Count == 0) return;

        var domainEvents = entities.SelectMany(e => e.DomainEvents).ToList();
        entities.ForEach(e => e.ClearDomainEvents());
        unitOfWork.Buffer(domainEvents);
    }

    /// <summary>
    /// Refuses a save that would publish a <see cref="PostingEvent"/> with no transaction to handle it
    /// in. It runs in SavingChanges, so nothing has been written (Pass 58).
    /// </summary>
    private static void ThrowIfPostingEventsOutsideUnitOfWork(DbContext context)
    {
        var postingEvent = context.ChangeTracker.Entries<IHasDomainEvents>()
            .SelectMany(e => e.Entity.DomainEvents)
            .OfType<PostingEvent>()
            .FirstOrDefault();
        if (postingEvent is null) return;

        throw new InvalidOperationException(
            $"{postingEvent.GetType().Name} is a PostingEvent, which is handled inside the transaction that raises it, " +
            "but this save is not part of a unit of work. Send the command as an ITransactionalRequest.");
    }

    private static List<IHasDomainEvents> EntitiesWithEvents(DbContext context, EntityState state, bool matches) =>
        context.ChangeTracker
            .Entries<IHasDomainEvents>()
            .Where(e => e.Entity.DomainEvents.Any() && (e.State == state) == matches)
            .Select(e => e.Entity)
            .ToList();

    /// <summary>
    /// Publishes and clears the domain events of every tracked <see cref="IHasDomainEvents"/> whose
    /// state does (or does not) equal <paramref name="state"/>.
    /// </summary>
    /// <remarks>
    /// By interface since Pass 55. It was <c>Entries&lt;BaseEntity&gt;()</c>, which silently skipped
    /// any entity not derived from the int-keyed base: an entity keyed by <c>long</c> or
    /// <see cref="Guid"/> could raise events that were never published, and nothing failed.
    /// </remarks>
    private async Task DispatchAsync(
        DbContext context, EntityState state, bool matches, CancellationToken cancellationToken)
    {
        var entities = EntitiesWithEvents(context, state, matches);

        if (entities.Count == 0) return;

        var domainEvents = entities.SelectMany(e => e.DomainEvents).ToList();
        entities.ForEach(e => e.ClearDomainEvents());

        foreach (var domainEvent in domainEvents)
        {
            await _mediator.Publish(domainEvent, cancellationToken);
        }
    }
}
