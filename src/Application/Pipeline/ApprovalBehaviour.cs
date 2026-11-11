using CleanArchitecture.Blazor.Domain.Common.Entities;
using System.Collections.Concurrent;
using System.Reflection;
using CleanArchitecture.Blazor.Application.Common.Approvals;
using CleanArchitecture.Blazor.Application.Common.Branches;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Application.Common.Transactions;

namespace CleanArchitecture.Blazor.Application.Pipeline;

/// <summary>
/// Checks an approval against <see cref="IApprovalPolicy"/> before the handler runs, with the header locked (Pass 60).
/// </summary>
/// <remarks>
/// <para>
/// <b>Placed immediately inside <c>TransactionBehaviour</c></b>, not beside the authorization and branch behaviours:
/// the value and the preparer must be read from the STORED header and must not change between the check and the
/// approval, and pass 58's answer to "must not change" is a row lock, which exists only inside the unit of work.
/// <c>AuthorizationBehaviour</c> (the approve permission, fresh) and <c>BranchAccessBehaviour</c> (the branch, fresh,
/// for a branch-scoped header) have already run.
/// </para>
/// <para>
/// <b>Each attempt</b>: load the header with <c>ForUpdate()</c> through the unit of work's context; decide; refuse with
/// <see cref="ForbiddenAccessException"/> (logged at Warning with the reason code), or install an
/// <see cref="ApprovalGrant"/> and run the handler, which gets the same locked instance. A deadlock retry re-runs the
/// whole attempt, so the check runs again on fresh data. A replayed <c>RequestId</c> returns its recorded response
/// before this behaviour runs: the approval already happened.
/// </para>
/// </remarks>
public sealed class ApprovalBehaviour<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : class, IMessage, IApprovalCommand
    where TResponse : IResult
{
    private static readonly ConcurrentDictionary<Type, HeaderLoader> Loaders = new();

    private readonly IUnitOfWork _unitOfWork;
    private readonly IApprovalPolicy _policy;
    private readonly IUserContextAccessor _userContextAccessor;
    private readonly ILogger<ApprovalBehaviour<TRequest, TResponse>> _logger;

    public ApprovalBehaviour(
        IUnitOfWork unitOfWork,
        IApprovalPolicy policy,
        IUserContextAccessor userContextAccessor,
        ILogger<ApprovalBehaviour<TRequest, TResponse>> logger)
    {
        _unitOfWork = unitOfWork;
        _policy = policy;
        _userContextAccessor = userContextAccessor;
        _logger = logger;
    }

    public async ValueTask<TResponse> Handle(TRequest request, MessageHandlerDelegate<TRequest, TResponse> next, CancellationToken cancellationToken)
    {
        var requestName = typeof(TRequest).Name;
        if (!_unitOfWork.IsActive)
            throw new InvalidOperationException(
                $"{requestName} approves a document and must run in a unit of work. Implement IIdempotentRequest on it.");

        var user = _userContextAccessor.Current;
        if (user is null || string.IsNullOrEmpty(user.UserId))
            throw new ForbiddenAccessException($"Access to '{requestName}' was denied because there is no authenticated user in context.");

        var loader = Loaders.GetOrAdd(typeof(TRequest), HeaderLoader.For);
        var header = await loader.LoadAsync(_unitOfWork.Context, request.DocumentId, cancellationToken).ConfigureAwait(false)
                     ?? throw new NotFoundException($"{loader.HeaderName} {request.DocumentId} was not found.");
        var branchId = (request as IBranchCommand)?.BranchId;

        var decision = await _policy.DecideAsync(header, loader.DocumentType, user.UserId, branchId, cancellationToken).ConfigureAwait(false);
        if (!decision.Approved)
        {
            _logger.LogWarning(
                "Approval refused ({Reason}): user {UserId}, {RequestType}, {DocumentType} {DocumentId}, branch {BranchId}, value {Value}, limit {Limit}.",
                decision.Refusal, user.UserId, requestName, loader.DocumentType, header.Id, branchId, header.ApprovalValue, decision.Limit);
            throw new ForbiddenAccessException(decision.Message ?? "This approval was refused.");
        }

        TResponse response;
        using (ApprovalGrant.Begin(header.Id, user.UserId))
        {
            response = await next(request, cancellationToken).ConfigureAwait(false);
        }

        if (response.Succeeded)
            _logger.LogInformation(
                "Approved: user {UserId}, {RequestType}, {DocumentType} {DocumentId}, branch {BranchId}, value {Value}, limit {Limit}.",
                user.UserId, requestName, loader.DocumentType, header.Id, branchId, header.ApprovalValue, decision.Limit);
        return response;
    }

    /// <summary>Loads and locks the header of one approval command type; built once per type.</summary>
    private sealed class HeaderLoader
    {
        public required string HeaderName { get; init; }
        public required string DocumentType { get; init; }
        public required Func<IApplicationDbContext, Guid, CancellationToken, Task<IApprovable?>> LoadAsync { get; init; }

        public static HeaderLoader For(Type requestType)
        {
            var header = ApprovalCommands.HeaderTypeOf(requestType)
                         ?? throw new InvalidOperationException($"{requestType.Name} is IApprovalCommand but not IApprovalCommand<THeader>.");
            var load = typeof(HeaderLoader).GetMethod(nameof(LoadTyped), BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(header);
            return new HeaderLoader
            {
                HeaderName = header.Name,
                DocumentType = ApprovalCommands.DocumentTypeOf(header),
                LoadAsync = (db, id, ct) => (Task<IApprovable?>)load.Invoke(null, [db, id, ct])!
            };
        }

        private static async Task<IApprovable?> LoadTyped<THeader>(IApplicationDbContext db, Guid id, CancellationToken cancellationToken)
            where THeader : class, IApprovable =>
            await db.Set<THeader>().Where(h => h.Id == id).ForUpdate().FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Reflection over approval command and header types (Pass 60).</summary>
public static class ApprovalCommands
{
    /// <summary>The <c>THeader</c> of <c>IApprovalCommand&lt;THeader&gt;</c>, or null.</summary>
    public static Type? HeaderTypeOf(Type requestType) =>
        requestType.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IApprovalCommand<>))
            ?.GetGenericArguments()[0];

    /// <summary>A header type's static <c>ApprovalDocumentType</c>.</summary>
    public static string DocumentTypeOf(Type headerType)
    {
        if (headerType.GetProperty("ApprovalDocumentType", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
                ?.GetValue(null) is string declared)
            return declared;

        var self = typeof(IApprovable<>).MakeGenericType(headerType);
        var map = headerType.GetInterfaceMap(self);
        var index = Array.FindIndex(map.InterfaceMethods, m => m.Name == "get_ApprovalDocumentType");
        return (string)map.TargetMethods[index].Invoke(null, null)!;
    }

    /// <summary>True when the header is branch-scoped, so its approval command must be a branch command (C1).</summary>
    public static bool IsBranchScoped(Type headerType) =>
        typeof(IMustHaveBranch).IsAssignableFrom(headerType)
        || headerType.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IBranchVisibility<>));
}
