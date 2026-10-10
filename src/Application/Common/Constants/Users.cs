// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace CleanArchitecture.Blazor.Application.Common.Constants;

/// <summary>
/// GX divergence: the <c>Demo</c> account and the shared <c>DefaultPassword</c> constant were
/// removed in Pass 7-3. A fresh database now yields exactly one account a person can sign in as,
/// and its password is generated per installation and written to the log once - see
/// <c>ApplicationDbContextInitializer.EnsureAdministratorAsync</c>. A credential in source is a
/// credential in every deployment of the template. (Pass 54 adds <see cref="System"/>, which
/// nobody can sign in as.)
/// </summary>
public abstract class Users
{
    public const string Administrator = nameof(Administrator);

    /// <summary>
    /// The account background work acts as - see <c>ISystemContext</c>. Provisioned in every
    /// environment with no password, no email and a permanent lockout, so nothing can sign in as it;
    /// it exists so that work with no human behind it still has a real user id to authorize and
    /// audit against. Prefixed so that it cannot plausibly collide with a person's user name, and
    /// provisioning refuses to adopt an existing account of this name that has a password.
    /// </summary>
    public const string System = "gx-system";
}
