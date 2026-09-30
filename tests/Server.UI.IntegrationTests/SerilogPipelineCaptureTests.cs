#nullable enable
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Serilog.Core;
using Serilog.Events;

namespace CleanArchitecture.Blazor.Server.UI.IntegrationTests;

/// <summary>
/// A sink registered in DI sees the events the real Serilog pipeline carries (pass 47, CO-161).
/// </summary>
/// <remarks>
/// <c>RegisterSerilog</c> reads sinks from the container (<c>ReadFrom.Services</c>), and the factory's
/// <c>configureServices</c> hook registers them after the application's own services. Together they
/// let a test observe every event a real boot emits - the bootstrap banner, a startup refusal - on
/// the pipeline the application actually configures, rather than on a copy of it.
/// </remarks>
[TestFixture]
public class SerilogPipelineCaptureTests
{
    private sealed class CapturingSink : ILogEventSink
    {
        public ConcurrentQueue<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Enqueue(logEvent);
    }

    [Test]
    public async Task ASinkRegisteredThroughTheFactory_SeesTheRealPipelinesEvents()
    {
        var sink = new CapturingSink();
        using var factory = new GxWebApplicationFactory(
            // Information, not the harness's quiet Warning: the boot's own startup lines are Information.
            extraConfiguration: new() { ["Serilog:MinimumLevel:Default"] = "Information" },
            configureServices: services => services.AddSingleton<ILogEventSink>(sink));

        using (var client = factory.CreateNonRedirectingClient())
        {
            await client.GetAsync("/");
        }

        sink.Events.Should().NotBeEmpty("the boot logs through Serilog, and the pipeline reads sinks from the container");
        sink.Events.Should().Contain(e => e.Properties.ContainsKey("TenantId"),
            "the events arrive enriched, as the application's own sinks receive them");
    }
}
#nullable restore
