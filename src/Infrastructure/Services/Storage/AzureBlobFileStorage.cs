// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace CleanArchitecture.Blazor.Infrastructure.Services.Storage;

/// <summary>
/// Stores files as blobs in a single Azure Storage container, the storage key being the blob name.
/// </summary>
/// <remarks>
/// One container, keys as blob names - the smaller change, and the same shape the disk provider
/// uses, so a key means the same thing under both. The container stays private: files are served by
/// the authenticated <c>/files</c> endpoint, so <c>PublicUrl</c> is that route rather than a blob
/// URL, and it is identical to what the disk provider returns.
/// </remarks>
public class AzureBlobFileStorage : IFileStorage
{
    /// <summary>
    /// How many derived names to try before giving up. Bounded so a pathological key cannot turn one
    /// upload into an unbounded run of network calls.
    /// </summary>
    private const int MaxDeriveAttempts = 32;

    private readonly BlobContainerClient _container;
    private readonly SemaphoreSlim _ensureContainerLock = new(1, 1);
    private bool _containerEnsured;

    public AzureBlobFileStorage(StorageSettings settings)
        : this(new BlobContainerClient(settings.ConnectionString, settings.ContainerName))
    {
    }

    /// <summary>Test seam: lets the provider be exercised against a mocked container client.</summary>
    public AzureBlobFileStorage(BlobContainerClient container)
    {
        _container = container;
    }

    /// <inheritdoc />
    public async Task<Result<StoredFile>> SaveAsync(FileUploadRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Data is null || request.Data.Length == 0)
        {
            return Result<StoredFile>.Failure("No file data provided");
        }

        var storageKey = StorageKeys.Canonicalize(StorageKeys.Compose(request));
        if (storageKey is null)
        {
            return Result<StoredFile>.Failure($"'{request.FileName}' is not a valid file name");
        }

        try
        {
            await EnsureContainerAsync(cancellationToken);

            var headers = new BlobHttpHeaders { ContentType = StorageKeys.ContentTypeOf(storageKey) };

            if (request.Overwrite)
            {
                await UploadAsync(storageKey, request.Data, headers, overwrite: true, cancellationToken);
                return Success(storageKey, request.Data.Length);
            }

            // Derive-and-retry, matching the disk provider: a conditional PUT fails with 409 when the
            // blob is already there, and the promise is that nothing existing is destroyed - NOT that
            // the caller gets the key it asked for. Callers persist the RETURNED key.
            for (var attempt = 0; attempt <= MaxDeriveAttempts; attempt++)
            {
                var candidate = attempt == 0 ? storageKey : StorageKeys.Derive(storageKey, attempt);
                try
                {
                    await UploadAsync(candidate, request.Data, headers, overwrite: false, cancellationToken);
                    return Success(candidate, request.Data.Length);
                }
                catch (RequestFailedException ex) when (ex.Status == 409)
                {
                    // Taken; try the next derived name.
                }
            }

            return Result<StoredFile>.Failure(
                $"Failed to upload file: '{storageKey}' and {MaxDeriveAttempts} derived names are all taken");
        }
        catch (Exception ex)
        {
            return Result<StoredFile>.Failure($"Failed to upload file: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<Result<StoredFileContent>> ReadAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        var key = StorageKeys.Canonicalize(storageKey);
        if (key is null)
        {
            return Result<StoredFileContent>.Failure($"'{storageKey}' is not a valid storage key");
        }

        try
        {
            // A missing blob is a FAILED result, never an empty success.
            var response = await _container.GetBlobClient(key).DownloadContentAsync(cancellationToken);
            var fileName = StorageKeys.FileNameOf(key);
            var contentType = string.IsNullOrWhiteSpace(response.Value.Details.ContentType)
                ? StorageKeys.ContentTypeOf(fileName)
                : response.Value.Details.ContentType;

            return Result<StoredFileContent>.Success(
                new StoredFileContent(fileName, response.Value.Content.ToArray(), contentType));
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return Result<StoredFileContent>.Failure($"File '{key}' was not found");
        }
        catch (Exception ex)
        {
            return Result<StoredFileContent>.Failure($"Failed to read file '{key}': {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<Result> DeleteAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        var key = StorageKeys.Canonicalize(storageKey);
        if (key is null)
        {
            return Result.Failure($"'{storageKey}' is not a valid storage key");
        }

        try
        {
            // Idempotent by construction: DeleteIfExists reports absence rather than failing on it.
            await _container.GetBlobClient(key).DeleteIfExistsAsync(cancellationToken: cancellationToken);
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure($"Failed to delete file '{key}': {ex.Message}");
        }
    }

    private async Task UploadAsync(string key, byte[] data, BlobHttpHeaders headers, bool overwrite,
        CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(data, writable: false);
        await _container.GetBlobClient(key).UploadAsync(
            stream,
            new BlobUploadOptions
            {
                HttpHeaders = headers,
                // No overwrite means an If-None-Match: * conditional PUT, which Azure answers with
                // 409 rather than clobbering.
                Conditions = overwrite ? null : new BlobRequestConditions { IfNoneMatch = ETag.All }
            },
            cancellationToken);
    }

    private static Result<StoredFile> Success(string storageKey, long size) =>
        Result<StoredFile>.Success(new StoredFile(
            storageKey,
            StorageKeys.PublicUrlOf(storageKey),
            StorageKeys.FileNameOf(storageKey),
            size));

    private async Task EnsureContainerAsync(CancellationToken cancellationToken)
    {
        if (_containerEnsured) return;

        await _ensureContainerLock.WaitAsync(cancellationToken);
        try
        {
            if (_containerEnsured) return;
            await _container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
            _containerEnsured = true;
        }
        finally
        {
            _ensureContainerLock.Release();
        }
    }
}
