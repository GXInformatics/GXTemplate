// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel.DataAnnotations;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Application.Features.Documents.Caching;
using CleanArchitecture.Blazor.Application.Features.Documents.Specifications;

namespace CleanArchitecture.Blazor.Application.Features.Documents.Commands.AddEdit;

[RequestAuthorize(Policy = Permissions.Documents.Create)]
[RequestAuthorize(Policy = Permissions.Documents.Edit)]
public class AddEditDocumentCommand : ICacheInvalidatorRequest<Result<int>>
{
    [Display(Name = "Id")] public int Id { get; set; }
    [Display(Name ="Title")] public string? Title { get; set; }
    [Display(Name = "Description")] public string? Description { get; set; }
    [Display(Name = "Is Public")] public bool IsPublic { get; set; }
    [Display(Name = "Storage Key")] public string? StorageKey { get; set; }
    [Display(Name = "URL")] public string? PublicUrl { get; set; }
    [Display(Name = "Document Type")] public DocumentType DocumentType { get; set; } = DocumentType.Document;
    [Display(Name = "Tenant Id")] public string? TenantId { get; set; }
    [Display(Name = "Tenant Name")] public string? TenantName { get; set; }
    public FileUploadRequest? UploadRequest { get; set; }
    public IEnumerable<string>? Tags => DocumentCacheKey.Tags;}

public class AddEditDocumentCommandHandler : IRequestHandler<AddEditDocumentCommand, Result<int>>
{
    private readonly IApplicationDbContextFactory _dbContextFactory;
    private readonly IObjectMapper _objectMapper;
    private readonly IUserContextAccessor _userContextAccessor;
    private readonly IStringLocalizer<AddEditDocumentCommandHandler> _localizer;

    public AddEditDocumentCommandHandler(
        IApplicationDbContextFactory dbContextFactory,
        IObjectMapper objectMapper,
        IUserContextAccessor userContextAccessor,
        IStringLocalizer<AddEditDocumentCommandHandler> localizer
    )
    {
        _dbContextFactory = dbContextFactory;
        _objectMapper = objectMapper;
        _userContextAccessor = userContextAccessor;
        _localizer = localizer;
    }

    public async ValueTask<Result<int>> Handle(AddEditDocumentCommand request, CancellationToken cancellationToken)
    {
        // Fail closed, as GetFileStreamQueryHandler does: no ambient principal, nothing to authorize.
        var currentUser = _userContextAccessor.Current;
        if (currentUser is null) return await Result<int>.FailureAsync(_localizer["Document Not Found!"]);

        await using var db = await _dbContextFactory.CreateAsync(cancellationToken);
        Document document;
        if (request.Id > 0)
        {
            // FindAsync used to resolve this by primary key alone, so holding Permissions.Documents.Edit
            // was enough to edit any document in any tenant. The visibility rule is applied first now,
            // and a document that exists but is not visible reports the SAME "not found" as one that
            // does not exist - so the response cannot be used to discover which ids exist elsewhere.
            var existingDocument = await db.Documents
                .Where(x => x.Id == request.Id)
                .Where(VisibleDocumentSpecification.IsVisibleTo(currentUser.UserId, currentUser.TenantId))
                .FirstOrDefaultAsync(cancellationToken);

            if (existingDocument == null) return await Result<int>.FailureAsync(_localizer["Document Not Found!"]);

            // The tenant a document belongs to is not editable, by anyone, through this command.
            //
            // The command carries a TenantId because the DTO it is mapped from does, and the mapper
            // copies it by name - so a request could re-parent any document it could reach into any
            // tenant it named, silently, as a side effect of an ordinary edit. The stored value is
            // captured before the map and put back after it, which keeps the guard next to the line
            // that would otherwise break it.
            var tenantId = existingDocument.TenantId;
            document = _objectMapper.Map(request, existingDocument);
            document.TenantId = tenantId;
        }
        else
        {
            document = _objectMapper.Map<Document>(request);

            // Cleared so the AuditableEntityInterceptor stamps it from the ambient principal - it
            // only fills a TenantId that is null, so a client-supplied one would win. A new document
            // belongs to the tenant of whoever created it, which is not a claim the caller gets to
            // make about itself. UploadDocumentCommand already relies on the same mechanism.
            document.TenantId = null;

            db.Documents.Add(document);
        }
        await db.SaveChangesAsync(cancellationToken);
        return await Result<int>.SuccessAsync(document.Id);
    }
}
