#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CleanArchitecture.Blazor.Application.Common.Security;
using FluentAssertions;
using Mediator;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.Security;

/// <summary>
/// Tests for the startup assertion that enforces the deny-by-default contract before the
/// application serves anything, rather than leaving an unmarked request to be discovered when a
/// user hits it.
/// </summary>
[TestFixture]
public class RequestAuthorizationRegistryTests
{
    // 22 until Pass 11B deleted ExportSystemLogsQuery, which had a handler and a
    // Permissions.Logs.Export policy but no caller: the SystemLogs page has never had an Export
    // button. Confirmed by inspection before the count was lowered, which is what this guard is for.
    private const int ExpectedRequestTypeCount = 21;

    private static Assembly ApplicationAssembly =>
        typeof(CleanArchitecture.Blazor.Application.DependencyInjection).Assembly;

    /// <summary>
    /// The expected number of request types. Hard-coded on purpose. Its job is not to compute the count but to force a
    /// conscious update: adding or removing a request should make someone confirm the new number and
    /// the authorization decision that came with it, rather than the suite silently tracking a drift.
    /// If this fails, check the new request carries the right RequestAuthorizeAttribute, then update
    /// the number here.
    /// </summary>
    [Test]
    public void TheApplicationDeclaresTheExpectedNumberOfRequestTypes()
    {
        var requests = RequestAuthorizationRegistry.FindRequestTypes(ApplicationAssembly);

        requests.Should().HaveCount(ExpectedRequestTypeCount,
            "the Mediator source-generated registry contained this many request types when the count was last confirmed");
    }

    [Test]
    public void EveryRequestTypeInTheApplicationIsMarkedForAuthorization()
    {
        var requests = RequestAuthorizationRegistry.FindRequestTypes(ApplicationAssembly);

        var unmarked = RequestAuthorizationRegistry.FindUnmarkedRequestTypes(requests);

        unmarked.Should().BeEmpty(
            "an unmarked request is denied at dispatch time, so shipping one is a broken feature");
    }

    [Test]
    public void TheAssertionPassesForTheApplicationAssembly()
    {
        var act = () => RequestAuthorizationRegistry.AssertAllRequestsAreMarked(ApplicationAssembly);

        act.Should().NotThrow();
    }

    [Test]
    public void TheAssertionFailsNamingEveryUnmarkedOffender()
    {
        // The assertion's value is that it says which types are wrong, not merely that something is.
        var types = new[] { typeof(MarkedProbe), typeof(UnmarkedProbe), typeof(AlsoUnmarkedProbe) };

        var unmarked = RequestAuthorizationRegistry.FindUnmarkedRequestTypes(types);

        unmarked.Should().BeEquivalentTo(new[] { typeof(UnmarkedProbe), typeof(AlsoUnmarkedProbe) });
        unmarked.Should().NotContain(typeof(MarkedProbe));
    }

    [Test]
    public void TheAssertionRejectsAnAssemblyWithNoRequestTypes()
    {
        // A registry that matches nothing would "pass" forever while checking nothing - the failure
        // mode if a Mediator upgrade renames or moves the request interfaces.
        var act = () => RequestAuthorizationRegistry.AssertAllRequestsAreMarked(typeof(string).Assembly);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*found no Mediator request types*");
    }

    [Test]
    public void FindRequestTypes_IgnoresAbstractTypesAndNotifications()
    {
        var found = RequestAuthorizationRegistry.FindRequestTypes(typeof(RequestAuthorizationRegistryTests).Assembly);

        found.Should().Contain(typeof(MarkedProbe));
        found.Should().NotContain(typeof(AbstractProbe), "abstract types are never dispatched");
        found.Should().NotContain(typeof(NotificationProbe), "notifications do not go through the request pipeline");
    }

    // ---- probes ----------------------------------------------------------------------------------

    [RequestAuthorize(Policy = "Permissions.Documents.View")]
    public sealed record MarkedProbe : IRequest<string>;

    public sealed record UnmarkedProbe : IRequest<string>;

    public sealed record AlsoUnmarkedProbe : IRequest<string>;

    public abstract record AbstractProbe : IRequest<string>;

    public sealed record NotificationProbe : INotification;
}
#nullable restore
