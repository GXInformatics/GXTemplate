// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Mapster;
using CleanArchitecture.Blazor.Application.Features.PicklistSets.Caching;
using CleanArchitecture.Blazor.Application.Features.PicklistSets.DTOs;
using CleanArchitecture.Blazor.Application.Features.PicklistSets.Specifications;

namespace CleanArchitecture.Blazor.Application.Features.PicklistSets.Queries.PaginationQuery;

[RequestAuthorize(Policy = Permissions.PicklistSets.View)]
public class PicklistSetsWithPaginationQuery : PicklistSetAdvancedFilter, ICacheableRequest<PaginatedData<PicklistSetDto>>
{
    public PicklistSetAdvancedSpecification Specification => new(this);
    public string CacheKey => $"{nameof(PicklistSetsWithPaginationQuery)},{this}";
    public IEnumerable<string>? Tags => PicklistSetCacheKey.Tags;
    
    /// <summary>
    /// <see cref="CacheScope.PerUserAndTenant"/> - the date window is per user, the rows are per
    /// tenant, and both have to be in the key.
    /// </summary>
    /// <remarks>
    /// <b>PerUser until Pass 31, and PerUser alone is not enough once the rows are filtered.</b> The
    /// specification narrows the date window by the caller's local time offset, which is why the
    /// user was in the key; the global query filter on <c>PicklistSet</c> now also narrows the rows
    /// by tenant, which the user id does not capture. One principal can occupy two tenants over
    /// time - that is exactly what the tenant switcher does - and under a <c>u:{userId}</c> key
    /// alone they would be served, after switching, the list they cached before it.
    /// <para>
    /// This is the failure mode the tenant switch makes reachable and a circuit reload does NOT fix:
    /// the FusionCache entry is process-wide and outlives the circuit.
    /// </para>
    /// </remarks>
    public CacheScope Scope => CacheScope.PerUserAndTenant;

    public override string ToString()
    {
        return $"ListView:{ListView}-{Picklist},Search:{Keyword},OrderBy:{OrderBy} {SortDirection},{PageNumber},{PageSize}";
    }
}

public class PicklistSetsQueryHandler : IRequestHandler<PicklistSetsWithPaginationQuery, PaginatedData<PicklistSetDto>>
{
    private readonly IApplicationDbContextFactory _dbContextFactory;
    private readonly TypeAdapterConfig _typeAdapterConfig;

    public PicklistSetsQueryHandler(
        IApplicationDbContextFactory dbContextFactory,
        TypeAdapterConfig typeAdapterConfig
    )
    {
        _dbContextFactory = dbContextFactory;
        _typeAdapterConfig = typeAdapterConfig;
    }

    public async ValueTask<PaginatedData<PicklistSetDto>> Handle(PicklistSetsWithPaginationQuery request, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateAsync(cancellationToken);
        var data = await db.PicklistSets.OrderBy($"{request.OrderBy} {request.SortDirection}")
            .ProjectToPaginatedDataAsync<PicklistSet, PicklistSetDto>(request.Specification, request.PageNumber, request.PageSize, _typeAdapterConfig, cancellationToken);
        return data;
    }
}
