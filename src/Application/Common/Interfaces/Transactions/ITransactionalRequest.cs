namespace CleanArchitecture.Blazor.Application.Common.Interfaces.Transactions;

/// <summary>
/// Marks a request that runs inside a unit of work: one context, one connection, one transaction,
/// with its posting events handled inside it (Pass 58). See <c>TransactionBehaviour</c> and the
/// README section "Posting transactions".
/// </summary>
/// <remarks>
/// <b>The handler must be safe to run more than once.</b> A deadlock or serialisation failure
/// retries the whole attempt, and Mediator's pipeline calls the SAME handler instance again with
/// the SAME request object. So a transactional handler holds no state between attempts and never
/// mutates the request.
/// <para>
/// Sent while a unit of work is already active, the request JOINS it: no transaction, commit,
/// event dispatch, retry or idempotency claim of its own, and any failure fails the outer one.
/// </para>
/// </remarks>
public interface ITransactionalRequest
{
}

/// <summary>
/// A transactional request that is deduplicated by <see cref="RequestId"/> (Pass 58).
/// </summary>
/// <remarks>
/// The client creates the id once per intended action (one per document form, kept across a
/// reconnect) and sends the same id on every retry. The first attempt that commits records its
/// response; any later request with the id returns that response without running the handler,
/// and one sent while the first is still running waits for it. Every Post, Approve and Reverse
/// command implements this.
/// <para>
/// It stops ONE request posting twice. It does not stop two different requests posting one
/// document - the handler's document lock does that (see the README, "The document lock").
/// </para>
/// <para>
/// The response must be <c>Result</c> or <c>Result&lt;T&gt;</c>: only a successful response is
/// recorded, and it is rebuilt from its <c>Data</c>.
/// </para>
/// </remarks>
public interface IIdempotentRequest : ITransactionalRequest
{
    Guid RequestId { get; }
}
