// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace CleanArchitecture.Blazor.Application.Common.Interfaces.Storage;

/// <summary>
/// The outcome of a successful <see cref="IFileStorage.SaveAsync"/>.
/// </summary>
/// <param name="StorageKey">
/// The provider-opaque identity of the stored object. Persist THIS, not the key you constructed:
/// an overwrite-averse save may have derived a different one.
/// </param>
/// <param name="PublicUrl">The browser-resolvable address of the same object.</param>
/// <param name="FileName">The file name the object was actually stored under.</param>
/// <param name="Size">Size of the stored content in bytes.</param>
public sealed record StoredFile(string StorageKey, string PublicUrl, string FileName, long Size);
