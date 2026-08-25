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
        catch (DbUpdateException exception)
        {
            var state = new RequestExceptionHandlerState<TResponse>();
            await new DbExceptionHandler<TRequest, TResponse, DbUpdateException>()
                .Handle(request, exception, state, cancellationToken)
                .ConfigureAwait(false);

            return state.Response;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Unhandled exception occurred for request {RequestType}", typeof(TRequest).Name);
            return ResultFailureFactory.Create<TResponse>($"An unexpected error occurred: {exception.Message}");
        }
    }
}
