namespace CleanArchitecture.Blazor.Domain.Entities;

/// <summary>
/// The next number for one document type, branch and year (Pass 58). Allocated in raw SQL by
/// <c>DocumentNumberAllocator</c> inside the posting transaction, which keeps the row locked until it
/// commits; a rollback undoes the increment, so numbers have no gaps.
/// </summary>
public class DocumentSequence
{
    /// <summary>A stable code chosen by the module, e.g. <c>GRN</c>.</summary>
    public string DocumentType { get; set; } = string.Empty;

    /// <summary>The branch the numbering belongs to; 0 where a document type is numbered company-wide.</summary>
    public int BranchId { get; set; }

    public int Year { get; set; }

    /// <summary>The number the next allocation returns.</summary>
    public long NextValue { get; set; }
}
