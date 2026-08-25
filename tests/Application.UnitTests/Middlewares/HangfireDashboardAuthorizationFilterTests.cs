#nullable enable
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Application.Common.Security;
using CleanArchitecture.Blazor.Server.UI.Middlewares;
using FluentAssertions;
using Hangfire;
using Hangfire.Dashboard;
using Hangfire.InMemory;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.Middlewares;

/// <summary>
/// Regression tests for the Hangfire dashboard filters. Before the fix both filters returned true
/// unconditionally, so /jobs was reachable by anyone - including anonymous callers - and the
/// Permissions.Hangfire.View constant had no consumers at all.
/// </summary>
[TestFixture]
public class HangfireDashboardAuthorizationFilterTests
{
    /// <summary>
    /// Registers the permission policies the same way Infrastructure's AddIdentityServices does:
    /// the permission string is the policy name, and the policy requires a matching permission claim.
    /// </summary>
    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorizationCore(options =>
            options.AddPolicy(Permissions.Hangfire.View, policy =>
                policy.RequireClaim(ApplicationClaimTypes.Permission, Permissions.Hangfire.View)));
        return services.BuildServiceProvider();
    }

    private static DashboardContext ContextFor(ClaimsPrincipal user, ServiceProvider provider)
    {
        var httpContext = new DefaultHttpContext
        {
            User = user,
            RequestServices = provider
        };
        return new AspNetCoreDashboardContext(new InMemoryStorage(), new DashboardOptions(), httpContext);
    }

    private static ClaimsPrincipal Anonymous() => new(new ClaimsIdentity());

    private static ClaimsPrincipal Authenticated(params string[] permissions)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, "someone") };
        foreach (var permission in permissions)
        {
            claims.Add(new Claim(ApplicationClaimTypes.Permission, permission));
        }
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "TestAuth"));
    }

    [Test]
    public async Task AnonymousCaller_IsDeniedByBothFilters()
    {
        using var provider = BuildServices();
        var context = ContextFor(Anonymous(), provider);

        new HangfireDashboardAuthorizationFilter().Authorize(context).Should().BeFalse();
        (await new HangfireDashboardAsyncAuthorizationFilter().AuthorizeAsync(context)).Should().BeFalse();
    }

    [Test]
    public async Task AuthenticatedCallerWithoutThePermission_IsDeniedByBothFilters()
    {
        // The seeded Basic role holds only Permissions.Documents.View/Download, so this is an ordinary member's position.
        using var provider = BuildServices();
        var context = ContextFor(Authenticated("Permissions.Documents.View"), provider);

        new HangfireDashboardAuthorizationFilter().Authorize(context).Should().BeFalse();
        (await new HangfireDashboardAsyncAuthorizationFilter().AuthorizeAsync(context)).Should().BeFalse();
    }

    [Test]
    public async Task AuthenticatedCallerWithThePermission_IsAllowedByBothFilters()
    {
        // The seeded Admin role is granted every permission by reflection, including this one.
        using var provider = BuildServices();
        var context = ContextFor(Authenticated(Permissions.Hangfire.View), provider);

        new HangfireDashboardAuthorizationFilter().Authorize(context).Should().BeTrue();
        (await new HangfireDashboardAsyncAuthorizationFilter().AuthorizeAsync(context)).Should().BeTrue();
    }

    [Test]
    public void ThePermissionConstantIsTheRegisteredPolicyName()
    {
        Permissions.Hangfire.View.Should().Be("Permissions.Hangfire.View");
    }
}
#nullable restore
