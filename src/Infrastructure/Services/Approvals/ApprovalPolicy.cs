using System.Globalization;
using CleanArchitecture.Blazor.Application.Common.Approvals;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Transactions;
using CleanArchitecture.Blazor.Domain.Common.Entities;
using CleanArchitecture.Blazor.Infrastructure.Persistence;

namespace CleanArchitecture.Blazor.Infrastructure.Services.Approvals;

/// <summary>
/// The approval rules (Pass 60), read fresh from the database in the unit of work's transaction on every attempt.
/// </summary>
/// <remarks>
/// In order: background work never approves; the administrator never approves (any member of the administrator
/// role, whatever else they hold); the preparer and the creator never approve; a document is approved once; a
/// branch-scoped document is approved in its own branch (amendment C1: skipped for a document with no branch);
/// and the approver's limit - the highest over their roles' active rows for the document type, a branch row
/// overriding the role's general row - covers the stored value. No row is no authority.
/// </remarks>
internal sealed class ApprovalPolicy(IUnitOfWork unitOfWork, IUserContextAccessor userContextAccessor) : IApprovalPolicy
{
    public async Task<ApprovalDecision> DecideAsync(IApprovable header, string documentType, string userId, int? branchId,
        CancellationToken cancellationToken)
    {
        var db = unitOfWork.Context as ApplicationDbContext
                 ?? throw new InvalidOperationException("The approval policy runs in a unit of work over ApplicationDbContext.");
        var what = documentType;

        if (userContextAccessor.Current?.Branches?.IsSystem == true)
            return ApprovalDecision.Refuse(ApprovalRefusal.System, "Background work cannot approve documents.");

        var roleIds = await db.UserRoles.AsNoTracking().Where(ur => ur.UserId == userId).Select(ur => ur.RoleId)
            .ToListAsync(cancellationToken);
        var isAdministrator = await db.Roles.AsNoTracking()
            .AnyAsync(r => roleIds.Contains(r.Id) && r.Name == CleanArchitecture.Blazor.Application.Common.Constants.Roles.Admin, cancellationToken);
        if (isAdministrator)
            return ApprovalDecision.Refuse(ApprovalRefusal.Administrator, "The administrator account cannot approve documents.");

        if (string.Equals(header.PreparedById, userId, StringComparison.Ordinal)
            || (header is IAuditableEntity audited && string.Equals(audited.CreatedById, userId, StringComparison.Ordinal)))
            return ApprovalDecision.Refuse(ApprovalRefusal.Preparer, $"You prepared this {what}. Someone else must approve it.");

        if (header.ApprovedById is not null)
            return ApprovalDecision.Refuse(ApprovalRefusal.AlreadyApproved, $"This {what} is already approved.");

        var documentBranch = await CheckBranchAsync(db, header, what, branchId, cancellationToken);
        if (documentBranch.Refusal is { } branchRefusal) return branchRefusal;

        var limit = await LimitAsync(db, roleIds, documentType, documentBranch.BranchId, cancellationToken);
        if (limit is null)
            return ApprovalDecision.Refuse(ApprovalRefusal.NoLimit, $"You have no approval limit for {what}.");
        if (header.ApprovalValue > limit.Value)
            return ApprovalDecision.Refuse(ApprovalRefusal.OverLimit,
                $"This {what} is {Money(header.ApprovalValue)}. Your approval limit is {Money(limit.Value)}, so it needs an approver whose limit covers it.",
                limit);

        return ApprovalDecision.Allow(limit.Value);
    }

    /// <summary>The branch the limits are looked up for, or a refusal (rule 5; C1: none for an unscoped header).</summary>
    private static async Task<(int? BranchId, ApprovalDecision? Refusal)> CheckBranchAsync(
        ApplicationDbContext db, IApprovable header, string what, int? commandBranch, CancellationToken cancellationToken)
    {
        IReadOnlyCollection<int> involved;
        if (header is IMustHaveBranch scoped) involved = [scoped.BranchId];
        else if (header.GetType().GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IBranchVisibility<>))
                     ?.GetProperty("InvolvedBranchIds")?.GetValue(header) is IEnumerable<int> ids) involved = ids.ToList();
        else return (null, null);

        if (commandBranch is { } branch && involved.Contains(branch)) return (branch, null);

        var codes = await db.Branches.AsNoTracking().Where(b => involved.Contains(b.Id) || b.Id == commandBranch)
            .ToDictionaryAsync(b => b.Id, b => b.Code, cancellationToken);
        string Code(int id) => codes.TryGetValue(id, out var code) ? code : id.ToString(CultureInfo.InvariantCulture);
        var own = string.Join(", ", involved.Select(Code));
        var named = commandBranch is { } c ? Code(c) : "none";
        return (null, ApprovalDecision.Refuse(ApprovalRefusal.WrongBranch, $"This {what} belongs to branch {own}, not {named}."));
    }

    private static async Task<decimal?> LimitAsync(ApplicationDbContext db, IReadOnlyCollection<string> roleIds, string documentType,
        int? branchId, CancellationToken cancellationToken)
    {
        if (roleIds.Count == 0) return null;
        var rows = await db.ApprovalLimits.AsNoTracking()
            .Where(l => l.IsActive && l.DocumentType == documentType && roleIds.Contains(l.RoleId)
                        && (l.BranchId == null || (branchId != null && l.BranchId == branchId)))
            .Select(l => new { l.RoleId, l.BranchId, l.MaxValue })
            .ToListAsync(cancellationToken);

        // Per role, its branch row overrides its general row; then the highest across roles.
        var perRole = rows.GroupBy(r => r.RoleId)
            .Select(g => (g.FirstOrDefault(r => r.BranchId != null) ?? g.First(r => r.BranchId == null)).MaxValue)
            .ToList();
        return perRole.Count == 0 ? null : perRole.Max();
    }

    private static string Money(decimal value) => value.ToString("N2", CultureInfo.InvariantCulture);
}
