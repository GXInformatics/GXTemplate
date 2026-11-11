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
