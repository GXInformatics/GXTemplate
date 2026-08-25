#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Caching;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Application.Pipeline;
using FluentAssertions;
using Mediator;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.Pipeline;

/// <summary>
/// Cache scoping. Before this existed, <c>ICacheableRequest.CacheKey</c> defaulted to
/// <c>string.Empty</c> - a request that forgot to declare one cached itself, and every other
/// forgetful request, under the single key "" - and any per-principal separation was hand-written
/// into each query's <c>ToString()</c>, which is how two principals came to share a document's bytes
/// (Pass 2) and a system-log window (Pass 3).
/// </summary>
[TestFixture]
public class CacheScopeBehaviourTests
{
    private const string DeclaredKey = "the-declared-key";

    private static UserContext User(string userId, string? tenantId = "tenant-1") =>
        new(UserId: userId, UserName: userId, TenantId: tenantId);

    // ---- key composition -------------------------------------------------------------------------

    [Test]
    public void GlobalScope_LeavesTheDeclaredKeyAlone()
    {
        CacheScopeKey.Compose(DeclaredKey, CacheScope.Global, User("a"))
            .Should().Be(DeclaredKey);
    }

    [Test]
    public void GlobalScope_IsTheSameEntryForEveryPrincipal()
    {
        var forA = CacheScopeKey.Compose(DeclaredKey, CacheScope.Global, User("a", "t1"));
        var forB = CacheScopeKey.Compose(DeclaredKey, CacheScope.Global, User("b", "t2"));

        forA.Should().Be(forB, "Global declares that the response does not vary by principal");
    }

    [Test]
    public void PerUser_SeparatesUsersAndOnlyUsers()
    {
        var a1 = CacheScopeKey.Compose(DeclaredKey, CacheScope.PerUser, User("a", "t1"));
        var b1 = CacheScopeKey.Compose(DeclaredKey, CacheScope.PerUser, User("b", "t1"));
        var a2 = CacheScopeKey.Compose(DeclaredKey, CacheScope.PerUser, User("a", "t2"));

        a1.Should().NotBe(b1, "different users must not share");
        a1.Should().Be(a2, "PerUser deliberately ignores the tenant");
    }

    [Test]
    public void PerTenant_SeparatesTenantsAndOnlyTenants()
    {
        var a1 = CacheScopeKey.Compose(DeclaredKey, CacheScope.PerTenant, User("a", "t1"));
        var b1 = CacheScopeKey.Compose(DeclaredKey, CacheScope.PerTenant, User("b", "t1"));
        var a2 = CacheScopeKey.Compose(DeclaredKey, CacheScope.PerTenant, User("a", "t2"));

        a1.Should().Be(b1, "two users in one tenant share tenant-scoped data");
        a1.Should().NotBe(a2, "different tenants must not share");
    }

    [Test]
    public void PerUserAndTenant_SeparatesBoth()
    {
        var a1 = CacheScopeKey.Compose(DeclaredKey, CacheScope.PerUserAndTenant, User("a", "t1"));
        var b1 = CacheScopeKey.Compose(DeclaredKey, CacheScope.PerUserAndTenant, User("b", "t1"));
        var a2 = CacheScopeKey.Compose(DeclaredKey, CacheScope.PerUserAndTenant, User("a", "t2"));

        a1.Should().NotBe(b1);
        a1.Should().NotBe(a2);
    }

    [Test]
    public void CallersWithNoTenantShareTheNoTenantPartition()
    {
        var a = CacheScopeKey.Compose(DeclaredKey, CacheScope.PerTenant, User("a", tenantId: null));
        var b = CacheScopeKey.Compose(DeclaredKey, CacheScope.PerTenant, User("b", tenantId: null));

        a.Should().Be(b, "which is exactly what the query itself would see for them");
    }

    [Test]
    public void OnlyGlobalSurvivesWithoutAnAmbientContext()
    {
        CacheScopeKey.RequiresUserContext(CacheScope.Global).Should().BeFalse();
        CacheScopeKey.RequiresUserContext(CacheScope.PerUser).Should().BeTrue();
        CacheScopeKey.RequiresUserContext(CacheScope.PerTenant).Should().BeTrue();
        CacheScopeKey.RequiresUserContext(CacheScope.PerUserAndTenant).Should().BeTrue();
    }

    [Test]
    public void ComposingAScopedKeyWithNoContextIsARefusal_NotAnUnscopedFallback()
    {
        // Falling back to the bare declared key here is precisely the cross-principal leak the scopes
        // exist to prevent, so the composer refuses rather than guessing.
        var act = () => CacheScopeKey.Compose(DeclaredKey, CacheScope.PerUser, null);

        act.Should().Throw<InvalidOperationException>().WithMessage("*ambient user context*");
    }

    // ---- the behaviour ---------------------------------------------------------------------------

    [Test]
    public async Task TwoPrincipalsShareAGlobalEntry()
    {
        var cache = new RecordingCache();
        var handlerRuns = 0;

        await Run<GlobalRequest>(cache, User("a"), () => { handlerRuns++; return "value"; });
        await Run<GlobalRequest>(cache, User("b"), () => { handlerRuns++; return "value"; });

        cache.Keys.Distinct().Should().HaveCount(1);
        handlerRuns.Should().Be(1, "the second caller was served the first caller's entry");
    }

    [Test]
    public async Task TwoPrincipalsDoNotShareAPerUserEntry()
    {
        var cache = new RecordingCache();
        var handlerRuns = 0;

        await Run<PerUserRequest>(cache, User("a"), () => { handlerRuns++; return "value"; });
        await Run<PerUserRequest>(cache, User("b"), () => { handlerRuns++; return "value"; });

        cache.Keys.Distinct().Should().HaveCount(2);
        handlerRuns.Should().Be(2, "each principal got their own entry");
    }

    [Test]
    public async Task NoAmbientContextOnAScopedRequest_BypassesTheCacheEntirely()
    {
        var cache = new RecordingCache();
        var handlerRuns = 0;

        var result = await Run<PerUserRequest>(cache, user: null, () => { handlerRuns++; return "value"; });

        result.Should().Be("value", "the handler still runs - the request is not refused");
        handlerRuns.Should().Be(1);
        cache.Keys.Should().BeEmpty("nothing was read");
        cache.Writes.Should().Be(0, "and nothing was written");
    }

    [Test]
    public async Task NoAmbientContextOnAGlobalRequest_StillCaches()
    {
        var cache = new RecordingCache();

        await Run<GlobalRequest>(cache, user: null, () => "value");

        cache.Keys.Should().ContainSingle().Which.Should().Be(DeclaredKey);
    }

    [Test]
    public async Task PipelineEntriesOptOutOfFailSafe()
    {
        // A duration is a staleness bound. Serving an expired entry because the refresh failed breaks
        // it silently, so the pipeline asks for fail-safe off on every entry it writes.
        var cache = new RecordingCache();

        await Run<GlobalRequest>(cache, User("a"), () => "value");

        cache.LastOptions.Should().NotBeNull();
        cache.LastOptions!.AllowStaleOnFailure.Should().BeFalse();
    }

    [Test]
    public async Task TheComposedKeyIsWhatReachesTheCache()
    {
        var cache = new RecordingCache();

        await Run<PerUserAndTenantRequest>(cache, User("alice", "acme"), () => "value");

        cache.Keys.Single().Should().Contain("alice").And.Contain("acme").And.Contain(DeclaredKey);
    }

    // ---- harness ---------------------------------------------------------------------------------

    private static async Task<string> Run<TRequest>(RecordingCache cache, UserContext? user, Func<string> handler)
        where TRequest : class, ICacheableRequest<string>, new()
    {
        var accessor = new StubAccessor(user);
        var behaviour = new FusionCacheBehaviour<TRequest, string>(
            cache, accessor, NullLogger<FusionCacheBehaviour<TRequest, string>>.Instance);

        return await behaviour.Handle(
            new TRequest(),
            (_, _) => ValueTask.FromResult(handler()),
            CancellationToken.None);
    }

    public sealed class GlobalRequest : ICacheableRequest<string>
    {
        public string CacheKey => DeclaredKey;
        public CacheScope Scope => CacheScope.Global;
        public IEnumerable<string>? Tags => new[] { "probe" };
    }

    public sealed class PerUserRequest : ICacheableRequest<string>
    {
        public string CacheKey => DeclaredKey;
        public CacheScope Scope => CacheScope.PerUser;
        public IEnumerable<string>? Tags => new[] { "probe" };
    }

    public sealed class PerUserAndTenantRequest : ICacheableRequest<string>
    {
        public string CacheKey => DeclaredKey;
        public CacheScope Scope => CacheScope.PerUserAndTenant;
        public IEnumerable<string>? Tags => new[] { "probe" };
    }

    private sealed class StubAccessor : IUserContextAccessor
    {
        public StubAccessor(UserContext? current) => Current = current;
        public UserContext? Current { get; }
        public IDisposable Push(UserContext context) => throw new NotSupportedException();
        public void Clear() { }
    }

    /// <summary>An in-memory stand-in that records exactly which keys and options the pipeline used.</summary>
    private sealed class RecordingCache : IAppCache
    {
        private readonly Dictionary<string, object?> _entries = new(StringComparer.Ordinal);

        public List<string> Keys { get; } = new();
        public int Writes { get; private set; }
        public CacheEntryOptions? LastOptions { get; private set; }

        public async Task<T> GetOrSetAsync<T>(
            string key,
            Func<CancellationToken, Task<T>> factory,
            IEnumerable<string>? tags = null,
            CacheEntryOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Keys.Add(key);
            LastOptions = options;

            if (_entries.TryGetValue(key, out var hit)) return (T)hit!;

            var value = await factory(cancellationToken);
            _entries[key] = value;
            Writes++;
            return value;
        }

        public void Remove(string key) => _entries.Remove(key);
        public Task RemoveByTagAsync(string tag, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveByTagsAsync(IEnumerable<string>? tags, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
#nullable restore
