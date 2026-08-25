using CleanArchitecture.Blazor.Application.Common.Security;
using Hangfire.Dashboard;
using Microsoft.AspNetCore.Authorization;

namespace CleanArchitecture.Blazor.Server.UI.Middlewares;

/// <summary>
/// Grants access to the Hangfire dashboard only to authenticated users holding the
/// <see cref="Permissions.Hangfire.View"/> permission. The permission name doubles as the
/// authorization policy name: every constant on <see cref="Permissions"/> is registered as a
/// policy requiring a matching <c>ApplicationClaimTypes.Permission</c> claim.
/// </summary>
public class HangfireDashboardAsyncAuthorizationFilter : IDashboardAsyncAuthorizationFilter
{
    public async Task<bool> AuthorizeAsync(DashboardContext context)
    {
        var httpContext = context.GetHttpContext();
        if (httpContext?.User?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        var authorizationService = httpContext.RequestServices.GetRequiredService<IAuthorizationService>();
        var result = await authorizationService.AuthorizeAsync(httpContext.User, Permissions.Hangfire.View);
        return result.Succeeded;
    }
}

/// <inheritdoc cref="HangfireDashboardAsyncAuthorizationFilter"/>
public class HangfireDashboardAuthorizationFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var httpContext = context.GetHttpContext();
        if (httpContext?.User?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        var authorizationService = httpContext.RequestServices.GetRequiredService<IAuthorizationService>();

        // Hangfire's synchronous filter contract has no async counterpart. The permission policies are
        // pure claim checks (no I/O), so evaluating them synchronously here cannot block on external work.
        return authorizationService.AuthorizeAsync(httpContext.User, Permissions.Hangfire.View)
            .GetAwaiter().GetResult().Succeeded;
    }
}
