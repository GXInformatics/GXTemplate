namespace CleanArchitecture.Blazor.Application.Common.ExceptionHandlers;

/// <summary>
/// A business rejection of a posting, such as "Insufficient stock for item X" (Pass 58).
/// </summary>
/// <remarks>
/// Thrown by a transactional command handler or by a posting event handler. <c>TransactionBehaviour</c>
/// rolls the whole unit of work back, discards its events, does not retry, records no idempotency
/// response, logs it at Information, and returns a failure result carrying <see cref="Exception.Message"/>
/// - so the message is what the user reads. Any other exception is a fault and reaches the user as a
/// fixed message with a reference id.
/// <para>
/// <see cref="ErrorCode"/> is logged with the rejection. <c>Result</c> has no code field, so it is not
/// returned to the caller.
/// </para>
/// </remarks>
public sealed class PostingRejectedException : Exception
{
    public PostingRejectedException(string message, string? errorCode = null)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    /// <summary>An optional machine-readable code, e.g. <c>INSUFFICIENT_STOCK</c>.</summary>
    public string? ErrorCode { get; }
}
