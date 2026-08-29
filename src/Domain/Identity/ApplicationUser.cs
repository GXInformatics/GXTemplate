// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace CleanArchitecture.Blazor.Domain.Identity;

public class ApplicationUser : IdentityUser
{
    public ApplicationUser()
    {
        UserClaims = new HashSet<ApplicationUserClaim>();
        UserRoles = new HashSet<ApplicationUserRole>();
        Logins = new HashSet<ApplicationUserLogin>();
        Tokens = new HashSet<ApplicationUserToken>();
        TenantUsers = new HashSet<TenantUser>();
    }

    public string? DisplayName { get; set; }
    public string? Provider { get; set; } = "Local";
    public string? TenantId { get; set; }
    public virtual Tenant? Tenant { get; set; }

    public string? ProfilePictureDataUrl { get; set; }

    public bool IsActive { get; set; }
    public bool IsLive { get; set; }
    public virtual ICollection<ApplicationUserClaim> UserClaims { get; set; }
    public virtual ICollection<ApplicationUserRole> UserRoles { get; set; }
    public virtual ICollection<ApplicationUserLogin> Logins { get; set; }
    public virtual ICollection<ApplicationUserToken> Tokens { get; set; }
    public ICollection<TenantUser> TenantUsers { get; set; } 
    public string? SuperiorId { get; set; } = null;
    public ApplicationUser? Superior { get; set; } = null;
    public DateTime? CreatedAt { get; set; }
    public DateTime? LastModifiedAt { get; set; }
    public string? TimeZoneId { get; set; }
    public string? LanguageCode { get; set; }

    /// <summary>
    /// True while the account still holds a password it did not choose - the generated bootstrap
    /// password, or one an administrator reset on the user's behalf. While it is set the user is
    /// held on the change-password page and cannot reach anything else.
    /// <para>
    /// ASP.NET Core Identity has no built-in equivalent (<c>IdentityUser</c> carries
    /// <c>EmailConfirmed</c>, <c>LockoutEnabled</c>, <c>TwoFactorEnabled</c> and the security stamp,
    /// but nothing for "this password must be replaced"), so this is a GX addition. It is projected
    /// onto the principal as a claim by <c>ApplicationUserClaimsPrincipalFactory</c> so that
    /// enforcement costs no database round-trip per request.
    /// </para>
    /// </summary>
    public bool MustChangePassword { get; set; }
}
