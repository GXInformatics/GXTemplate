using CleanArchitecture.Blazor.Application.Common.Branches;
using CleanArchitecture.Blazor.Application.Features.Branches;
using CleanArchitecture.Blazor.Domain.Modules.Sample.Contracts;
using CleanArchitecture.Blazor.Domain.Modules.Sample.Entities;

namespace CleanArchitecture.Blazor.Application.Modules.Sample.Features.Warehouses;

// Pass 60c (plan §3.8): the conventions in a generated project - a business table, a lookup and a ledger in core with
// plural TBL_ / TBL_LK_ names, snake_case columns, int and bigint keys, an integer enum, and the posting-event id pattern.

public sealed record WarehouseDto(int Id, int BranchId, string Code, string Name, string Type, WarehouseStatus Status, int Movements);

/// <summary>Creates a warehouse in one branch; the module's own handler writes its opening movement in the same transaction.</summary>
[RequestAuthorize(Policy = Permissions.SampleBins.Create)]
public sealed class CreateWarehouseCommand : IRequest<Result<int>>, ITransactionalRequest, IBranchCommand
{
    public int BranchId { get; init; }
    public string Code { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public int WarehouseTypeId { get; init; } = WarehouseType.Main;
}

public sealed class CreateWarehouseCommandValidator : AbstractValidator<CreateWarehouseCommand>
{
    public CreateWarehouseCommandValidator()
    {
        RuleFor(c => c.Code).NotEmpty().MaximumLength(10).Matches("^[A-Z0-9]+$");
        RuleFor(c => c.Name).NotEmpty().MaximumLength(100);
    }
}

public sealed class CreateWarehouseCommandHandler(IApplicationDbContextFactory dbContextFactory)
    : IRequestHandler<CreateWarehouseCommand, Result<int>>
{
    public async ValueTask<Result<int>> Handle(CreateWarehouseCommand request, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateAsync(cancellationToken);
        var warehouse = new Warehouse
        {
            BranchId = request.BranchId, Code = request.Code.Trim(), Name = request.Name.Trim(), WarehouseTypeId = request.WarehouseTypeId
        };
        warehouse.AddDomainEvent(new WarehouseCreated(warehouse)); // the id is 0 here; the event reads it after the save
        db.Set<Warehouse>().Add(warehouse);
        await db.SaveChangesAsync(cancellationToken);
        return Result<int>.Success(warehouse.Id);
    }
}

/// <summary>The warehouses the current user can see (the branch filter applies), with their type and movement count.</summary>
[RequestAuthorize(Policy = Permissions.SampleBins.View)]
public sealed class GetWarehousesQuery : IRequest<IReadOnlyList<WarehouseDto>>;

public sealed class GetWarehousesQueryHandler(IApplicationDbContextFactory dbContextFactory)
    : IRequestHandler<GetWarehousesQuery, IReadOnlyList<WarehouseDto>>
{
    public async ValueTask<IReadOnlyList<WarehouseDto>> Handle(GetWarehousesQuery request, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateAsync(cancellationToken);
        return await db.Set<Warehouse>().AsNoTracking().OrderBy(w => w.Code)
            .Select(w => new WarehouseDto(w.Id, w.BranchId, w.Code, w.Name,
                db.Set<WarehouseType>().Where(t => t.Id == w.WarehouseTypeId).Select(t => t.Name).FirstOrDefault() ?? "",
                w.Status,
                db.Set<WarehouseMovement>().Count(m => m.WarehouseId == w.Id)))
            .ToListAsync(cancellationToken);
    }
}

/// <summary>
/// The module's branch rule (Pass 60c, T9): a code of 1 to 6 upper-case letters and digits starting with a letter - the
/// floor README "Extending branches" says every application rule must accept, so the template's own tests pass under it.
/// </summary>
public sealed class SampleBranchRules : AbstractValidator<AddEditBranchCommand>
{
    public const string Message = "A branch code is 1 to 6 capital letters and digits, starting with a letter.";

    public SampleBranchRules() =>
        RuleFor(c => c.Code).Matches("^[A-Z][A-Z0-9]{0,5}$").WithMessage(Message);
}
