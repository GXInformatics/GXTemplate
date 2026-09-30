#nullable enable
using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Application.Common.PublishStrategies;
using CleanArchitecture.Blazor.Domain.Entities;
using CleanArchitecture.Blazor.Domain.Identity;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using CleanArchitecture.Blazor.Infrastructure.Persistence.Interceptors;
using CleanArchitecture.Blazor.Infrastructure.Services.Identity;
using FluentAssertions;
using Mediator;
using Npgsql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.Common.PublishStrategies;

/// <summary>
/// A notification handler observes the ambient state of the publisher that RAISED it, not of the
/// scope that happened to construct the publisher.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect these pin.</b> <c>ChannelBasedNoWaitPublisher</c> starts its consumer with
/// <c>Task.Run</c> in its constructor, which captures the <c>ExecutionContext</c> - and every
/// <c>AsyncLocal</c> with it - at that moment. Every handler then ran under the ambient state of
/// whichever scope first resolved the publisher. Measured in Pass 34 and again at Pass 39's HEAD:
/// constructed under tenant A and published under tenant B, the handler saw <b>tenant A</b>.
/// </para>
/// <para>
/// <b>Behaviour, not structure.</b> A test asserting that the <c>Task.Run</c> moved would pin the
/// implementation; these publish under one ambient state from a publisher built under another and
/// assert what the handler OBSERVES, so any mechanism that gets it right passes and any that gets it
/// wrong fails.
/// </para>
/// <para>
/// <b>Three kinds of ambient state, because three were affected.</b> The tenant and user
/// (<c>DocumentCreatedEventHandler</c> logs the user name; <c>PicklistSetChangedEventHandler</c>
/// composes a PerTenant cache key from the tenant), the UI culture (the three mail handlers localise
/// through <c>IStringLocalizer</c>), and - in <see cref="TheLandmine"/> - what the audit interceptor
/// stamps onto a row a handler writes.
/// </para>
/// </remarks>
[TestFixture]
public class PublisherAmbientContextTests
{
    private sealed record Ping : INotification;

    private static NotificationHandlers<Ping> One(INotificationHandler<Ping> handler) =>
        new(new[] { handler }, false);

    private static ChannelBasedNoWaitPublisher NewPublisher() =>
        new(NullLogger<ChannelBasedNoWaitPublisher>.Instance);

    /// <summary>Records whatever ambient value the test asks it for, then completes.</summary>
    private sealed class Observer<T> : INotificationHandler<Ping>
    {
        private readonly Func<T> _read;
        public TaskCompletionSource<T> Seen { get; } = new();
        public Observer(Func<T> read) => _read = read;

        public ValueTask Handle(Ping notification, CancellationToken ct)
        {
            Seen.SetResult(_read());
            return ValueTask.CompletedTask;
        }
    }

    private static async Task<T> ObservedAsync<T>(TaskCompletionSource<T> seen) =>
        await seen.Task.WaitAsync(TimeSpan.FromSeconds(10));

    // ---- the tenant ----------------------------------------------------------------------------

    [Test]
    public async Task AHandlerObservesThePublishingTenant_NotTheConstructingOne()
    {
        // THE regression. Before Pass 39 this observed "tenant-A".
        var accessor = new UserContextAccessor();
        var observer = new Observer<string?>(() => accessor.Current?.TenantId);

        ChannelBasedNoWaitPublisher publisher;
        using (accessor.Push(new UserContext("u-a", "u-a", TenantId: "tenant-A")))
        {
            publisher = NewPublisher();
        }

        await using (publisher)
        {
            using (accessor.Push(new UserContext("u-b", "u-b", TenantId: "tenant-B")))
            {
                await publisher.Publish(One(observer), new Ping(), CancellationToken.None);
            }

            (await ObservedAsync(observer.Seen)).Should().Be("tenant-B");
        }
    }

    [Test]
    public async Task AHandlerObservesThePublishingUser()
    {
        // The tenant is not the only thing read: DocumentCreatedEventHandler logs the USER NAME
        // straight from the ambient accessor, so the freeze put the wrong person in the message.
        var accessor = new UserContextAccessor();
        var observer = new Observer<string?>(() => accessor.Current?.UserName);

        ChannelBasedNoWaitPublisher publisher;
        using (accessor.Push(new UserContext("u-a", "alice", TenantId: "tenant-A")))
        {
            publisher = NewPublisher();
        }

        await using (publisher)
        {
            using (accessor.Push(new UserContext("u-b", "bob", TenantId: "tenant-B")))
            {
                await publisher.Publish(One(observer), new Ping(), CancellationToken.None);
            }

            (await ObservedAsync(observer.Seen)).Should().Be("bob");
        }
    }

    [Test]
    public async Task AConstructingTenantDoesNotLeakIntoAnUnauthenticatedPublish()
    {
        // The case Pass 34 did not measure and Pass 39 found: a publisher built inside a circuit and
        // then published from a background or HTTP path with no principal handed the handler the
        // CIRCUIT's tenant. That is the direction that would stamp a business write with a tenant
        // having nothing to do with it - see TheLandmine.
        var accessor = new UserContextAccessor();
        var observer = new Observer<string?>(() => accessor.Current?.TenantId);

        ChannelBasedNoWaitPublisher publisher;
        using (accessor.Push(new UserContext("u-a", "u-a", TenantId: "tenant-A")))
        {
            publisher = NewPublisher();
        }

        await using (publisher)
        {
            // No push here: the publish site has no ambient principal.
            await publisher.Publish(One(observer), new Ping(), CancellationToken.None);

            (await ObservedAsync(observer.Seen)).Should().BeNull(
                "a publish with no principal must not inherit one from whoever built the publisher");
        }
    }

    // ---- the culture ---------------------------------------------------------------------------

    [Test]
    public async Task AHandlerObservesThePublishingCulture()
    {
        // The three mail handlers localise through IStringLocalizer, which reads
        // CultureInfo.CurrentUICulture - also AsyncLocal-backed, and also frozen before Pass 39. A
        // welcome email was rendered in the language ambient when the publisher was constructed.
        var original = CultureInfo.CurrentUICulture;
        try
        {
            var observer = new Observer<string>(() => CultureInfo.CurrentUICulture.Name);

            CultureInfo.CurrentUICulture = new CultureInfo("de-DE");
            var publisher = NewPublisher();

            await using (publisher)
            {
                CultureInfo.CurrentUICulture = new CultureInfo("fr-FR");
                await publisher.Publish(One(observer), new Ping(), CancellationToken.None);

                (await ObservedAsync(observer.Seen)).Should().Be("fr-FR");
            }
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    // ---- the landmine --------------------------------------------------------------------------

    /// <summary>
    /// A handler that WRITES through <c>ApplicationDbContext</c>, and the row it writes is stamped
    /// with the publishing scope's tenant.
    /// </summary>
    /// <remarks>
    /// <b>This is the case that does not exist today and would be silently wrong tomorrow.</b> None
    /// of the six shipped notification handlers writes to the business database, which is why the
    /// frozen context was confined to log labelling - a property of the current handler set, not of
    /// the design. The moment one does, <c>AuditableEntityInterceptor</c> stamps from the ambient
    /// context (Pass 24) and Pass 29's global filter then routes the row to whichever tenant that
    /// context named. A comment addressed to a future pass has no failure mode (Pass 32 A1); this
    /// test does.
    /// </remarks>
    [Test]
    public async Task TheLandmine_AHandlerWritingToTheDatabaseStampsThePublishingTenant()
    {
        await using var connection = UnitTestDatabase.NewConnection();
        await connection.OpenAsync();

        var accessor = new UserContextAccessor();
        var dateTime = new Mock<IDateTime>();
        dateTime.SetupGet(x => x.UtcNow).Returns(new DateTime(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc));

        ApplicationDbContext Build() => new(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseNpgsql(connection)
                .AddInterceptors(new AuditableEntityInterceptor(accessor, dateTime.Object))
                .Options,
            accessor);

        await using (var seed = Build())
        {
            await UnitTestDatabase.ResetAsync();
            // Pass 32 A5: the audit row has a real foreign key to AspNetUsers, so a successful write
            // needs its author to exist.
            seed.Users.Add(new ApplicationUser { Id = "u-b", UserName = "u-b", Email = "b@example.test" });
            await seed.SaveChangesAsync();
        }

        var writer = new WritingHandler(Build);

        ChannelBasedNoWaitPublisher publisher;
        using (accessor.Push(new UserContext("u-a", "u-a", TenantId: "tenant-A")))
        {
            publisher = NewPublisher();
        }

        await using (publisher)
        {
            using (accessor.Push(new UserContext("u-b", "u-b", TenantId: "tenant-B")))
            {
                await publisher.Publish(One(writer), new Ping(), CancellationToken.None);
            }

            await ObservedAsync(writer.Written);
        }

        await using var read = Build();
        var row = await read.PicklistSets.IgnoreQueryFilters().AsNoTracking()
            .FirstAsync(p => p.Value == "written-by-handler");

        row.TenantId.Should().Be("tenant-B",
            "the interceptor stamps from the ambient context, so a frozen context would have filed " +
            "this row under tenant-A - visible to the wrong tenant and invisible to the right one");
        row.CreatedById.Should().Be("u-b");
    }

    private sealed class WritingHandler : INotificationHandler<Ping>
    {
        private readonly Func<ApplicationDbContext> _build;
        public TaskCompletionSource<bool> Written { get; } = new();
        public WritingHandler(Func<ApplicationDbContext> build) => _build = build;

        public async ValueTask Handle(Ping notification, CancellationToken ct)
        {
            try
            {
                await using var db = _build();
                db.PicklistSets.Add(new PicklistSet
                {
                    Name = Picklist.Brand,
                    Value = "written-by-handler",
                    Text = "written-by-handler"
                });
                await db.SaveChangesAsync(ct);
                Written.SetResult(true);
            }
            catch (Exception ex)
            {
                Written.SetException(ex);
            }
        }
    }
}
