namespace CleanArchitecture.Blazor.Domain.Common;

/// <summary>
/// A domain event handled INSIDE the transaction that raised it (Pass 58).
/// </summary>
/// <remarks>
/// Raised exactly like any other event, with <c>AddDomainEvent</c> on an entity the command saves.
/// The difference is who handles it and when:
/// <list type="bullet">
/// <item><description>An ordinary <see cref="DomainEvent"/> is published through Mediator AFTER the
/// transaction commits, on the background publisher. It may send mail or call out; it cannot
/// change what was committed.</description></item>
/// <item><description>A <see cref="PostingEvent"/> is dispatched inline, in order, to
/// <c>IPostingEventHandler&lt;T&gt;</c> handlers that share the command's context, connection
/// and transaction. Their writes commit or roll back with the command's. This is how Inventory
/// and Accounting react to a posted document.</description></item>
/// </list>
/// It is only meaningful inside a unit of work: saving an entity that carries one outside
/// <c>ITransactionalRequest</c> throws, naming the event, before anything is written.
/// <para>
/// A handler may raise a further posting event on an entity it saves; it is dispatched in the next
/// round of the same transaction. That is the supported way for one module to depend on another
/// module's result: subscribe to that module's event, never to the original one.
/// </para>
/// </remarks>
public abstract class PostingEvent : DomainEvent
{
}
