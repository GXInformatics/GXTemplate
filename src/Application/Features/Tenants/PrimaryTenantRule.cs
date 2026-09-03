// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace CleanArchitecture.Blazor.Application.Features.Tenants;

/// <summary>
/// Which of a user's tenants is their primary one - the value stored on
/// <c>ApplicationUser.TenantId</c>.
/// </summary>
/// <remarks>
/// <b>A user's tenancy is recorded twice</b>, and the two must agree. <c>TenantUsers</c> is the set
/// of tenants a user belongs to; <c>ApplicationUser.TenantId</c> is the one they act in, and it is
/// what the interceptor stamps onto new rows and what every tenant-aware query compares against.
/// <para>
/// <b>They used to be able to disagree, and did.</b> The user-edit dialog rewrote the membership set
/// unconditionally but assigned <c>TenantId</c> only when it was empty, so moving a user from tenant
/// A to tenant B left membership on B and the primary tenant on A - permanently. That user went on
/// creating documents in A, went on matching A's grid filter, and reported an <c>AllowedTenantIds</c>
/// of [B]. Nothing detected it because nothing compared the two.
/// </para>
/// <para>
/// This lives in the Application layer, not in the dialog that calls it, because it is an invariant
/// about the data rather than a detail of one form - and because a rule inside a <c>.razor</c> file
/// can only be tested by replaying a copy of it, which is a test of the copy.
/// </para>
/// </remarks>
public static class PrimaryTenantRule
{
    /// <summary>
    /// The primary tenant for a user whose selected tenants are <paramref name="selectedTenantIds"/>.
    /// </summary>
    /// <param name="currentTenantId">The user's existing primary tenant, or null for a new user.</param>
    /// <param name="selectedTenantIds">The tenants the user is to belong to.</param>
    /// <returns>
    /// One of <paramref name="selectedTenantIds"/>, or <c>null</c> when that set is empty.
    /// </returns>
    /// <remarks>
    /// <b>The result is always a member of the selected set, or null when there is none.</b> That is
    /// what makes the invariant total: after a caller writes both this value and the membership rows
    /// from the same set, there is no state in which the two disagree - including the "belongs to
    /// nothing" state, where both say so.
    /// <para>
    /// <b>An existing primary is kept whenever it is still selected</b>, rather than always taking
    /// the first. The user form offers a SET with no primary-tenant concept, so "first" is whatever
    /// order a multi-select happens to return; re-deriving it on every save would let an edit to a
    /// phone number silently move a multi-tenant user's primary tenant, and with it every row they
    /// subsequently create. It moves only when the current primary is no longer selected, and then
    /// to the first of what remains.
    /// </para>
    /// <para>
    /// <b>Ordinal comparison</b>, matching how tenant ids are compared everywhere else - they are
    /// GUID strings, and a culture-aware comparison of an identifier is a bug waiting for a Turkish
    /// locale.
    /// </para>
    /// </remarks>
    public static string? Resolve(string? currentTenantId, IEnumerable<string?>? selectedTenantIds)
    {
        if (selectedTenantIds is null) return null;

        var selected = selectedTenantIds
            .Where(id => !string.IsNullOrEmpty(id))
            .ToList();

        if (selected.Count == 0) return null;

        if (!string.IsNullOrEmpty(currentTenantId) &&
            selected.Any(id => string.Equals(id, currentTenantId, StringComparison.Ordinal)))
        {
            return currentTenantId;
        }

        return selected[0];
    }
}
