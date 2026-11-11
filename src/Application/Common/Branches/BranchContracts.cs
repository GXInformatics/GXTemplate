using CleanArchitecture.Blazor.Domain.Common.Entities;

namespace CleanArchitecture.Blazor.Application.Common.Branches;

/// <summary>
/// A command that creates or changes branch-scoped rows (<see cref="IMustHaveBranch"/> or
/// <see cref="IBranchVisibility{TSelf}"/>) in one branch (Pass 59).
/// </summary>
/// <remarks>
/// <b>The branch is never taken from ambient state.</b> The form puts the active branch on the command,
/// and <c>BranchAccessBehaviour</c> refuses it unless the user may write there: an assigned active branch,
/// or any active branch for a holder of <c>Permissions.Branches.WriteAll</c>. Saving a branch-scoped row
/// from a command without this interface fails, naming the command.
/// </remarks>
public interface IBranchCommand
{
    int BranchId { get; }
}

/// <summary>
/// A command that saves a branch itself (Pass 60b, item 4b): <c>AddEditBranchCommand</c>, or an application command that
/// wraps it. While it runs, the branch it saves is granted to it and to its posting-event handlers, so they can write
/// that branch's rows in the same transaction (for example, a module creating a new branch's default stock location).
/// </summary>
/// <remarks>
/// <c>BranchDefinitionGrantBehaviour</c> opens an empty grant around the whole command, outside the unit of work, so it
/// spans the handler, every retry and every posting-event round. The handler adds the branch with
/// <see cref="BranchGrant.ExtendDefining"/> once it is saved. A nested branch-defining command joins the outer one's
/// grant, so a wrapping command keeps the grant after the nested send returns. The grant is that one branch, for that
/// one command: it is a bounded exception to "a user writes only where assigned" (decision 9).
/// </remarks>
public interface IBranchDefiningCommand;

/// <summary>
/// The branches the current call chain may write to (Pass 59). Installed by <c>BranchAccessBehaviour</c>
/// after its check, read by the save rules.
/// </summary>
/// <remarks>
/// <b>A holder, emptied at the end</b> - pass 58's A6 pattern. An <c>ExecutionContext</c> captured while a
/// grant was active (a background notification handler) sees no grant afterwards, never a stale one.
/// <para>
/// <b>Nested commands push their own grant</b>, popped when they end. A grant can be <b>extended</b> by
/// saving a two-branch document whose involved branches include a granted one: a posting by the sending
/// branch may then write the receiving branch's derived rows, for the rest of that command.
/// </para>
/// </remarks>
public static class BranchGrant
{
    private sealed class Node
    {
        public readonly HashSet<int> Branches = new();
        public Node? Parent;
        public bool Ended;
        public bool Defining;
    }

    private static readonly AsyncLocal<Node?> Ambient = new();

    /// <summary>The branches the current call chain may write, or null when no grant is active.</summary>
    public static IReadOnlySet<int>? Current
    {
        get
        {
            var node = Ambient.Value;
            while (node is not null && node.Ended) node = node.Parent;
            return node?.Branches;
        }
    }

    /// <summary>Grants <paramref name="branchId"/> to the current call chain until the result is disposed.</summary>
    public static IDisposable Begin(int branchId)
    {
        var node = new Node { Parent = Ambient.Value };
        node.Branches.Add(branchId);
        Ambient.Value = node;
        return new End(node);
    }

    /// <summary>
    /// Opens an EMPTY grant for a branch-defining command (Pass 60b), or joins the one already open further out. Until
    /// <see cref="ExtendDefining"/> adds the saved branch, a branch-scoped write is refused, naming why.
    /// </summary>
    public static IDisposable BeginDefining()
    {
        if (Innermost(n => n.Defining) is not null) return Joined.Instance;
        var node = new Node { Parent = Ambient.Value, Defining = true };
        Ambient.Value = node;
        return new End(node);
    }

    /// <summary>True while the innermost active grant is a branch-defining one that has no branch yet.</summary>
    public static bool IsAwaitingDefinedBranch => Innermost(_ => true) is { Defining: true, Branches.Count: 0 };

    /// <summary>
    /// Grants <paramref name="branchId"/>, just saved, to the branch-defining command that is running (Pass 60b).
    /// </summary>
    /// <exception cref="InvalidOperationException">No branch-defining command is running: the request does not
    /// implement <see cref="IBranchDefiningCommand"/>. Never a silent no-op.</exception>
    public static void ExtendDefining(int branchId)
    {
        var node = Innermost(n => n.Defining) ?? throw new InvalidOperationException(
            "BranchGrant.ExtendDefining was called outside a branch-defining command. The request that saves a branch must " +
            "implement IBranchDefiningCommand, so that BranchDefinitionGrantBehaviour opens the grant around it.");
        node.Branches.Add(branchId);
    }

    private static Node? Innermost(Func<Node, bool> match)
    {
        for (var node = Ambient.Value; node is not null; node = node.Parent)
            if (!node.Ended && match(node)) return node;
        return null;
    }

    private sealed class Joined : IDisposable
    {
        public static readonly Joined Instance = new();
        public void Dispose() { }
    }

    /// <summary>Adds branches to the innermost active grant. No-op without one.</summary>
    public static void Extend(IEnumerable<int> branchIds)
    {
        var node = Ambient.Value;
        while (node is not null && node.Ended) node = node.Parent;
        if (node is null) return;
        foreach (var id in branchIds) node.Branches.Add(id);
    }

    private sealed class End(Node node) : IDisposable
    {
        public void Dispose()
        {
            node.Ended = true;
            if (ReferenceEquals(Ambient.Value, node)) Ambient.Value = node.Parent;
        }
    }
}

/// <summary>
/// The one sanctioned way to read across branches (Pass 59, amendment B5).
/// </summary>
/// <remarks>
/// <b>Removes only the branch filter, from one query, after checking a permission the caller names.</b>
/// <c>Permissions.Branches.ViewAll</c> is one such permission; a module may define a narrower one for a
/// single query, for example letting counter staff see a part's stock in other branches without reading
/// those branches' documents. The tenant and soft-delete filters still apply. Writes are unaffected.
/// <para>
/// Never call <c>IgnoreQueryFilters([QueryFilters.Branch])</c> directly: this method is the only use, so
/// every cross-branch read can be found by searching for <c>AcrossBranchesAsync</c>.
/// </para>
/// </remarks>
public interface IBranchQueryScope
{
    /// <summary>
    /// <paramref name="query"/> without the branch filter, if the current user holds
    /// <paramref name="permission"/> (checked fresh).
    /// </summary>
    /// <exception cref="ForbiddenAccessException">The user does not hold the permission, or there is no user.</exception>
    Task<IQueryable<TEntity>> AcrossBranchesAsync<TEntity>(
        IQueryable<TEntity> query, string permission, CancellationToken cancellationToken = default)
        where TEntity : class;
}
