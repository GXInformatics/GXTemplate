// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Mapster;
using CleanArchitecture.Blazor.Application.Features.PicklistSets.Caching;
using CleanArchitecture.Blazor.Application.Features.PicklistSets.DTOs;


namespace CleanArchitecture.Blazor.Application.Features.PicklistSets.Queries.GetAll;

[RequestAuthorize(Policy = Permissions.PicklistSets.View)]
public class GetAllPicklistSetsQuery : ICacheableRequest<IEnumerable<PicklistSetDto>>
{
    public string CacheKey => PicklistSetCacheKey.GetAllCacheKey;
    public IEnumerable<string>? Tags => PicklistSetCacheKey.Tags;
    
    /// <summary>
    /// <see cref="CacheScope.PerTenant"/> - shared reference data plus this tenant's own additions.
    /// </summary>
    /// <remarks>
    /// <b>Global until Pass 31, and the global query filter is what made that wrong.</b> The handler
    /// below never mentions a tenant and does not need to - the filter on <c>PicklistSet</c> scopes
    /// it - which is precisely why the scope had to be revisited by hand: nothing in this file
    /// changed, and its correct answer did. A filtered query behind a process-wide key serves the
    /// first tenant's list to every other one.
    /// </remarks>
    public CacheScope Scope => CacheScope.PerTenant;
}

public class GetAllPicklistSetsQueryHandler : IRequestHandler<GetAllPicklistSetsQuery, IEnumerable<PicklistSetDto>>
{
    private readonly IApplicationDbContextFactory _dbContextFactory;
    private readonly TypeAdapterConfig _typeAdapterConfig;

    public GetAllPicklistSetsQueryHandler(
        IApplicationDbContextFactory dbContextFactory,
        TypeAdapterConfig typeAdapterConfig
    )
    {
        _dbContextFactory = dbContextFactory;
        _typeAdapterConfig = typeAdapterConfig;
    }

    public async ValueTask<IEnumerable<PicklistSetDto>> Handle(GetAllPicklistSetsQuery request, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateAsync(cancellationToken);
        var data = await db.PicklistSets.OrderBy(x => x.Name).ThenBy(x => x.Value)
            .ProjectToType<PicklistSetDto>(_typeAdapterConfig)
            .ToListAsync(cancellationToken);
        return data;
    }
}
