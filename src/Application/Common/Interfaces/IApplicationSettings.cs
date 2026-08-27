namespace CleanArchitecture.Blazor.Application.Common.Interfaces;

public interface IApplicationSettings
{
    string App { get; set; }
    string ApplicationUrl { get; set; }
    string AppName { get; set; }
    string Company { get; set; }
    string Copyright { get; set; }
    string Version { get; set; }

    /// <summary>
    /// The time zone a newly provisioned account is given when nobody has chosen one.
    /// </summary>
    /// <remarks>
    /// A configuration value rather than <c>TimeZoneInfo.Local</c>: the server's zone says nothing
    /// about where the person being provisioned is, and would otherwise vary with the deployment
    /// host. Validated at startup, so an unresolvable id fails the app rather than throwing later
    /// inside <c>UserProfile.LocalTimeOffset</c>.
    /// </remarks>
    string DefaultTimeZone { get; set; }

    /// <summary>
    /// Whether anonymous visitors may create their own accounts.
    /// </summary>
    /// <remarks>
    /// When false, BOTH self-service doors are closed: the registration pages, and the
    /// external-login callback that provisions an account for an unrecognised identity.
    /// </remarks>
    bool AllowSelfRegistration { get; set; }
}
