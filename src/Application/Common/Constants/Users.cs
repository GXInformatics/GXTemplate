// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace CleanArchitecture.Blazor.Application.Common.Constants;

/// <summary>
/// GX divergence: the <c>Demo</c> account and the shared <c>DefaultPassword</c> constant were
/// removed in Pass 7-3. A fresh database now yields exactly one account, and its password is
/// generated per installation and written to the log once - see
/// <c>ApplicationDbContextInitializer.EnsureAdministratorAsync</c>. A credential in source is a
/// credential in every deployment of the template.
/// </summary>
public abstract class Users
{
    public const string Administrator = nameof(Administrator);
} 
