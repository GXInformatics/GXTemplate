using System.Security.Claims;
using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Application.Common.ExceptionHandlers;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Application.Common.Security;
using CleanArchitecture.Blazor.Domain.Identity;
using Microsoft.AspNetCore.Identity;

namespace CleanArchitecture.Blazor.Infrastructure.Services.Identity;

public class PermissionAssignmentService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IPermissionQueryService _permissionQueryService;
    private readonly IUserContextAccessor _userContextAccessor;
    private readonly AdministratorProtectionService _administratorProtection;
    private readonly ILogger<PermissionAssignmentService> _logger;

    public PermissionAssignmentService(
        IServiceScopeFactory scopeFactory,
        IPermissionQueryService permissionQueryService,
        IUserContextAccessor userContextAccessor,
        AdministratorProtectionService administratorProtection,
        ILogger<PermissionAssignmentService> logger)
    {
        _scopeFactory = scopeFactory;
        _permissionQueryService = permissionQueryService;
        _userContextAccessor = userContextAccessor;
        _administratorProtection = administratorProtection;
        _logger = logger;
    }

    public Task<IList<PermissionModel>> LoadUserPermissionsAsync(string userId)
    {
        return _permissionQueryService.GetAllPermissionsByUserId(userId);
    }

    public Task<IList<PermissionModel>> LoadRolePermissionsAsync(string roleId)
    {
        return _permissionQueryService.GetAllPermissionsByRoleId(roleId);
    }

    public async Task AssignUserAsync(PermissionModel model)
    {
        using var scope = _scopeFactory.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var userId = model.UserId ?? throw new ArgumentNullException(nameof(model.UserId));
        var user = await userManager.FindByIdAsync(userId)
                   ?? throw new NotFoundException($"User not found: {userId}");

        var actor = await GetActorAsync(scope);
        EnsureNotTargetingSelf(actor, userId);
        EnsureActorHolds(actor, model);

        var claim = new Claim(model.ClaimType, model.ClaimValue);
        var result = model.Assigned
            ? await userManager.AddClaimAsync(user, claim)
            : await userManager.RemoveClaimAsync(user, claim);

        EnsureSucceeded(result, "update user permission", userId);
    }

    public async Task AssignRoleAsync(PermissionModel model)
    {
        using var scope = _scopeFactory.CreateScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();

        var roleId = model.RoleId ?? throw new ArgumentNullException(nameof(model.RoleId));
        var role = await roleManager.FindByIdAsync(roleId)
                   ?? throw new NotFoundException($"Role not found: {roleId}");

        _administratorProtection.EnsureRolePermissionsCanBeModified(role.Name);
        var actor = await GetActorAsync(scope);
        EnsureNotTargetingAHeldRole(actor, role.Name);
        EnsureActorHolds(actor, model);

        var claim = new Claim(model.ClaimType, model.ClaimValue);
        var result = model.Assigned
            ? await roleManager.AddClaimAsync(role, claim)
            : await roleManager.RemoveClaimAsync(role, claim);

        EnsureSucceeded(result, "update role permission", roleId);
    }

    public async Task AssignUserBulkAsync(IEnumerable<PermissionModel> models)
    {
        using var scope = _scopeFactory.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var list = models.ToList();
        if (!list.Any())
        {
            return;
        }

        var userId = GetSingleUserId(list);
        var user = await userManager.FindByIdAsync(userId)
                   ?? throw new NotFoundException($"User not found: {userId}");

        // The actor is resolved ONCE for the whole batch: building their claims principal costs
        // several database round-trips, and a bulk grant can carry every permission in the system.
        var actor = await GetActorAsync(scope);
        EnsureNotTargetingSelf(actor, userId);
        foreach (var model in list)
        {
            EnsureActorHolds(actor, model);
        }

        foreach (var model in list)
        {
            var claim = new Claim(model.ClaimType, model.ClaimValue);
            var result = model.Assigned
                ? await userManager.AddClaimAsync(user, claim)
                : await userManager.RemoveClaimAsync(user, claim);

            EnsureSucceeded(result, "update user permission", userId);
        }
    }

    public async Task AssignRoleBulkAsync(IEnumerable<PermissionModel> models)
    {
        using var scope = _scopeFactory.CreateScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();

        var list = models.ToList();
        if (!list.Any())
        {
            return;
        }

        var roleId = GetSingleRoleId(list);
        var role = await roleManager.FindByIdAsync(roleId)
                   ?? throw new NotFoundException($"Role not found: {roleId}");

        _administratorProtection.EnsureRolePermissionsCanBeModified(role.Name);
        // Resolved once for the batch - see AssignUserBulkAsync.
        var actor = await GetActorAsync(scope);
        EnsureNotTargetingAHeldRole(actor, role.Name);
        foreach (var model in list)
        {
            EnsureActorHolds(actor, model);
        }

        foreach (var model in list)
        {
            var claim = new Claim(model.ClaimType, model.ClaimValue);
            var result = model.Assigned
                ? await roleManager.AddClaimAsync(role, claim)
                : await roleManager.RemoveClaimAsync(role, claim);

            EnsureSucceeded(result, "update role permission", roleId);
        }
    }

    /// <summary>
    /// A snapshot of the acting principal: who they are, which roles they hold, and every
    /// permission they effectively have. Built once per operation.
    /// </summary>
    private sealed record Actor(string UserId, IReadOnlySet<string> Roles, IReadOnlySet<string> Permissions);

    /// <summary>
    /// Resolves the acting principal from the ambient user context and materialises their effective
    /// permission set in ONE claims-principal build. The factory folds user claims and the claims of
    /// every role they hold into a single principal, which is why a bulk grant of eighty permissions
    /// costs one rebuild rather than eighty AuthorizeAsync round-trips.
    /// </summary>
    private async Task<Actor> GetActorAsync(IServiceScope scope)
    {
        // Fail closed, exactly as AuthorizationBehaviour does: no ambient principal, no grant. Every
        // caller of this service is a Blazor circuit event handler, so the hub filter has populated
        // the context by the time we get here.
        var current = _userContextAccessor.Current;
        if (current is null || string.IsNullOrEmpty(current.UserId))
        {
            throw new ForbiddenAccessException(
                "Permission changes require an authenticated user; none is in context.");
        }

        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var principalFactory = scope.ServiceProvider
            .GetRequiredService<IUserClaimsPrincipalFactory<ApplicationUser>>();

        var actorUser = await userManager.FindByIdAsync(current.UserId)
            ?? throw new ForbiddenAccessException(
                "Permission changes require an authenticated user; the acting account no longer exists.");

        var principal = await principalFactory.CreateAsync(actorUser);
        var permissions = principal.FindAll(ApplicationClaimTypes.Permission)
            .Select(claim => claim.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var roles = (await userManager.GetRolesAsync(actorUser))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new Actor(actorUser.Id, roles, permissions);
    }

    /// <summary>
    /// Grant-what-you-hold: a principal may only add or remove a permission they themselves hold.
    /// Without this, anyone who reaches the permissions editor can grant themselves - or anyone
    /// else - every permission in the system.
    /// </summary>
    private void EnsureActorHolds(Actor actor, PermissionModel model)
    {
        if (!string.Equals(model.ClaimType, ApplicationClaimTypes.Permission, StringComparison.Ordinal))
        {
            throw new ForbiddenAccessException(
                $"Only '{ApplicationClaimTypes.Permission}' claims can be assigned here.");
        }

        if (actor.Permissions.Contains(model.ClaimValue))
        {
            return;
        }

        _logger.LogWarning(
            "User {ActorId} attempted to change permission {Permission}, which they do not hold.",
            actor.UserId, model.ClaimValue);
        throw new ForbiddenAccessException(
            $"You cannot grant or revoke '{model.ClaimValue}' because you do not hold it.");
    }

    /// <summary>Closes self-escalation and self-lockout on the actor's own account.</summary>
    private static void EnsureNotTargetingSelf(Actor actor, string targetUserId)
    {
        if (string.Equals(actor.UserId, targetUserId, StringComparison.Ordinal))
        {
            throw new ForbiddenAccessException(
                "You cannot change the permissions on your own account.");
        }
    }

    /// <summary>Same rule one level up: a role the actor holds is an extension of their own account.</summary>
    private static void EnsureNotTargetingAHeldRole(Actor actor, string? roleName)
    {
        if (!string.IsNullOrEmpty(roleName) && actor.Roles.Contains(roleName))
        {
            throw new ForbiddenAccessException(
                $"You cannot change the permissions on '{roleName}' because you are a member of it.");
        }
    }

    private static string GetSingleUserId(IReadOnlyCollection<PermissionModel> models)
    {
        var userIds = models.Select(model => model.UserId)
            .Where(userId => !string.IsNullOrWhiteSpace(userId))
            .Distinct()
            .ToList();

        return userIds.Count switch
        {
            1 => userIds[0]!,
            0 => throw new ArgumentException("Permission models must include a user id.", nameof(models)),
            _ => throw new ArgumentException("Bulk user permission updates must target a single user.", nameof(models))
        };
    }

    private static string GetSingleRoleId(IReadOnlyCollection<PermissionModel> models)
    {
        var roleIds = models.Select(model => model.RoleId)
            .Where(roleId => !string.IsNullOrWhiteSpace(roleId))
            .Distinct()
            .ToList();

        return roleIds.Count switch
        {
            1 => roleIds[0]!,
            0 => throw new ArgumentException("Permission models must include a role id.", nameof(models)),
            _ => throw new ArgumentException("Bulk role permission updates must target a single role.", nameof(models))
        };
    }

    private void EnsureSucceeded(IdentityResult result, string action, string entityId)
    {
        if (result.Succeeded)
        {
            return;
        }

        var errors = string.Join(", ", result.Errors.Select(error => error.Description));
        _logger.LogWarning("Failed to {Action} for {EntityId}: {Errors}", action, entityId, errors);
        throw new InvalidOperationException($"Failed to {action} for '{entityId}': {errors}");
    }
}