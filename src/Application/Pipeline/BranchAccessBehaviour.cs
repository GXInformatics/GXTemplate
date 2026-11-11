using CleanArchitecture.Blazor.Application.Common.Branches;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;

namespace CleanArchitecture.Blazor.Application.Pipeline;

/// <summary>
/// Refuses a branch command for a branch the user may not write to, and grants the branch to the rest of
/// the call chain when it may (Pass 59).
/// </summary>
/// <remarks>
/// <b>Placed immediately after <c>AuthorizationBehaviour</c></b>: that behaviour answers "may this user send
/// this request?", this one "in this branch?". It sits outside validation and outside the unit of work, so it
/// runs once per send and its grant spans every retry attempt and every posting-event round.
/// <para>
/// <b>Everything is read fresh on each send</b>, not from the cached <see cref="UserContext"/>: whether the
/// user is assigned to the branch and the branch is active, and otherwise whether they hold
/// <c>Permissions.Branches.WriteAll</c> (amendment B1) and the branch is active. A removed assignment or a
/// revoked permission therefore refuses the very next write. <c>Branches.ViewAll</c> plays no part.
/// </para>
/// The system context (<see cref="BranchAccess.IsSystem"/>) is granted every branch without a query.
/// </remarks>
public sealed class BranchAccessBehaviour<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : class, IMessage, IBranchCommand
{
    private readonly IUserContextAccessor _userContextAccessor;
    private readonly IApplicationDbContextFactory _dbContextFactory;
    private readonly IIdentityService _identityService;
    private readonly ILogger<BranchAccessBehaviour<TRequest, TResponse>> _logger;

    public BranchAccessBehaviour(
        IUserContextAccessor userContextAccessor,
        IApplicationDbContextFactory dbContextFactory,
        IIdentityService identityService,
        ILogger<BranchAccessBehaviour<TRequest, TResponse>> logger)
    {
        _userContextAccessor = userContextAccessor;
        _dbContextFactory = dbContextFactory;
        _identityService = identityService;
        _logger = logger;
    }

    public async ValueTask<TResponse> Handle(TRequest request, MessageHandlerDelegate<TRequest, TResponse> next, CancellationToken cancellationToken)
    {
        var requestName = typeof(TRequest).Name;
        var user = _userContextAccessor.Current;
        if (user is null || string.IsNullOrEmpty(user.UserId))
            throw new ForbiddenAccessException($"Access to '{requestName}' was denied because there is no authenticated user in context.");

        if (request.BranchId <= 0)
        {
            _logger.LogWarning("{RequestType} from user {UserId} named no branch; refused.", requestName, user.UserId);
            throw new ForbiddenAccessException($"'{requestName}' names no branch. Choose a branch and try again.");
        }

        if (user.Branches?.IsSystem != true)
        {
            await EnsureMayWriteAsync(request.BranchId, user.UserId, requestName, cancellationToken).ConfigureAwait(false);
        }

        using (BranchGrant.Begin(request.BranchId))
        {
            return await next(request, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task EnsureMayWriteAsync(int branchId, string userId, string requestName, CancellationToken cancellationToken)
    {
        var db = await _dbContextFactory.CreateAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var branch = await db.Branches.AsNoTracking()
                .Where(b => b.Id == branchId)
                .Select(b => new { b.Code, b.IsActive })
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

            var assigned = branch is not null && await db.UserBranches.AsNoTracking()
                .AnyAsync(ub => ub.UserId == userId && ub.BranchId == branchId, cancellationToken).ConfigureAwait(false);

            if (branch is not null && branch.IsActive)
            {
                if (assigned) return;
                if (await _identityService.AuthorizeAsync(userId, Permissions.Branches.WriteAll, cancellationToken).ConfigureAwait(false))
                    return;
            }

            var code = branch?.Code ?? branchId.ToString(CultureInfo.InvariantCulture);
            if (branch is not null && !branch.IsActive && assigned)
            {
                _logger.LogWarning("User {UserId} was refused {RequestType}: branch {BranchCode} is inactive.", userId, requestName, code);
                throw new ForbiddenAccessException($"Branch {code} is inactive and cannot be written to.");
            }

            _logger.LogWarning("User {UserId} was refused {RequestType}: not assigned to branch {BranchCode}.", userId, requestName, code);
            throw new ForbiddenAccessException($"You are not assigned to branch {code}.");
        }
        finally
        {
            await db.DisposeAsync().ConfigureAwait(false);
        }
    }
}
