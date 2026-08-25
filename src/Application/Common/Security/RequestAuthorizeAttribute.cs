// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace CleanArchitecture.Blazor.Application.Common.Security;

#nullable disable
/// <summary>
///     Specifies the class this attribute is applied to requires authorization.
/// <para>
///     <b>Deny-by-default contract.</b> Every request dispatched through the mediator must carry at
///     least one of these attributes. <c>AuthorizationBehaviour</c> runs first in the pipeline and
///     refuses any request type that carries none - an unmarked request is a denied request, not an
///     unrestricted one. A startup assertion fails the application if any request type in the
///     Application assembly is unmarked, so the omission cannot reach production silently.
/// </para>
/// <para>
///     <b>ANY-OF semantics.</b> The attribute is <c>AllowMultiple</c>. When a request carries several,
///     the principal need satisfy only <i>one</i> of them to proceed - they are alternatives, not
///     conjuncts. This is what lets an add-or-edit command accept either the create or the edit
///     permission. Within a single attribute, <see cref="Roles" /> is evaluated before
///     <see cref="Policy" /> because the role test is in-memory while the policy test costs several
///     database round-trips.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public class RequestAuthorizeAttribute : Attribute
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="RequestAuthorizeAttribute" /> class.
    /// </summary>
    public RequestAuthorizeAttribute()
    {
    }

    /// <summary>
    ///     Gets or sets a comma delimited list of roles that are allowed to access the resource.
    /// </summary>
    public string Roles { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the policy name that determines access to the resource.
    /// </summary>
    public string Policy { get; set; } = string.Empty;
}
