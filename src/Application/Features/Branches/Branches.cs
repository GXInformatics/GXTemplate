using CleanArchitecture.Blazor.Application.Common.Concurrency;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;

namespace CleanArchitecture.Blazor.Application.Features.Branches;

/// <summary>A branch as the UI sees it (Pass 59).</summary>
public sealed class BranchDto
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; }

    /// <summary>The row version, sent back with an edit (Pass 59).</summary>
    public uint Version { get; set; }
}

/// <summary>One user's assignment to a branch (Pass 59).</summary>
public sealed record UserBranchDto(int BranchId, bool IsDefault);

// ---- Queries ----------------------------------------------------------------------------------

/// <summary>Every branch, active or not, ordered by code.</summary>
[RequestAuthorize(Policy = Permissions.Branches.View)]
public sealed class GetAllBranchesQuery : IRequest<IReadOnlyList<BranchDto>>;

public sealed class GetAllBranchesQueryHandler(IApplicationDbContextFactory dbContextFactory)
    : IRequestHandler<GetAllBranchesQuery, IReadOnlyList<BranchDto>>
{
    public async ValueTask<IReadOnlyList<BranchDto>> Handle(GetAllBranchesQuery request, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateAsync(cancellationToken);
        return await db.Branches.AsNoTracking().OrderBy(b => b.Code)
            .Select(b => new BranchDto { Id = b.Id, Code = b.Code, Name = b.Name, IsActive = b.IsActive, Version = b.Version })
            .ToListAsync(cancellationToken);
    }
}

/// <summary>A user's branch assignments, for the user dialog.</summary>
[RequestAuthorize(Policy = Permissions.Branches.Edit)]
public sealed record GetUserBranchesQuery(string UserId) : IRequest<IReadOnlyList<UserBranchDto>>;

public sealed class GetUserBranchesQueryHandler(IApplicationDbContextFactory dbContextFactory)
    : IRequestHandler<GetUserBranchesQuery, IReadOnlyList<UserBranchDto>>
{
    public async ValueTask<IReadOnlyList<UserBranchDto>> Handle(GetUserBranchesQuery request, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateAsync(cancellationToken);
        return await db.UserBranches.AsNoTracking().Where(ub => ub.UserId == request.UserId).OrderBy(ub => ub.BranchId)
            .Select(ub => new UserBranchDto(ub.BranchId, ub.IsDefault))
            .ToListAsync(cancellationToken);
    }
}

// ---- Commands ---------------------------------------------------------------------------------

/// <summary>Creates a branch, or edits one (including deactivating it). Branches are never deleted.</summary>
[RequestAuthorize(Policy = Permissions.Branches.Create)]
[RequestAuthorize(Policy = Permissions.Branches.Edit)]
public sealed class AddEditBranchCommand : IRequest<Result<Saved<int>>>, IVersionedEdit
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public uint? Version { get; set; }
    public bool IsEdit => Id > 0;
}

public sealed class AddEditBranchCommandValidator : AbstractValidator<AddEditBranchCommand>
{
    public AddEditBranchCommandValidator()
    {
        RuleFor(c => c.Code).NotEmpty().MaximumLength(20);
        RuleFor(c => c.Name).NotEmpty().MaximumLength(100);
    }
}

public sealed class AddEditBranchCommandHandler(
    IApplicationDbContextFactory dbContextFactory,
    IUserContextInvalidator userContextInvalidator)
    : IRequestHandler<AddEditBranchCommand, Result<Saved<int>>>
{
    public async ValueTask<Result<Saved<int>>> Handle(AddEditBranchCommand request, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateAsync(cancellationToken);
        var code = request.Code.Trim();

        if (await db.Branches.AnyAsync(b => b.Code == code && b.Id != request.Id, cancellationToken))
            return Result<Saved<int>>.Failure($"A branch with code '{code}' already exists.");

        Branch branch;
        if (request.IsEdit)
        {
            branch = await db.Branches.FirstOrDefaultAsync(b => b.Id == request.Id, cancellationToken)
                     ?? throw new NotFoundException($"Branch {request.Id} was not found.");
            db.ApplyLoadedVersion(branch, request.Version);
        }
        else
        {
            branch = new Branch();
            db.Branches.Add(branch);
        }

        var activeChanged = request.IsEdit && branch.IsActive != request.IsActive;
        branch.Code = code;
        branch.Name = request.Name.Trim();
        branch.IsActive = request.IsActive;

        var saved = db.SavedVersionOf(branch, 0);
        await db.SaveChangesAsync(cancellationToken);
        saved.Id = branch.Id;

        // Deactivating a branch removes it from its users' write sets (owner decision 11).
        if (activeChanged) userContextInvalidator.InvalidateAll();
        return Result<Saved<int>>.Success(saved);
    }
}

/// <summary>
/// Replaces one user's branch assignments (Pass 59). The only writer of <c>UserBranches</c>. At most one
/// assignment is the default, and it must be one of the assigned branches.
/// </summary>
[RequestAuthorize(Policy = Permissions.Branches.Edit)]
public sealed class SetUserBranchesCommand : IRequest<Result>
{
    public string UserId { get; set; } = string.Empty;
    public IReadOnlyCollection<int> BranchIds { get; set; } = [];
    public int? DefaultBranchId { get; set; }
}

public sealed class SetUserBranchesCommandValidator : AbstractValidator<SetUserBranchesCommand>
{
    public SetUserBranchesCommandValidator()
    {
        RuleFor(c => c.UserId).NotEmpty();
        RuleFor(c => c.DefaultBranchId)
            .Must((c, id) => id is null || c.BranchIds.Contains(id.Value))
            .WithMessage("The default branch must be one of the assigned branches.");
    }
}

public sealed class SetUserBranchesCommandHandler(
    IApplicationDbContextFactory dbContextFactory,
    IUserContextInvalidator userContextInvalidator)
    : IRequestHandler<SetUserBranchesCommand, Result>
{
    public async ValueTask<Result> Handle(SetUserBranchesCommand request, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateAsync(cancellationToken);
        var wanted = request.BranchIds.Distinct().ToHashSet();
        var known = await db.Branches.Where(b => wanted.Contains(b.Id)).Select(b => b.Id).ToListAsync(cancellationToken);
        if (known.Count != wanted.Count)
            return Result.Failure("One or more of the branches does not exist.");

        var existing = await db.UserBranches.Where(ub => ub.UserId == request.UserId).ToListAsync(cancellationToken);
        db.UserBranches.RemoveRange(existing.Where(ub => !wanted.Contains(ub.BranchId)));
        foreach (var row in existing.Where(ub => wanted.Contains(ub.BranchId)))
            row.IsDefault = row.BranchId == request.DefaultBranchId;
        foreach (var id in wanted.Except(existing.Select(ub => ub.BranchId)))
            db.UserBranches.Add(new UserBranch { UserId = request.UserId, BranchId = id, IsDefault = id == request.DefaultBranchId });

        await db.SaveChangesAsync(cancellationToken);

        // The read set lives in the cached user context; the write check needs no invalidation (it reads
        // the database on every send), but reads do.
        userContextInvalidator.Invalidate(request.UserId);
        return Result.Success();
    }
}
