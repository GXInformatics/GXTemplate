using System.Runtime.ExceptionServices;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Application.Common.Transactions;

namespace CleanArchitecture.Blazor.Application.Pipeline;

/// <summary>
/// Runs an <see cref="ITransactionalRequest"/> in one unit of work: one context, one connection,
/// one READ COMMITTED transaction (Pass 58). The README section "Posting transactions" is the
/// author-facing account; the plan is docs/passes/pass58-transactions-plan.md.
/// </summary>
/// <remarks>
/// <para><b>One attempt.</b></para>
/// <list type="number">
/// <item><description>Begin the unit of work (context + transaction, made ambient).</description></item>
/// <item><description>Claim the <see cref="IIdempotentRequest.RequestId"/>; if it was already
/// completed, return the recorded response.</description></item>
/// <item><description>Run the handler. Every <c>IApplicationDbContextFactory.CreateAsync</c> inside
/// returns the shared context.</description></item>
/// <item><description>Save; the events interceptor buffers events instead of publishing them.
/// Dispatch buffered posting events inline, save, repeat until none are left (at most
/// <see cref="MaxPostingRounds"/> rounds).</description></item>
/// <item><description>Record the idempotent response, commit.</description></item>
/// <item><description>End the unit of work - the ambient holder is EMPTIED, so a captured execution
/// context sees no unit of work - and only then publish the ordinary events.</description></item>
/// </list>
/// <para>
/// <b>Failure.</b> An exception, a failure result, a <see cref="PostingRejectedException"/> or a failed
/// nested request rolls everything back; nothing is published or recorded. A rejection returns its
/// message, logged at Information, never retried. A deadlock or serialisation failure retries the
/// whole attempt (at most <see cref="MaxAttempts"/>) with a fresh context and empty buffers.
/// </para>
/// <para>
/// <b>Retry calls <c>next</c> again.</b> Verified against Mediator 3.0.2's generated
/// <c>RequestHandlerWrapper</c>: <c>next</c> is a closure chain over handler and behaviour instances
/// resolved once per send, so invoking it again re-runs the inner behaviours and the SAME handler
/// instance with the SAME request. That is why a transactional handler must hold no state between
/// attempts and must not mutate the request.
/// </para>
/// <para>
/// <b>Nested.</b> With a unit of work already active the request JOINS it: no transaction, commit,
/// event dispatch, retry or claim of its own; any failure marks the unit of work failed, which fails
/// the outer request.
/// </para>
/// </remarks>
public sealed class TransactionBehaviour<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : class, IMessage, ITransactionalRequest
    where TResponse : IResult
{
    public const int MaxAttempts = 3;
    public const int MaxPostingRounds = 10;
    public const string NestedFailureMessage = "A nested operation failed, so nothing was saved.";
    public const string RequestIdRequiredMessage = "The request has no RequestId, so it cannot be protected against being posted twice.";
    public const string RequestIdReusedMessage = "This RequestId was already used for a different request.";

    private readonly IUnitOfWorkManager _unitOfWork;
    private readonly IPostingEventDispatcher _dispatcher;
    private readonly IIdempotencyStore _idempotency;
    private readonly IPublisher _publisher;
    private readonly IUserContextAccessor _userContextAccessor;
    private readonly ILogger<TransactionBehaviour<TRequest, TResponse>> _logger;

    public TransactionBehaviour(
        IUnitOfWorkManager unitOfWork,
        IPostingEventDispatcher dispatcher,
        IIdempotencyStore idempotency,
        IPublisher publisher,
        IUserContextAccessor userContextAccessor,
        ILogger<TransactionBehaviour<TRequest, TResponse>> logger)
    {
        _unitOfWork = unitOfWork;
        _dispatcher = dispatcher;
        _idempotency = idempotency;
        _publisher = publisher;
        _userContextAccessor = userContextAccessor;
        _logger = logger;
    }

    public async ValueTask<TResponse> Handle(
        TRequest request, MessageHandlerDelegate<TRequest, TResponse> next, CancellationToken cancellationToken)
    {
        if (_unitOfWork.Current is { } outer)
        {
            return await JoinAsync(outer, request, next, cancellationToken).ConfigureAwait(false);
        }

        if (request is IIdempotentRequest idempotent)
        {
            if (idempotent.RequestId == Guid.Empty) return ResultFailureFactory.Create<TResponse>(RequestIdRequiredMessage);
            if (!IdempotentResponseCodec.Supports(typeof(TResponse)))
                throw new NotSupportedException(
                    $"{typeof(TRequest).Name} is IIdempotentRequest but returns {typeof(TResponse).Name}; it must return Result or Result<T>.");
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await RunAttemptAsync(request, next, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (attempt < MaxAttempts && _unitOfWork.IsRetryable(exception))
            {
                _logger.LogWarning(exception,
                    "Transient database conflict in {RequestType}, attempt {Attempt} of {MaxAttempts}; retrying the whole unit of work.",
                    typeof(TRequest).Name, attempt, MaxAttempts);
                await Task.Delay(Backoff(attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<TResponse> RunAttemptAsync(
        TRequest request, MessageHandlerDelegate<TRequest, TResponse> next, CancellationToken cancellationToken)
    {
        IReadOnlyList<DomainEvent> afterCommit;
        TResponse response;

        await using (var session = await _unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false))
        {
            var idempotent = request as IIdempotentRequest;
            if (idempotent is not null)
            {
                var userId = _userContextAccessor.Current?.UserId;
                var requestType = typeof(TRequest).FullName!;
                var claim = await _idempotency.ClaimAsync(session.Context, idempotent.RequestId, requestType, userId, cancellationToken)
                    .ConfigureAwait(false);
                if (!claim.Claimed)
                {
                    if (claim.RequestType != requestType || claim.UserId != userId)
                    {
                        _logger.LogWarning("RequestId {RequestId} was reused by {RequestType} for user {UserId}; refused.",
                            idempotent.RequestId, typeof(TRequest).Name, userId);
                        return ResultFailureFactory.Create<TResponse>(RequestIdReusedMessage);
                    }

                    _logger.LogInformation("RequestId {RequestId} for {RequestType} was already completed; returning its recorded response.",
                        idempotent.RequestId, typeof(TRequest).Name);
                    return IdempotentResponseCodec.Deserialize<TResponse>(claim.Response);
                }
            }

            try
            {
                response = await next(request, cancellationToken).ConfigureAwait(false);
                if (!session.HasFailed && response.Succeeded)
                {
                    await DrainPostingEventsAsync(session, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (PostingRejectedException rejection)
            {
                return Rejected(rejection);
            }

            if (session.HasFailed) return FailedByNestedRequest(session, response);
            if (!response.Succeeded) return response;

            if (idempotent is not null)
            {
                await _idempotency.RecordAsync(session.Context, idempotent.RequestId,
                    IdempotentResponseCodec.Serialize(response), cancellationToken).ConfigureAwait(false);
            }

            await session.CommitAsync(cancellationToken).ConfigureAwait(false);
            afterCommit = session.TakeAfterCommitEvents();
        }

        // The session is disposed and the ambient holder emptied: a notification handler that
        // captures this execution context sees no unit of work, never the disposed context.
        //
        // The transaction has COMMITTED. A publish failure (the channel publisher only queues, so in
        // practice an event type Mediator does not know) must not turn a committed posting into a
        // failure the user would retry; it is logged at Error and the committed response returned.
        foreach (var domainEvent in afterCommit)
        {
            try
            {
                await _publisher.Publish(domainEvent, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception,
                    "{RequestType} committed, but publishing its {EventType} afterwards failed.",
                    typeof(TRequest).Name, domainEvent.GetType().Name);
            }
        }

        return response;
    }

    private async Task DrainPostingEventsAsync(IUnitOfWorkSession session, CancellationToken cancellationToken)
    {
        await session.Context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        for (var round = 1; round <= MaxPostingRounds; round++)
        {
            var postingEvents = session.TakePostingEvents();
            if (postingEvents.Count == 0) return;

            foreach (var postingEvent in postingEvents)
            {
                // Pass 60c (decision Q-J): the save above has assigned every new row's id; an event still holding 0
                // captured it too early, and its handlers would write rows pointing at nothing.
                PostingEventIdCheck.Assert(postingEvent);
                await _dispatcher.DispatchAsync(postingEvent, cancellationToken).ConfigureAwait(false);
                if (session.HasFailed) return;
            }

            await session.Context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        if (session.TakePostingEvents().Count > 0)
        {
            throw new InvalidOperationException(
                $"{typeof(TRequest).Name}: posting events were still being raised after {MaxPostingRounds} rounds. " +
                "Two handlers are probably raising each other's events.");
        }
    }

    private async Task<TResponse> JoinAsync(
        IUnitOfWorkSession outer, TRequest request, MessageHandlerDelegate<TRequest, TResponse> next, CancellationToken cancellationToken)
    {
        try
        {
            var response = await next(request, cancellationToken).ConfigureAwait(false);
            if (!response.Succeeded) outer.Fail(response.Errors, cause: null);
            return response;
        }
        catch (PostingRejectedException rejection)
        {
            outer.Fail([rejection.Message], cause: null);
            return Rejected(rejection);
        }
        catch (Exception exception)
        {
            outer.Fail([], exception);
            throw;
        }
    }

    private TResponse FailedByNestedRequest(IUnitOfWorkSession session, TResponse response)
    {
        // A nested deadlock reached us as a failed result (its ResultExceptionBehavior converted it);
        // the cause is kept so the outer attempt can still be retried.
        if (session.FailureCause is { } cause && _unitOfWork.IsRetryable(cause))
        {
            ExceptionDispatchInfo.Capture(cause).Throw();
        }

        _logger.LogInformation("{RequestType} rolled back because a nested request failed: {Errors}",
            typeof(TRequest).Name, string.Join("; ", session.FailureErrors));

        if (!response.Succeeded) return response;
        return session.FailureErrors.Count > 0
            ? ResultFailureFactory.Create<TResponse>(session.FailureErrors.ToArray())
            : ResultFailureFactory.Create<TResponse>(NestedFailureMessage);
    }

    private TResponse Rejected(PostingRejectedException rejection)
    {
        _logger.LogInformation("{RequestType} was rejected and rolled back: {Reason} (code {ErrorCode})",
            typeof(TRequest).Name, rejection.Message, rejection.ErrorCode ?? "none");
        return ResultFailureFactory.ForRejection<TResponse>(rejection);
    }

    private static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromMilliseconds(50 * attempt + Random.Shared.Next(0, 100));
}
