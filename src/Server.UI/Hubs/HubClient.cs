using Microsoft.AspNetCore.SignalR.Client;

namespace CleanArchitecture.Blazor.Server.UI.Hubs;

/// <summary>
/// Circuit-scoped client for <see cref="ISignalRHub"/>.
/// </summary>
/// <remarks>
/// Pass 30 removed the chat and page-component members (<c>SendAsync</c>, <c>NotifyAsync</c>,
/// <c>OpenPageComponentAsync</c>, <c>ClosePageComponentAsync</c> and their events and event args)
/// along with the hub methods they called. Nothing in the application subscribed to or invoked any
/// of them.
/// <para>
/// This type is scoped, so it is disposed and rebuilt when the Blazor circuit is - which is how a
/// tenant switch gets a connection in the new tenant's group. <c>TenantSelector.razor</c> forces a
/// full page load for that reason.
/// </para>
/// </remarks>
public sealed class HubClient : IAsyncDisposable
{
    private readonly HubConnection _hubConnection;
    private bool _started;

    public HubClient(IHubConnectionFactory hubConnectionFactory)
    {
        _hubConnection = hubConnectionFactory.CreateForCurrentUser(ISignalRHub.Url);

        _hubConnection.ServerTimeout = TimeSpan.FromSeconds(20);
        _hubConnection.KeepAliveInterval = TimeSpan.FromSeconds(10);

        // Observe and await the async result of the event invocation
        _hubConnection.On<string, string>(nameof(ISignalRHub.Connect), OnLoginEventAsync);

        _hubConnection.On<string, string>(nameof(ISignalRHub.Disconnect), OnLogoutEventAsync);
    }

    // Handle the result of async event invocations
    private Task OnLoginEventAsync(string connectionId, string userName)
    {
        LoginEvent?.Invoke(this, new UserStateChangeEventArgs(connectionId, userName));
        return Task.CompletedTask;
    }

    private Task OnLogoutEventAsync(string connectionId, string userName)
    {
        LogoutEvent?.Invoke(this, new UserStateChangeEventArgs(connectionId, userName));
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _hubConnection.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            await _hubConnection.DisposeAsync().ConfigureAwait(false);
        }
    }

    // Event handlers
    public event EventHandler<UserStateChangeEventArgs>? LoginEvent;
    public event EventHandler<UserStateChangeEventArgs>? LogoutEvent;

    public async Task StartAsync(CancellationToken cancellation = default)
    {
        if (_started) return;
        _started = true;
        await _hubConnection.StartAsync(cancellation).ConfigureAwait(false);
    }

    // Snapshot online users from the hub. The hub scopes the result to the caller's tenant; this
    // client applies no filter of its own and must not, since the connection is the authority.
    public async Task<List<CleanArchitecture.Blazor.Application.Common.Interfaces.Identity.UserContext>> GetOnlineUsersAsync(CancellationToken cancellationToken = default)
    {
        var users = await _hubConnection.InvokeAsync<List<CleanArchitecture.Blazor.Application.Common.Interfaces.Identity.UserContext>>(nameof(ISignalRHub.GetOnlineUsers), cancellationToken).ConfigureAwait(false);
        return users ?? new List<CleanArchitecture.Blazor.Application.Common.Interfaces.Identity.UserContext>();
    }
}

public class UserStateChangeEventArgs : EventArgs
{
    public UserStateChangeEventArgs(string connectionId, string userName)
    {
        ConnectionId = connectionId;
        UserName = userName;
    }

    public string ConnectionId { get; set; }
    public string UserName { get; set; }
}
