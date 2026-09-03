using CleanArchitecture.Blazor.Application.Features.Tenants.DTOs;
namespace CleanArchitecture.Blazor.Application.Common.Interfaces;

/// <summary>
/// Service for managing tenant switching functionality
/// </summary>
public interface ITenantSwitchService
{
    
    
    /// <summary>
    /// Switch user to specified tenant
    /// </summary>
    /// <param name="userId">User ID</param>
    /// <param name="tenantId">Target tenant ID</param>
    /// <returns>Result of the switch operation</returns>
    Task<Result> SwitchToTenantAsync(string userId, string tenantId);
    
    /// <summary>
    /// Check if user can switch to specified tenant
    /// </summary>
    /// <param name="userId">User ID</param>
    /// <param name="tenantId">Target tenant ID</param>
    /// <returns>True if user can switch to the tenant</returns>
    Task<bool> CanSwitchToTenantAsync(string userId, string tenantId);

    /// <summary>
    /// The tenants this user may switch into.
    /// </summary>
    /// <remarks>
    /// <b>Exactly the set <see cref="CanSwitchToTenantAsync"/> would answer true for.</b> Both derive
    /// from one rule inside the implementation, so a menu built from this cannot offer a switch the
    /// service will refuse, nor hide one it would allow. That agreement is structural, and it is
    /// asserted as a single property rather than by checking each side alone.
    /// <para>
    /// Empty means the principal may not switch at all - which is a real answer, not a failure, and
    /// is what a caller should render as "no switching offered" rather than as an empty menu.
    /// </para>
    /// </remarks>
    /// <param name="userId">User ID</param>
    Task<IReadOnlyList<TenantDto>> GetSwitchableTenantsAsync(string userId);
}

 
