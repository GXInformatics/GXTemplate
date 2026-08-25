#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Caching;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Application.Pipeline;
using CleanArchitecture.Blazor.Infrastructure.Services.Caching;
using FluentAssertions;
using Mediator;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using ZiggyCreatures.Caching.Fusion;

namespace CleanArchitecture.Blazor.Application.UnitTests.Pipeline;

/// <summary>
/// Invalidation against a real FusionCache, plus the fail-safe posture of pipeline entries.
/// <para>
/// The load-bearing claim here is that invalidation needs no scope of its own: a tag flush reaches
/// every scoped variant of an entry, including other principals'. Scoping the flush would clear only
/// the acting user's copies and leave everyone else reading data the command had just changed.
/// </para>
/// </summary>
[TestFixture]
public class CacheInvalidationScopeTests
{
    private const string Tag = "probe-feature";

    private FusionCache _fusionCache = null!;
    private FusionAppCache _appCache = null!;

    [SetUp]
    public void SetUp()
    {
        _fusionCache = new FusionCache(new FusionCacheOptions
        {
            DefaultEntryOptions = new FusionCacheEntryOptions
            {
                Duration = TimeSpan.FromMinutes(30),
                IsFailSafeEnabled = true,
                FailSafeMaxDuration = TimeSpan.FromHours(3)
            }
        });
        _appCache = new FusionAppCache(_fusionCache);
    }

    [TearDown]
    public void TearDown() => _fusionCache.Dispose();

    private static UserContext User(string userId) =>
        new(UserId: userId, UserName: userId, TenantId: "tenant-1");

    private async Task<string> ReadAs(UserContext user, Func<string> handler)
    {
        var behaviour = new FusionCacheBehaviour<PerUserProbe, string>(
            _appCache, new StubAccessor(user), NullLogger<FusionCacheBehaviour<PerUserProbe, string>>.Instance);
        return await behaviour.Handle(new PerUserProbe(), (_, _) => ValueTask.FromResult(handler()), CancellationToken.None);
    }

    // ---- invalidation reaches every scope --------------------------------------------------------

    [Test]
    public async Task ATagFlushClearsEveryPrincipalsCopy_NotJustTheActingOne()
    {
        (await ReadAs(User("alice"), () => "v1")).Should().Be("v1");
        (await ReadAs(User("bob"), () => "v1")).Should().Be("v1");

        // Both are cached now: the handler is not consulted again.
        (await ReadAs(User("alice"), () => "SHOULD-NOT-RUN")).Should().Be("v1");
        (await ReadAs(User("bob"), () => "SHOULD-NOT-RUN")).Should().Be("v1");

        // Alice runs an invalidating command. It flushes by tag, with no scope of its own.
        var invalidator = new CacheInvalidationBehaviour<TagInvalidator, string>(
            _appCache, NullLogger<CacheInvalidationBehaviour<TagInvalidator, string>>.Instance);
        await invalidator.Handle(new TagInvalidator(), (_, _) => ValueTask.FromResult("done"), CancellationToken.None);

        (await ReadAs(User("alice"), () => "v2")).Should().Be("v2", "the acting user's entry was cleared");
        (await ReadAs(User("bob"), () => "v2")).Should().Be("v2",
            "and so was every other principal's - otherwise bob would still be reading v1");
    }

    [Test]
    public async Task ScopedEntriesReallyAreSeparateInTheCache()
    {
        (await ReadAs(User("alice"), () => "alice-value")).Should().Be("alice-value");
        (await ReadAs(User("bob"), () => "bob-value")).Should().Be("bob-value");

        (await ReadAs(User("alice"), () => "SHOULD-NOT-RUN")).Should().Be("alice-value");
        (await ReadAs(User("bob"), () => "SHOULD-NOT-RUN")).Should().Be("bob-value");
    }

    // ---- fail-safe is off for pipeline entries ---------------------------------------------------

    [Test]
    public async Task AnExpiredPipelineEntryIsNotServedWhenTheFactoryFails()
    {
        // Fail-safe would hand back the expired value rather than surface the error - which is exactly
        // what makes a duration meaningless. The pipeline writes its entries with fail-safe off.
        var behaviour = new FusionCacheBehaviour<ShortLivedProbe, string>(
            _appCache, new StubAccessor(User("alice")),
            NullLogger<FusionCacheBehaviour<ShortLivedProbe, string>>.Instance);

        // Seed an entry, then expire it.
        await behaviour.Handle(new ShortLivedProbe(), (_, _) => ValueTask.FromResult("cached"), CancellationToken.None);
        await _fusionCache.ExpireAsync(
            CacheScopeKey.Compose(new ShortLivedProbe().CacheKey, CacheScope.PerUser, User("alice")));

        var act = async () => await behaviour.Handle(
            new ShortLivedProbe(),
            (_, _) => throw new InvalidOperationException("refresh failed"),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>(
            "with fail-safe off the caller learns the refresh failed instead of silently receiving stale data");
    }

    [Test]
    public async Task TheSameEntryWrittenWithFailSafeOnWouldHaveServedTheStaleValue()
    {
        // The control for the test above: written straight through IFusionCache with the global
        // defaults (fail-safe ON), the very same scenario returns the expired value.
        const string key = "failsafe-control";
        await _fusionCache.GetOrSetAsync<string>(key, _ => Task.FromResult("cached"), token: CancellationToken.None);
        await _fusionCache.ExpireAsync(key);

        var value = await _fusionCache.GetOrSetAsync<string>(
            key, _ => throw new InvalidOperationException("refresh failed"), token: CancellationToken.None);

        value.Should().Be("cached", "this is the behaviour the pipeline deliberately opts out of");
    }

    // ---- probes ----------------------------------------------------------------------------------

    public sealed class PerUserProbe : ICacheableRequest<string>
    {
        public string CacheKey => "probe-key";
        public CacheScope Scope => CacheScope.PerUser;
        public IEnumerable<string>? Tags => new[] { Tag };
    }

    public sealed class ShortLivedProbe : ICacheableRequest<string>
    {
        public string CacheKey => "short-probe-key";
        public CacheScope Scope => CacheScope.PerUser;
        public IEnumerable<string>? Tags => new[] { Tag };
    }

    public sealed class TagInvalidator : ICacheInvalidatorRequest<string>
    {
        public IEnumerable<string>? Tags => new[] { Tag };
    }

    private sealed class StubAccessor : IUserContextAccessor
    {
        public StubAccessor(UserContext? current) => Current = current;
        public UserContext? Current { get; }
        public IDisposable Push(UserContext context) => throw new NotSupportedException();
        public void Clear() { }
    }
}
#nullable restore
