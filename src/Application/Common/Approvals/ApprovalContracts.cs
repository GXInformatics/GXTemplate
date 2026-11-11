using CleanArchitecture.Blazor.Domain.Common.Entities;
using System.Reflection;

namespace CleanArchitecture.Blazor.Application.Common.Approvals;

/// <summary>
/// The non-generic face of <see cref="IApprovalCommand{THeader}"/>, so <c>ApprovalBehaviour</c> can be constrained
/// to it (Pass 60).
/// </summary>
public interface IApprovalCommand
{
    /// <summary>The header to approve. Its value and preparer are read from the database, never from the command.</summary>
    Guid DocumentId { get; }
}

/// <summary>
/// A command that approves one <typeparamref name="THeader"/> (Pass 60). <c>ApprovalBehaviour</c> locks the header,
/// checks it with <see cref="IApprovalPolicy"/> and grants the approval to the handler, which sets
/// <c>ApprovedById</c> and <c>ApprovedAt</c> (and whatever status the module keeps).
/// </summary>
/// <remarks>
/// Checked at startup (<c>RequestContractRegistry</c>): the command is an <c>IIdempotentRequest</c> (so it runs in a
/// unit of work and cannot approve twice); it is an <c>IBranchCommand</c> when <typeparamref name="THeader"/> is
/// branch-scoped (amendment C1: not otherwise); and every <c>[RequestAuthorize]</c> policy it carries is an
/// <see cref="ApprovalPermissionAttribute">approval permission</see>.
/// </remarks>
public interface IApprovalCommand<THeader> : IApprovalCommand where THeader : class, IApprovable<THeader>;

/// <summary>
/// Marks a <c>Permissions.*</c> constant as one that approves documents (Pass 60). The administrator is never granted
/// one: <c>AdministratorPermissionRegistry</c> fails startup if one is in its granted list.
/// </summary>
/// <remarks>Explicit, rather than a naming convention: a convention would silently miss <c>Authorise</c> or
/// <c>ApproveAboveLimit</c>.</remarks>
[AttributeUsage(AttributeTargets.Field)]
public sealed class ApprovalPermissionAttribute : Attribute;

/// <summary>Finds approval permissions (Pass 60).</summary>
public static class ApprovalPermissions
{
    /// <summary>
    /// Every public static string field marked <see cref="ApprovalPermissionAttribute"/> in <paramref name="assemblies"/>
    /// (nested types included). The application's own are nested classes of <c>Permissions</c>.
    /// </summary>
    public static IReadOnlySet<string> Discover(params Assembly[] assemblies) =>
        assemblies.Distinct()
            .SelectMany(a => a.GetTypes())
            .SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(f => f.FieldType == typeof(string) && f.IsDefined(typeof(ApprovalPermissionAttribute), inherit: false))
            .Select(f => (string?)f.GetValue(null))
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Every approval permission in the application's loaded assemblies: those whose name starts with this
    /// assembly's root (<c>CleanArchitecture.Blazor.</c>, renamed in a generated project), so a module's permissions
    /// in any of its layers are found.
    /// </summary>
    public static IReadOnlySet<string> DiscoverLoaded()
    {
        var name = typeof(ApprovalPermissions).Assembly.GetName().Name ?? string.Empty;
        var root = name.Contains('.') ? name[..name.LastIndexOf('.')] + "." : name;
        return Discover(AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && (a.GetName().Name ?? string.Empty).StartsWith(root, StringComparison.Ordinal))
            .ToArray());
    }
}

/// <summary>The approvable document types in the model: every <c>IApprovable&lt;T&gt;</c>'s code (Pass 60).</summary>
public interface IApprovalDocumentTypes
{
    IReadOnlyList<string> All { get; }
}

/// <summary>Role facts the approval-limit commands need, without Identity in Application (Pass 60).</summary>
public interface IApprovalRoles
{
    Task<bool> ExistsAsync(string roleId, CancellationToken cancellationToken);

    Task<bool> IsAdministratorRoleAsync(string roleId, CancellationToken cancellationToken);
}

/// <summary>Why an approval was refused (Pass 60). Also the reason code that is logged.</summary>
public enum ApprovalRefusal
{
    None,
    System,
    Administrator,
    Preparer,
    AlreadyApproved,
    WrongBranch,
    NoLimit,
    OverLimit
}

/// <summary>The policy's answer.</summary>
public sealed record ApprovalDecision(ApprovalRefusal Refusal, string? Message, decimal? Limit)
{
    public bool Approved => Refusal == ApprovalRefusal.None;

    public static ApprovalDecision Allow(decimal limit) => new(ApprovalRefusal.None, null, limit);

    public static ApprovalDecision Refuse(ApprovalRefusal refusal, string message, decimal? limit = null) => new(refusal, message, limit);
}

/// <summary>
/// The one approval check (Pass 60). Reads everything fresh from the database: the user's role membership, the
/// limits, and the header as stored.
/// </summary>
public interface IApprovalPolicy
{
    /// <param name="header">The header, loaded (and locked) in the current unit of work.</param>
    /// <param name="documentType">Its <c>IApprovable.ApprovalDocumentType</c>.</param>
    /// <param name="userId">The approver.</param>
    /// <param name="branchId">The command's branch, for a branch command; null otherwise.</param>
    Task<ApprovalDecision> DecideAsync(IApprovable header, string documentType, string userId, int? branchId, CancellationToken cancellationToken);
}

/// <summary>
/// The headers the current call chain was allowed to approve (Pass 60): installed by <c>ApprovalBehaviour</c> after
/// the policy passed, ended when the command ends. The save rule refuses a change to <c>ApprovedById</c> or
/// <c>ApprovedAt</c> on any header not in it - the loud failure for a command that approves without the contract.
/// </summary>
public static class ApprovalGrant
{
    private sealed class Node(Guid headerId, string approverId, Node? parent)
    {
        public Guid HeaderId { get; } = headerId;
        public string ApproverId { get; } = approverId;
        public Node? Parent { get; } = parent;
    }

    private static readonly AsyncLocal<Node?> Ambient = new();

    /// <summary>The approver granted <paramref name="headerId"/> in this call chain, or null.</summary>
    public static string? ApproverOf(Guid headerId)
    {
        for (var node = Ambient.Value; node is not null; node = node.Parent)
            if (node.HeaderId == headerId) return node.ApproverId;
        return null;
    }

    public static IDisposable Begin(Guid headerId, string approverId)
    {
        var node = new Node(headerId, approverId, Ambient.Value);
        Ambient.Value = node;
        return new End(node);
    }

    private sealed class End(Node node) : IDisposable
    {
        public void Dispose()
        {
            if (Ambient.Value == node) Ambient.Value = node.Parent;
        }
    }
}
