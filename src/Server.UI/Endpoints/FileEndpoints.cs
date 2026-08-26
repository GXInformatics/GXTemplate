// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Ardalis.Specification.EntityFrameworkCore;
using CleanArchitecture.Blazor.Application.Common.Security;
using CleanArchitecture.Blazor.Application.Features.Documents.Specifications;
using CleanArchitecture.Blazor.Domain.Common.Enums;
using CleanArchitecture.Blazor.Infrastructure.Configurations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using IResult = Microsoft.AspNetCore.Http.IResult;

namespace CleanArchitecture.Blazor.Server.UI.Endpoints;

/// <summary>
/// Serves stored files to the browser, through the storage abstraction rather than off the disk.
/// </summary>
/// <remarks>
/// This replaces the anonymous <c>/Files</c> static-file mount. Static-file middleware runs BEFORE
/// authorization, so that route handed every uploaded document and every avatar to anyone who could
/// guess a path. Everything now arrives here instead, behind authentication, and document keys get
/// the same per-object visibility check the download button already applies.
/// </remarks>
public static class FileEndpoints
{
    /// <summary>The route <c>StoredFile.PublicUrl</c> points at, under every provider.</summary>
    public const string RoutePattern = "/files/{**key}";

    public static IEndpointConventionBuilder MapFileEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // RequireAuthorization is redundant against the fallback policy and is stated anyway: this
        // endpoint's authentication requirement should be visible at the endpoint, not inferred.
        return endpoints.MapGet(RoutePattern, HandleAsync)
            .RequireAuthorization()
            .WithName("GetStoredFile");
    }

    private static async Task<IResult> HandleAsync(
        string key,
        HttpContext httpContext,
        IFileStorage fileStorage,
        StorageSettings settings,
        IApplicationDbContextFactory dbContextFactory,
        IAuthorizationService authorizationService,
        CancellationToken cancellationToken)
    {
        if (!await IsPermittedAsync(key, httpContext.User, dbContextFactory, authorizationService, cancellationToken))
        {
            // Refused and missing are reported identically, so keys cannot be probed by comparing
            // the two responses.
            return Results.NotFound();
        }

        var content = await fileStorage.ReadAsync(key, cancellationToken);
        if (!content.Succeeded || content.Data is null)
        {
            return Results.NotFound();
        }

        // private, not public: these are per-principal authorized bytes, so a shared proxy must not
        // hold them. This is what keeps an avatar to one browser fetch per cache lifetime rather
        // than one per render, which is the cost of moving off the static route.
        httpContext.Response.Headers.CacheControl = $"private, max-age={settings.CacheControlMaxAgeSeconds}";
        return Results.File(content.Data.Content, content.Data.ContentType, content.Data.FileName);
    }

    /// <summary>
    /// Authentication is enforced by the route. This adds the per-object half.
    /// </summary>
    /// <remarks>
    /// Public and static, and taking a principal rather than an HttpContext, so the decision can be
    /// asserted directly against a constructed principal instead of only through a running host -
    /// the same shape as <c>ForcePasswordChangeMiddleware.ShouldRedirect</c>, for the same reason.
    /// </remarks>
    /// <remarks>
    /// Profile pictures are deliberately readable by any authenticated user: seven render sites show
    /// OTHER users' avatars (grids, presence lists, org chart), so a per-owner check there would
    /// break the feature rather than protect anything - an avatar is already visible next to its
    /// owner's name.
    /// <para>
    /// Document keys are different. Document file names come from the uploader ("invoice.png"), so
    /// they are guessable, and a private document belongs to one user inside one tenant. Those keys
    /// therefore get the Documents.Download permission plus
    /// <see cref="VisibleDocumentSpecification"/> - the same rule
    /// <c>GetFileStreamQueryHandler</c> applies - resolved by storage key.
    /// </para>
    /// </remarks>
    public static async Task<bool> IsPermittedAsync(
        string key,
        ClaimsPrincipal user,
        IApplicationDbContextFactory dbContextFactory,
        IAuthorizationService authorizationService,
        CancellationToken cancellationToken)
    {
        var firstSegment = key.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (!string.Equals(firstSegment, UploadType.Document.GetDisplayName(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var authorized = await authorizationService.AuthorizeAsync(user, Permissions.Documents.Download);
        if (!authorized.Succeeded)
        {
            return false;
        }

        var userId = user.GetUserId();
        if (string.IsNullOrEmpty(userId))
        {
            return false;
        }

        await using var db = await dbContextFactory.CreateAsync(cancellationToken);
        return await db.Documents
            .Where(x => x.StorageKey == key)
            .WithSpecification(new VisibleDocumentSpecification(userId, user.GetTenantId() ?? string.Empty))
            .AnyAsync(cancellationToken);
    }
}
