namespace CleanArchitecture.Blazor.Application.Pipeline;

public sealed class ValidationBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : class, IMessage
{
    private readonly IReadOnlyCollection<IValidator<TRequest>> _validators;
    private readonly IUnitOfWork? _unitOfWork;

    /// <param name="validators">Every validator registered for the request.</param>
    /// <param name="unitOfWork">
    /// Optional (Pass 58) so a test can build the behaviour from its validators alone.
    /// </param>
    public ValidationBehavior(IEnumerable<IValidator<TRequest>> validators, IUnitOfWork? unitOfWork = null)
    {
        if (validators is null) throw new ArgumentNullException(nameof(validators));
        _validators = validators.ToList();
        _unitOfWork = unitOfWork;
    }

    public async ValueTask<TResponse> Handle(
        TRequest request,
        MessageHandlerDelegate<TRequest, TResponse> next,
        CancellationToken cancellationToken)
    {
        if (_validators.Any())
        {
            var failures = _unitOfWork?.IsActive == true
                ? await ValidateOneAtATimeAsync(request, cancellationToken).ConfigureAwait(false)
                : await _validators.ValidateAsync(request, cancellationToken).ConfigureAwait(false);

            if (failures.Any())
                throw new ValidationException(string.Join(", ", failures.Select(x => x.ErrorMessage)));
        }

        return await next(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Inside a unit of work (a request sent from a transactional handler) every validator that reads
    /// the database is handed the ONE shared context, and EF throws on concurrent use of a context -
    /// so the validators run in turn instead of through <c>Task.WhenAll</c> (Pass 58).
    /// </summary>
    private async Task<List<FluentValidation.Results.ValidationFailure>> ValidateOneAtATimeAsync(
        TRequest request, CancellationToken cancellationToken)
    {
        var failures = new List<FluentValidation.Results.ValidationFailure>();
        foreach (var validator in _validators)
        {
            var result = await validator.ValidateAsync(new ValidationContext<TRequest>(request), cancellationToken)
                .ConfigureAwait(false);
            failures.AddRange(result.Errors.Where(f => f != null));
        }
        return failures;
    }
}
