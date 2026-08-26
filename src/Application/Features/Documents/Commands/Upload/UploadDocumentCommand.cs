// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using CleanArchitecture.Blazor.Application.Features.Documents.Caching;

namespace CleanArchitecture.Blazor.Application.Features.Documents.Commands.Upload;

[RequestAuthorize(Policy = Permissions.Documents.Create)]
public class UploadDocumentCommand : ICacheInvalidatorRequest<Result<int>>
{
    public UploadDocumentCommand(List<FileUploadRequest> uploadRequests)
    {
        UploadRequests = uploadRequests;
    }
    public List<FileUploadRequest> UploadRequests { get; set; }
    public IEnumerable<string>? Tags => DocumentCacheKey.Tags;
}

public class UploadDocumentCommandHandler : IRequestHandler<UploadDocumentCommand, Result<int>>
{
  
    private readonly IApplicationDbContextFactory _dbContextFactory;
    private readonly IFileStorage _fileStorage;

    public UploadDocumentCommandHandler(
       IApplicationDbContextFactory dbContextFactory,
        IFileStorage fileStorage
    )
    {
        _dbContextFactory = dbContextFactory;
        _fileStorage = fileStorage;
    }

    public async ValueTask<Result<int>> Handle(UploadDocumentCommand request, CancellationToken cancellationToken)
    {
        var list = new List<Document>();
        foreach (var uploadRequest in request.UploadRequests)
        {
            var fileName = uploadRequest.FileName;
            var uploadResult = await _fileStorage.SaveAsync(uploadRequest, cancellationToken);
            if (!uploadResult.Succeeded)
            {
                return await Result<int>.FailureAsync(uploadResult.ErrorMessage ?? "Failed to upload document");
            }
            // The RETURNED key, not the one the request implied: an overwrite-averse save may have
            // derived a different one, and the derived key is where the bytes actually are.
            var document = new Document
            {
                Title = fileName,
                StorageKey = uploadResult.Data!.StorageKey,
                PublicUrl = uploadResult.Data.PublicUrl,
                IsPublic = true,
                DocumentType = DocumentType.Image
            };
            document.AddDomainEvent(new DocumentCreatedEvent(document));
            list.Add(document);
        }

        if (!list.Any()) return await Result<int>.SuccessAsync(0);
        await using var db = await _dbContextFactory.CreateAsync(cancellationToken);
        await db.Documents.AddRangeAsync(list, cancellationToken);
        var result = await db.SaveChangesAsync(cancellationToken);
        return await Result<int>.SuccessAsync(result);
    }
}
