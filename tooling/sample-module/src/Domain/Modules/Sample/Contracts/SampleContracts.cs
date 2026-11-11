using CleanArchitecture.Blazor.Domain.Common;
using CleanArchitecture.Blazor.Domain.Common.Modules;

namespace CleanArchitecture.Blazor.Domain.Modules.Sample.Contracts;

/// <summary>Published: a bin was created. Other modules may subscribe to it.</summary>
public sealed class SampleBinCreated(Guid binId, int branchId) : PostingEvent
{
    public Guid BinId { get; } = binId;
    public int BranchId { get; } = branchId;
}

/// <summary>Published shape (amendment E1): other modules implement it on their own types; never resolved.</summary>
[NotAService("A shape other modules implement on their own rows; read by type checks, never resolved from the container.")]
public interface IHasBinLabel
{
    string BinLabel { get; }
}
