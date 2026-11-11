using CleanArchitecture.Blazor.Application.Common.Interfaces.Transactions;

namespace CleanArchitecture.Blazor.Infrastructure.Persistence.Transactions;

/// <summary>
/// <c>ProcessedRequests</c> in raw SQL, inside the unit of work's transaction (Pass 58).
/// </summary>
/// <remarks>
/// <b>The claim is one <c>INSERT … ON CONFLICT DO NOTHING</c>.</b> When another transaction has
/// inserted the same id and not finished, PostgreSQL makes this insert WAIT for it: if it commits,
/// the insert does nothing and the recorded response is read back (a fresh statement, so READ
/// COMMITTED sees it); if it rolls back, the insert succeeds and this request runs. A retry sent while
/// the first attempt is still posting therefore cannot post twice.
/// </remarks>
internal sealed class IdempotencyStore(IDateTime dateTime) : IIdempotencyStore
{
    public async Task<IdempotencyClaim> ClaimAsync(IApplicationDbContext db, Guid requestId, string requestType, string? userId,
        CancellationToken cancellationToken)
    {
        var context = PostingSql.AsDbContext(db);
        PostingSql.RequirePostgreSql(context, "Idempotency");
        var (table, column) = PostingSql.Names<ProcessedRequest>(context);

        var insert =
            $"INSERT INTO {table} ({column(nameof(ProcessedRequest.RequestId))}, {column(nameof(ProcessedRequest.RequestType))}, " +
            $"{column(nameof(ProcessedRequest.UserId))}, {column(nameof(ProcessedRequest.CreatedAt))}) " +
            $"VALUES (@id, @type, @user, @at) ON CONFLICT ({column(nameof(ProcessedRequest.RequestId))}) DO NOTHING";
        await using (var command = await PostingSql.CommandAsync(context, insert, cancellationToken))
        {
            PostingSql.AddParameter(command, "id", requestId);
            PostingSql.AddParameter(command, "type", requestType);
            PostingSql.AddParameter(command, "user", userId);
            PostingSql.AddParameter(command, "at", DateTime.SpecifyKind(dateTime.UtcNow, DateTimeKind.Utc));
            if (await command.ExecuteNonQueryAsync(cancellationToken) == 1) return new IdempotencyClaim(true);
        }

        var select =
            $"SELECT {column(nameof(ProcessedRequest.RequestType))}, {column(nameof(ProcessedRequest.UserId))}, " +
            $"{column(nameof(ProcessedRequest.Response))} FROM {table} WHERE {column(nameof(ProcessedRequest.RequestId))} = @id";
        await using (var command = await PostingSql.CommandAsync(context, select, cancellationToken))
        {
            PostingSql.AddParameter(command, "id", requestId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException($"RequestId {requestId} conflicted on insert but cannot be read back.");

            return new IdempotencyClaim(
                false,
                reader.GetString(0),
                await reader.IsDBNullAsync(1, cancellationToken) ? null : reader.GetString(1),
                await reader.IsDBNullAsync(2, cancellationToken) ? null : reader.GetString(2));
        }
    }

    public async Task RecordAsync(IApplicationDbContext db, Guid requestId, string response, CancellationToken cancellationToken)
    {
        var context = PostingSql.AsDbContext(db);
        PostingSql.RequirePostgreSql(context, "Idempotency");
        var (table, column) = PostingSql.Names<ProcessedRequest>(context);

        var update = $"UPDATE {table} SET {column(nameof(ProcessedRequest.Response))} = @response " +
                     $"WHERE {column(nameof(ProcessedRequest.RequestId))} = @id";
        await using var command = await PostingSql.CommandAsync(context, update, cancellationToken);
        PostingSql.AddParameter(command, "id", requestId);
        PostingSql.AddParameter(command, "response", response);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException($"RequestId {requestId} was not claimed by this unit of work.");
    }
}
