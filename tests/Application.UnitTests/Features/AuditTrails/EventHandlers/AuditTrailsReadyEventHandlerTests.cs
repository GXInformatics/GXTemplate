#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Application.Common.PublishStrategies;
using CleanArchitecture.Blazor.Application.Features.AuditTrails.EventHandlers;
using CleanArchitecture.Blazor.Domain.Entities;
using CleanArchitecture.Blazor.Domain.Events;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using FluentAssertions;
using Mediator;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.Features.AuditTrails.EventHandlers;

/// <summary>
/// Regression tests for audit-trail durability. Audit rows travel two hops: the interceptor publishes
/// an <see cref="AuditTrailsReadyEvent"/>, <see cref="ChannelBasedNoWaitPublisher"/> queues the handler
/// on a bounded channel and drains it on a background task, and its DisposeAsync awaits that drain so
/// shutdown does not drop queued work. Before the fix the handler wrapped its database write in a bare
/// <c>Task.Run</c> and returned as soon as that work was <em>scheduled</em>, so the drain guarantee was
/// hollow: the channel could complete with nothing written.
/// </summary>
[TestFixture]
public class AuditTrailsReadyEventHandlerTests
{
    private SqliteConnection _connection = null!;
    private DbContextOptions<ApplicationDbContext> _options = null!;

    [SetUp]
    public async Task SetUp()
    {
        // A shared in-memory SQLite connection: every context built from _options sees the same
        // database, so "did the row land" is asked of a context the handler never touched.
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();

        _options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .Options;

        await using var db = new ApplicationDbContext(_options);
        await db.Database.EnsureCreatedAsync();
    }

    [TearDown]
    public async Task TearDown() => await _connection.DisposeAsync();

    private async Task<int> CountAuditRowsAsync()
    {
        await using var db = new ApplicationDbContext(_options);
        return await db.AuditTrails.CountAsync();
    }

    private static AuditTrailsReadyEvent Event(int count) =>
        new(Enumerable.Range(0, count)
            .Select(i => new AuditTrail
            {
                AuditType = AuditType.Create,
                TableName = "Products",
                DateTime = DateTime.UtcNow,
                UserId = null,
                PrimaryKey = new Dictionary<string, string> { ["Id"] = i.ToString() }
            })
            .ToList());

    private AuditTrailsReadyEventHandler CreateHandler() =>
        new(new TestDbContextFactory(_options), NullLogger<AuditTrailsReadyEventHandler>.Instance);

    /// <summary>
    /// The core guarantee: when the handler's task completes, the rows are already in the database.
    /// Pre-fix this failed - Handle returned once Task.Run had been scheduled, so the count was 0.
    /// </summary>
    [Test]
    public async Task Handle_PersistsTheRows_BeforeItCompletes()
    {
        await CreateHandler().Handle(Event(3), CancellationToken.None);

        (await CountAuditRowsAsync()).Should().Be(3,
            "the handler must not report completion until the rows it was given are persisted");
    }

    /// <summary>
    /// The shutdown case. Publishing through the real publisher and then disposing it is exactly what
    /// happens when the host stops: DisposeAsync completes the channel and awaits the drain. That is
    /// only a durability guarantee if the handler awaits its own work.
    /// </summary>
    [Test]
    public async Task DisposingThePublisherAfterPublishing_LeavesTheRowsPersisted()
    {
        var publisher = new ChannelBasedNoWaitPublisher(NullLogger<ChannelBasedNoWaitPublisher>.Instance);
        INotificationHandler<AuditTrailsReadyEvent>[] handlerArray = [CreateHandler()];
        var handlers = new NotificationHandlers<AuditTrailsReadyEvent>(handlerArray, isArray: true);

        await publisher.Publish(handlers, Event(5), CancellationToken.None);

        // Shutdown: complete the channel and await the drain. Nothing else waits for the write.
        await publisher.DisposeAsync();

        (await CountAuditRowsAsync()).Should().Be(5,
            "draining the publisher's channel must mean the audit rows are written, not merely scheduled");
    }

    [Test]
    public async Task Handle_WithNoAuditTrails_WritesNothing()
    {
        await CreateHandler().Handle(new AuditTrailsReadyEvent(Array.Empty<AuditTrail>()), CancellationToken.None);

        (await CountAuditRowsAsync()).Should().Be(0);
    }

    /// <summary>
    /// The handler still swallows persistence failures (they are logged, not propagated) so that an
    /// audit write can never fail the user's operation. Disposing the connection makes every write fail.
    /// </summary>
    [Test]
    public async Task Handle_WhenPersistenceFails_DoesNotPropagate()
    {
        await _connection.DisposeAsync();

        var act = async () => await CreateHandler().Handle(Event(1), CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    private sealed class TestDbContextFactory : IApplicationDbContextFactory
    {
        private readonly DbContextOptions<ApplicationDbContext> _options;
        public TestDbContextFactory(DbContextOptions<ApplicationDbContext> options) => _options = options;

        public ValueTask<IApplicationDbContext> CreateAsync(CancellationToken ct = default) =>
            new(new ApplicationDbContext(_options));
    }
}
#nullable restore
