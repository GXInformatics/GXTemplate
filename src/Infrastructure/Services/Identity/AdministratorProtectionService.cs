using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Application.Common.ExceptionHandlers;
using CleanArchitecture.Blazor.Domain.Identity;
using Microsoft.AspNetCore.Identity;

namespace CleanArchitecture.Blazor.Infrastructure.Services.Identity;

/// <summary>
/// The rules that keep the application administrable: the Administrator role must continue to exist,
/// must keep its full grant, and must keep at least one member.
/// <para>
/// These live in one service rather than at each call site so that the whole protection surface can
/// be read in one file. Role and user administration does not go through Mediator - the pages call
/// <see cref="RoleManager{TRole}"/> and <see cref="UserManager{TUser}"/> directly - so
/// AuthorizationBehaviour's deny-by-default does not reach them and there is no natural chokepoint
/// to hook. Every UI path that can violate a rule therefore calls the matching method here.
/// </para>
/// </summary>
public sealed class AdministratorProtectionService
{
    private readonly IServiceScopeFactory _scopeFactory;

    public AdministratorProtectionService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    /// <summary>The role name that must always exist and always have a member.</summary>
    public static string AdministratorRole => Roles.Admin;

    public static bool IsAdministratorRole(string? roleName) =>
        string.Equals(roleName, AdministratorRole, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Deleting the Administrator role would leave nobody able to administer the application, and
    /// nothing recreates it outside first-run seeding.
    /// </summary>
    public void EnsureRoleCanBeDeleted(string? roleName)
    {
        if (IsAdministratorRole(roleName))
        {
            throw new ForbiddenAccessException(
                $"The '{AdministratorRole}' role is protected and cannot be deleted.");
        }
    }

    /// <summary>
    /// The Administrator role is granted every permission by seeding, so an edit to its permission
    /// set is either a no-op or a removal of the application's own administrability.
    /// </summary>
    public void EnsureRolePermissionsCanBeModified(string? roleName)
    {
        if (IsAdministratorRole(roleName))
        {
            throw new ForbiddenAccessException(
                $"Permissions on the '{AdministratorRole}' role are protected and cannot be modified.");
        }
    }

    /// <summary>
    /// Refuses a change that would leave the Administrator role with no members.
    /// </summary>
    /// <param name="userId">The user whose administrator membership is about to end.</param>
    /// <param name="action">Named in the error message, e.g. "removed from the role" or "deleted".</param>
    public async Task EnsureNotRemovingLastAdministratorAsync(string userId, string action)
    {
        if (string.IsNullOrEmpty(userId))
        {
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var user = await userManager.FindByIdAsync(userId);
        if (user is null || !await userManager.IsInRoleAsync(user, AdministratorRole))
        {
            // Not an administrator, so this change cannot reduce the administrator count.
            return;
        }

        var administrators = await userManager.GetUsersInRoleAsync(AdministratorRole);
        if (administrators.Count <= 1)
        {
            throw new ForbiddenAccessException(
                $"The last remaining member of the '{AdministratorRole}' role cannot be {action}. " +
                "Grant the role to another account first.");
        }
    }

    /// <summary>
    /// Convenience overload for a role-membership rewrite: only guards when the user currently holds
    /// the Administrator role and the new set does not.
    /// </summary>
    public Task EnsureRoleRewriteKeepsAnAdministratorAsync(
        string userId, IEnumerable<string> currentRoles, IEnumerable<string> assignedRoles)
    {
        var losesAdministrator =
            currentRoles.Any(IsAdministratorRole) && !assignedRoles.Any(IsAdministratorRole);

        return losesAdministrator
            ? EnsureNotRemovingLastAdministratorAsync(userId, "removed from the role")
            : Task.CompletedTask;
    }
}
