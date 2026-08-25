using System.Threading.Channels;

namespace CleanArchitecture.Blazor.Application.Common.PublishStrategies;

/// <summary>
/// High-performance publisher using Channel with backpressure control
/// <para>
/// It implements <see cref="IAsyncDisposable"/> so the DI container actually disposes it. The
/// class already had a DisposeAsync that completes the channel and awaits the drain, but without
/// the interface the container never called it: it checks the resolved instance for IDisposable /
/// IAsyncDisposable at runtime. The channel was therefore never completed, the drain never ran, and
/// the background reader stayed pending for the life of the process - one per scope.
/// </para>
/// </summary>
public class ChannelBasedNoWaitPublisher : INotificationPublisher, IAsyncDisposable, IDisposable
{
    private readonly ILogger<ChannelBasedNoWaitPublisher> _logger;
    private readonly Channel<(Func<CancellationToken, ValueTask> Callback, string NotificationType)> _channel;
    private readonly ChannelWriter<(Func<CancellationToken, ValueTask> Callback, string NotificationType)> _writer;
    private readonly Task _processingTask;
    private int _disposeState;

    public ChannelBasedNoWaitPublisher(ILogger<ChannelBasedNoWaitPublisher> logger, int capacity = 1000)
    {
        _logger = logger;
        
        var options = new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        };

        _channel = Channel.CreateBounded<(Func<CancellationToken, ValueTask>, string)>(options);
        _writer = _channel.Writer;

        // Start background processing task
        _processingTask = Task.Run(ProcessNotifications);
    }

    public async ValueTask Publish<TNotification>(NotificationHandlers<TNotification> handlers, TNotification notification,
        CancellationToken cancellationToken)
        where TNotification : INotification
    {
        var handlerList = handlers.ToList();
        
        if (!handlerList.Any())
            return;

        // Add all handlers to channel for async processing
        foreach (var handler in handlerList)
        {
            try
            {
                await _writer.WriteAsync(
                    (token => handler.Handle(notification, token), notification.GetType().Name),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to queue handler for {NotificationType}", notification.GetType().Name);
            }
        }
    }

    private async Task ProcessNotifications()
    {
        await foreach (var (callback, notificationType) in _channel.Reader.ReadAllAsync())
        {
            try
            {
                await callback(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Handler execution failed for {NotificationType}: {ErrorMessage}", 
                    notificationType, ex.Message);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!BeginDispose()) return;

        try
        {
            await _processingTask.ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            // The reader observed a normal channel shutdown while draining.
        }
    }

    /// <summary>
    /// Synchronous disposal, present because a service that implements <i>only</i>
    /// <see cref="IAsyncDisposable"/> makes <c>IServiceScope.Dispose()</c> throw
    /// ("type only implements IAsyncDisposable"), and the application disposes some scopes
    /// synchronously. It drains on the same terms as <see cref="DisposeAsync"/>.
    /// </summary>
    public void Dispose()
    {
        if (!BeginDispose()) return;

        try
        {
            _processingTask.GetAwaiter().GetResult();
        }
        catch (ChannelClosedException)
        {
            // The reader observed a normal channel shutdown while draining.
        }
    }

    /// <summary>Completes the channel exactly once. Returns false if disposal already happened.</summary>
    private bool BeginDispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return false;
        }

        _writer.TryComplete();
        return true;
    }
}
