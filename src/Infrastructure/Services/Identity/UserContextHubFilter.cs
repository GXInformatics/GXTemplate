using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using CleanArchitecture.Blazor.Infrastructure.Extensions;

namespace CleanArchitecture.Blazor.Infrastructure.Services.Identity;

/// <summary>
/// SignalR hub filter that sets user context when connections are established.
/// </summary>
public class UserContextHubFilter : IHubFilter
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IUserContextAccessor _userContextAccessor;
    private readonly IClientInfoAccessor _clientInfoAccessor;
    // Owned by HubUserContext so that a hub can read the same key rather than repeating the
    // literal - see the remarks there for why a drifted duplicate would fail silently.
    private const string UserContextKey = HubUserContext.ItemsKey;
    private const string ClientInfoKey = "__client_info";

    /// <summary>
    /// Initializes a new instance of the <see cref="UserContextHubFilter"/> class.
    /// </summary>
    /// <param name="scopeFactory">The service scope factory.</param>
    /// <param name="userContextAccessor">The user context accessor.</param>
    /// <param name="clientInfoAccessor">The client info accessor.</param>
    public UserContextHubFilter(IServiceScopeFactory scopeFactory, IUserContextAccessor userContextAccessor, IClientInfoAccessor clientInfoAccessor)
    {
        _scopeFactory = scopeFactory;
        _userContextAccessor = userContextAccessor;
        _clientInfoAccessor = clientInfoAccessor;
    }

    /// <summary>
    /// Invokes the hub method with user context set.
    /// </summary>
    /// <param name="invocationContext">The invocation context.</param>
    /// <param name="next">The next filter in the pipeline.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext invocationContext, Func<HubInvocationContext, ValueTask<object?>> next)
    {
        invocationContext.Context.Items.TryGetValue(UserContextKey, out var userVal);
        var user = userVal as UserContext;
        if (user is not null)
        {
            user = await RefreshAsync(invocationContext, user);
        }

        invocationContext.Context.Items.TryGetValue(ClientInfoKey, out var clientInfoVal);
        var clientInfo = clientInfoVal as ClientInfo;

     
        if (user is not null)
        {
            var effectiveClientInfo = clientInfo ?? new ClientInfo(user.IpAddress, null);
            using (_userContextAccessor.Push(user))
            using (_clientInfoAccessor.Push(effectiveClientInfo))
            {
                return await next(invocationContext);
            }
        }

        if (clientInfo is not null)
        {
            using (_clientInfoAccessor.Push(clientInfo))
            {
                return await next(invocationContext);
            }
        }

        return await next(invocationContext);

    }

    /// <summary>
    /// Re-reads the connection's user context through the loader (Pass 59).
    /// </summary>
    /// <remarks>
    /// <b>Why on every invocation.</b> The context used to be a snapshot taken in <see cref="OnConnectedAsync"/>
    /// and kept for the life of the connection, so a branch assignment removed while the user was signed in
    /// stayed readable until the circuit reconnected. The loader caches per user and is invalidated when
    /// assignments or the permissions behind them change, so this is a memory hit in the ordinary case and a
    /// reload just after a change. The IP address is kept from the snapshot, and <c>Items</c> is updated so a
    /// hub reading it agrees. Group membership is still decided in <see cref="OnConnectedAsync"/> only.
    /// <para>
    /// If the reload fails (the database is unreachable), the snapshot is used and a warning logged: reads keep
    /// the last known read set, and writes are unaffected because the branch check reads the database itself.
    /// A user who no longer exists gets no context, and is refused.
    /// </para>
    /// </remarks>
    private async ValueTask<UserContext?> RefreshAsync(HubInvocationContext invocationContext, UserContext snapshot)
    {
        var principal = invocationContext.Context.User;
        if (principal?.Identity?.IsAuthenticated != true) return snapshot;

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var loader = scope.ServiceProvider.GetRequiredService<IUserContextLoader>();
            var fresh = await loader.LoadAsync(principal, invocationContext.Context.ConnectionAborted);
            if (fresh is null)
            {
                invocationContext.Context.Items.Remove(UserContextKey);
                return null;
            }

            if (ReferenceEquals(fresh, snapshot)) return snapshot;
            fresh = fresh with { IpAddress = snapshot.IpAddress };
            invocationContext.Context.Items[UserContextKey] = fresh;
            return fresh;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var logger = invocationContext.ServiceProvider.GetService<ILogger<UserContextHubFilter>>();
            logger?.LogWarning(exception, "Reloading the user context for {UserId} failed; using the connection's snapshot.", snapshot.UserId);
            return snapshot;
        }
    }

    /// <summary>
    /// Called when a client connects to the hub.
    /// </summary>
    /// <param name="context">The hub context.</param>
    /// <param name="next">The next filter in the pipeline.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {

        var httpContext = context.Context.GetHttpContext();
        var ipAddress = httpContext.GetClientIp();
        var userAgent = httpContext.GetUserAgent();
        var clientInfo = new ClientInfo(ipAddress, userAgent);
        context.Context.Items[ClientInfoKey] = clientInfo;
        var principal = context.Context.User;

        if (principal?.Identity?.IsAuthenticated == true)
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var loader = scope.ServiceProvider.GetRequiredService<IUserContextLoader>();
            var userContext = await loader.LoadAsync(principal, context.Context.ConnectionAborted);

            if (userContext is not null)
            {
                userContext = userContext with
                {
                    IpAddress = ipAddress
                };

                context.Context.Items[UserContextKey] = userContext;
            }
        }
        await next(context);

    }

    /// <summary>
    /// Called when a client disconnects from the hub.
    /// </summary>
    /// <param name="context">The hub context.</param>
    /// <param name="exception">The exception that caused the disconnection, if any.</param>
    /// <param name="next">The next filter in the pipeline.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async Task OnDisconnectedAsync(HubLifetimeContext context, Exception? exception, Func<HubLifetimeContext, Exception?, Task> next)
    {
        // Clear user context when client disconnects
        _userContextAccessor.Clear();
        _clientInfoAccessor.Clear();
        await next(context, exception);
    }

    
}
