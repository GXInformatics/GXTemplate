// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel.DataAnnotations;
using CleanArchitecture.Blazor.Application.Features.Tenants.Caching;
using CleanArchitecture.Blazor.Application.Features.Tenants.DTOs;

namespace CleanArchitecture.Blazor.Application.Features.Tenants.Commands.Update;

[RequestAuthorize(Policy = Permissions.Tenants.Edit)]
public class UpdateTenantCommand : ITenantForm, ICacheInvalidatorRequest<Result<string>>
{
    /// <summary>The tenant to change. No default: an update of an invented id is refused.</summary>
    [Display(Name = "Tenant Id")] public string Id { get; set; } = string.Empty;
    [Display(Name = "Tenant Name")] public string? Name { get; set; }
    [Display(Name = "Description")] public string? Description { get; set; }
    public string CacheKey => TenantCacheKey.GetAllCacheKey;
    public IEnumerable<string>? Tags => TenantCacheKey.Tags;
}

public class UpdateTenantCommandValidator : TenantFormValidator<UpdateTenantCommand>
{
}

public class UpdateTenantCommandHandler : IRequestHandler<UpdateTenantCommand, Result<string>>
{
    public const string NotFound = "Tenant not found.";

    private readonly IApplicationDbContextFactory _dbContextFactory;
    private readonly IDataSourceService<TenantDto> _tenantsService;

    public UpdateTenantCommandHandler(
        IApplicationDbContextFactory dbContextFactory,
        IDataSourceService<TenantDto> tenantsService)
    {
        _dbContextFactory = dbContextFactory;
        _tenantsService = tenantsService;
    }

    public async ValueTask<Result<string>> Handle(UpdateTenantCommand request, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateAsync(cancellationToken);

        // Update never creates - the mirror of CreateTenantCommandHandler's rule.
        var item = await db.Tenants.FindAsync([request.Id], cancellationToken);
        if (item is null)
            return await Result<string>.FailureAsync(NotFound);

        item.Name = request.Name;
        item.Description = request.Description;
        await db.SaveChangesAsync(cancellationToken);
        await _tenantsService.RefreshAsync();
        return await Result<string>.SuccessAsync(item.Id);
    }
}
