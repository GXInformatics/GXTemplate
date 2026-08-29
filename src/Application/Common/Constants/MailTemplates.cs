// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;

namespace CleanArchitecture.Blazor.Application.Common.Constants;

/// <summary>
///     Every mail template this application ships, by name.
/// </summary>
/// <remarks>
///     Constants rather than the magic strings the call sites used to pass, so that
///     <c>MailStartupCheck</c> can enumerate them and prove every one is present, readable and
///     parseable before the application serves a request. Until Pass 12B a missing template was
///     invisible until a customer reported not receiving an email: the send threw inside a
///     notification handler, and the notification publisher swallows handler exceptions.
///     <para>
///     Discovery by reflection follows <c>AdministratorPermissionRegistry.DiscoverAllPermissions</c>,
///     which reads its constants the same way for the same reason - a list nobody has to remember to
///     update cannot fall out of date.
///     </para>
/// </remarks>
public static class MailTemplates
{
    /// <summary>Sent when someone asks to reset a forgotten password.</summary>
    public const string RecoveryPassword = "recovery-password";

    /// <summary>Sent when an account needs its email address confirmed before it can be used.</summary>
    public const string UserActivation = "user-activation";

    /// <summary>Sent once an account is confirmed and ready to sign in to.</summary>
    public const string Welcome = "welcome";

    /// <summary>The file extension every template carries.</summary>
    /// <remarks>
    ///     <c>.sbn</c>, Scriban's own extension, because that is what these files are - they have
    ///     always been Scriban, and were only ever named <c>.cshtml</c>. This is hardening rather
    ///     than repair: the templates did survive <c>dotnet publish</c> under the old name in this
    ///     repository, verified by running it. What the rename buys is a name that does not lie, and
    ///     independence from an explicit csproj override that kept Razor's build pipeline off files
    ///     it had no business touching.
    /// </remarks>
    public const string Extension = ".sbn";

    /// <summary>The directory, relative to the application's base directory, templates live in.</summary>
    public const string Directory = "Resources/EmailTemplates";

    /// <summary>
    ///     Every template name declared above, discovered rather than listed a second time.
    /// </summary>
    public static IReadOnlyList<string> All =>
        typeof(MailTemplates)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
            .Where(f => f.Name is not (nameof(Extension) or nameof(Directory)))
            .Select(f => (string)f.GetRawConstantValue()!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

    /// <summary>The path a template is expected at, relative to the application's base directory.</summary>
    public static string RelativePath(string template) => $"{Directory}/{template}{Extension}";
}
