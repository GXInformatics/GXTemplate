// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Application.Features.PicklistSets.Caching;
namespace CleanArchitecture.Blazor.Application.Features.PicklistSets.Commands.AddEdit;

[RequestAuthorize(Policy = Permissions.PicklistSets.Create)]
[RequestAuthorize(Policy = Permissions.PicklistSets.Edit)]
public class AddEditPicklistSetCommand : ICacheInvalidatorRequest<Result<int>>
{
    [Description("Id")] public int Id { get; set; }
    [Description("Name")] public Picklist Name { get; set; }
    [Description("Value")] public string? Value { get; set; }
    [Description("Text")] public string? Text { get; set; }
    [Description("Description")] public string? Description { get; set; }
    public TrackingState TrackingState { get; set; } = TrackingState.Unchanged;
    public string CacheKey => PicklistSetCacheKey.GetAllCacheKey;
    public IEnumerable<string>? Tags => PicklistSetCacheKey.Tags;}

public class AddEditPicklistSetCommandHandler : IRequestHandler<AddEditPicklistSetCommand, Result<int>>
{
    private readonly IApplicationDbContextFactory _dbContextFactory;
    private readonly IObjectMapper _objectMapper;
    private readonly IPermissionQueryService _permissionQueryService;
    private readonly IUserContextAccessor _userContextAccessor;

    public AddEditPicklistSetCommandHandler(
        IApplicationDbContextFactory dbContextFactory,
        IObjectMapper objectMapper,
        IPermissionQueryService permissionQueryService,
        IUserContextAccessor userContextAccessor
    )
    {
        _dbContextFactory = dbContextFactory;
        _objectMapper = objectMapper;
        _permissionQueryService = permissionQueryService;
        _userContextAccessor = userContextAccessor;
    }

    public async ValueTask<Result<int>> Handle(AddEditPicklistSetCommand request, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateAsync(cancellationToken);
        var userId = _userContextAccessor.Current?.UserId;

        if (request.Id > 0)
        {
            // The row is read back before the guard runs, so the tenant checked is the STORED one.
            // Taking it from the request would let a client claim a row was private and edit a
            // shared value through the claim - the DTO round-trips through the browser.
            //
            // FindAsync is itself bounded by the global tenant filter, so another tenant's private
            // row is not found here at all; what reaches the guard is either a shared row or one of
            // this principal's own. Pass 31's ARowCannotBeReachedByIdFromAnotherTenant pins that.
            var item = await db.PicklistSets.FindAsync(request.Id, cancellationToken);
            if (item == null) return await Result<int>.FailureAsync($"PicklistSet with id: [{request.Id}] not found.");

            if (!await SharedPicklistWrite.IsAllowedAsync([item.TenantId], _permissionQueryService, userId))
                return await Result<int>.FailureAsync(SharedPicklistWrite.Refused);

            item = _objectMapper.Map(request, item);
            item.AddDomainEvent(new PicklistSetUpdatedEvent(item));
            await db.SaveChangesAsync(cancellationToken);
            return await Result<int>.SuccessAsync(item.Id);
        }
        else
        {
            // A new row is stamped by AuditableEntityInterceptor from the ambient principal, so the
            // tenant it WILL carry is the caller's own - and a caller with no tenant would produce a
            // SHARED row without ever touching one. That is the case this guards: creating
            // installation-wide reference data is the same capability as editing it.
            //
            // A tenant-scoped caller creates a private row and never reaches the permission query.
            if (!await SharedPicklistWrite.IsAllowedAsync(
                    [_userContextAccessor.Current?.TenantId], _permissionQueryService, userId))
                return await Result<int>.FailureAsync(SharedPicklistWrite.Refused);

            var keyValue = _objectMapper.Map<PicklistSet>(request);
            keyValue.AddDomainEvent(new PicklistSetCreatedEvent(keyValue));
            db.PicklistSets.Add(keyValue);
            await db.SaveChangesAsync(cancellationToken);
            return await Result<int>.SuccessAsync(keyValue.Id);
        }
    }
}
