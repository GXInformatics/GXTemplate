using CleanArchitecture.Blazor.Application.Common.Interfaces;

namespace CleanArchitecture.Blazor.Server.UI.Middlewares;

/// <summary>
/// Closes the self-service account-creation surface when
/// <see cref="IApplicationSettings.AllowSelfRegistration"/> is false.
/// </summary>
/// <remarks>
/// A runtime configuration flag rather than conditional source removal, so a generated project can
/// turn registration on or off later without regenerating from the template.
/// <para>
/// There are <b>two</b> self-service doors, and closing only the obvious one would make the flag a
/// lie. The registration pages are the first. The second is the external-login callback: when an
/// external identity signs in and no account matches it, the app redirects to
/// <c>/account/linkexternallogin</c> and <c>/pages/authentication/performlinkexternallogin</c>
/// <b>creates a brand-new user</b> for it. Both are blocked here.
/// </para>
/// <para>
/// External login for accounts that <b>already exist</b> is untouched: that path signs in at
/// <c>/pages/authentication/externallogin</c> and only falls through to the provisioning pages when
/// no account matches.
/// </para>
/// <para>
/// The response is <b>404, not 403</b>: with registration disabled the feature does not exist, and
/// saying "forbidden" would confirm the endpoint is there.
/// </para>
/// </remarks>
public class SelfRegistrationMiddleware
{
    /// <summary>
    /// Every path that can create an account without an existing one.
    /// </summary>
    private static readonly string[] SelfRegistrationPaths =
    [
        // The registration form and the page it hands off to.
        "/account/register",
        "/account/registerconfirmation",

        // External-login provisioning: the form that collects tenant/timezone/language, and the
        // endpoint that actually calls UserManager.CreateAsync with them.
        "/account/linkexternallogin",
        "/pages/authentication/performlinkexternallogin"
    ];

    private readonly RequestDelegate _next;

    public SelfRegistrationMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, IApplicationSettings applicationSettings)
    {
        if (ShouldBlock(context.Request.Path, applicationSettings.AllowSelfRegistration))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await _next(context);
    }

    /// <summary>
    /// The decision, separated from the pipeline so it can be tested directly against a path rather
    /// than only through a running host.
    /// </summary>
    public static bool ShouldBlock(PathString path, bool allowSelfRegistration)
    {
        if (allowSelfRegistration) return false;
        if (!path.HasValue) return false;

        var value = path.Value!;
        foreach (var blocked in SelfRegistrationPaths)
        {
            if (value.Equals(blocked, StringComparison.OrdinalIgnoreCase)) return true;

            // Trailing segments and trailing slashes must not be a way around the list.
            if (value.StartsWith(blocked + "/", StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }
}
