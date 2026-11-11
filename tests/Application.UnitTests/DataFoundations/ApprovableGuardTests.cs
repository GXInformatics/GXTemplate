#nullable enable
using System;
using System.Collections.Generic;
using CleanArchitecture.Blazor.Domain.Common.Entities;
using CleanArchitecture.Blazor.Domain.Common.Modules;
using CleanArchitecture.Blazor.Domain.Common.Precision;
using CleanArchitecture.Blazor.Infrastructure.Persistence.Rules;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.DataFoundations;

// Pass 60 plan §5 row 14: the model rules for an approvable document.

[Module<GuardModule>]
public class UnversionedApprovable : BaseEntity<Guid>, IApprovable<UnversionedApprovable>
{
    public static string ApprovalDocumentType => "guard.unversioned";
    public string? PreparedById { get; set; }
    [Money] public decimal ApprovalValue { get; set; }
    public string? ApprovedById { get; set; }
    public DateTime? ApprovedAt { get; set; }
}

[Module<GuardModule>]
public class QuantityValuedApprovable : BaseVersionedEntity<Guid>, IApprovable<QuantityValuedApprovable>
{
    public static string ApprovalDocumentType => "guard.quantity";
    public string? PreparedById { get; set; }
    [Quantity] public decimal ApprovalValue { get; set; }
    public string? ApprovedById { get; set; }
    public DateTime? ApprovedAt { get; set; }
}

[Module<GuardModule>]
public class FirstSharedCode : BaseVersionedEntity<Guid>, IApprovable<FirstSharedCode>
{
    public static string ApprovalDocumentType => "guard.shared";
    public string? PreparedById { get; set; }
    [Money] public decimal ApprovalValue { get; set; }
    public string? ApprovedById { get; set; }
    public DateTime? ApprovedAt { get; set; }
}

[Module<GuardModule>]
public class SecondSharedCode : BaseVersionedEntity<Guid>, IApprovable<SecondSharedCode>
{
    public static string ApprovalDocumentType => "guard.shared";
    public string? PreparedById { get; set; }
    [Money] public decimal ApprovalValue { get; set; }
    public string? ApprovedById { get; set; }
    public DateTime? ApprovedAt { get; set; }
}

/// <summary>C1: an approvable with no branch scope at all is a sound model.</summary>
[Module<GuardModule>]
public class UnscopedApprovable : BaseVersionedEntity<Guid>, IApprovable<UnscopedApprovable>
{
    public static string ApprovalDocumentType => "guard.unscoped";
    public string? PreparedById { get; set; }
    [Money] public decimal ApprovalValue { get; set; }
    public string? ApprovedById { get; set; }
    public DateTime? ApprovedAt { get; set; }
}

public sealed class UnsoundApprovablesContext() : GuardContext(Options())
{
    protected override void Add(ModelBuilder b)
    {
        b.Entity<UnversionedApprovable>();
        b.Entity<QuantityValuedApprovable>();
        b.Entity<FirstSharedCode>();
        b.Entity<SecondSharedCode>();
    }
}

public sealed class UnscopedApprovableContext() : GuardContext(Options()) { protected override void Add(ModelBuilder b) => b.Entity<UnscopedApprovable>(); }

public class ApprovableGuardTests
{
    [Test]
    public void T14_AnApprovableMustBeVersioned_ValuedInMoney_AndHaveItsOwnCode()
    {
        IReadOnlyList<string> problems;
        using (var context = new UnsoundApprovablesContext()) problems = DataModelGuard.FindProblems(context.Model);

        problems.Should().Contain(p => p.StartsWith("UnversionedApprovable:") && p.Contains("not IVersioned"));
        problems.Should().Contain(p => p.StartsWith("QuantityValuedApprovable.ApprovalValue") && p.Contains("[Money]"));
        problems.Should().Contain(p => p.Contains("FirstSharedCode and SecondSharedCode share the approval document type 'guard.shared'"));
    }

    [Test]
    public void T14_C1_AnApprovableWithNoBranchScope_IsAccepted()
    {
        using var context = new UnscopedApprovableContext();
        DataModelGuard.FindProblems(context.Model).Should().BeEmpty();
    }
}
