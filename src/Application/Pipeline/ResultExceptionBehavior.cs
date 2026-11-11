using System.Diagnostics;

namespace CleanArchitecture.Blazor.Application.Pipeline;

public sealed class ResultExceptionBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
    where TResponse : IResult
{
    private readonly ILogger<ResultExceptionBehavior<TRequest, TResponse>> _logger;

    public ResultExceptionBehavior(ILogger<ResultExceptionBehavior<TRequest, TResponse>> logger)
    {
        _logger = logger;
    }

    public async ValueTask<TResponse> Handle(
        TRequest request,
        MessageHandlerDelegate<TRequest, TResponse> next,
        CancellationToken cancellationToken)
    {
        try
        {
            return await next(request, cancellationToken).ConfigureAwait(false);
        }
        catch (ValidationException)
        {
            throw;
        }
        catch (ForbiddenAccessException)
        {
            // A denial must never be downgraded into a failed Result that a call site can ignore.
            // AuthorizationBehaviour runs outside this behaviour so its own denials never arrive here;
            // this arm is for denials thrown deeper, by a handler doing its own ownership check.
            throw;
        }
        catch (NotFoundException exception)
        {
            _logger.LogError(
                exception,
                "NotFoundException occurred for request {RequestType}: {ErrorMessage}",
                typeof(TRequest).Name,
                exception.Message);

            return ResultFailureFactory.Create<TResponse>(exception.Message);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            // Pass 58: its own message, because the user's remedy is specific - reload and redo.
            _logger.LogError(exception, "Concurrency conflict saving {RequestType}", typeof(TRequest).Name);
            return ResultFailureFactory.Create<TResponse>(ConcurrencyConflictMessage);
        }
        catch (DbUpdateException exception)
        {
            // Pass 58: logged. DbExceptionHandler turns it into a user-facing message and has no
            // logger, so until now a failed save left no trace anywhere.
            _logger.LogError(exception, "Database update failed for {RequestType}", typeof(TRequest).Name);

            var state = new RequestExceptionHandlerState<TResponse>();
            await new DbExceptionHandler<TRequest, TResponse, DbUpdateException>()
                .Handle(request, exception, state, cancellationToken)
                .ConfigureAwait(false);

            return state.Response;
        }
        catch (Exception exception)
        {
            // Pass 58: the user gets a fixed message and a reference that finds this log entry, not
            // the exception's own text, which can carry SQL, table names or data.
            var reference = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
            _logger.LogError(exception, "Unhandled exception occurred for request {RequestType} (reference {ErrorReference})",
                typeof(TRequest).Name, reference);
            return ResultFailureFactory.Create<TResponse>(UnexpectedErrorMessage(reference));
        }
    }

    public const string ConcurrencyConflictMessage = CleanArchitecture.Blazor.Application.Common.Concurrency.ConcurrencyConflict.Message;

    public static string UnexpectedErrorMessage(string reference) =>
        $"An unexpected error occurred. Reference: {reference}.";
}
