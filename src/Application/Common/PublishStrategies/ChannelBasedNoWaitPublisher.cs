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
    /// <summary>
    /// One queued handler invocation, together with the ambient state of the publisher that queued
    /// it.
    /// </summary>
    /// <remarks>
    /// <b><see cref="Context"/> is the whole of Pass 39.</b> Without it the consumer runs every
    /// handler under the <c>ExecutionContext</c> captured by <c>Task.Run</c> in the CONSTRUCTOR, so a
    /// notification published under tenant B is handled as though tenant A - whichever tenant
    /// happened to be ambient when this publisher was first resolved in its scope - were current.
    /// Null when the publisher captured no context, which is possible but not expected.
    /// </remarks>
    private readonly record struct QueuedHandler(
        Func<CancellationToken, ValueTask> Callback,
        string NotificationType,
        ExecutionContext? Context);

    private readonly ILogger<ChannelBasedNoWaitPublisher> _logger;
    private readonly Channel<QueuedHandler> _channel;
    private readonly ChannelWriter<QueuedHandler> _writer;
    private readonly Task _processingTask;
    private int _disposeState;

    /// <remarks>
    /// <b>Registered Scoped, and Pass 39 measured that it should stay so</b> - the option Pass 5
    /// recorded for later was a singleton, and the numbers say no. At 600 notifications with a 40 ms
    /// handler across 8 concurrent circuits: scoped 3,550 ms, naive singleton 28,519 ms, singleton
    /// with 24 consumers 1,188 ms. Scoped never loses, a naive singleton is eight times worse and is
    /// what a careless conversion produces, and the pooled variant's win is on fire-and-forget
    /// background work that nobody waits on.
    /// <para>
    /// The blocking objection is not throughput. <b>None of the six notification handlers resolves
    /// its own scope</b>; they take scoped services directly - <c>IDataSourceService</c>,
    /// <c>IMailService</c>, <c>IStringLocalizer</c> - so a singleton publisher would invoke them
    /// holding dependencies from scopes that may already be disposed. It would also move the drain
    /// below from SCOPE disposal, where it is deterministic per circuit, to process shutdown. A
    /// singleton is reachable, but only after those six handlers resolve their own scopes.
    /// </para>
    /// </remarks>
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

        _channel = Channel.CreateBounded<QueuedHandler>(options);
        _writer = _channel.Writer;

        // Start background processing task.
        //
        // This Task.Run captures the ExecutionContext - and with it every AsyncLocal - as it stands
        // HERE, in whichever scope first resolved this publisher. That capture is unavoidable and it
        // is why each message carries its own context: the consumer restores the publisher's, so
        // what is captured here never reaches a handler. See QueuedHandler.Context.
        _processingTask = Task.Run(ProcessNotifications);
    }

    public async ValueTask Publish<TNotification>(NotificationHandlers<TNotification> handlers, TNotification notification,
        CancellationToken cancellationToken)
        where TNotification : INotification
    {
        var handlerList = handlers.ToList();
        
        if (!handlerList.Any())
            return;

        // Captured ONCE per Publish, not per handler: every handler of one notification observes the
        // same ambient state, which is the state of the caller that raised it.
        var context = ExecutionContext.Capture();

        // Add all handlers to channel for async processing
        foreach (var handler in handlerList)
        {
            try
            {
                await _writer.WriteAsync(
                    new QueuedHandler(
                        token => handler.Handle(notification, token),
                        notification.GetType().Name,
                        context),
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
        await foreach (var queued in _channel.Reader.ReadAllAsync())
        {
            try
            {
                await InvokeAsync(queued).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Handler execution failed for {NotificationType}: {ErrorMessage}",
                    queued.NotificationType, ex.Message);
            }
        }
    }

    /// <summary>
    /// Runs one handler under the ambient state of the publisher that queued it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why <see cref="ExecutionContext.Run"/> rather than re-pushing a UserContext.</b> Three
    /// unrelated kinds of ambient state were being frozen, all of them <c>AsyncLocal</c>-backed and
    /// all measured in Pass 39: the tenant and user
    /// (<c>IUserContextAccessor</c> - <c>DocumentCreatedEventHandler</c> logs the user name, and
    /// <c>PicklistSetChangedEventHandler</c>'s <c>RefreshAsync</c> composes a PerTenant cache key
    /// from it), the UI culture (<c>CultureInfo.CurrentUICulture</c>, which the three mail handlers'
    /// <c>IStringLocalizer</c> reads), and trace correlation. Restoring the whole context fixes all
    /// three at once; capturing the user context alone would have fixed one and left a mail handler
    /// still localising into the wrong language.
    /// </para>
    /// <para>
    /// <b>The delegate must not be awaited inside <see cref="ExecutionContext.Run"/>.</b> That method
    /// takes a synchronous <c>ContextCallback</c>, so the callback only STARTS the handler; the
    /// returned task is awaited outside. That is sufficient and is the standard pattern: the handler
    /// begins under the restored context, and every continuation inside it inherits from there, so
    /// an <c>AsyncLocal</c> read after an await still sees the publisher's values.
    /// </para>
    /// <para>
    /// <b>A null context is not an error.</b> <see cref="ExecutionContext.Capture"/> returns null when
    /// flow is suppressed at the publish site. The handler then runs on whatever the consumer loop
    /// has, which is the pre-Pass-39 behaviour and the best available answer - a caller that
    /// suppressed flow asked for exactly that.
    /// </para>
    /// </remarks>
    private static ValueTask InvokeAsync(QueuedHandler queued)
    {
        if (queued.Context is null)
        {
            return queued.Callback(CancellationToken.None);
        }

        ValueTask pending = default;
        ExecutionContext.Run(
            queued.Context,
            state => pending = ((QueuedHandler)state!).Callback(CancellationToken.None),
            queued);

        return pending;
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
