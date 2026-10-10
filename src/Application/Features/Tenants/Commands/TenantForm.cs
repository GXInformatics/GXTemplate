// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel.DataAnnotations;

namespace CleanArchitecture.Blazor.Application.Features.Tenants.Commands;

/// <summary>
/// The fields <see cref="Create.CreateTenantCommand"/> and <see cref="Update.UpdateTenantCommand"/>
/// share, so that one form dialog can edit either without the two commands becoming one again.
/// </summary>
/// <remarks>
/// Pass 54 split <c>AddEditTenantCommand</c>. It carried <c>[RequestAuthorize]</c> for Create AND
/// Edit, and the attributes are OR'd, so holding either right let a caller do both - an Edit-only
/// operator could create tenants by sending an unknown id. Each command now names exactly the right
/// its own operation needs.
/// <para>
/// The <c>[Display]</c> names are repeated here because the dialog is generic over the command, so
/// its <c>x =&gt; x.Name</c> lambdas resolve to THESE members, and the label is read from them.
/// </para>
/// </remarks>
public interface ITenantForm
{
    [Display(Name = "Tenant Id")] string Id { get; set; }
    [Display(Name = "Tenant Name")] string? Name { get; set; }
    [Display(Name = "Description")] string? Description { get; set; }
}

/// <summary>The rules the two tenant commands share.</summary>
public abstract class TenantFormValidator<T> : AbstractValidator<T> where T : ITenantForm
{
    protected TenantFormValidator()
    {
        RuleFor(v => v.Id)
            .NotEmpty()
            .MaximumLength(450);
        RuleFor(v => v.Name)
            .MaximumLength(256)
            .NotEmpty();
    }
}
