using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;

namespace CleanArchitecture.Blazor.Server.UI.Hubs;

/// <summary>
/// The client-facing surface of <see cref="ServerHub"/>: online presence, and nothing else.
/// </summary>
/// <remarks>
/// Pass 30 removed <c>SendMessage</c>, <c>SendPrivateMessage</c>, <c>SendNotification</c> and the
/// <c>PageComponent*</c> signals. All five were unreachable - the chat trio had no caller anywhere
/// in the application after Pass 7-2 removed the AI chatbot, and the page-component pair was
/// consumed only by <c>ActiveUserSession.razor</c>, which was never rendered. Between them they
/// accounted for four of the six <c>Clients.All</c> broadcasts Pass 23 §3.6 found, plus a
/// direct-message channel whose recipient was a client-supplied, unchecked user name.
/// </remarks>
public interface ISignalRHub
{
    public const string Url = "/signalRHub";

    Task Connect(string connectionId, string userName);
    Task Disconnect(string connectionId, string userName);

    // Snapshot method: fetch current online users with profile data
    // Note: invoked via HubConnection.InvokeAsync from clients
    Task<List<UserContext>> GetOnlineUsers();
}
