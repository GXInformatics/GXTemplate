namespace CleanArchitecture.Blazor.Domain.Entities;

/// <summary>
/// One idempotent request that committed (Pass 58). Its row is inserted when the request claims its
/// id and is part of the request's transaction, so a request that rolls back leaves no row.
/// </summary>
/// <remarks>
/// Infrastructure, not business data: it is neither an <c>IBusinessEntity</c> (so it stays in the
/// default schema) nor tenant-marked. Written only in raw SQL by <c>IdempotencyStore</c>.
/// </remarks>
public class ProcessedRequest
{
    public Guid RequestId { get; set; }

    /// <summary>The request's full type name; a reused id with another type is refused.</summary>
    public string RequestType { get; set; } = string.Empty;

    /// <summary>The user who sent it; a reused id from another user is refused.</summary>
    public string? UserId { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>The successful response's <c>Data</c> as JSON, written just before the commit.</summary>
    public string? Response { get; set; }
}
