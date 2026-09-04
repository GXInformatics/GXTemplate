// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Application.Features.PicklistSets.Caching;

namespace CleanArchitecture.Blazor.Application.Features.PicklistSets.Commands.Delete;

[RequestAuthorize(Policy = Permissions.PicklistSets.Delete)]
public class DeletePicklistSetCommand : ICacheInvalidatorRequest<Result>
{
    public DeletePicklistSetCommand(int[] id)
    {
        Id = id;
    }

    public int[] Id { get; }
    public string CacheKey => PicklistSetCacheKey.GetAllCacheKey;
    public IEnumerable<string>? Tags => PicklistSetCacheKey.Tags;
}

public class DeletePicklistSetCommandHandler : IRequestHandler<DeletePicklistSetCommand, Result>

{
    private readonly IApplicationDbContextFactory _dbContextFactory;
    private readonly IPermissionQueryService _permissionQueryService;
    private readonly IUserContextAccessor _userContextAccessor;

    public DeletePicklistSetCommandHandler(
        IApplicationDbContextFactory dbContextFactory,
        IPermissionQueryService permissionQueryService,
        IUserContextAccessor userContextAccessor
    )
    {
        _dbContextFactory = dbContextFactory;
        _permissionQueryService = permissionQueryService;
        _userContextAccessor = userContextAccessor;
    }

    public async ValueTask<Result> Handle(DeletePicklistSetCommand request, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateAsync(cancellationToken);
        var items = await db.PicklistSets.Where(x => request.Id.Contains(x.Id)).ToListAsync(cancellationToken);

        // ALL OR NOTHING, checked before anything is removed. A multi-row delete mixing a shared row
        // with the caller's own must not half-succeed: partially applying it would leave the caller
        // to work out which rows survived, and a retry would then look like a different request.
        // The guard runs over every affected row's STORED tenant, so one shared row in the selection
        // refuses the whole command.
        if (!await SharedPicklistWrite.IsAllowedAsync(
                items.Select(i => i.TenantId), _permissionQueryService, _userContextAccessor.Current?.UserId))
            return await Result.FailureAsync(SharedPicklistWrite.Refused);

        foreach (var item in items)
        {
            item.AddDomainEvent(new PicklistSetDeletedEvent(item));
        }
        db.PicklistSets.RemoveRange(items);
        await db.SaveChangesAsync(cancellationToken);
        return await Result.SuccessAsync();
    }
}
