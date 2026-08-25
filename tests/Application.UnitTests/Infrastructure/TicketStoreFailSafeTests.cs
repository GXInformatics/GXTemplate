#nullable enable
using System;
using System.Security.Claims;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Infrastructure.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using NUnit.Framework;
using ZiggyCreatures.Caching.Fusion;

namespace CleanArchitecture.Blazor.Application.UnitTests.Infrastructure;

/// <summary>
/// Authentication tickets must not outlive their own expiry.
/// <para>
/// The store caches tickets with a Duration equal to the ticket's remaining lifetime and reads them
/// back with <c>GetOrDefaultAsync</c> - a factory-less read. With fail-safe enabled that read can
/// still hand back a logically expired entry, which for a session ticket means an expired session
/// stays usable. Fail-safe is therefore off for this cache; these tests pin both halves of that.
/// </para>
/// </summary>
[TestFixture]
public class TicketStoreFailSafeTests
{
    private static AuthenticationTicket Ticket(DateTimeOffset? expiresUtc)
    {
        var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "someone") }, "TestAuth");
        var properties = new AuthenticationProperties { ExpiresUtc = expiresUtc };
        return new AuthenticationTicket(new ClaimsPrincipal(identity), properties, "TestScheme");
    }

    [Test]
    public async Task AnExpiredTicketIsNotServed()
    {
        var store = new MemoryCacheTicketStore();

        var key = await store.StoreAsync(Ticket(DateTimeOffset.UtcNow.AddMinutes(30)));
        (await store.RetrieveAsync(key)).Should().NotBeNull("the ticket is live");

        // Re-store the same key with a ticket that has already expired. The store's own contract is to
        // remove rather than cache it - the session is over.
        await store.RenewAsync(key, Ticket(DateTimeOffset.UtcNow.AddMinutes(-1)));

        (await store.RetrieveAsync(key)).Should().BeNull("an expired session must not be retrievable");
    }

    [Test]
    public async Task RemovingATicketMakesItUnretrievable()
    {
        var store = new MemoryCacheTicketStore();
        var key = await store.StoreAsync(Ticket(DateTimeOffset.UtcNow.AddMinutes(30)));

        await store.RemoveAsync(key);

        (await store.RetrieveAsync(key)).Should().BeNull();
    }

    [Test]
    public async Task WithFailSafeOn_AnExpiredEntryIsServedOnlyIfStaleReadsAreAllowed()
    {
        // What actually governs a factory-less read is AllowStaleOnReadOnly, which defaults to false.
        // This is why the store was never in fact serving expired tickets: fail-safe alone does not
        // do it. Disabling fail-safe is defence in depth, not a fix for a live hole.
        using var cache = new FusionCache(new FusionCacheOptions
        {
            DefaultEntryOptions = new FusionCacheEntryOptions
            {
                Duration = TimeSpan.FromMinutes(30),
                IsFailSafeEnabled = true,
                FailSafeMaxDuration = TimeSpan.FromMinutes(20)
            }
        });

        await cache.SetAsync("k", "a-session");
        await cache.ExpireAsync("k");

        var defaultRead = await cache.GetOrDefaultAsync<string>("k");
        var staleAllowedRead = await cache.GetOrDefaultAsync<string>(
            "k", options: new FusionCacheEntryOptions { AllowStaleOnReadOnly = true });

        defaultRead.Should().BeNull("a plain read does not surface an expired entry even with fail-safe on");
        staleAllowedRead.Should().Be("a-session", "but an opted-in stale read does");
    }

    [Test]
    public async Task IsFailSafeEnabledDoesNotGateStaleReadsAtAll()
    {
        // Recorded because it is counter-intuitive and it corrects an earlier assumption of mine
        // (Pass 6 anomaly A2): turning fail-safe OFF does not stop an opted-in stale read from
        // finding the expired entry. IsFailSafeEnabled governs the factory-failure fallback inside
        // GetOrSet; AllowStaleOnReadOnly alone governs read-only methods. The ticket store uses
        // neither GetOrSet nor AllowStaleOnReadOnly, so fail-safe was inert there either way.
        using var cache = new FusionCache(new FusionCacheOptions
        {
            DefaultEntryOptions = new FusionCacheEntryOptions
            {
                Duration = TimeSpan.FromMinutes(30),
                IsFailSafeEnabled = false
            }
        });

        await cache.SetAsync("k", "a-session");
        await cache.ExpireAsync("k");

        var plainRead = await cache.GetOrDefaultAsync<string>("k");
        var staleAllowedRead = await cache.GetOrDefaultAsync<string>(
            "k", options: new FusionCacheEntryOptions { AllowStaleOnReadOnly = true });

        plainRead.Should().BeNull("which is the read the ticket store actually performs");
        staleAllowedRead.Should().Be("a-session", "fail-safe off does not evict the retained copy");
    }
}
#nullable restore
