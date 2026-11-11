namespace CleanArchitecture.Blazor.Domain.Identity;

/// <summary>
/// When a signed-in user loses access (Pass 60, Part D item 4 and owner decision 15).
/// </summary>
/// <remarks>
/// <para>
/// A live session ends when the user is <b>deactivated</b> (<c>IsActive</c> false) or <b>locked out by an
/// administrator</b>, which the template writes as a permanent lockout, <see cref="AdministrativeLockoutEnd"/>.
/// </para>
/// <para>
/// <b>An automatic lockout after failed sign-ins does not end a live session</b>; it only blocks new sign-ins. The two
/// are told apart by value: Identity's automatic lockout is always time-limited (<c>UtcNow + DefaultLockoutTimeSpan</c>),
/// and startup fails if that span is not finite, so no failed sign-in can produce <see cref="AdministrativeLockoutEnd"/>.
/// A caller-set "administrative" scope was rejected: an administrative path that forgot it would leave the session open
/// with nothing failing, whereas the value travels with the save from whichever path wrote it.
/// </para>
/// <para>
/// The system account is inactive and permanently locked out by design, and never has a session; it is exempt by name.
/// </para>
/// </remarks>
public static class UserAccess
{
    /// <summary>The lockout an administrator sets: permanent. Deactivation sets it too.</summary>
    public static readonly DateTimeOffset AdministrativeLockoutEnd = DateTimeOffset.MaxValue;

    /// <summary>The longest automatic lockout the configuration may set; startup fails above it.</summary>
    public static readonly TimeSpan LongestAutomaticLockout = TimeSpan.FromDays(3650);

    /// <summary>True when the user may keep a session: active, and not locked out by an administrator.</summary>
    public static bool HasAccess(bool isActive, DateTimeOffset? lockoutEnd) =>
        isActive && lockoutEnd != AdministrativeLockoutEnd;

    /// <inheritdoc cref="HasAccess(bool, DateTimeOffset?)"/>
    public static bool HasAccess(ApplicationUser user) => HasAccess(user.IsActive, user.LockoutEnd);
}
