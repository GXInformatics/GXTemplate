#nullable enable
using CleanArchitecture.Blazor.Server.UI.Middlewares;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.Middlewares;

/// <summary>
/// "Enabled: false makes the idle timeout inert" has to include its screens, or the claim is only
/// half true. Pass 16A found the security-settings route still answering 200 with the feature off,
/// rendering a panel that explained the feature was off - which is not the same thing as the feature
/// not being there.
///
/// Same shape and same answer as <see cref="SelfRegistrationMiddleware"/>: 404, because with the
/// feature disabled the screen does not exist, and 403 would confirm that it does.
/// </summary>
[TestFixture]
public class SecuritySettingsPageMiddlewareTests
{
    [Test]
    public void WhenTheIdleTimeoutIsOff_TheSettingsRouteIsBlocked()
    {
        SecuritySettingsPageMiddleware
            .ShouldBlock(new PathString("/system/security-settings"), idleTimeoutEnabled: false)
            .Should().BeTrue();
    }

    [Test]
    public void WhenTheIdleTimeoutIsOn_TheSettingsRouteIsNotBlocked()
    {
        SecuritySettingsPageMiddleware
            .ShouldBlock(new PathString("/system/security-settings"), idleTimeoutEnabled: true)
            .Should().BeFalse();
    }

    [TestCase("/system/security-settings/")]
    [TestCase("/system/security-settings/anything")]
    [TestCase("/SYSTEM/SECURITY-SETTINGS")]
    public void TrailingSegmentsAndCasing_AreNotAWayAround(string path)
    {
        SecuritySettingsPageMiddleware
            .ShouldBlock(new PathString(path), idleTimeoutEnabled: false)
            .Should().BeTrue();
    }

    [TestCase("/")]
    [TestCase("/system/logs")]
    [TestCase("/system/audittrails")]
    [TestCase("/system/picklistset")]
    [TestCase("/user/profile")]
    [TestCase("/account/login")]
    [TestCase("/account/keep-alive")]
    public void NoOtherPathIsAffected(string path)
    {
        // The blast radius, stated. A prefix match that caught "/system/..." would take the whole
        // System menu down with the feature.
        SecuritySettingsPageMiddleware
            .ShouldBlock(new PathString(path), idleTimeoutEnabled: false)
            .Should().BeFalse();
    }
}
