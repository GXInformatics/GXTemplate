#nullable enable
using System;
using System.Security.Claims;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Domain.Identity;
using CleanArchitecture.Blazor.Infrastructure.Services.Identity;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using ZiggyCreatures.Caching.Fusion;

namespace CleanArchitecture.Blazor.Application.UnitTests.Identity;

/// <summary>
/// Regression tests for the null-caching behaviour of <see cref="UserContextLoader"/>. Before the fix
/// a bare <c>catch (Exception)</c> returned null from the cache factory and FusionCache stored that
/// null for a full hour, so one transient failure blanked a user's context until the entry expired.
/// The genuine "no such user" branch cached identically.
/// </summary>
[TestFixture]
public class UserContextLoaderTests
{
    private static ClaimsPrincipal Principal(string userId = "user-1") =>
        new(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, userId) },
            authenticationType: "TestAuth"));

    private static UserContextLoader CreateLoader(IServiceScopeFactory scopeFactory) =>
        new(scopeFactory, new FusionCache(new FusionCacheOptions()), NullLogger<UserContextLoader>.Instance);

    /// <summary>
    /// A scope factory whose scopes resolve from <paramref name="providerFactory"/>, counting how many
    /// times a scope was requested - i.e. how many times the cache factory actually ran.
    /// </summary>
    private sealed class CountingScopeFactory : IServiceScopeFactory
    {
        private readonly Func<IServiceProvider> _providerFactory;
        public int ScopesCreated { get; private set; }

        public CountingScopeFactory(Func<IServiceProvider> providerFactory) => _providerFactory = providerFactory;

        public IServiceScope CreateScope()
        {
            ScopesCreated++;
            return new Scope(_providerFactory());
        }

        private sealed class Scope : IServiceScope
        {
            public Scope(IServiceProvider provider) => ServiceProvider = provider;
            public IServiceProvider ServiceProvider { get; }
            public void Dispose() { }
        }
    }

    /// <summary>A provider that throws on every resolution, standing in for a transient failure.</summary>
    private sealed class ThrowingProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => throw new InvalidOperationException("transient failure");
    }

    [Test]
    public async Task LoadAsync_WhenTheFactoryThrows_DoesNotCacheAndRetriesOnTheNextCall()
    {
        var scopeFactory = new CountingScopeFactory(() => new ThrowingProvider());
        var loader = CreateLoader(scopeFactory);

        var first = async () => await loader.LoadAsync(Principal());
        var second = async () => await loader.LoadAsync(Principal());

        await first.Should().ThrowAsync<Exception>();
        await second.Should().ThrowAsync<Exception>();

        // Two calls, two factory executions: the failure was never written to the cache.
        scopeFactory.ScopesCreated.Should().Be(2);
    }

    [Test]
    public async Task LoadAsync_WhenTheUserDoesNotExist_CachesTheNullForTheShortDuration()
    {
        var userManager = MockUserManager(returnsUser: null);
        var services = new ServiceCollection();
        services.AddSingleton(userManager.Object);
        await using var provider = services.BuildServiceProvider();

        var scopeFactory = new CountingScopeFactory(() => provider);
        var loader = CreateLoader(scopeFactory);

        (await loader.LoadAsync(Principal())).Should().BeNull();
        (await loader.LoadAsync(Principal())).Should().BeNull();

        // A genuine not-found IS cached: the second call within the window did not re-run the factory.
        scopeFactory.ScopesCreated.Should().Be(1);
    }

    [Test]
    public void NotFoundIsCachedFarMoreBrieflyThanASuccessfulLoad()
    {
        // The not-found branch overrides the entry duration through FusionCache adaptive caching
        // (ctx.Options.Duration) instead of inheriting the one-hour context duration.
        UserContextLoader.NotFoundCacheDuration.Should().BeLessThan(UserContextLoader.ContextCacheDuration);
        UserContextLoader.NotFoundCacheDuration.Should().BeLessThanOrEqualTo(TimeSpan.FromMinutes(5));
        UserContextLoader.ContextCacheDuration.Should().Be(TimeSpan.FromHours(1));
    }

    [Test]
    public async Task LoadAsync_ForAnUnauthenticatedPrincipal_ReturnsNullWithoutTouchingTheFactory()
    {
        var scopeFactory = new CountingScopeFactory(() => new ThrowingProvider());
        var loader = CreateLoader(scopeFactory);

        (await loader.LoadAsync(new ClaimsPrincipal(new ClaimsIdentity()))).Should().BeNull();
        scopeFactory.ScopesCreated.Should().Be(0);
    }

    private static Mock<UserManager<ApplicationUser>> MockUserManager(ApplicationUser? returnsUser)
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        var manager = new Mock<UserManager<ApplicationUser>>(
            store.Object, null!, null!, null!, null!, null!, null!, null!, null!);
        manager.Setup(x => x.GetUserAsync(It.IsAny<ClaimsPrincipal>())).ReturnsAsync(returnsUser);
        return manager;
    }
}
#nullable restore
