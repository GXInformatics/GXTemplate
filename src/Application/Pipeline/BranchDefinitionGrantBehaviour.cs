using CleanArchitecture.Blazor.Application.Common.Branches;

namespace CleanArchitecture.Blazor.Application.Pipeline;

/// <summary>
/// Opens the branch grant of a branch-defining command (Pass 60b, item 4b): empty until the handler saves the branch
/// and calls <see cref="BranchGrant.ExtendDefining"/>, then that one branch, for the rest of the command.
/// </summary>
/// <remarks>
/// <b>Placed immediately after <c>BranchAccessBehaviour</c>, outside <c>TransactionBehaviour</c>.</b> Posting events are
/// dispatched by <c>TransactionBehaviour</c> after the handler returns, so a grant opened inside the handler would have
/// ended before the handlers that need it run. Here it spans the handler, every retry and every posting-event round.
/// A retried attempt may leave a rolled-back branch id in the grant; identity values are never reused, so no other
/// branch can ever carry it. Constrained to <see cref="IBranchDefiningCommand"/>, so no other request's pipeline changes.
/// </remarks>
public sealed class BranchDefinitionGrantBehaviour<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : class, IMessage, IBranchDefiningCommand
{
    public async ValueTask<TResponse> Handle(TRequest request, MessageHandlerDelegate<TRequest, TResponse> next, CancellationToken cancellationToken)
    {
        using (BranchGrant.BeginDefining())
        {
            return await next(request, cancellationToken).ConfigureAwait(false);
        }
    }
}
