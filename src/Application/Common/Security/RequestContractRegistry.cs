using System.Reflection;
using CleanArchitecture.Blazor.Application.Common.Approvals;
using CleanArchitecture.Blazor.Application.Common.Branches;
using CleanArchitecture.Blazor.Application.Common.Transactions;

namespace CleanArchitecture.Blazor.Application.Common.Security;

/// <summary>
/// Startup proof that every request's marker interfaces fit together (Pass 60, architecture rule 6, and the
/// approval rules of Part A). Run by <c>AddApplication</c> beside <see cref="RequestAuthorizationRegistry"/>.
/// </summary>
/// <remarks>
/// <b>Rule 6.</b> An <see cref="IIdempotentRequest"/> returns <c>Result</c> or <c>Result&lt;T&gt;</c>, because the
/// idempotency store records and replays exactly those (pass 58). That it is also an
/// <see cref="ITransactionalRequest"/> needs no check: the interface inherits it. Until Pass 60 the return type was
/// checked only when such a request was first SENT (<c>TransactionBehaviour</c>), so a wrong one failed in front of
/// a user; now it fails the start.
/// </remarks>
public static class RequestContractRegistry
{
    /// <summary>Every problem among <paramref name="candidates"/>' request types, one line each.</summary>
    /// <param name="candidates">The types to check; the request types among them are found.</param>
    /// <param name="approvalPermissions">The approval permissions; by default every <c>[ApprovalPermission]</c>
    /// constant in this assembly and in the candidates' assemblies.</param>
    public static IReadOnlyList<string> FindProblems(IEnumerable<Type> candidates, IReadOnlySet<string>? approvalPermissions = null)
    {
        var types = candidates.ToList();
        approvalPermissions ??= ApprovalPermissions.Discover(
            types.Select(t => t.Assembly).Append(typeof(RequestContractRegistry).Assembly).ToArray());

        var problems = new List<string>();
        foreach (var request in RequestAuthorizationRegistry.FindRequestTypes(types))
        {
            if (typeof(IIdempotentRequest).IsAssignableFrom(request))
            {
                var response = ResponseTypeOf(request);
                if (response is null || !IdempotentResponseCodec.Supports(response))
                    problems.Add($"{request.FullName}: is IIdempotentRequest but returns {response?.Name ?? "nothing"}; " +
                                 "it must return Result or Result<T>, which is what the idempotency store records and replays.");
            }

            CheckApproval(request, approvalPermissions, problems);
        }
        return problems;
    }

    /// <summary>
    /// Pass 60, Part A. An approval command runs in a unit of work, carries only approval policies, and is a branch
    /// command when (and only because) its header is branch-scoped (amendment C1). A request that is NOT an approval
    /// command may not carry an approval policy: that would be a command approving while dressed as an edit.
    /// </summary>
    private static void CheckApproval(Type request, IReadOnlySet<string> approvalPermissions, List<string> problems)
    {
        var policies = request.GetCustomAttributes<RequestAuthorizeAttribute>(inherit: true)
            .Select(a => a.Policy).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!).ToList();

        if (!typeof(IApprovalCommand).IsAssignableFrom(request))
        {
            foreach (var policy in policies.Where(approvalPermissions.Contains))
                problems.Add($"{request.FullName}: carries the approval permission {policy} but is not an IApprovalCommand<THeader>. " +
                             "Only an approval command may approve, so that ApprovalBehaviour checks the approver.");
            return;
        }

        var header = Pipeline.ApprovalCommands.HeaderTypeOf(request);
        if (header is null)
        {
            problems.Add($"{request.FullName}: is IApprovalCommand but not IApprovalCommand<THeader>.");
            return;
        }

        if (!typeof(IIdempotentRequest).IsAssignableFrom(request))
            problems.Add($"{request.FullName}: approves {header.Name} but is not IIdempotentRequest. An approval runs in a unit of " +
                         "work, with its header locked, and must not be applied twice.");

        if (Pipeline.ApprovalCommands.IsBranchScoped(header) && !typeof(IBranchCommand).IsAssignableFrom(request))
            problems.Add($"{request.FullName} approves the branch-scoped {header.Name} and must implement IBranchCommand, so the " +
                         "approver's branch is checked before the approval.");

        if (policies.Count == 0)
            problems.Add($"{request.FullName}: approves {header.Name} but names no [RequestAuthorize(Policy = ...)] approval permission.");
        foreach (var policy in policies.Where(p => !approvalPermissions.Contains(p)))
            problems.Add($"{request.FullName}: is authorized by {policy}, which is not marked [ApprovalPermission]. An approval command " +
                         "is authorized only by approval permissions.");
    }

    public static void AssertSound(Assembly assembly) =>
        AssertSound(assembly.GetTypes(), assembly.GetName().Name ?? "assembly");

    public static void AssertSound(IEnumerable<Type> candidates, string source, IReadOnlySet<string>? approvalPermissions = null)
    {
        var problems = FindProblems(candidates, approvalPermissions);
        if (problems.Count == 0) return;
        throw new InvalidOperationException(
            $"{problems.Count} request type(s) in '{source}' break the request contracts (README, \"Architecture tests\"):" +
            Environment.NewLine + string.Join(Environment.NewLine, problems.Select(p => "  - " + p)));
    }

    /// <summary>The <c>TResponse</c> of a Mediator request, command or query.</summary>
    public static Type? ResponseTypeOf(Type request) =>
        request.GetInterfaces()
            .Where(i => i.IsGenericType)
            .FirstOrDefault(i => i.GetGenericTypeDefinition() == typeof(IRequest<>)
                                 || i.GetGenericTypeDefinition() == typeof(ICommand<>)
                                 || i.GetGenericTypeDefinition() == typeof(IQuery<>))
            ?.GetGenericArguments()[0];
}
