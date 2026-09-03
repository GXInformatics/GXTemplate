// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using Mapster;
using CleanArchitecture.Blazor.Application.Features.AuditTrails.Caching;
using CleanArchitecture.Blazor.Application.Features.AuditTrails.DTOs;
using CleanArchitecture.Blazor.Application.Features.AuditTrails.Specifications;

namespace CleanArchitecture.Blazor.Application.Features.AuditTrails.Queries.PaginationQuery;

[RequestAuthorize(Policy = Permissions.AuditTrails.View)]
public class AuditTrailsWithPaginationQuery : AuditTrailAdvancedFilter, ICacheableRequest<PaginatedData<AuditTrailDto>>
{
    public AuditTrailAdvancedSpecification Specification => new(this);
    public string CacheKey => AuditTrailsCacheKey.GetPaginationCacheKey($"{this}");
    public IEnumerable<string>? Tags => AuditTrailsCacheKey.Tags;
    
    /// <summary>the specification filters the date window by the caller's local time offset.</summary>
    public CacheScope Scope => CacheScope.PerUser;

    public override string ToString()
    {
        return
            $"Listview:{ListView},AuditType:{AuditType},Search:{Keyword},Sort:{SortDirection},OrderBy:{OrderBy},{PageNumber},{PageSize}";
    }
}

public class AuditTrailsQueryHandler : IRequestHandler<AuditTrailsWithPaginationQuery, PaginatedData<AuditTrailDto>>
{
    private readonly IApplicationDbContextFactory _dbContextFactory;
    private readonly IPermissionQueryService _permissionQueryService;
    private readonly IUserContextAccessor _userContextAccessor;
    private readonly TypeAdapterConfig _typeAdapterConfig;

    public AuditTrailsQueryHandler(
        IApplicationDbContextFactory dbContextFactory,
        IPermissionQueryService permissionQueryService,
        IUserContextAccessor userContextAccessor,
        TypeAdapterConfig typeAdapterConfig
    )
    {
        _dbContextFactory = dbContextFactory;
        _permissionQueryService = permissionQueryService;
        _userContextAccessor = userContextAccessor;
        _typeAdapterConfig = typeAdapterConfig;
    }

    public async ValueTask<PaginatedData<AuditTrailDto>> Handle(AuditTrailsWithPaginationQuery request,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateAsync(cancellationToken);
        // Tenant-scoped by the global filter unless this principal holds
        // Permissions.AuditTrails.ViewAllTenants - see AuditTrailTenantScope, which is the ONLY
        // exemption and is shared with the export, so the two cannot diverge on whether to
        // grant it.
        var visible = await AuditTrailTenantScope.VisibleAsync(
            db.AuditTrails, _permissionQueryService, _userContextAccessor.Current?.UserId);

        var data = await visible.OrderBy($"{request.OrderBy} {request.SortDirection}")
            .ProjectToPaginatedDataAsync<AuditTrail, AuditTrailDto>(request.Specification, request.PageNumber,
                request.PageSize, _typeAdapterConfig, cancellationToken);

        return data;
    }
}
