// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using Mapster;
using CleanArchitecture.Blazor.Application.Features.AuditTrails.DTOs;

namespace CleanArchitecture.Blazor.Application.Features.AuditTrails.Queries.Export;

[RequestAuthorize(Policy = Permissions.AuditTrails.Export)]
public class ExportAuditTrailsQuery : IRequest<byte[]>
{
    public string Keyword { get; set; } = string.Empty;
    public string OrderBy { get; set; } = "Id";
    public string SortDirection { get; set; } = "Descending";
}

public class ExportAuditTrailsQueryHandler :
    IRequestHandler<ExportAuditTrailsQuery, byte[]>
{
    private readonly IApplicationDbContextFactory _dbContextFactory;
    private readonly IPermissionQueryService _permissionQueryService;
    private readonly IUserContextAccessor _userContextAccessor;
    private readonly TypeAdapterConfig _typeAdapterConfig;
    private readonly IExcelService _excelService;
    private readonly IStringLocalizer<ExportAuditTrailsQueryHandler> _localizer;

    public ExportAuditTrailsQueryHandler(
        IApplicationDbContextFactory dbContextFactory,
        IPermissionQueryService permissionQueryService,
        IUserContextAccessor userContextAccessor,
        TypeAdapterConfig typeAdapterConfig,
        IExcelService excelService,
        IStringLocalizer<ExportAuditTrailsQueryHandler> localizer
    )
    {
        _dbContextFactory = dbContextFactory;
        _permissionQueryService = permissionQueryService;
        _userContextAccessor = userContextAccessor;
        _typeAdapterConfig = typeAdapterConfig;
        _excelService = excelService;
        _localizer = localizer;
    }

    public async ValueTask<byte[]> Handle(ExportAuditTrailsQuery request, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateAsync(cancellationToken);
        // Same exemption as the grid, from the same shared rule. The export is the surface where
        // a divergence would matter most: it hands the rows to a file that leaves the system.
        var visible = await AuditTrailTenantScope.VisibleAsync(
            db.AuditTrails, _permissionQueryService, _userContextAccessor.Current?.UserId);

        var data = await visible.Where(x=>x.TableName != null && x.TableName.Contains(request.Keyword ?? string.Empty))
            .ProjectToType<AuditTrailDto>(_typeAdapterConfig)
            .ToListAsync(cancellationToken);
        var result = await _excelService.ExportAsync(data,
            new Dictionary<string, Func<AuditTrailDto, object?>>
            {
                //{ _localizer["Id"], item => item.Id },
                { _localizer["Date Time"], item => item.DateTime.ToString("yyyy-MM-dd HH:mm:ss") },
                { _localizer["Table Name"], item => item.TableName },
                { _localizer["Audit Type"], item => item.AuditType },
                { _localizer["Changes"], item => item.Changes },
                { _localizer["Primary Key"], item => item.PrimaryKey }
            }, _localizer["AuditTrails"]
        );
        return result;
    }
}
