#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.ExceptionHandlers;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Application.Common.Security;
using CleanArchitecture.Blazor.Application.Features.Identity.DTOs;
using CleanArchitecture.Blazor.Application.Pipeline;
using FluentAssertions;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.Pipeline;

/// <summary>
/// Regression tests for deny-by-default authorization. Before this behaviour existed, no request
/// carried any authorization marker and nothing checked one: every command and query in the
/// application executed for whoever could reach a page.
/// </summary>
[TestFixture]
public class AuthorizationBehaviourTests
{
    private const string UserId = "user-1";
    private const string GrantedPolicy = "Permissions.Documents.View";
    private const string OtherPolicy = "Permissions.Documents.Edit";

    // ---- request doubles -------------------------------------------------------------------------
    // Declared in the test assembly, so they are invisible to the startup assertion over Application.

    public sealed record UnmarkedRequest : IRequest<string>;

    [RequestAuthorize(Policy = GrantedPolicy)]
    public sealed record PolicyRequest : IRequest<string>;

    [RequestAuthorize(Policy = OtherPolicy)]
    [RequestAuthorize(Policy = GrantedPolicy)]
    public sealed record TwoPolicyRequest : IRequest<string>;

    [RequestAuthorize(Roles = "Admin, Basic")]
    public sealed record RolesRequest : IRequest<string>;

    [RequestAuthorize(Roles = "Admin", Policy = GrantedPolicy)]
    public sealed record RolesAndPolicyRequest : IRequest<string>;

    // ---- doubles ---------------------------------------------------------------------------------

    private sealed class StubUserContextAccessor : IUserContextAccessor
    {
        public StubUserContextAccessor(UserContext? current) => Current = current;
        public UserContext? Current { get; }
        public IDisposable Push(UserContext context) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
    }

    /// <summary>Counts policy checks so the Roles-before-Policy ordering is observable.</summary>
    private sealed class CountingIdentityService : IIdentityService
    {
        private readonly Func<string, bool> _grant;
        public CountingIdentityService(Func<string, bool> grant) => _grant = grant;

        public int AuthorizeCallCount { get; private set; }
        public List<string> PoliciesChecked { get; } = new();
        public Func<string, Task<bool>>? Override { get; set; }

        public Task<bool> AuthorizeAsync(string userId, string policyName, CancellationToken cancellation = default)
        {
            AuthorizeCallCount++;
            PoliciesChecked.Add(policyName);
            return Override is not null ? Override(policyName) : Task.FromResult(_grant(policyName));
        }

        public Task<string?> GetUserNameAsync(string userId, CancellationToken cancellation = default) =>
            Task.FromResult<string?>(null);
        public Task<bool> IsInRoleAsync(string userId, string role, CancellationToken cancellation = default) =>
            Task.FromResult(false);
        public Task<IDictionary<string, string?>> FetchUsers(string roleName, CancellationToken cancellation = default) =>
            Task.FromResult<IDictionary<string, string?>>(new Dictionary<string, string?>());
        public Task<ApplicationUserDto?> GetApplicationUserDto(string userName, CancellationToken cancellation = default) =>
            Task.FromResult<ApplicationUserDto?>(null);
        public string GetUserName(string userId) => string.Empty;
        public Task<List<ApplicationUserDto>?> GetUsers(string? tenantId, CancellationToken cancellation = default) =>
            Task.FromResult<List<ApplicationUserDto>?>(null);
        public void RemoveApplicationUserCache(string userName) { }
    }

    private static UserContext Context(params string[] roles) =>
        new(UserId: UserId, UserName: "u", Roles: roles.Length == 0 ? null : roles.ToList().AsReadOnly());

    private static AuthorizationBehaviour<TRequest, string> Behaviour<TRequest>(
        UserContext? context, IIdentityService identityService)
        where TRequest : class, IMessage =>
        new(new StubUserContextAccessor(context),
            identityService,
            NullLogger<AuthorizationBehaviour<TRequest, string>>.Instance);

    private static ValueTask<string> Next<TRequest>(TRequest _, CancellationToken __) =>
        ValueTask.FromResult("handler-ran");

    // ---- tests -----------------------------------------------------------------------------------

    [Test]
    public async Task AnUnmarkedRequest_IsDenied_WithTheUnmarkedMessage()
    {
        var identity = new CountingIdentityService(_ => true);
        var behaviour = Behaviour<UnmarkedRequest>(Context("Admin"), identity);

        var act = async () => await behaviour.Handle(new UnmarkedRequest(), Next, CancellationToken.None);

        (await act.Should().ThrowAsync<ForbiddenAccessException>())
            .Which.Message.Should().Contain("is not marked for authorization")
            .And.Contain(nameof(UnmarkedRequest));
        identity.AuthorizeCallCount.Should().Be(0, "an unmarked request is refused before any check");
    }

    [Test]
    public async Task TheUnmarkedMessageIsDistinctFromTheFailedCheckMessage()
    {
        var identity = new CountingIdentityService(_ => false);

        var unmarked = await Capture(async () =>
            await Behaviour<UnmarkedRequest>(Context("Admin"), identity)
                .Handle(new UnmarkedRequest(), Next, CancellationToken.None));
        var failedCheck = await Capture(async () =>
            await Behaviour<PolicyRequest>(Context(), identity)
                .Handle(new PolicyRequest(), Next, CancellationToken.None));

        unmarked.Should().NotBe(failedCheck);
        unmarked.Should().Contain("not marked for authorization");
        failedCheck.Should().Contain("do not have permission");
    }

    [Test]
    public async Task AMarkedRequest_WithNoAmbientContext_IsDenied()
    {
        var identity = new CountingIdentityService(_ => true);
        var behaviour = Behaviour<PolicyRequest>(context: null, identity);

        var act = async () => await behaviour.Handle(new PolicyRequest(), Next, CancellationToken.None);

        (await act.Should().ThrowAsync<ForbiddenAccessException>())
            .Which.Message.Should().Contain("no authenticated user in context");
        identity.AuthorizeCallCount.Should().Be(0);
    }

    [Test]
    public async Task AMarkedRequest_WithAContextCarryingNoUserId_IsDenied()
    {
        var identity = new CountingIdentityService(_ => true);
        var behaviour = Behaviour<PolicyRequest>(new UserContext(UserId: string.Empty, UserName: "u"), identity);

        var act = async () => await behaviour.Handle(new PolicyRequest(), Next, CancellationToken.None);

        await act.Should().ThrowAsync<ForbiddenAccessException>();
    }

    [Test]
    public async Task AMarkedRequest_WithoutTheRequiredPermission_IsDenied()
    {
        var identity = new CountingIdentityService(_ => false);
        var behaviour = Behaviour<PolicyRequest>(Context(), identity);

        var act = async () => await behaviour.Handle(new PolicyRequest(), Next, CancellationToken.None);

        (await act.Should().ThrowAsync<ForbiddenAccessException>())
            .Which.Message.Should().Contain(nameof(PolicyRequest));
        identity.PoliciesChecked.Should().Equal(GrantedPolicy);
    }

    [Test]
    public async Task AMarkedRequest_WithTheRequiredPermission_Proceeds()
    {
        var identity = new CountingIdentityService(policy => policy == GrantedPolicy);
        var behaviour = Behaviour<PolicyRequest>(Context(), identity);

        var result = await behaviour.Handle(new PolicyRequest(), Next, CancellationToken.None);

        result.Should().Be("handler-ran");
    }

    [Test]
    public async Task MultipleAttributes_AreAnyOf_SoTheSecondCanSatisfyIt()
    {
        // The user holds only the second attribute's policy - an AND reading would deny this.
        var identity = new CountingIdentityService(policy => policy == GrantedPolicy);
        var behaviour = Behaviour<TwoPolicyRequest>(Context(), identity);

        var result = await behaviour.Handle(new TwoPolicyRequest(), Next, CancellationToken.None);

        result.Should().Be("handler-ran");
        identity.PoliciesChecked.Should().Equal(OtherPolicy, GrantedPolicy);
    }

    [Test]
    public async Task ARolesAttribute_PassesOnAMatchInTheAmbientRoles()
    {
        var identity = new CountingIdentityService(_ => false);
        var behaviour = Behaviour<RolesRequest>(Context("Basic"), identity);

        var result = await behaviour.Handle(new RolesRequest(), Next, CancellationToken.None);

        result.Should().Be("handler-ran", "the comma-separated role list is ANY-OF too");
        identity.AuthorizeCallCount.Should().Be(0, "a role match needs no policy lookup");
    }

    [Test]
    public async Task ARolesAttribute_DeniesWhenTheAmbientRolesDoNotMatch()
    {
        var identity = new CountingIdentityService(_ => false);
        var behaviour = Behaviour<RolesRequest>(Context("SomethingElse"), identity);

        var act = async () => await behaviour.Handle(new RolesRequest(), Next, CancellationToken.None);

        await act.Should().ThrowAsync<ForbiddenAccessException>();
    }

    [Test]
    public async Task ARolesAttribute_DeniesWhenTheContextCarriesNoRoles()
    {
        var identity = new CountingIdentityService(_ => false);
        var behaviour = Behaviour<RolesRequest>(Context(), identity);

        var act = async () => await behaviour.Handle(new RolesRequest(), Next, CancellationToken.None);

        await act.Should().ThrowAsync<ForbiddenAccessException>();
    }

    [Test]
    public async Task RolesAreEvaluatedBeforePolicies()
    {
        // The role test is in-memory; a policy test rebuilds a ClaimsPrincipal from the database.
        // A satisfied role must therefore short-circuit before any policy lookup happens.
        var identity = new CountingIdentityService(_ => true);
        var behaviour = Behaviour<RolesAndPolicyRequest>(Context("Admin"), identity);

        var result = await behaviour.Handle(new RolesAndPolicyRequest(), Next, CancellationToken.None);

        result.Should().Be("handler-ran");
        identity.AuthorizeCallCount.Should().Be(0,
            "the ambient role satisfied the request, so the database was never consulted");
    }

    [Test]
    public async Task WhenTheIdentityServiceReportsTheUserIsGone_TheRequestIsDenied_NotFaulted()
    {
        // A context cached for an account that has since been deleted: AuthorizeAsync throws
        // NotFoundException. That is a denial, not an error to surface as its own failure.
        var identity = new CountingIdentityService(_ => true)
        {
            Override = _ => throw new NotFoundException("User Not Found")
        };
        var behaviour = Behaviour<PolicyRequest>(Context(), identity);

        var act = async () => await behaviour.Handle(new PolicyRequest(), Next, CancellationToken.None);

        await act.Should().ThrowAsync<ForbiddenAccessException>();
        await act.Should().NotThrowAsync<NotFoundException>();
    }

    [Test]
    public void TheBehaviourIsRegisteredOutermost()
    {
        // Mediator composes behaviours last-to-first, so index 0 is the outermost. Nothing may be
        // allowed to run - validation, caching, a handler - before authorization has passed.
        var behaviours = CapturePipelineBehaviours();

        behaviours.Should().NotBeEmpty();
        behaviours[0].Should().Be(typeof(AuthorizationBehaviour<,>));
    }

    [Test]
    public void TheBehaviourUsesTheLoosestConstraint_SoTheGeneratorCannotSkipUnmarkedRequests()
    {
        // The source generator silently omits a behaviour for request types that fail its generic
        // constraints. A tighter constraint here (e.g. a marker interface) would skip exactly the
        // unmarked requests this behaviour exists to catch, and the pipeline would enforce nothing.
        var constraints = typeof(AuthorizationBehaviour<,>).GetGenericArguments()[0]
            .GetGenericParameterConstraints();

        constraints.Should().ContainSingle().Which.Should().Be(typeof(IMessage));
        typeof(AuthorizationBehaviour<,>).GetGenericArguments()[0]
            .GenericParameterAttributes.Should().HaveFlag(
                System.Reflection.GenericParameterAttributes.ReferenceTypeConstraint);
    }

    /// <summary>
    /// Reads the pipeline order back out of the real DI registration rather than restating the
    /// array: AddApplication runs the source-generated AddMediator, which registers one closed
    /// IPipelineBehavior descriptor per (behaviour, request) pair in PipelineBehaviors order.
    /// </summary>
    private static IReadOnlyList<Type> CapturePipelineBehaviours()
    {
        var services = new ServiceCollection();
        CleanArchitecture.Blazor.Application.DependencyInjection.AddApplication(services);

        // Any request type will do; each gets the full applicable set, in PipelineBehaviors order.
        var probe = typeof(CleanArchitecture.Blazor.Application.Features.Documents.Queries.PaginationQuery.DocumentsWithPaginationQuery);

        return services
            .Where(d => d.ServiceType.IsGenericType &&
                        d.ServiceType.GetGenericTypeDefinition() == typeof(IPipelineBehavior<,>) &&
                        d.ServiceType.GetGenericArguments()[0] == probe)
            .Select(d => d.ImplementationType!.GetGenericTypeDefinition())
            .ToList();
    }

    private static async Task<string> Capture(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (ForbiddenAccessException ex)
        {
            return ex.Message;
        }
        return string.Empty;
    }
}
#nullable restore
