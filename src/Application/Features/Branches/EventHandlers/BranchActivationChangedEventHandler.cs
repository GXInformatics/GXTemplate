using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;

namespace CleanArchitecture.Blazor.Application.Features.Branches.EventHandlers;

/// <summary>
/// Drops every user's cached context once a branch's activation change has committed (Pass 60b): the branch left, or
/// rejoined, its users' write sets (owner decision 11 of pass 59). Before pass 60b the command did this itself after
/// its save; inside a unit of work that would run before the commit, and a circuit reloading in between would cache
/// the old state. Writes never depended on it: BranchAccessBehaviour reads the branch fresh on every send.
/// </summary>
public sealed class BranchActivationChangedEventHandler(IUserContextInvalidator userContextInvalidator)
    : INotificationHandler<BranchActivationChangedEvent>
{
    public ValueTask Handle(BranchActivationChangedEvent notification, CancellationToken cancellationToken)
    {
        userContextInvalidator.InvalidateAll();
        return ValueTask.CompletedTask;
    }
}
