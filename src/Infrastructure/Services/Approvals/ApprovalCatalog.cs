using CleanArchitecture.Blazor.Application.Common.Approvals;
using CleanArchitecture.Blazor.Domain.Common.Entities;
using CleanArchitecture.Blazor.Domain.Identity;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using CleanArchitecture.Blazor.Infrastructure.Persistence.Rules;
using Microsoft.AspNetCore.Identity;

namespace CleanArchitecture.Blazor.Infrastructure.Services.Approvals;

/// <summary>The approvable document types, read from the business model once (Pass 60).</summary>
internal sealed class ApprovalDocumentTypes(IDbContextFactory<ApplicationDbContext> contextFactory) : IApprovalDocumentTypes
{
    // Per context type, because a model is built once per context type for the life of the process.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, IReadOnlyList<string>> Cache = new();

    public IReadOnlyList<string> All
    {
        get
        {
            using var context = contextFactory.CreateDbContext();
            return Cache.GetOrAdd(context.GetType(), _ => Read(context));
        }
    }

    private static IReadOnlyList<string> Read(ApplicationDbContext context)
    {
        return context.Model.GetEntityTypes()
            .Where(e => !e.IsOwned() && !e.ClrType.IsAbstract && typeof(IApprovable).IsAssignableFrom(e.ClrType))
            .Select(e => DataModelGuard.ApprovalDocumentTypeOf(e.ClrType))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();
    }
}

/// <summary>Role lookups for the approval-limit commands (Pass 60).</summary>
internal sealed class ApprovalRoles(RoleManager<ApplicationRole> roleManager) : IApprovalRoles
{
    public async Task<bool> ExistsAsync(string roleId, CancellationToken cancellationToken) =>
        await roleManager.FindByIdAsync(roleId) is not null;

    public async Task<bool> IsAdministratorRoleAsync(string roleId, CancellationToken cancellationToken) =>
        (await roleManager.FindByIdAsync(roleId))?.Name is { } name && AdministratorProtectionService.IsAdministratorRole(name);
}
