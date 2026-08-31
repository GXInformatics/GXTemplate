using CleanArchitecture.Blazor.Application.Common.Interfaces;

namespace CleanArchitecture.Blazor.Server.UI.Middlewares;

/// <summary>
/// Closes the security-settings screen when the idle timeout is switched off in configuration.
/// </summary>
/// <remarks>
/// The same shape as <see cref="SelfRegistrationMiddleware"/>, and for the same reason: a runtime
/// flag rather than conditional source removal, so a generated project can turn the feature on or
/// off without regenerating from the template.
/// <para>
/// The response is <b>404, not 403</b>, exactly as the self-registration surface answers: with the
/// idle timeout disabled the screen does not exist, and saying "forbidden" would confirm it is
/// there. It is also not an authorization failure - a user holding
/// <c>Permissions.SecuritySettings.Edit</c> is not being refused, there is simply nothing to edit.
/// </para>
/// <para>
/// This is one of the two surfaces the feature owns. The other, the Security tab on the profile
/// page, is a component rather than a route, so <c>Profile.razor</c> omits the tab panel itself -
/// there is no route to close. Both must agree, or "Enabled: false makes the feature inert" is only
/// half true (Pass 16A, Finding 3, which found this screen answering 200 and the profile showing an
/// empty tab).
/// </para>
/// </remarks>
public class SecuritySettingsPageMiddleware
{
    /// <summary>The route the security-settings page is served at.</summary>
    public const string SecuritySettingsPath = "/system/security-settings";

    private readonly RequestDelegate _next;

    public SecuritySettingsPageMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, IIdleTimeoutSettings idleTimeoutSettings)
    {
        if (ShouldBlock(context.Request.Path, idleTimeoutSettings.Enabled))
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
    public static bool ShouldBlock(PathString path, bool idleTimeoutEnabled)
    {
        if (idleTimeoutEnabled) return false;
        if (!path.HasValue) return false;

        var value = path.Value!;

        // Trailing segments and trailing slashes must not be a way around the block.
        return value.Equals(SecuritySettingsPath, StringComparison.OrdinalIgnoreCase)
               || value.StartsWith(SecuritySettingsPath + "/", StringComparison.OrdinalIgnoreCase);
    }
}
