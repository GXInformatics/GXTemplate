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

    /// <summary>
    /// On CREATE, asks for the new row to belong to the installation rather than to the caller's
    /// tenant. Ignored on edit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A request, not a grant.</b> The handler checks
    /// <c>Permissions.PicklistSets.ManageShared</c> over the tenant the row would carry, so setting
    /// this without the right is refused rather than obeyed - and a caller who already has no
    /// tenant creates a shared row with or without it.
    /// </para>
    /// <para>
    /// <b>Ignored on edit, deliberately.</b> Moving an existing row between the shared and private
    /// partitions changes which tenants see it and which rows the unique index constrains it
    /// against. That is a different operation from editing a value, nobody has asked for it, and
    /// silently doing it on a DTO round-trip - the DTO passes through the browser - would be the
    /// worst way to acquire it. <c>TheEditPathIgnoresTheSharedFlag</c> pins that.
    /// </para>
    /// </remarks>
    [Description("Shared with every tenant")] public bool IsShared { get; set; }

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

            // request.IsShared is NOT read here. The mapping cannot move the row either - the
            // command has no TenantId and PicklistSet.CreateAsShared has no counterpart on it - so
            // an edit leaves the row in whichever partition it was already in. See the property.
            item = _objectMapper.Map(request, item);
            item.AddDomainEvent(new PicklistSetUpdatedEvent(item));
            await db.SaveChangesAsync(cancellationToken);
            return await Result<int>.SuccessAsync(item.Id);
        }
        else
        {
            // The tenant the new row WILL carry: the caller's own, because
            // AuditableEntityInterceptor stamps an unset tenant from the ambient principal - unless
            // the caller has ASKED for a shared row, in which case it will carry none. A caller who
            // has no tenant produces a shared row either way.
            //
            // This is one expression rather than two branches on purpose: it is the same question
            // the interceptor answers, and Pass 32 A2 recorded that the two are separate copies of
            // one rule. Writing the prediction once, immediately above the guard that consumes it,
            // is as close as they can be brought without merging a prediction with an act.
            var prospectiveTenantId = request.IsShared
                ? null
                : _userContextAccessor.Current?.TenantId;

            // Creating installation-wide reference data is the same capability as editing it, so a
            // caller who asked for a shared row is refused here unless they hold ManageShared. A
            // tenant-scoped caller creating a private row never reaches the permission query.
            if (!await SharedPicklistWrite.IsAllowedAsync(
                    [prospectiveTenantId], _permissionQueryService, userId))
                return await Result<int>.FailureAsync(SharedPicklistWrite.Refused);

            var keyValue = _objectMapper.Map<PicklistSet>(request);

            // Set AFTER the guard and AFTER the mapping, so it is unreachable on any path that did
            // not pass the check above - and set from the prediction rather than from the request,
            // so the flag the interceptor reads and the tenant the guard authorised are the same
            // decision rather than two that happen to agree.
            keyValue.CreateAsShared = SharedPicklistWrite.IsShared(prospectiveTenantId);
            keyValue.AddDomainEvent(new PicklistSetCreatedEvent(keyValue));
            db.PicklistSets.Add(keyValue);
            await db.SaveChangesAsync(cancellationToken);
            return await Result<int>.SuccessAsync(keyValue.Id);
        }
    }
}
