#nullable enable
using CleanArchitecture.Blazor.Application.Common.Interfaces.Caching;
using CleanArchitecture.Blazor.Application.Common.Security;
using CleanArchitecture.Blazor.Application.Features.SystemLogs.Queries.PaginationQuery;
using CleanArchitecture.Blazor.Application.Features.SystemLogs.Specifications;
using FluentAssertions;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.Features.SystemLogs.Queries;

/// <summary>
/// Regression tests for the <see cref="SystemLogsWithPaginationQuery"/> cache key.
/// <see cref="SystemLogAdvancedSpecification"/> derives its TODAY / LAST_30_DAYS window from
/// <c>CurrentUser.LocalTimeOffset</c>, but the key omitted the principal entirely, so two users in
/// different time zones shared one entry and whoever asked second was served the first one's window.
/// The key now carries the offset itself: it is precisely what the result depends on, and system logs
/// are not otherwise principal-scoped, so users in the same zone can still share an entry.
/// </summary>
[TestFixture]
public class SystemLogsWithPaginationQueryCacheKeyTests
{
    private static SystemLogsWithPaginationQuery Query(string? timeZoneId) => new()
    {
        CurrentUser = new UserProfile(
            UserId: timeZoneId is null ? "anonymous" : $"user-in-{timeZoneId}",
            UserName: "u",
            Email: "u@example.com",
            TimeZoneId: timeZoneId),
        ListView = SystemLogListView.TODAY,
        PageNumber = 1,
        PageSize = 15
    };

    [Test]
    public void TwoPrincipalsInDifferentTimeZones_GetDifferentCacheKeys()
    {
        // Tokyo is UTC+9, Honolulu UTC-10: their "today" windows do not even overlap.
        var tokyo = Query("Asia/Tokyo");
        var honolulu = Query("Pacific/Honolulu");

        tokyo.CacheKey.Should().NotBe(honolulu.CacheKey,
            "the date window differs between these principals, so their results must not share an entry");
    }

    [Test]
    public void TwoPrincipalsInTheSameTimeZone_DeliberatelyShareACacheKey()
    {
        // Keying on the offset rather than the user id is the deliberate choice: system logs are a
        // global table, so fragmenting the cache per user would buy nothing.
        var first = Query("Asia/Tokyo");
        var second = Query("Asia/Tokyo");
        second.CurrentUser = second.CurrentUser! with { UserId = "a-different-user" };

        second.CacheKey.Should().Be(first.CacheKey);
    }

    [Test]
    public void TheCacheKeyCarriesTheOffsetTheSpecificationUses()
    {
        var query = Query("Asia/Tokyo");

        query.CacheKey.Should().Contain(query.CurrentUser!.LocalTimeOffset.ToString(),
            "the key must be derived from the same value the specification builds its window from");
    }

    [Test]
    public void TheQueryDeclaresGlobalScope_SoTheOffsetMustStayInTheKey()
    {
        // System logs are not principal-scoped, so no CacheScope separates these entries - and none
        // could supply the offset anyway: UserContext carries UserId and TenantId, not a time zone.
        // That is why this one query still folds its own principal-derived component into the key.
        Query("Asia/Tokyo").Scope.Should().Be(CacheScope.Global);
    }

    [Test]
    public void TheRestOfTheFilterStillDistinguishesEntries()
    {
        var today = Query("Asia/Tokyo");
        var last30 = Query("Asia/Tokyo");
        last30.ListView = SystemLogListView.LAST_30_DAYS;

        last30.CacheKey.Should().NotBe(today.CacheKey);
    }
}
#nullable restore
