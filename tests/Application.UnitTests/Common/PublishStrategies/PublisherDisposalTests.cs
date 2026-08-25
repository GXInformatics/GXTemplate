#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.PublishStrategies;
using FluentAssertions;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.Common.PublishStrategies;

/// <summary>
/// <see cref="ChannelBasedNoWaitPublisher"/> always had a DisposeAsync that completes its channel and
/// awaits the drain, but it did not implement <see cref="IAsyncDisposable"/> - and the DI container
/// decides what to dispose by testing the resolved instance for that interface at runtime. The
/// container therefore never called it: the channel was never completed, the drain never ran, and the
/// background reader stayed pending for the life of the process, one per scope.
/// </summary>
[TestFixture]
public class PublisherDisposalTests
{
    [Test]
    public void ThePublisherIsAsyncDisposable()
    {
        // The declaration is the whole fix; without it nothing below can happen.
        typeof(IAsyncDisposable).IsAssignableFrom(typeof(ChannelBasedNoWaitPublisher))
            .Should().BeTrue("the container only disposes instances that declare the interface");
    }

    [Test]
    public async Task DisposingTheScopeCompletesAndDrainsTheChannel()
    {
        SlowHandler.Reset();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<ChannelBasedNoWaitPublisher>();
        await using var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
        {
            var publisher = scope.ServiceProvider.GetRequiredService<ChannelBasedNoWaitPublisher>();
            INotificationHandler<Ping>[] handlers = [new SlowHandler()];

            await publisher.Publish(
                new NotificationHandlers<Ping>(handlers, isArray: true), new Ping(), CancellationToken.None);

            SlowHandler.Completed.Should().BeFalse("the publisher returns before the handler runs");
        }

        // The scope has gone. If the container had skipped disposal - as it did before this fix - the
        // queued handler would still be sitting in an abandoned channel.
        SlowHandler.Completed.Should().BeTrue(
            "scope disposal must complete the channel and await the drain");
    }

    [Test]
    public async Task DisposingTwiceIsSafe()
    {
        var publisher = new ChannelBasedNoWaitPublisher(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ChannelBasedNoWaitPublisher>.Instance);

        await publisher.DisposeAsync();
        var act = async () => await publisher.DisposeAsync();

        await act.Should().NotThrowAsync("scope disposal and an explicit dispose can both happen");
    }

    public sealed record Ping : INotification;

    public sealed class SlowHandler : INotificationHandler<Ping>
    {
        public static bool Completed;
        public static void Reset() => Completed = false;

        public async ValueTask Handle(Ping notification, CancellationToken cancellationToken)
        {
            await Task.Delay(150, CancellationToken.None);
            Completed = true;
        }
    }
}
#nullable restore
