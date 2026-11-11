using CleanArchitecture.Blazor.Domain.Common;

namespace CleanArchitecture.Blazor.Domain.Events;

// Pass 60b (item 4a). Raised by AddEditBranchCommand inside its unit of work. The first three are posting events:
// handled in the same transaction by IPostingEventHandler<T>, so a module can veto a branch change by throwing
// PostingRejectedException (everything rolls back, the user sees its message) or write rows for the branch (the
// command grants it; README "Branches"). They are template events, so any module may name them.

/// <summary>A branch was created. <see cref="BranchId"/> is its new id, saved in this transaction before the event is handled.</summary>
/// <remarks>Raised before the save, when the id does not exist yet, so it reads the id from the branch when asked.</remarks>
public sealed class BranchCreatedEvent(Branch branch) : PostingEvent
{
    private readonly Branch _branch = branch;

    public int BranchId => _branch.Id;
    public string Code { get; } = branch.Code;
    public string Name { get; } = branch.Name;
    public bool IsActive { get; } = branch.IsActive;
}

/// <summary>
/// A branch's code or name changed, or it was reactivated. A deactivation raises <see cref="BranchDeactivatingEvent"/>
/// instead, and this one too only if the code or name also changed.
/// </summary>
public sealed class BranchChangedEvent(int branchId, string code, string name, string oldCode, string oldName,
    bool isActive, bool wasActive) : PostingEvent
{
    public int BranchId { get; } = branchId;
    public string Code { get; } = code;
    public string Name { get; } = name;
    public string OldCode { get; } = oldCode;
    public string OldName { get; } = oldName;
    public bool IsActive { get; } = isActive;
    public bool WasActive { get; } = wasActive;
    public bool Reactivated => IsActive && !WasActive;
}

/// <summary>
/// A branch is being deactivated. A handler that throws <c>PostingRejectedException</c> ("Branch ABJ still has open stock
/// counts.") keeps it active.
/// </summary>
public sealed class BranchDeactivatingEvent(int branchId, string code) : PostingEvent
{
    public int BranchId { get; } = branchId;
    public string Code { get; } = code;
}

/// <summary>
/// A branch's active flag changed and the change committed. An ordinary event, published after the commit: the
/// template's handler drops every user's cached context, because the branch left or rejoined their write sets.
/// </summary>
public sealed class BranchActivationChangedEvent(int branchId, bool isActive) : DomainEvent
{
    public int BranchId { get; } = branchId;
    public bool IsActive { get; } = isActive;
}
