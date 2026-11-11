using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace CleanArchitecture.Blazor.Application.Common.Transactions;

/// <summary>
/// Calls every <see cref="IPostingEventHandler{TEvent}"/> registered for a posting event's RUNTIME
/// type, one after another, in registration order (Pass 58).
/// </summary>
/// <remarks>
/// Not Mediator: Mediator's notification publisher here is the background channel, which is exactly
/// what a posting event must not go through. Handlers resolve from the scope that sent the request.
/// </remarks>
internal sealed class PostingEventDispatcher(IServiceProvider services) : IPostingEventDispatcher
{
    private static readonly ConcurrentDictionary<Type, Func<IServiceProvider, PostingEvent, CancellationToken, ValueTask>> Invokers = new();

    private static readonly MethodInfo InvokeAllMethod =
        typeof(PostingEventDispatcher).GetMethod(nameof(InvokeAllAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    public ValueTask DispatchAsync(PostingEvent postingEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(postingEvent);
        var invoker = Invokers.GetOrAdd(postingEvent.GetType(), static type =>
            InvokeAllMethod.MakeGenericMethod(type)
                .CreateDelegate<Func<IServiceProvider, PostingEvent, CancellationToken, ValueTask>>());
        return invoker(services, postingEvent, cancellationToken);
    }

    private static async ValueTask InvokeAllAsync<TEvent>(
        IServiceProvider services, PostingEvent postingEvent, CancellationToken cancellationToken)
        where TEvent : PostingEvent
    {
        foreach (var handler in services.GetServices<IPostingEventHandler<TEvent>>())
        {
            await handler.Handle((TEvent)postingEvent, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>Registration of posting event handlers.</summary>
public static class PostingEventHandlerRegistration
{
    /// <summary>
    /// Registers every non-abstract class in <paramref name="assemblies"/> implementing
    /// <see cref="IPostingEventHandler{TEvent}"/>, scoped, once per closed interface.
    /// <c>AddApplication</c> calls it for the Application assembly, where module features live.
    /// </summary>
    public static IServiceCollection AddPostingEventHandlers(this IServiceCollection services, params Assembly[] assemblies)
    {
        foreach (var type in assemblies.SelectMany(a => a.GetTypes()))
        {
            if (!type.IsClass || type.IsAbstract || type.IsGenericTypeDefinition) continue;

            foreach (var contract in type.GetInterfaces().Where(i =>
                         i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IPostingEventHandler<>)))
            {
                if (services.Any(d => d.ServiceType == contract && d.ImplementationType == type)) continue;
                services.AddScoped(contract, type);
            }
        }
        return services;
    }
}
