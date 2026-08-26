// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace CleanArchitecture.Blazor.Application.Common.Constants;

/// <summary>
/// The file-storage providers this application can actually build an <c>IFileStorage</c> for.
/// Read by <c>StorageSettings.Validate</c> so the supported set cannot drift from the switch in
/// <c>DependencyInjection.AddFileStorage</c> that consumes it.
/// </summary>
public class StorageProviderKeys
{
    public const string Disk = "disk";
    public const string AzureBlob = "azureblob";
}
