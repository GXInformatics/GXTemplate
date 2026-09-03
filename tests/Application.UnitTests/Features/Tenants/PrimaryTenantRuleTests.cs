#nullable enable
using System;
using System.Collections.Generic;
using CleanArchitecture.Blazor.Application.Features.Tenants;
using FluentAssertions;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.Features.Tenants;

/// <summary>
/// The rule that keeps <c>ApplicationUser.TenantId</c> and <c>TenantUsers</c> from disagreeing.
/// </summary>
/// <remarks>
/// The property under test is <b>totality</b>: for every input, the answer is a member of the
/// selected set or null when that set is empty. A caller that writes this value and the membership
/// rows from the same set therefore cannot produce a divergent user - which is what the user-edit
/// dialog did for as long as it assigned the primary tenant only when it was already empty.
/// </remarks>
[TestFixture]
public class PrimaryTenantRuleTests
{
    private const string A = "tenant-a";
    private const string B = "tenant-b";
    private const string C = "tenant-c";

    [Test]
    public void ANewUserTakesTheFirstSelectedTenant()
    {
        PrimaryTenantRule.Resolve(null, new[] { A, B }).Should().Be(A);
    }

    [Test]
    public void MovingAUserFromOneTenantToAnother_MovesThePrimaryTenantWithThem()
    {
        // The defect, stated directly. Before Pass 25 this returned "tenant-a" - the caller assigned
        // the primary only when it was empty - while the membership rows were rewritten to B.
        PrimaryTenantRule.Resolve(currentTenantId: A, selectedTenantIds: new[] { B }).Should().Be(B);
    }

    [Test]
    public void AnExistingPrimaryIsKeptWhenItIsStillSelected()
    {
        // Stability. The form is a set with no primary-tenant concept, so "first" is whatever order
        // the multi-select returns; re-deriving unconditionally would let an edit to an unrelated
        // field move a multi-tenant user's primary tenant, and with it every row they go on to
        // create.
        PrimaryTenantRule.Resolve(currentTenantId: B, selectedTenantIds: new[] { A, B, C }).Should().Be(B);
    }

    [Test]
    public void AnExistingPrimaryMovesOnlyWhenItIsNoLongerSelected()
    {
        PrimaryTenantRule.Resolve(currentTenantId: C, selectedTenantIds: new[] { A, B }).Should().Be(A);
    }

    [Test]
    public void AnEmptySelectionYieldsNoPrimaryTenant()
    {
        // "Belongs to nothing" is representable, and both halves say so - rather than leaving a
        // primary tenant with no membership row behind it.
        PrimaryTenantRule.Resolve(currentTenantId: A, selectedTenantIds: Array.Empty<string>()).Should().BeNull();
    }

    [Test]
    public void ANullSelectionYieldsNoPrimaryTenant()
    {
        PrimaryTenantRule.Resolve(A, null).Should().BeNull();
    }

    [Test]
    public void EmptyAndNullEntriesAreNotEligibleToBecomeThePrimaryTenant()
    {
        // A blank id is not a tenant. Taking selected[0] blindly would have made one the primary.
        PrimaryTenantRule.Resolve(null, new[] { null, "", B }).Should().Be(B);
        PrimaryTenantRule.Resolve(null, new string?[] { null, "" }).Should().BeNull();
    }

    [Test]
    public void TheResultIsAlwaysAMemberOfTheSelectedSetOrNull()
    {
        // The invariant itself, over every combination that matters, rather than one example of it.
        var currents = new string?[] { null, "", A, B, C, "tenant-gone" };
        var selections = new List<string?[]>
        {
            Array.Empty<string?>(),
            new string?[] { A },
            new string?[] { A, B },
            new string?[] { B, C },
            new string?[] { A, B, C },
            new string?[] { null, "", A }
        };

        foreach (var current in currents)
        foreach (var selection in selections)
        {
            var result = PrimaryTenantRule.Resolve(current, selection);

            if (result is null) continue;

            selection.Should().Contain(result,
                $"Resolve(\"{current}\", [{string.Join(",", selection)}]) returned a tenant that was not selected");
        }
    }

    [Test]
    public void AnEmptySelectionAlwaysYieldsNull_WhateverThePrevious()
    {
        foreach (var current in new string?[] { null, "", A, "tenant-gone" })
        {
            PrimaryTenantRule.Resolve(current, Array.Empty<string>()).Should().BeNull();
        }
    }
}
#nullable restore
