namespace CleanArchitecture.Blazor.Domain.Common.Entities;

/// <summary>
/// A document header that one person prepares and another approves, within a value limit (Pass 60).
/// </summary>
/// <remarks>
/// <para>
/// An approval is made only by an <c>IApprovalCommand&lt;THeader&gt;</c>, which <c>ApprovalBehaviour</c> checks
/// against <c>IApprovalPolicy</c> before the handler runs, with the header locked: the approver is not the preparer
/// (nor its creator), holds a limit covering <see cref="ApprovalValue"/> for <see cref="ApprovalDocumentType"/>, is
/// not the administrator, and, for a branch-scoped header, approves in the header's branch. A save that sets
/// <see cref="ApprovedById"/> any other way fails. See the README section "Approvals".
/// </para>
/// <para>
/// Required with it: <see cref="IVersioned"/> (an approval conflicts with a concurrent edit), a <c>[Money]</c>
/// <see cref="ApprovalValue"/>, and no <see cref="ISoftDelete"/>. Branch scope is optional (amendment C1): an application
/// without branches approves without one.
/// </para>
/// </remarks>
public interface IApprovable
{
    /// <summary>The header's key; document headers are Guid v7 (pass 58).</summary>
    Guid Id { get; }

    /// <summary>Who submitted the document for approval. Neither they nor its creator may approve it.</summary>
    string? PreparedById { get; }

    /// <summary>The value the approver's limit must cover: the STORED value, never one the command carries.</summary>
    decimal ApprovalValue { get; }

    /// <summary>Who approved it; null until approved. Set only by an approval command's handler.</summary>
    string? ApprovedById { get; }

    /// <summary>When it was approved (UTC); null until approved.</summary>
    DateTime? ApprovedAt { get; }

}

/// <summary>
/// An approvable document type with its code (Pass 60): <c>public class PurchaseOrder : BaseVersionedEntity&lt;Guid&gt;,
/// IApprovable&lt;PurchaseOrder&gt;</c>. A separate interface because a static abstract member would stop
/// <see cref="IApprovable"/> itself being used as a type argument.
/// </summary>
public interface IApprovable<TSelf> : IApprovable where TSelf : IApprovable<TSelf>
{
    /// <summary>
    /// The stable code approval limits are kept against, for example <c>"pur.order"</c>. Unique across the model.
    /// </summary>
    static abstract string ApprovalDocumentType { get; }
}
