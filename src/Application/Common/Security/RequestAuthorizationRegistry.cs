using System.Reflection;

namespace CleanArchitecture.Blazor.Application.Common.Security;

/// <summary>
/// Startup-time enforcement of the deny-by-default contract.
/// <para>
/// <c>AuthorizationBehaviour</c> refuses unmarked requests at dispatch time, which is safe but late:
/// the omission would only surface when a user hit the feature. This registry closes that gap by
/// failing the application at startup instead, so an unmarked request cannot ship.
/// </para>
/// <para>
/// The logic lives in static methods so it can be tested directly against a controlled type list
/// rather than only through a running host.
/// </para>
/// </summary>
public static class RequestAuthorizationRegistry
{
    /// <summary>
    /// Every concrete Mediator request type declared in <paramref name="assembly"/>.
    /// Notifications are deliberately excluded: they are not dispatched through the request pipeline
    /// and <c>AuthorizationBehaviour</c> never sees them.
    /// </summary>
    public static IReadOnlyList<Type> FindRequestTypes(Assembly assembly)
    {
        return assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && IsRequest(t))
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The request types from <paramref name="types"/> that carry no
    /// <see cref="RequestAuthorizeAttribute"/> - i.e. the ones deny-by-default would refuse.
    /// </summary>
    public static IReadOnlyList<Type> FindUnmarkedRequestTypes(IEnumerable<Type> types)
    {
        return types
            .Where(t => t.GetCustomAttributes<RequestAuthorizeAttribute>(inherit: true).Any() == false)
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Throws unless every request type in <paramref name="assembly"/> is marked for authorization.
    /// Also throws when the assembly yields no request types at all - that means the reflection has
    /// silently stopped matching (a Mediator upgrade, a moved namespace), and a registry that finds
    /// nothing would otherwise "pass" forever while checking nothing.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The assembly declares no request types, or one or more request types are unmarked.
    /// </exception>
    public static void AssertAllRequestsAreMarked(Assembly assembly)
    {
        var requests = FindRequestTypes(assembly);

        if (requests.Count == 0)
        {
            throw new InvalidOperationException(
                $"Authorization registry found no Mediator request types in '{assembly.GetName().Name}'. " +
                "The deny-by-default check would pass vacuously, so this is treated as a failure: " +
                $"verify that {nameof(RequestAuthorizationRegistry)}.{nameof(FindRequestTypes)} still recognises the request interfaces.");
        }

        var unmarked = FindUnmarkedRequestTypes(requests);
        if (unmarked.Count > 0)
        {
            var names = string.Join(Environment.NewLine, unmarked.Select(t => "  - " + t.FullName));
            throw new InvalidOperationException(
                $"{unmarked.Count} of {requests.Count} Mediator request type(s) in '{assembly.GetName().Name}' carry no " +
                $"{nameof(RequestAuthorizeAttribute)} and would be denied at dispatch time:{Environment.NewLine}{names}{Environment.NewLine}" +
                "Every request must declare the permission it requires - see RequestAuthorizeAttribute.");
        }
    }

    private static bool IsRequest(Type type) =>
        type.GetInterfaces().Any(i =>
            i == typeof(IRequest) ||
            (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequest<>)));
}
