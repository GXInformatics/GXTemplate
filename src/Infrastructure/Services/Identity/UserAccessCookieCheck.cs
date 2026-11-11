using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Http;

namespace CleanArchitecture.Blazor.Infrastructure.Services.Identity;

/// <summary>
/// Signs out a cookie whose user has lost access - deactivated, or locked out by an administrator (Pass 60, Part D
/// item 4). Chained into <c>OnValidatePrincipal</c> after the security-stamp validator, on every request.
/// </summary>
/// <remarks>
/// It reads the same cached user context the hub filter reads, so it costs a memory hit in the ordinary case; the save
/// that changes a user's access invalidates that entry at once. An automatic lockout after failed sign-ins keeps the
/// cookie (owner decision 15). If the context cannot be loaded at all (the database is unreachable), the cookie is kept
/// and a warning logged: availability over a check that cannot be made, and the circuit's own revalidation remains.
/// </remarks>
public static class UserAccessCookieCheck
{
    public static async Task<bool> HasAccessAsync(HttpContext httpContext, ClaimsPrincipal principal)
    {
        try
        {
            var loader = httpContext.RequestServices.GetRequiredService<IUserContextLoader>();
            return await loader.LoadAsync(principal, httpContext.RequestAborted).ConfigureAwait(false) is not null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            httpContext.RequestServices.GetService<ILogger<UserContextLoader>>()?.LogWarning(exception,
                "Could not check whether {User} still has access; the cookie is kept for this request.", principal.Identity?.Name);
            return true;
        }
    }

    /// <summary>Wraps <paramref name="inner"/>: after it, reject and sign out a principal that has lost access.</summary>
    public static Func<CookieValidatePrincipalContext, Task> After(Func<CookieValidatePrincipalContext, Task> inner) =>
        async context =>
        {
            await inner(context).ConfigureAwait(false);
            if (context.Principal is null) return; // the stamp validator already rejected it

            if (await HasAccessAsync(context.HttpContext, context.Principal).ConfigureAwait(false)) return;

            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme).ConfigureAwait(false);
        };
}
