// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Ardalis.Specification.EntityFrameworkCore;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Application.Features.Documents.Caching;

namespace CleanArchitecture.Blazor.Application.Features.Documents.Queries.GetFileStream;

public class GetFileStreamQuery : ICacheableRequest<(string, byte[])>
{
    public GetFileStreamQuery(int id, string? userId = null, string? tenantId = null)
    {
        Id = id;
        UserId = userId;
        TenantId = tenantId;
    }
    public int Id { get; set; }

    /// <summary>
    /// Id of the requesting user. Part of <see cref="CacheKey"/> so that two principals can never
    /// share a cached entry, and checked against the ambient user context by the handler.
    /// </summary>
    public string? UserId { get; set; }

    /// <summary>
    /// Tenant of the requesting user. Part of <see cref="CacheKey"/> for the same reason as
    /// <see cref="UserId"/>.
    /// </summary>
    public string? TenantId { get; set; }

    public string CacheKey => DocumentCacheKey.GetStreamByIdKey(Id, UserId, TenantId);
    public IEnumerable<string>? Tags => DocumentCacheKey.Tags;
}

public class GetFileStreamQueryHandler : IRequestHandler<GetFileStreamQuery, (string, byte[])>
{
    private readonly IApplicationDbContextFactory _dbContextFactory;
    private readonly IUserContextAccessor _userContextAccessor;


    public GetFileStreamQueryHandler(
        IApplicationDbContextFactory dbContextFactory,
        IUserContextAccessor userContextAccessor
    )
    {
        _dbContextFactory = dbContextFactory;
        _userContextAccessor = userContextAccessor;
    }

    public async ValueTask<(string, byte[])> Handle(GetFileStreamQuery request, CancellationToken cancellationToken)
    {
        // Fail closed: with no ambient user context there is no principal to authorize against.
        var currentUser = _userContextAccessor.Current;
        if (currentUser is null) throw NotFound(request.Id);

        // CacheKey is built from the principal carried on the request, so a request whose carried
        // principal disagrees with the ambient one must never be served: doing so would file this
        // principal's bytes under another principal's cache key.
        if (!string.Equals(request.UserId, currentUser.UserId, StringComparison.Ordinal) ||
            !string.Equals(request.TenantId, currentUser.TenantId, StringComparison.Ordinal))
        {
            throw NotFound(request.Id);
        }

        await using var db = await _dbContextFactory.CreateAsync(cancellationToken);

        // Apply the same visibility rule as the rest of the feature (see DocumentsQuery below): the
        // document must be public or created by the requester, and inside the requester's tenant.
        var item = await db.Documents
            .Where(x => x.Id == request.Id)
            .WithSpecification(new DocumentsQuery(currentUser.UserId, currentUser.TenantId ?? string.Empty, string.Empty))
            .FirstOrDefaultAsync(cancellationToken);

        // A document that exists but is not visible to this user is reported exactly like a missing
        // one, so document ids cannot be enumerated by comparing the two responses.
        if (item is null) throw NotFound(request.Id);
        if (string.IsNullOrEmpty(item.URL)) return (string.Empty, Array.Empty<byte>());

        var filepath = Path.Combine(Directory.GetCurrentDirectory(), item.URL);
        if (!File.Exists(filepath)) return (string.Empty, Array.Empty<byte>());

        var fileName = new FileInfo(filepath).Name;
        var buffer = await File.ReadAllBytesAsync(filepath, cancellationToken);
        return (fileName, buffer);
    }

    private static Exception NotFound(int id) => new Exception($"not found document entry by Id:{id}.");

    internal class DocumentsQuery : Specification<Document>
    {
        public DocumentsQuery(string userId, string tenantId, string keyword)
        {
            Query.Where(p => (p.CreatedById == userId && p.IsPublic == false) || p.IsPublic == true)
                .Where(x => x.TenantId == tenantId, !string.IsNullOrEmpty(tenantId))
                .Where(x => x.Title!.Contains(keyword) || x.Description!.Contains(keyword),
                    !string.IsNullOrEmpty(keyword));
        }
    }
}
