// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace CleanArchitecture.Blazor.Application.Common.Constants;

public static class ApplicationClaimTypes
{
    public const string Provider = "Provider";
    public const string TenantId = "TenantId";
    public const string SuperiorId = "SuperiorId";
    public const string SuperiorName = "SuperiorName";
    public const string Status = "Status";
    public const string TenantName = "TenantName";
    public const string Permission = "Permission";
    public const string AssignedRoles = "AssignedRoles";
    public const string ProfilePictureDataUrl = "ProfilePictureDataUrl";

    /// <summary>
    /// Present only while the signed-in user still holds a password nobody chose. Projected from
    /// <c>ApplicationUser.MustChangePassword</c> by <c>ApplicationUserClaimsPrincipalFactory</c> so
    /// that enforcement needs no database round-trip per request.
    /// </summary>
    public const string MustChangePassword = "MustChangePassword";
} 
