// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace CleanArchitecture.Blazor.Application.Common.ExceptionHandlers;

/// <summary>
/// Thrown when the current principal is not permitted to execute a request.
/// <para>
/// Raised by <c>AuthorizationBehaviour</c> in two distinct situations, deliberately carrying
/// different messages so the logs distinguish them:
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// the request type carries no <c>RequestAuthorizeAttribute</c> at all - a deny-by-default
/// refusal, which is a developer error rather than an access decision;
/// </description>
/// </item>
/// <item>
/// <description>
/// the request is marked, but the principal satisfied none of its attributes.
/// </description>
/// </item>
/// </list>
/// <para>
/// It is deliberately a distinct type from <see cref="UnauthorizedAccessException"/>, which the
/// BCL also raises for file-system and I/O permission faults: sharing that type would make a
/// security decision indistinguishable from an infrastructure error in the logs.
/// </para>
/// </summary>
public class ForbiddenAccessException : Exception
{
    public ForbiddenAccessException(string message)
        : base(message)
    {
    }

    public ForbiddenAccessException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
