using CleanArchitecture.Blazor.Domain.Common;
using CleanArchitecture.Blazor.Domain.Common.Modules;
using CleanArchitecture.Blazor.Domain.Modules.Sample.Entities;

namespace CleanArchitecture.Blazor.Domain.Modules.Sample.Contracts;

/// <summary>
/// Published: a bin was created. Other modules may subscribe to it. It keeps the bin and reads its id when asked: the
/// event is raised before the save, when an identity key is still 0, and handled after it (Pass 60c, owner decision D6;
/// the BranchCreatedEvent pattern). Until 1.7.1 it captured a Guid in its constructor.
/// </summary>
public sealed class SampleBinCreated(SampleBin bin) : PostingEvent
{
    private readonly SampleBin _bin = bin;

    public int BinId => _bin.Id;
    public int BranchId { get; } = bin.BranchId;
}

/// <summary>Published: a warehouse was created (Pass 60c). The same pattern: the id is read at dispatch, after the save.</summary>
public sealed class WarehouseCreated(Warehouse warehouse) : PostingEvent
{
    private readonly Warehouse _warehouse = warehouse;

    public int WarehouseId => _warehouse.Id;
    public int BranchId { get; } = warehouse.BranchId;
}

/// <summary>Published shape (amendment E1): other modules implement it on their own types; never resolved.</summary>
[NotAService("A shape other modules implement on their own rows; read by type checks, never resolved from the container.")]
public interface IHasBinLabel
{
    string BinLabel { get; }
}
