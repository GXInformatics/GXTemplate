// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Application.Features.Documents.Caching;
using CleanArchitecture.Blazor.Application.Features.Documents.Specifications;

namespace CleanArchitecture.Blazor.Application.Features.Documents.Commands.Delete;

[RequestAuthorize(Policy = Permissions.Documents.Delete)]
public class DeleteDocumentCommand : ICacheInvalidatorRequest<Result>
{
    public DeleteDocumentCommand(int[] id)
    {
        Id = id;
    }

    public int[] Id { get; set; }
    public IEnumerable<string>? Tags => DocumentCacheKey.Tags;
}

public class DeleteDocumentCommandHandler : IRequestHandler<DeleteDocumentCommand, Result>

{
    private readonly IApplicationDbContextFactory _dbContextFactory;
    private readonly IUserContextAccessor _userContextAccessor;

    public DeleteDocumentCommandHandler(
        IApplicationDbContextFactory dbContextFactory,
        IUserContextAccessor userContextAccessor
    )
    {
        _dbContextFactory = dbContextFactory;
        _userContextAccessor = userContextAccessor;
    }

    public async ValueTask<Result> Handle(DeleteDocumentCommand request, CancellationToken cancellationToken)
    {
        // Fail closed: with no ambient principal there is nobody to authorize against, so there is
        // nothing this call is allowed to delete. Matches GetFileStreamQueryHandler.
        var currentUser = _userContextAccessor.Current;
        if (currentUser is null) return await Result.SuccessAsync();

        await using var db = await _dbContextFactory.CreateAsync(cancellationToken);

        // The ids the caller asked for, INTERSECTED with the documents they are allowed to see.
        // Holding Permissions.Documents.Delete used to be the whole check, so any id from any tenant
        // was deletable by anyone who could guess it - and DocumentDeletedEvent removes the stored
        // object too, so the blob went with it.
        //
        // A document that exists but is not visible is treated exactly like one that does not exist:
        // nothing is deleted and the result is success, which is already what deleting an unknown id
        // does. Reporting a refusal instead would answer "does this id exist in some other tenant?"
        // for anyone who cared to ask - the same id-enumeration reasoning GetFileStreamQueryHandler
        // documents for the download path.
        var items = await db.Documents
            .Where(x => request.Id.Contains(x.Id))
            .Where(VisibleDocumentSpecification.IsVisibleTo(currentUser.UserId, currentUser.TenantId))
            .ToListAsync(cancellationToken);

        foreach (var item in items)
        {
            item.AddDomainEvent(new DocumentDeletedEvent(item));
            db.Documents.Remove(item);
        }

        await db.SaveChangesAsync(cancellationToken);
        return await Result.SuccessAsync();
    }
}
