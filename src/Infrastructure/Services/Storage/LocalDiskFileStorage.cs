// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace CleanArchitecture.Blazor.Infrastructure.Services.Storage;

/// <summary>
/// Stores files on the local filesystem, under a single configurable root.
/// </summary>
/// <remarks>
/// A storage key maps to a relative path under the root, one key segment per directory. Both the
/// key and the public URL are derived from the same canonical string, which is the defect this
/// replaces: the previous implementation returned a Windows path with backslashes and no leading
/// slash and expected it to work both as a filesystem path and as an img src.
/// </remarks>
public class LocalDiskFileStorage : IFileStorage
{
    private static readonly string NumberPattern = " ({0})";

    private readonly string _root;

    public LocalDiskFileStorage(StorageSettings settings)
    {
        _root = Path.GetFullPath(Path.IsPathRooted(settings.RootPath)
            ? settings.RootPath
            : Path.Combine(Directory.GetCurrentDirectory(), settings.RootPath));
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
            var fullPath = ToFullPath(storageKey);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

            // Overwrite: false promises only that nothing existing is destroyed - not that the
            // caller gets the key it asked for. The derived key is returned, and it is the returned
            // key that callers persist.
            if (!request.Overwrite && File.Exists(fullPath))
            {
                fullPath = NextAvailableFilename(fullPath);
                storageKey = ReplaceFileName(storageKey, Path.GetFileName(fullPath));
            }

            await using (var stream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await stream.WriteAsync(request.Data, cancellationToken);
            }

            return Result<StoredFile>.Success(new StoredFile(
                storageKey,
                StorageKeys.PublicUrlOf(storageKey),
                StorageKeys.FileNameOf(storageKey),
                request.Data.Length));
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
            var fullPath = ToFullPath(key);

            // A missing object is a FAILED result, never an empty success - an empty success is
            // indistinguishable from an empty file, which is how the previous download breakage hid.
            if (!File.Exists(fullPath))
            {
                return Result<StoredFileContent>.Failure($"File '{key}' was not found");
            }

            var content = await File.ReadAllBytesAsync(fullPath, cancellationToken);
            var fileName = StorageKeys.FileNameOf(key);
            return Result<StoredFileContent>.Success(
                new StoredFileContent(fileName, content, StorageKeys.ContentTypeOf(fileName)));
        }
        catch (Exception ex)
        {
            return Result<StoredFileContent>.Failure($"Failed to read file '{key}': {ex.Message}");
        }
    }

    /// <inheritdoc />
    public Task<Result> DeleteAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        var key = StorageKeys.Canonicalize(storageKey);
        if (key is null)
        {
            return Task.FromResult(Result.Failure($"'{storageKey}' is not a valid storage key"));
        }

        try
        {
            var fullPath = ToFullPath(key);

            // Idempotent: an object that is already gone is the state the caller asked for.
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }

            return Task.FromResult(Result.Success());
        }
        catch (Exception ex)
        {
            return Task.FromResult(Result.Failure($"Failed to delete file '{key}': {ex.Message}"));
        }
    }

    /// <summary>
    /// Resolves a canonical key to an absolute path and proves the result is still inside the root.
    /// <see cref="StorageKeys.Canonicalize"/> already refuses traversal segments; this is the second
    /// wall, because the key reaches this provider straight from an HTTP route value.
    /// </summary>
    private string ToFullPath(string canonicalKey)
    {
        var fullPath = Path.GetFullPath(Path.Combine(_root, canonicalKey.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException($"Storage key '{canonicalKey}' resolves outside the storage root.");
        }

        return fullPath;
    }

    private static string ReplaceFileName(string storageKey, string fileName)
    {
        var lastSlash = storageKey.LastIndexOf('/');
        return lastSlash < 0 ? fileName : string.Concat(storageKey.AsSpan(0, lastSlash + 1), fileName);
    }

    /// <summary>
    /// Gets the next available filename based on the given path.
    /// </summary>
    /// <param name="path">The path to check for availability.</param>
    /// <returns>The next available filename.</returns>
    public static string NextAvailableFilename(string path)
    {
        if (!File.Exists(path))
            return path;

        if (Path.HasExtension(path))
            return GetNextFilename(path.Insert(path.LastIndexOf(Path.GetExtension(path)), NumberPattern));

        return GetNextFilename(path + NumberPattern);
    }

    /// <summary>
    /// Gets the next available filename based on the given pattern.
    /// </summary>
    /// <param name="pattern">The pattern to generate the filename.</param>
    /// <returns>The next available filename.</returns>
    private static string GetNextFilename(string pattern)
    {
        var tmp = string.Format(pattern, 1);

        if (!File.Exists(tmp))
            return tmp;

        int min = 1, max = 2;

        while (File.Exists(string.Format(pattern, max)))
        {
            min = max;
            max *= 2;
        }

        while (max != min + 1)
        {
            var pivot = (max + min) / 2;
            if (File.Exists(string.Format(pattern, pivot)))
                min = pivot;
            else
                max = pivot;
        }

        return string.Format(pattern, max);
    }
}
