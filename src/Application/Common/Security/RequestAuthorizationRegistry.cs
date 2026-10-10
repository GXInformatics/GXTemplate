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
    public static IReadOnlyList<Type> FindRequestTypes(Assembly assembly) => FindRequestTypes(assembly.GetTypes());

    /// <summary>
    /// The concrete Mediator request types among <paramref name="candidates"/> - classes AND value
    /// types, so that a struct request is found and refused rather than overlooked (see
    /// <see cref="AssertAllRequestsAreMarked(IEnumerable{Type}, string)"/>).
    /// </summary>
    public static IReadOnlyList<Type> FindRequestTypes(IEnumerable<Type> candidates)
    {
        return candidates
            .Where(t => !t.IsAbstract && !t.IsInterface && (t.IsClass || t.IsValueType) && IsRequest(t))
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
    public static void AssertAllRequestsAreMarked(Assembly assembly) =>
        AssertAllRequestsAreMarked(assembly.GetTypes(), assembly.GetName().Name ?? assembly.FullName ?? "assembly");

    /// <summary>
    /// The same check over an explicit set of candidate types - what the assembly overload runs, and
    /// what a test uses to check one request shape without the rest of an assembly in the way.
    /// </summary>
    /// <param name="candidates">Any types; non-requests are ignored.</param>
    /// <param name="source">Names where the types came from, in the failure message.</param>
    /// <exception cref="InvalidOperationException">
    /// No request types; a request declared as a value type; or one or more unmarked requests.
    /// </exception>
    public static void AssertAllRequestsAreMarked(IEnumerable<Type> candidates, string source)
    {
        var requests = FindRequestTypes(candidates);

        if (requests.Count == 0)
        {
            throw new InvalidOperationException(
                $"Authorization registry found no Mediator request types in '{source}'. " +
                "The deny-by-default check would pass vacuously, so this is treated as a failure: " +
                $"verify that {nameof(RequestAuthorizationRegistry)}.{nameof(FindRequestTypes)} still recognises the request interfaces.");
        }

        // A struct request is never authorized at all, marked or not: AuthorizationBehaviour is
        // constrained to `class`, and the source generator silently skips message types that do not
        // satisfy a behaviour's constraints (see the remarks on AuthorizationBehaviour). Refused here,
        // at startup, rather than discovered as an unauthenticated write.
        var structs = requests.Where(t => t.IsValueType).ToList();
        if (structs.Count > 0)
        {
            var names = string.Join(Environment.NewLine, structs.Select(t => "  - " + t.FullName));
            throw new InvalidOperationException(
                $"{structs.Count} Mediator request type(s) in '{source}' are value types, which AuthorizationBehaviour " +
                $"never sees - they would run unauthorized:{Environment.NewLine}{names}{Environment.NewLine}" +
                "Declare requests as classes or records (not record structs).");
        }

        var unmarked = FindUnmarkedRequestTypes(requests);
        if (unmarked.Count > 0)
        {
            var names = string.Join(Environment.NewLine, unmarked.Select(t => "  - " + t.FullName));
            throw new InvalidOperationException(
                $"{unmarked.Count} of {requests.Count} Mediator request type(s) in '{source}' carry no " +
                $"{nameof(RequestAuthorizeAttribute)} and would be denied at dispatch time:{Environment.NewLine}{names}{Environment.NewLine}" +
                "Every request must declare the permission it requires - see RequestAuthorizeAttribute.");
        }
    }

    /// <summary>
    /// Whether <paramref name="type"/> is something Mediator dispatches through the request pipeline:
    /// a request, a command or a query, generic or not.
    /// </summary>
    /// <remarks>
    /// Keyed off Mediator's three base interfaces, which every form implements - <c>IRequest</c>,
    /// <c>IRequest&lt;T&gt;</c>, <c>ICommand</c>, <c>ICommand&lt;T&gt;</c> and <c>IQuery&lt;T&gt;</c>
    /// (Mediator 3 has no non-generic <c>IQuery</c>). Until Pass 55 only the two <c>IRequest</c>
    /// forms were recognised, so an unmarked <c>ICommand&lt;T&gt;</c> passed this startup check and
    /// was only refused when a user first hit it. Notifications (<c>INotification</c>) and stream
    /// messages (<c>IStreamMessage</c>) are not requests here: neither runs through
    /// <c>AuthorizationBehaviour</c>.
    /// </remarks>
    private static bool IsRequest(Type type) =>
        typeof(IBaseRequest).IsAssignableFrom(type)
        || typeof(IBaseCommand).IsAssignableFrom(type)
        || typeof(IBaseQuery).IsAssignableFrom(type);
}
