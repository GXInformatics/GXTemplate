// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace CleanArchitecture.Blazor.Application.Common.Models;

/// <summary>
///     Who an email is going to, and what to call them.
/// </summary>
/// <param name="Email">The address. The only field the transport needs.</param>
/// <param name="DisplayName">The person's chosen name, if they have set one.</param>
/// <param name="UserName">Their sign-in name. Often an email address; a poor greeting, but better than nothing.</param>
/// <remarks>
///     A record rather than an <c>ApplicationUser</c>, so the mail layer never learns about Identity
///     and a caller holding only a DTO - as the user-management page does - does not have to reload
///     an entity to send a message.
///     <para>
///     The point of the type is <see cref="Greeting"/>. Every template opens "Hi {{ user_name }},"
///     and Scriban renders a missing variable as empty, so a name that fails to arrive produces
///     "Hi ," in a real person's inbox. Computing the fallback here means it cannot be forgotten at
///     a call site: there is no way to construct a recipient that greets nobody.
///     </para>
/// </remarks>
public sealed record MailRecipient(string Email, string? DisplayName = null, string? UserName = null)
{
    /// <summary>
    ///     What to call this person: their display name, else their user name, else "there".
    /// </summary>
    /// <remarks>
    ///     "there" rather than "" or "user": "Hi there," is a sentence a human wrote, and it is the
    ///     one greeting that is never wrong.
    /// </remarks>
    public string Greeting =>
        !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName!
        : !string.IsNullOrWhiteSpace(UserName) ? UserName!
        : "there";
}
