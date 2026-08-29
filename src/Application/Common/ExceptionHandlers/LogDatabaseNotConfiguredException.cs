// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace CleanArchitecture.Blazor.Application.Common.ExceptionHandlers;

/// <summary>
/// Thrown when something asks for the log database and no log connection string is configured.
/// </summary>
/// <remarks>
/// This is a supported configuration, not a defect - logs are best-effort, and an application with
/// no log database still starts, serves and audits normally. It is an exception rather than an
/// empty result because the two must not look alike: a page that renders "no rows" when the truth
/// is "nobody configured a log database" is exactly the silent failure this arrangement exists to
/// avoid. Callers catch this specifically and say so.
/// <para>
/// The alternative - falling back to <c>DatabaseSettings:ConnectionString</c> - is deliberately not
/// offered anywhere. It would put the SystemLogs table back into the business database in the one
/// configuration nobody would think to check, silently undoing the point of the separation.
/// </para>
/// </remarks>
public class LogDatabaseNotConfiguredException : Exception
{
    public LogDatabaseNotConfiguredException()
        : base("No log database is configured. Set DatabaseSettings:LogConnectionString.")
    {
    }
}
