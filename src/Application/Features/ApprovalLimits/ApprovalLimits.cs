using CleanArchitecture.Blazor.Application.Common.Approvals;
using CleanArchitecture.Blazor.Application.Common.Concurrency;

namespace CleanArchitecture.Blazor.Application.Features.ApprovalLimits;

/// <summary>An approval limit as the UI sees it (Pass 60).</summary>
public sealed class ApprovalLimitDto
{
    public int Id { get; set; }
    public string RoleId { get; set; } = string.Empty;
    public string DocumentType { get; set; } = string.Empty;
    public int? BranchId { get; set; }
    public decimal MaxValue { get; set; }
    public bool IsActive { get; set; }

    /// <summary>The row version, sent back with an edit (pass 59).</summary>
    public uint Version { get; set; }
}

// ---- Queries ----------------------------------------------------------------------------------

/// <summary>Every approval limit, active or not, by document type.</summary>
[RequestAuthorize(Policy = Permissions.ApprovalLimits.View)]
public sealed class GetAllApprovalLimitsQuery : IRequest<IReadOnlyList<ApprovalLimitDto>>;

public sealed class GetAllApprovalLimitsQueryHandler(IApplicationDbContextFactory dbContextFactory)
    : IRequestHandler<GetAllApprovalLimitsQuery, IReadOnlyList<ApprovalLimitDto>>
{
    public async ValueTask<IReadOnlyList<ApprovalLimitDto>> Handle(GetAllApprovalLimitsQuery request, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateAsync(cancellationToken);
        return await db.ApprovalLimits.AsNoTracking()
            .OrderBy(l => l.DocumentType).ThenBy(l => l.RoleId).ThenBy(l => l.BranchId)
            .Select(l => new ApprovalLimitDto
            {
                Id = l.Id, RoleId = l.RoleId, DocumentType = l.DocumentType, BranchId = l.BranchId,
                MaxValue = l.MaxValue, IsActive = l.IsActive, Version = l.Version
            })
            .ToListAsync(cancellationToken);
    }
}

/// <summary>The approvable document types in the model, for the maintenance page (Pass 60).</summary>
[RequestAuthorize(Policy = Permissions.ApprovalLimits.View)]
public sealed class GetApprovalDocumentTypesQuery : IRequest<IReadOnlyList<string>>;

public sealed class GetApprovalDocumentTypesQueryHandler(IApprovalDocumentTypes documentTypes)
    : IRequestHandler<GetApprovalDocumentTypesQuery, IReadOnlyList<string>>
{
    public ValueTask<IReadOnlyList<string>> Handle(GetApprovalDocumentTypesQuery request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(documentTypes.All);
}

// ---- Commands ---------------------------------------------------------------------------------

/// <summary>
/// Adds an approval limit, or edits one (including deactivating it). Limits are never deleted: their history stays
/// in the audit trail. (P60: deactivation is this command with <see cref="IsActive"/> false, as for branches.)
/// </summary>
[RequestAuthorize(Policy = Permissions.ApprovalLimits.Edit)]
public sealed class AddEditApprovalLimitCommand : IRequest<Result<Saved<int>>>, IVersionedEdit
{
    public int Id { get; set; }
    public string RoleId { get; set; } = string.Empty;
    public string DocumentType { get; set; } = string.Empty;
    public int? BranchId { get; set; }
    public decimal MaxValue { get; set; }
    public bool IsActive { get; set; } = true;
    public uint? Version { get; set; }
    public bool IsEdit => Id > 0;
}

public sealed class AddEditApprovalLimitCommandValidator : AbstractValidator<AddEditApprovalLimitCommand>
{
    public AddEditApprovalLimitCommandValidator(IApprovalDocumentTypes documentTypes)
    {
        RuleFor(c => c.RoleId).NotEmpty();
        RuleFor(c => c.DocumentType).NotEmpty()
            .Must(t => documentTypes.All.Contains(t, StringComparer.Ordinal))
            .WithMessage(c => $"'{c.DocumentType}' is not an approvable document type.");
        RuleFor(c => c.MaxValue).GreaterThanOrEqualTo(0)
            .Must(v => decimal.Round(v, 2) == v).WithMessage("An approval limit is an amount of money, with at most two decimal places.");
    }
}

public sealed class AddEditApprovalLimitCommandHandler(IApplicationDbContextFactory dbContextFactory, IApprovalRoles approvalRoles)
    : IRequestHandler<AddEditApprovalLimitCommand, Result<Saved<int>>>
{
    public async ValueTask<Result<Saved<int>>> Handle(AddEditApprovalLimitCommand request, CancellationToken cancellationToken)
    {
        if (await approvalRoles.IsAdministratorRoleAsync(request.RoleId, cancellationToken))
            return Result<Saved<int>>.Failure("The administrator role cannot hold an approval limit: the administrator never approves.");
        if (!await approvalRoles.ExistsAsync(request.RoleId, cancellationToken))
            return Result<Saved<int>>.Failure("The role does not exist.");

        await using var db = await dbContextFactory.CreateAsync(cancellationToken);
        if (request.BranchId is { } branchId && !await db.Branches.AnyAsync(b => b.Id == branchId, cancellationToken))
            return Result<Saved<int>>.Failure("The branch does not exist.");

        // One row per (role, document type, branch): kept here, because a nullable branch has no portable unique index.
        var duplicate = await db.ApprovalLimits.AnyAsync(l => l.Id != request.Id && l.RoleId == request.RoleId
                                                              && l.DocumentType == request.DocumentType && l.BranchId == request.BranchId,
            cancellationToken);
        if (duplicate)
            return Result<Saved<int>>.Failure("That role already has a limit for this document type and branch. Edit it instead.");

        ApprovalLimit limit;
        if (request.IsEdit)
        {
            limit = await db.ApprovalLimits.FirstOrDefaultAsync(l => l.Id == request.Id, cancellationToken)
                    ?? throw new NotFoundException($"Approval limit {request.Id} was not found.");
            db.ApplyLoadedVersion(limit, request.Version);
        }
        else
        {
            limit = new ApprovalLimit();
            db.ApprovalLimits.Add(limit);
        }

        limit.RoleId = request.RoleId;
        limit.DocumentType = request.DocumentType;
        limit.BranchId = request.BranchId;
        limit.MaxValue = request.MaxValue;
        limit.IsActive = request.IsActive;

        var saved = db.SavedVersionOf(limit, 0);
        await db.SaveChangesAsync(cancellationToken);
        saved.Id = limit.Id;
        return Result<Saved<int>>.Success(saved);
    }
}
