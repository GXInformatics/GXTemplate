#nullable enable
using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Infrastructure.Configurations;
using CleanArchitecture.Blazor.Infrastructure.Services.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Server.UI.IntegrationTests;

/// <summary>
/// How the idle timeout is wired into the real cookie handler, measured on the booted application
/// rather than on options built for the occasion.
/// </summary>
/// <remarks>
/// One of these assertions matters far more than the rest. Identity installs its security-stamp
/// validator as <c>OnValidatePrincipal</c>, and that is the mechanism by which "changing a user's
/// roles or password signs their existing sessions out" is true - the escalation guards depend on
/// it. Adding an idle check there means CHAINING, and a future edit that assigns over the delegate
/// instead would delete that guarantee in a way nothing else notices: the application would compile,
/// boot, pass its own permission tests, and quietly stop ending sessions whose permissions had been
/// revoked. <see cref="TheIdleCheck_DoesNotReplaceTheSecurityStampValidator"/> is what notices.
/// </remarks>
[TestFixture]
public class IdleTimeoutWiringTests
{
    private GxWebApplicationFactory _factory = null!;

    [OneTimeSetUp]
    public void StartTheApplication()
    {
        _factory = new GxWebApplicationFactory();
        _ = _factory.Services;
    }

    [OneTimeTearDown]
    public void StopTheApplication() => _factory.Dispose();

    private CookieAuthenticationOptions CookieOptions() =>
        _factory.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);

    [Test]
    public void TheCookieLifetime_IsSizedFromTheConfiguredMaximum()
    {
        // Not from the administered policy: the cookie is issued once at sign-in and cannot be
        // shortened afterwards, so it has to cover the widest window an administrator could ever
        // set. Tightening is enforced per request instead.
        var settings = _factory.Services.GetRequiredService<IdleTimeoutSettings>();

        CookieOptions().ExpireTimeSpan.Should().Be(settings.CookieLifetime);
    }

    [Test]
    public void TheCookieStillSlides()
    {
        // The keep-alive ping only helps if the cookie renews on requests.
        CookieOptions().SlidingExpiration.Should().BeTrue();
    }

    [Test]
    public async Task TheIdleCheck_DoesNotReplaceTheSecurityStampValidator()
    {
        // Driven rather than inspected: the delegate is a lambda, so there is nothing to compare it
        // against. Instead, hand it a principal that the STAMP validator must reject - one with an
        // identity Identity does not recognise - and assert the principal comes back null. If the
        // idle check had been assigned over the stamp validator, this principal would survive.
        var options = CookieOptions();
        using var scope = _factory.Services.CreateScope();
        // Two details make this test isolate the stamp validator rather than confound the two
        // checks. The ticket is issued two hours ago, because SecurityStampValidator returns early
        // for a ticket younger than its validation interval and would otherwise never run at all.
        // Activity is stamped as NOW, so the idle check passes - leaving the stamp validator as the
        // only thing that can null this principal.
        var context = await ValidateAsync(options, scope.ServiceProvider,
            issued: DateTimeOffset.UtcNow.AddHours(-2),
            lastActivity: DateTimeOffset.UtcNow);

        context.Principal.Should().BeNull(
            "the security-stamp validator must still run - assigning over OnValidatePrincipal would " +
            "silently disable it and every escalation guard that depends on it");
    }

    [Test]
    public void TheKeepAliveEndpoint_IsTheOneTheEnforcerRecognises()
    {
        // Two layers name this route - the UI maps it, Infrastructure matches it - and they read
        // the same constant so they cannot drift. Asserted because a mismatch is silent: pings
        // would return 204 and no session would ever renew.
        IdleTimeoutRoutes.KeepAlive.Should().Be("/account/keep-alive");
    }

    private static AuthenticationProperties Properties(DateTimeOffset issued, DateTimeOffset lastActivity)
    {
        var properties = new AuthenticationProperties { IssuedUtc = issued };
        properties.Items[IdleSessionEnforcer.LastActivityKey] =
            lastActivity.ToUnixTimeMilliseconds().ToString();
        return properties;
    }

    private static async Task<CookieValidatePrincipalContext> ValidateAsync(
        CookieAuthenticationOptions options,
        IServiceProvider services,
        DateTimeOffset issued,
        DateTimeOffset lastActivity)
    {
        var httpContext = new DefaultHttpContext
        {
            RequestServices = services
        };
        httpContext.Request.Path = "/";

        // The stamp validator rejects by calling SignInManager.SignOutAsync, which reads the ambient
        // HttpContext from the accessor rather than from the validation context.
        if (services.GetService<IHttpContextAccessor>() is { } accessor)
        {
            accessor.HttpContext = httpContext;
        }

        var principal = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "nobody")], "cookie"));

        var ticket = new AuthenticationTicket(
            principal,
            Properties(issued, lastActivity),
            IdentityConstants.ApplicationScheme);

        var context = new CookieValidatePrincipalContext(
            httpContext,
            new AuthenticationScheme(
                IdentityConstants.ApplicationScheme, null, typeof(CookieAuthenticationHandler)),
            options,
            ticket);

        await options.Events.ValidatePrincipal(context);
        return context;
    }
}
