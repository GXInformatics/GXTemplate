// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using CleanArchitecture.Blazor.Application.Common.Extensions;
using Microsoft.AspNetCore.StaticFiles;

namespace CleanArchitecture.Blazor.Infrastructure.Services.Storage;

/// <summary>
/// Composition, validation and decomposition of storage keys, shared by every provider so the
/// key shape is defined once.
/// </summary>
/// <remarks>
/// A key is <c>{UploadType}/{Folder?}/{FileName}</c>: forward slashes, no leading slash, no
/// traversal. It is a relative path under the disk provider's root and a blob name under Azure.
/// </remarks>
internal static class StorageKeys
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    /// <summary>The public URL of a key, identical under every provider - see the streaming endpoint.</summary>
    public const string PublicUrlPrefix = "/files/";

    /// <summary>Builds the key a request implies, before any overwrite-avoidance is applied.</summary>
    public static string Compose(FileUploadRequest request)
    {
        var segments = new List<string> { request.UploadType.GetDisplayName() };
        if (!string.IsNullOrWhiteSpace(request.Folder))
        {
            segments.AddRange(Split(request.Folder));
        }
        segments.Add(request.FileName.Trim('"').Trim());
        return string.Join('/', segments);
    }

    /// <summary>
    /// Accepts a key from an untrusted source and returns its canonical form, or <c>null</c> if it
    /// is not a key at all. Rejecting here is what stops <c>../../appsettings.json</c> reaching the
    /// filesystem through the streaming endpoint.
    /// </summary>
    public static string? Canonicalize(string? storageKey)
    {
        if (string.IsNullOrWhiteSpace(storageKey)) return null;

        var segments = Split(storageKey);
        if (segments.Count == 0) return null;

        foreach (var segment in segments)
        {
            if (segment == "." || segment == "..") return null;
            if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        }

        // A key is relative by construction; anything that survives as rooted is not one.
        var candidate = string.Join('/', segments);
        return Path.IsPathRooted(candidate) ? null : candidate;
    }

    /// <summary>The last segment of a key - the name the object is stored under.</summary>
    public static string FileNameOf(string storageKey) => Split(storageKey)[^1];

    /// <summary>The public URL for a key.</summary>
    public static string PublicUrlOf(string storageKey) =>
        PublicUrlPrefix + string.Join('/', Split(storageKey).Select(Uri.EscapeDataString));

    /// <summary>MIME type inferred from the extension, falling back to <c>application/octet-stream</c>.</summary>
    public static string ContentTypeOf(string fileNameOrKey) =>
        ContentTypes.TryGetContentType(fileNameOrKey, out var contentType)
            ? contentType
            : "application/octet-stream";

    /// <summary>
    /// The derive half of derive-and-retry: <c>Documents/a.png</c> at attempt 1 becomes
    /// <c>Documents/a (1).png</c>. Matches the disk provider's historical
    /// <c>NextAvailableFilename</c> naming, so the two providers name collisions identically.
    /// </summary>
    public static string Derive(string storageKey, int attempt)
    {
        var extension = Path.GetExtension(storageKey);
        return extension.Length == 0
            ? $"{storageKey} ({attempt})"
            : $"{storageKey[..^extension.Length]} ({attempt}){extension}";
    }

    private static List<string> Split(string value) =>
        value.Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
}
