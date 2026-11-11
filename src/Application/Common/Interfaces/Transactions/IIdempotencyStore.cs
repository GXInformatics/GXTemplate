namespace CleanArchitecture.Blazor.Application.Common.Interfaces.Transactions;

/// <summary>The outcome of claiming a request id (Pass 58).</summary>
/// <param name="Claimed">True when this transaction now holds the id.</param>
/// <param name="RequestType">When not claimed: the request type the id was first used for.</param>
/// <param name="UserId">When not claimed: the user who first used the id.</param>
/// <param name="Response">When not claimed: the recorded response.</param>
public sealed record IdempotencyClaim(bool Claimed, string? RequestType = null, string? UserId = null, string? Response = null);

/// <summary>
/// The <c>ProcessedRequests</c> table, written inside the unit of work's transaction (Pass 58).
/// PostgreSQL only; other providers throw <see cref="NotSupportedException"/>.
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>
    /// Inserts the id, or - when it exists - waits for any transaction holding it and returns what
    /// was recorded.
    /// </summary>
    Task<IdempotencyClaim> ClaimAsync(IApplicationDbContext db, Guid requestId, string requestType, string? userId,
        CancellationToken cancellationToken);

    /// <summary>Records the response of the request that holds the claim.</summary>
    Task RecordAsync(IApplicationDbContext db, Guid requestId, string response, CancellationToken cancellationToken);
}
