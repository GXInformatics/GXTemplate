using System.Linq.Expressions;

namespace CleanArchitecture.Blazor.Domain.Common.Entities;

/// <summary>
/// A row that belongs to one branch (Pass 59). Reads are filtered to the user's branch read set by
/// <c>QueryFilters.Branch</c>; writes must come from an <c>IBranchCommand</c> whose branch the user may
/// write to. <see cref="BranchId"/> is never stamped from ambient state, and it never changes once set.
/// </summary>
public interface IMustHaveBranch
{
    int BranchId { get; }
}

/// <summary>
/// A row that several branches see, such as an inter-branch transfer (Pass 59). It states its own
/// visibility rule instead of a single <c>BranchId</c>.
/// </summary>
/// <typeparam name="TSelf">The implementing entity.</typeparam>
public interface IBranchVisibility<TSelf> where TSelf : IBranchVisibility<TSelf>
{
    /// <summary>
    /// Whether a row is visible to a reader of the given branches, for example
    /// <c>(t, ids) =&gt; ids.Contains(t.FromBranchId) || ids.Contains(t.ToBranchId)</c>. It may use only the
    /// entity's own columns (no navigations), so that it composes with <c>ForUpdate()</c>.
    /// </summary>
    static abstract Expression<Func<TSelf, int[], bool>> VisibleTo { get; }

    /// <summary>
    /// The branches this row involves. A branch command may write the row if its branch is one of them,
    /// and saving it extends the command's grant to the others for the rest of the unit of work.
    /// </summary>
    IEnumerable<int> InvolvedBranchIds { get; }
}
