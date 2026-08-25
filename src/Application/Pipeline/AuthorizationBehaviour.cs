using System.Collections.Concurrent;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;

namespace CleanArchitecture.Blazor.Application.Pipeline;

/// <summary>
/// Enforces deny-by-default authorization on every request dispatched through the mediator.
/// <para>
/// <b>Position.</b> This behaviour must stay first in <c>options.PipelineBehaviors</c>. Mediator's
/// source generator composes behaviours from last to first, so the first entry is the outermost -
/// nothing reaches validation, caching or a handler before this check has passed. Being outside
/// <see cref="ResultExceptionBehavior{TRequest, TResponse}"/> is deliberate: a denial propagates to
/// the caller as an exception rather than being converted into a failed <c>Result</c> that a call
/// site could ignore.
/// </para>
/// <para>
/// <b>Constraint.</b> The constraint is exactly <c>where TRequest : class, IMessage</c> - the
/// loosest one that compiles, matching <see cref="ValidationBehavior{TRequest, TResponse}"/>. This
/// is load-bearing. The source generator only registers a behaviour for request types that satisfy
/// its generic constraints, and it does so silently: constraining this behaviour to an
/// authorization marker interface would make the generator skip precisely the unmarked requests it
/// exists to catch, and the pipeline would look correct while enforcing nothing.
/// </para>
/// <para>
/// <b>Identity.</b> The principal comes from the ambient <see cref="IUserContextAccessor"/>, which
/// is populated by the SignalR hub filter for the duration of a circuit invocation. A null context
/// is a denial. This couples the behaviour to <c>App.razor</c>'s
/// <c>new InteractiveServerRenderMode(false)</c>: with prerendering disabled, routed components
/// render only over an established circuit, so the ambient context is always present when a
/// component dispatches. Re-enabling prerendering would make the first render of every page run
/// over plain HTTP with no ambient context, and every such dispatch would be denied.
/// </para>
/// <para>
/// <b>ANY-OF.</b> A request may carry several <see cref="RequestAuthorizeAttribute"/>s; the
/// principal need satisfy only one of them. Within the set, every <c>Roles</c> entry is tested
/// before any <c>Policy</c> entry, because the role test is an in-memory comparison against the
/// ambient context while each policy test rebuilds a ClaimsPrincipal from the database.
/// </para>
/// </summary>
public sealed class AuthorizationBehaviour<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : class, IMessage
{
    /// <summary>
    /// Attribute lookup is reflection over a closed type and never changes for the life of the
    /// process, so it is resolved once per request type.
    /// </summary>
    private static readonly ConcurrentDictionary<Type, RequestAuthorizeAttribute[]> AttributeCache = new();

    private readonly IUserContextAccessor _userContextAccessor;
    private readonly IIdentityService _identityService;
    private readonly ILogger<AuthorizationBehaviour<TRequest, TResponse>> _logger;

    public AuthorizationBehaviour(
        IUserContextAccessor userContextAccessor,
        IIdentityService identityService,
        ILogger<AuthorizationBehaviour<TRequest, TResponse>> logger)
    {
        _userContextAccessor = userContextAccessor;
        _identityService = identityService;
        _logger = logger;
    }

    public async ValueTask<TResponse> Handle(
        TRequest request,
        MessageHandlerDelegate<TRequest, TResponse> next,
        CancellationToken cancellationToken)
    {
        var requestName = typeof(TRequest).Name;
        var attributes = GetAuthorizeAttributes(typeof(TRequest));

        // Deny-by-default. An unmarked request is refused rather than waved through, and the message
        // says so plainly: this is a developer omission, not an access decision about this principal.
        if (attributes.Length == 0)
        {
            _logger.LogError(
                "Request {RequestType} is not marked for authorization and was denied. Apply a RequestAuthorizeAttribute to it.",
                requestName);
            throw new ForbiddenAccessException(
                $"Request '{requestName}' is not marked for authorization and was therefore denied.");
        }

        var currentUser = _userContextAccessor.Current;
        if (currentUser is null || string.IsNullOrEmpty(currentUser.UserId))
        {
            _logger.LogWarning(
                "Request {RequestType} was denied: there is no ambient user context to authorize against.",
                requestName);
            throw new ForbiddenAccessException(
                $"Access to '{requestName}' was denied because there is no authenticated user in context.");
        }

        if (await SatisfiesAnyAsync(attributes, currentUser, requestName, cancellationToken).ConfigureAwait(false))
        {
            return await next(request, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogWarning(
            "User {UserId} was denied access to {RequestType}: none of its {AttributeCount} authorization requirement(s) were satisfied.",
            currentUser.UserId, requestName, attributes.Length);
        throw new ForbiddenAccessException(
            $"You do not have permission to perform '{requestName}'.");
    }

    /// <summary>
    /// ANY-OF across the request's attributes. Roles are tested first across all attributes, then
    /// policies - the role test is in-memory, a policy test is several database round-trips.
    /// </summary>
    private async Task<bool> SatisfiesAnyAsync(
        RequestAuthorizeAttribute[] attributes,
        UserContext currentUser,
        string requestName,
        CancellationToken cancellationToken)
    {
        foreach (var attribute in attributes)
        {
            if (SatisfiesRoles(attribute, currentUser))
            {
                return true;
            }
        }

        foreach (var attribute in attributes)
        {
            if (string.IsNullOrWhiteSpace(attribute.Policy))
            {
                continue;
            }

            if (await SatisfiesPolicyAsync(attribute.Policy, currentUser, requestName, cancellationToken)
                    .ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    private static bool SatisfiesRoles(RequestAuthorizeAttribute attribute, UserContext currentUser)
    {
        if (string.IsNullOrWhiteSpace(attribute.Roles))
        {
            return false;
        }

        var held = currentUser.Roles;
        if (held is null || held.Count == 0)
        {
            return false;
        }

        // The attribute documents Roles as a comma delimited list.
        var required = attribute.Roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return required.Any(role => held.Contains(role, StringComparer.OrdinalIgnoreCase));
    }

    private async Task<bool> SatisfiesPolicyAsync(
        string policy,
        UserContext currentUser,
        string requestName,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _identityService
                .AuthorizeAsync(currentUser.UserId, policy, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (NotFoundException)
        {
            // The ambient context outlived the user row - a context cached for an account that has
            // since been deleted. That is a denial, not a lookup failure to surface as its own error.
            _logger.LogWarning(
                "User {UserId} no longer exists while authorizing {RequestType}; treating as denied.",
                currentUser.UserId, requestName);
            return false;
        }
    }

    private static RequestAuthorizeAttribute[] GetAuthorizeAttributes(Type requestType) =>
        AttributeCache.GetOrAdd(
            requestType,
            static type => type.GetCustomAttributes<RequestAuthorizeAttribute>(inherit: true).ToArray());
}
