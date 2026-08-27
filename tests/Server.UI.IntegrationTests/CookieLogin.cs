#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace CleanArchitecture.Blazor.Server.UI.IntegrationTests;

/// <summary>
/// Drives the real cookie sign-in, end to end, over HTTP.
/// </summary>
/// <remarks>
/// Pass 2 A9 recorded that cookie login could not be driven this way, and every pass afterwards
/// carried that limitation - which is how a login regression reached a browser before anyone caught
/// it. It is drivable; it just has three requirements that all have to be met at once, and missing
/// any one of them looks like "login is untestable":
/// <list type="number">
/// <item>an antiforgery token, taken from a real page load in the same cookie session (App.razor
/// emits it as <c>&lt;meta name="xsrf-token"&gt;</c>) and sent as the RequestVerificationToken header;</item>
/// <item>a Referer header matching the request's own origin, which the login endpoint checks
/// explicitly before it will read the form;</item>
/// <item>a client that keeps cookies and does not auto-follow the redirect the endpoint answers with.</item>
/// </list>
/// </remarks>
public static class CookieLogin
{
    private static readonly Regex XsrfToken =
        new("name=\"xsrf-token\" content=\"([^\"]+)\"", RegexOptions.Compiled);

    public const string LoginPath = "/account/login";
    public const string LoginEndpoint = "/pages/authentication/login";

    /// <summary>
    /// Signs the client in and returns the login endpoint's own response.
    /// </summary>
    public static async Task<HttpResponseMessage> SignInAsync(
        HttpClient client, string userName, string password)
    {
        var token = await GetAntiforgeryTokenAsync(client);

        var request = new HttpRequestMessage(HttpMethod.Post, LoginEndpoint)
        {
            Content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("userName", userName),
                new KeyValuePair<string, string>("password", password),
                new KeyValuePair<string, string>("rememberMe", "false")
            })
        };

        // Both headers are load-bearing: without the token the antiforgery middleware refuses to let
        // the endpoint read the form at all, and without the Referer the endpoint's own origin check
        // returns Forbid before it looks at the credentials.
        request.Headers.TryAddWithoutValidation("RequestVerificationToken", token);
        request.Headers.TryAddWithoutValidation("Referer", client.BaseAddress + LoginPath.TrimStart('/'));

        return await client.SendAsync(request);
    }

    /// <summary>
    /// Signs in and asserts it worked, so a test that merely needs an authenticated client fails at
    /// the sign-in rather than misattributing the failure to whatever it was actually checking.
    /// </summary>
    public static async Task SignInAndExpectSuccessAsync(
        HttpClient client, string userName, string password)
    {
        var response = await SignInAsync(client, userName, password);
        if (response.StatusCode is not (HttpStatusCode.Redirect or HttpStatusCode.Found or HttpStatusCode.OK))
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"Sign-in failed with {(int)response.StatusCode}: {body}");
        }
    }

    /// <summary>
    /// Fetches a page and pulls the antiforgery request token out of it.
    /// </summary>
    public static async Task<string> GetAntiforgeryTokenAsync(HttpClient client, string path = LoginPath)
    {
        var html = await client.GetStringAsync(path);
        var match = XsrfToken.Match(html);
        if (!match.Success)
        {
            throw new InvalidOperationException(
                $"No antiforgery token found on {path}; App.razor is expected to emit one as <meta name=\"xsrf-token\">.");
        }

        return match.Groups[1].Value;
    }

    /// <summary>An <c>&lt;img&gt;</c>-shaped request, so navigation-only middleware is not what gets measured.</summary>
    public static Task<HttpResponseMessage> GetAsAssetAsync(HttpClient client, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "no-cors");
        request.Headers.TryAddWithoutValidation("Accept", "image/*");
        return client.SendAsync(request);
    }
}
#nullable restore
