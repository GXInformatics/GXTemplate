using CleanArchitecture.Blazor.Domain.Modules.Sample.Contracts;
using CleanArchitecture.Blazor.Domain.Modules.Sample.Entities;

namespace CleanArchitecture.Blazor.Application.Modules.Sample.EventHandlers;

/// <summary>Every new branch gets a default bin, in the transaction that created the branch (item 4b's grant).</summary>
public sealed class OpenBinForNewBranch(IApplicationDbContextFactory dbContextFactory) : IPostingEventHandler<BranchCreatedEvent>
{
    public const string DefaultBinName = "Default bin";

    public async ValueTask Handle(BranchCreatedEvent postingEvent, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateAsync(cancellationToken);
        db.Set<SampleBin>().Add(new SampleBin { BranchId = postingEvent.BranchId, Name = DefaultBinName });
        await db.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>The module's own reaction to its published event, in the same transaction.</summary>
public sealed class AnnounceSampleBin(IApplicationDbContextFactory dbContextFactory) : IPostingEventHandler<SampleBinCreated>
{
    public async ValueTask Handle(SampleBinCreated postingEvent, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateAsync(cancellationToken);
        var bin = await db.Set<SampleBin>().SingleAsync(b => b.Id == postingEvent.BinId, cancellationToken);
        bin.Announced = true;
        await db.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>
/// A new warehouse's opening movement, in the transaction that created it (Pass 60c). It writes a row pointing at the
/// warehouse's id, which exists only because the event read it after the save.
/// </summary>
public sealed class OpenWarehouseLedger(IApplicationDbContextFactory dbContextFactory) : IPostingEventHandler<WarehouseCreated>
{
    public const string OpeningNote = "Opened";

    public async ValueTask Handle(WarehouseCreated postingEvent, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateAsync(cancellationToken);
        db.Set<WarehouseMovement>().Add(new WarehouseMovement { WarehouseId = postingEvent.WarehouseId, Quantity = 0m, Note = OpeningNote });
        await db.SaveChangesAsync(cancellationToken);
    }
}
