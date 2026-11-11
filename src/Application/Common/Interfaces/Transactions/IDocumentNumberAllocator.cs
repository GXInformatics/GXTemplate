namespace CleanArchitecture.Blazor.Application.Common.Interfaces.Transactions;

/// <summary>
/// Gap-free document numbers per document type, branch and year (Pass 58).
/// </summary>
/// <remarks>
/// The counter row stays locked until the unit of work commits, and a rollback undoes the increment,
/// so a number is never skipped and never issued twice. Postings of one type, branch and year are
/// therefore serialised. Allocation is lock 2 in the global lock order: after the document header,
/// before item cost and stock balance rows. Formatting the number belongs to the module.
/// <para>Throws unless a unit of work is active. PostgreSQL only.</para>
/// </remarks>
public interface IDocumentNumberAllocator
{
    /// <summary>The next number, starting at 1.</summary>
    ValueTask<long> NextAsync(string documentType, int branchId, int year, CancellationToken cancellationToken);
}
