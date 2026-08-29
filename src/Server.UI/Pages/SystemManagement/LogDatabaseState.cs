// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace CleanArchitecture.Blazor.Server.UI.Pages.SystemManagement;

/// <summary>
/// What the SystemLogs page and its chart last learned about the log database.
/// </summary>
/// <remarks>
/// Three states rather than two, because collapsing them is how an outage gets mistaken for a quiet
/// week. Before Pass 11 the log table lived in the business database, so "no rows" could only mean
/// no rows; now the log database can be absent or unreachable independently of an application that
/// is otherwise running perfectly, and an empty grid would report all three identically.
/// </remarks>
public enum LogDatabaseState
{
    /// <summary>The log database answered. An empty grid here genuinely means no matching rows.</summary>
    Available,

    /// <summary>
    /// No <c>DatabaseSettings:LogConnectionString</c> is configured. Nothing is being recorded to a
    /// database at all; the application is otherwise fine.
    /// </summary>
    NotConfigured,

    /// <summary>A log database is configured but did not answer. Worth retrying.</summary>
    Unavailable
}
