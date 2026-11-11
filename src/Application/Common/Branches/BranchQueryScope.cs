using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;

namespace CleanArchitecture.Blazor.Application.Common.Branches;

/// <inheritdoc cref="IBranchQueryScope"/>
public sealed class BranchQueryScope : IBranchQueryScope
{
    private readonly IUserContextAccessor _userContextAccessor;
    private readonly IIdentityService _identityService;
    private readonly ILogger<BranchQueryScope> _logger;

    public BranchQueryScope(IUserContextAccessor userContextAccessor, IIdentityService identityService, ILogger<BranchQueryScope> logger)
    {
        _userContextAccessor = userContextAccessor;
        _identityService = identityService;
        _logger = logger;
    }

    public async Task<IQueryable<TEntity>> AcrossBranchesAsync<TEntity>(
        IQueryable<TEntity> query, string permission, CancellationToken cancellationToken = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);

        var user = _userContextAccessor.Current;
        if (user is null || string.IsNullOrEmpty(user.UserId))
            throw new ForbiddenAccessException("Reading across branches was denied: there is no authenticated user in context.");

        var allowed = user.Branches?.IsSystem == true
                      || await _identityService.AuthorizeAsync(user.UserId, permission, cancellationToken).ConfigureAwait(false);
        if (!allowed)
        {
            _logger.LogWarning("User {UserId} was refused a cross-branch read of {Entity}: does not hold {Permission}.",
                user.UserId, typeof(TEntity).Name, permission);
            throw new ForbiddenAccessException("You do not have permission to see other branches' records here.");
        }

        // The ONE sanctioned removal of the branch filter. The other named filters still apply.
        return query.IgnoreQueryFilters([QueryFilters.Branch]);
    }
}
