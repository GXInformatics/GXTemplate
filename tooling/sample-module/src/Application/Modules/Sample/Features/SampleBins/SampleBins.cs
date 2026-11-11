using CleanArchitecture.Blazor.Application.Common.Branches;
using CleanArchitecture.Blazor.Domain.Modules.Sample.Contracts;
using CleanArchitecture.Blazor.Domain.Modules.Sample.Entities;

namespace CleanArchitecture.Blazor.Application.Modules.Sample.Features.SampleBins;

public sealed record SampleBinDto(Guid Id, int BranchId, string Name, bool Announced);

/// <summary>Creates a bin in one branch, raising the module's published posting event.</summary>
[RequestAuthorize(Policy = Permissions.SampleBins.Create)]
public sealed class CreateSampleBinCommand : IRequest<Result<Guid>>, ITransactionalRequest, IBranchCommand
{
    public int BranchId { get; init; }
    public string Name { get; init; } = string.Empty;
}

public sealed class CreateSampleBinCommandValidator : AbstractValidator<CreateSampleBinCommand>
{
    public CreateSampleBinCommandValidator() => RuleFor(c => c.Name).NotEmpty().MaximumLength(100);
}

public sealed class CreateSampleBinCommandHandler(IApplicationDbContextFactory dbContextFactory)
    : IRequestHandler<CreateSampleBinCommand, Result<Guid>>
{
    public async ValueTask<Result<Guid>> Handle(CreateSampleBinCommand request, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateAsync(cancellationToken);
        var bin = new SampleBin { BranchId = request.BranchId, Name = request.Name.Trim() };
        bin.AddDomainEvent(new SampleBinCreated(bin.Id, bin.BranchId));
        db.Set<SampleBin>().Add(bin);
        await db.SaveChangesAsync(cancellationToken);
        return Result<Guid>.Success(bin.Id);
    }
}

/// <summary>The bins the current user can see (the branch filter applies).</summary>
[RequestAuthorize(Policy = Permissions.SampleBins.View)]
public sealed class GetSampleBinsQuery : IRequest<IReadOnlyList<SampleBinDto>>;

public sealed class GetSampleBinsQueryHandler(IApplicationDbContextFactory dbContextFactory)
    : IRequestHandler<GetSampleBinsQuery, IReadOnlyList<SampleBinDto>>
{
    public async ValueTask<IReadOnlyList<SampleBinDto>> Handle(GetSampleBinsQuery request, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateAsync(cancellationToken);
        return await db.Set<SampleBin>().AsNoTracking().OrderBy(b => b.Name)
            .Select(b => new SampleBinDto(b.Id, b.BranchId, b.Name, b.Announced)).ToListAsync(cancellationToken);
    }
}

/// <summary>
/// The module's tightening of the branch rules (item 4c): codes starting with ZZ are reserved. It runs beside the
/// template's validator; the template's file is not edited.
/// </summary>
public sealed class ReservedBranchCodeValidator : AbstractValidator<CleanArchitecture.Blazor.Application.Features.Branches.AddEditBranchCommand>
{
    public const string Message = "Branch codes starting with ZZ are reserved by the sample module.";

    public ReservedBranchCodeValidator() =>
        RuleFor(c => c.Code).Must(code => !code.Trim().StartsWith("ZZ", StringComparison.OrdinalIgnoreCase)).WithMessage(Message);
}
