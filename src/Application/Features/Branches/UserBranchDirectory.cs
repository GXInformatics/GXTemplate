using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;

namespace CleanArchitecture.Blazor.Application.Features.Branches;

/// <summary>
/// The branches a user may currently write to, read fresh (Pass 59): for the top-bar switcher and the
/// active-branch state. Not a Mediator request because it answers only about the caller's own access,
/// like <c>ITenantSwitchService</c>; the write check itself is <c>BranchAccessBehaviour</c>.
/// </summary>
public interface IUserBranchDirectory
{
    /// <summary>
    /// The assigned active branches, or every active branch for a holder of <c>Branches.WriteAll</c>
    /// (amendment B1), ordered by code. Empty for an unknown user.
    /// </summary>
    Task<IReadOnlyList<BranchDto>> WritableAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>The user's default branch, if it is assigned and active.</summary>
    Task<int?> DefaultBranchIdAsync(string userId, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IUserBranchDirectory"/>
public sealed class UserBranchDirectory(IApplicationDbContextFactory dbContextFactory, IIdentityService identityService)
    : IUserBranchDirectory
{
    public async Task<IReadOnlyList<BranchDto>> WritableAsync(string userId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(userId)) return [];
        var writesAll = await identityService.AuthorizeAsync(userId, Permissions.Branches.WriteAll, cancellationToken);

        await using var db = await dbContextFactory.CreateAsync(cancellationToken);
        var query = db.Branches.AsNoTracking().Where(b => b.IsActive);
        if (!writesAll)
            query = query.Where(b => db.UserBranches.Any(ub => ub.UserId == userId && ub.BranchId == b.Id));

        return await query.OrderBy(b => b.Code)
            .Select(b => new BranchDto { Id = b.Id, Code = b.Code, Name = b.Name, IsActive = b.IsActive, Version = b.Version })
            .ToListAsync(cancellationToken);
    }

    public async Task<int?> DefaultBranchIdAsync(string userId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(userId)) return null;
        await using var db = await dbContextFactory.CreateAsync(cancellationToken);
        return await db.UserBranches.AsNoTracking()
            .Where(ub => ub.UserId == userId && ub.IsDefault && ub.Branch!.IsActive)
            .Select(ub => (int?)ub.BranchId)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
