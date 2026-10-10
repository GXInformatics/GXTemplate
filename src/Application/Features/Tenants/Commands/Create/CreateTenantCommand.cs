// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel.DataAnnotations;
using CleanArchitecture.Blazor.Application.Common.Interfaces.MultiTenant;
using CleanArchitecture.Blazor.Application.Features.Tenants.Caching;
using CleanArchitecture.Blazor.Application.Features.Tenants.DTOs;

namespace CleanArchitecture.Blazor.Application.Features.Tenants.Commands.Create;

[RequestAuthorize(Policy = Permissions.Tenants.Create)]
public class CreateTenantCommand : ITenantForm, ICacheInvalidatorRequest<Result<string>>
{
    /// <summary>
    /// Shown in the form before saving, so it is generated here - with the same
    /// <see cref="Guid.CreateVersion7()"/> as <see cref="Tenant.Id"/> and <see cref="TenantDto.Id"/>
    /// (it used to be <c>Guid.NewGuid()</c>, the one random-v4 id among them).
    /// </summary>
    [Display(Name = "Tenant Id")] public string Id { get; set; } = Guid.CreateVersion7().ToString();
    [Display(Name = "Tenant Name")] public string? Name { get; set; }
    [Display(Name = "Description")] public string? Description { get; set; }
    public string CacheKey => TenantCacheKey.GetAllCacheKey;
    public IEnumerable<string>? Tags => TenantCacheKey.Tags;
}

public class CreateTenantCommandValidator : TenantFormValidator<CreateTenantCommand>
{
}

public class CreateTenantCommandHandler : IRequestHandler<CreateTenantCommand, Result<string>>
{
    public const string AlreadyExists = "A tenant with this id already exists.";

    private readonly IApplicationDbContextFactory _dbContextFactory;
    private readonly IDataSourceService<TenantDto> _tenantsService;
    private readonly ITenantSeedRunner _tenantSeedRunner;

    public CreateTenantCommandHandler(
        IApplicationDbContextFactory dbContextFactory,
        IDataSourceService<TenantDto> tenantsService,
        ITenantSeedRunner tenantSeedRunner)
    {
        _dbContextFactory = dbContextFactory;
        _tenantsService = tenantsService;
        _tenantSeedRunner = tenantSeedRunner;
    }

    public async ValueTask<Result<string>> Handle(CreateTenantCommand request, CancellationToken cancellationToken)
    {
        await using (var db = await _dbContextFactory.CreateAsync(cancellationToken))
        {
            // Create never updates. The old AddEdit command fell through to an update when the id
            // existed, which is how a Create-only caller could rewrite an existing tenant.
            if (await db.Tenants.AnyAsync(t => t.Id == request.Id, cancellationToken))
                return await Result<string>.FailureAsync(AlreadyExists);

            db.Tenants.Add(new Tenant { Id = request.Id, Name = request.Name, Description = request.Description });
            await db.SaveChangesAsync(cancellationToken);
        }

        // After the tenant is committed, so a seeder can reference it. If a seeder fails the tenant
        // still exists and the error reaches the caller; the next start's ProvisionAsync re-runs every
        // seeder for every tenant, which is why seeders are idempotent per item (see ITenantSeeder).
        await _tenantSeedRunner.SeedAsync(request.Id, cancellationToken);

        await _tenantsService.RefreshAsync();
        return await Result<string>.SuccessAsync(request.Id);
    }
}
