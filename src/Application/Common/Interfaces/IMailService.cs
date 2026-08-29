// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace CleanArchitecture.Blazor.Application.Common.Interfaces;

/// <summary>
///     Sends templated mail.
/// </summary>
/// <remarks>
///     <b>Returns <see cref="Result"/> and never throws for an expected failure.</b> Mailgun
///     answering 4xx, the network being down, a template being absent or unparseable - all of those
///     are outcomes, not exceptions, and a caller can inspect them. Unexpected exceptions are caught
///     at the implementation boundary and returned as failures too: a mail send must not propagate
///     into whatever was happening when it was triggered.
///     <para>
///     The single overload takes a template name and a model; the old free-body overload is gone.
///     Every email this application sends is a template, and an overload that took a pre-rendered
///     body would be the one place token injection could be bypassed.
///     </para>
/// </remarks>
public interface IMailService
{
    /// <summary>
    ///     Renders <paramref name="template" /> with <paramref name="model" /> and sends it.
    /// </summary>
    /// <param name="to">Who it is going to, and what to call them.</param>
    /// <param name="subject">The subject line, already localised by the caller.</param>
    /// <param name="template">A name from <c>MailTemplates</c>, not a free string.</param>
    /// <param name="model">
    ///     The tokens this template needs beyond the four supplied centrally
    ///     (<c>user_name</c>, <c>app_name</c>, <c>company</c>, <c>base_url</c>). Anything set here
    ///     wins over the injected value.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<Result> SendAsync(
        MailRecipient to,
        string subject,
        string template,
        object? model = null,
        CancellationToken cancellationToken = default);
}
