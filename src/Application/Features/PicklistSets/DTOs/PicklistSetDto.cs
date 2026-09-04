// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel.DataAnnotations;

namespace CleanArchitecture.Blazor.Application.Features.PicklistSets.DTOs;

[Description("Picklist")]
public class PicklistSetDto
{
    [Display(Name ="Id")] public int Id { get; set; }
    [Display(Name = "Name")] public Picklist Name { get; set; }
    [Display(Name = "Value")] public string? Value { get; set; }
    [Display(Name = "Text")] public string? Text { get; set; }
    [Display(Name = "Description")] public string? Description { get; set; }

    /// <summary>
    /// The tenant this value belongs to, or null when it is shared with every tenant.
    /// </summary>
    /// <remarks>
    /// <b>Added in Pass 32, and Pass 31 A5 recorded why it had to be.</b> The admin grid could not
    /// tell a shared row from a private one, and that - not any MudBlazor limitation - was what
    /// blocked marking shared rows or rendering them read-only. The constraint sat one layer away
    /// from where the question was being asked.
    /// <para>
    /// <b>It is a display and affordance input, never the guard.</b> Whether a write is permitted is
    /// decided in the command handlers through <see cref="SharedPicklistWrite"/>, on the row read
    /// back from the database. A DTO round-trips through the client, so a value on it is a claim
    /// about what the server last sent, not a fact the server may act on.
    /// </para>
    /// <para>
    /// Populated by Mapster's convention mapping from <c>PicklistSet.TenantId</c>. Discloses nothing:
    /// the query filter means a principal only ever receives rows that are shared or their own, so
    /// this is either null or the tenant they are already acting in.
    /// </para>
    /// </remarks>
    public string? TenantId { get; set; }

    /// <summary>True when this value is shared with every tenant rather than owned by one.</summary>
    public bool IsShared => SharedPicklistWrite.IsShared(TenantId);

    public TrackingState TrackingState { get; set; } = TrackingState.Unchanged;
}
