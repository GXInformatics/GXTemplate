using CleanArchitecture.Blazor.Application.Common.Interfaces.Transactions;

namespace CleanArchitecture.Blazor.Infrastructure.Persistence.Transactions;

/// <summary>
/// Gap-free numbers from <c>DocumentSequences</c> (Pass 58). See <see cref="IDocumentNumberAllocator"/>.
/// </summary>
/// <remarks>
/// One upsert: the first allocation for a (type, branch, year) inserts <c>NextValue = 2</c> and returns
/// 1; later ones increment and return the previous value. Either way the row is now locked by this
/// transaction until it ends, so concurrent postings queue on it, and a rollback undoes the increment.
/// </remarks>
internal sealed class DocumentNumberAllocator(IUnitOfWork unitOfWork) : IDocumentNumberAllocator
{
    public async ValueTask<long> NextAsync(string documentType, int branchId, int year, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentType);
        if (!unitOfWork.IsActive)
            throw new InvalidOperationException(
                "Document numbers are allocated inside the posting transaction only. Send the command as an ITransactionalRequest.");

        var context = PostingSql.AsDbContext(unitOfWork.Context);
        PostingSql.RequirePostgreSql(context, "Document numbering");
        var (table, column) = PostingSql.Names<DocumentSequence>(context);
        var type = column(nameof(DocumentSequence.DocumentType));
        var branch = column(nameof(DocumentSequence.BranchId));
        var yearColumn = column(nameof(DocumentSequence.Year));
        var next = column(nameof(DocumentSequence.NextValue));

        var sql = $"INSERT INTO {table} AS s ({type}, {branch}, {yearColumn}, {next}) VALUES (@type, @branch, @year, 2) " +
                  $"ON CONFLICT ({type}, {branch}, {yearColumn}) DO UPDATE SET {next} = s.{next} + 1 " +
                  $"RETURNING s.{next} - 1";
        await using var command = await PostingSql.CommandAsync(context, sql, cancellationToken);
        PostingSql.AddParameter(command, "type", documentType);
        PostingSql.AddParameter(command, "branch", branchId);
        PostingSql.AddParameter(command, "year", year);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }
}
