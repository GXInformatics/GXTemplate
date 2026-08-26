// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace CleanArchitecture.Blazor.Application.Common.Interfaces.Storage;

/// <summary>
/// Stores, reads back and removes uploaded files, independently of where the bytes actually live.
/// </summary>
/// <remarks>
/// The unit of identity is the <c>storageKey</c>: a provider-opaque string of the shape
/// <c>{UploadType}/{Folder?}/{FileName}</c> — forward slashes, no leading slash. It is what the
/// caller persists and hands back to <see cref="ReadAsync"/> and <see cref="DeleteAsync"/>.
/// <para>
/// A key is deliberately NOT a URL. <see cref="StoredFile.PublicUrl"/> is the browser-resolvable
/// address of the same object; the two are separate because one string cannot be both a lookup
/// identity for the provider and an <c>&lt;img src&gt;</c> for the browser.
/// </para>
/// <para>
/// Expected failures are reported as failed <see cref="Result"/>s, never as exceptions: several
/// call sites are UI event handlers with nowhere to catch.
/// </para>
/// </remarks>
public interface IFileStorage
{
    /// <summary>
    /// Stores bytes and returns where they went. The key is the provider-opaque identity of the
    /// object; the caller persists it and passes it back to Read/Delete.
    /// </summary>
    /// <remarks>
    /// When <see cref="FileUploadRequest.Overwrite"/> is <c>false</c> and the requested key is
    /// taken, the provider stores under a derived key rather than destroying anything — so the
    /// returned <see cref="StoredFile.StorageKey"/> may differ from the one the caller implied,
    /// and it is the returned key that must be persisted.
    /// </remarks>
    Task<Result<StoredFile>> SaveAsync(FileUploadRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a stored object back. Returns a failed Result when the key does not resolve —
    /// never an empty success, which is how the previous download failure hid.
    /// </summary>
    Task<Result<StoredFileContent>> ReadAsync(string storageKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a stored object. Succeeds if the object is already absent (idempotent);
    /// fails only if the removal itself failed.
    /// </summary>
    Task<Result> DeleteAsync(string storageKey, CancellationToken cancellationToken = default);
}
