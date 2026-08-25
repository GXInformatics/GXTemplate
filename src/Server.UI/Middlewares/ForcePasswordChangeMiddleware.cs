using CleanArchitecture.Blazor.Application.Common.Constants;

namespace CleanArchitecture.Blazor.Server.UI.Middlewares;

/// <summary>
/// Holds a signed-in user on the change-password page while they still carry a password nobody
/// chose.
/// <para>
/// This is the HTTP half of the enforcement. It catches full page loads, direct URL entry, and every
/// non-Blazor endpoint - the Hangfire dashboard, the file routes, the account endpoints. It does NOT
/// catch navigation inside a live Blazor circuit, because that never becomes an HTTP request; that
/// is what <c>ForcePasswordChangeGuard</c> covers. Both are needed, and neither is sufficient alone.
/// </para>
/// <para>
/// The flag is read from a claim rather than the database, so this costs no round-trip. See
/// <c>ApplicationUserClaimsPrincipalFactory</c> for how the claim gets there and how it is cleared.
/// </para>
/// </summary>
public class ForcePasswordChangeMiddleware
{
    /// <summary>
    /// Paths a flagged user must still be able to reach. Getting this list wrong in either direction
    /// is the whole risk of this middleware: too narrow and the user is trapped in a redirect loop
    /// or cannot sign out; too wide and the gate leaks.
    /// </summary>
    private static readonly string[] AlwaysAllowed =
    [
        // The destination itself. Redirecting it to itself is the redirect loop.
        "/account/change-password",

        // The Blazor circuit and framework assets the change-password page is rendered by. Blocking
        // these means the page cannot render at all, which looks like a hang rather than a redirect.
        "/_blazor",
        "/_framework",
        "/_content",

        // Leaving must never be blocked, or the flag becomes a lockout.
        "/pages/authentication/logout",
        "/account/logout",

        // The sign-in surface, so an expired or half-finished session can start over.
        "/account/login",
        "/pages/authentication/login"
    ];

    private readonly RequestDelegate _next;

    public ForcePasswordChangeMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (ShouldRedirect(context))
        {
            context.Response.Redirect("/account/change-password");
            return;
        }

        await _next(context);
    }

    /// <summary>
    /// The decision, separated from the pipeline so it can be tested directly against a constructed
    /// <see cref="HttpContext"/> rather than only through a running host.
    /// </summary>
    public static bool ShouldRedirect(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated != true) return false;
        if (!context.User.HasClaim(c => c.Type == ApplicationClaimTypes.MustChangePassword)) return false;

        var path = context.Request.Path;
        if (!path.HasValue) return false;

        if (IsAllowed(path.Value!)) return false;

        // Only ever redirect a navigation. Redirecting a stylesheet, an image or a background fetch
        // produces a broken page rather than a visible bounce.
        return IsNavigation(context.Request);
    }

    private static bool IsAllowed(string path)
    {
        foreach (var allowed in AlwaysAllowed)
        {
            if (path.Equals(allowed, StringComparison.OrdinalIgnoreCase)) return true;
            if (path.StartsWith(allowed + "/", StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static bool IsNavigation(HttpRequest request)
    {
        // Sec-Fetch-Mode is the precise answer where the browser sends it; the Accept sniff is the
        // fallback for the browsers and tools that do not.
        var fetchMode = request.Headers["Sec-Fetch-Mode"].ToString();
        if (!string.IsNullOrEmpty(fetchMode))
        {
            return string.Equals(fetchMode, "navigate", StringComparison.OrdinalIgnoreCase);
        }

        var accept = request.Headers.Accept.ToString();
        return accept.Contains("text/html", StringComparison.OrdinalIgnoreCase);
    }
}
