// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace CleanArchitecture.Blazor.Application.Common.Interfaces.Storage;

/// <summary>
/// The bytes of a stored object, returned by <see cref="IFileStorage.ReadAsync"/>.
/// </summary>
/// <param name="FileName">The object's file name, taken from the last segment of its key.</param>
/// <param name="Content">The stored bytes.</param>
/// <param name="ContentType">MIME type inferred from the extension; <c>application/octet-stream</c> when unknown.</param>
public sealed record StoredFileContent(string FileName, byte[] Content, string ContentType);
