namespace CleanArchitecture.Blazor.Application.Common.Interfaces.Transactions;

/// <summary>
/// Handles a <see cref="PostingEvent"/> inside the transaction that raised it (Pass 58).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>Write through <c>IApplicationDbContextFactory</c> as any handler does: inside
/// the unit of work it returns the shared context, and disposing it does nothing.</description></item>
/// <item><description>To reject the posting for a business reason ("insufficient stock"), throw
/// <c>PostingRejectedException</c>: everything rolls back and the user sees its message. Any other
/// exception is a fault.</description></item>
/// <item><description>No external I/O (mail, HTTP, files) - that belongs to an ordinary
/// <see cref="DomainEvent"/> handler, which runs after the commit.</description></item>
/// <item><description>No parallel calls on the shared context.</description></item>
/// <item><description>To depend on another module's result, subscribe to that module's event.</description></item>
/// </list>
/// Registered by <c>AddPostingEventHandlers</c>, scoped.
/// </remarks>
public interface IPostingEventHandler<in TEvent>
    where TEvent : PostingEvent
{
    ValueTask Handle(TEvent postingEvent, CancellationToken cancellationToken);
}

/// <summary>Dispatches one posting event to every handler registered for its runtime type.</summary>
public interface IPostingEventDispatcher
{
    ValueTask DispatchAsync(PostingEvent postingEvent, CancellationToken cancellationToken);
}
