// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace CleanArchitecture.Blazor.Domain.Common.Entities;

/// <summary>
/// Marks an entity as a project business model: schema <c>core</c>, table name
/// <c>TBL_</c> + its plural in upper snake case, snake_case columns (GX conventions, Pass 60c).
/// </summary>
/// <remarks>
/// <see cref="BaseEntity"/> implements this, so a project entity inherits the convention simply by
/// deriving from the template's base - there is no per-entity opt-in to forget.
/// <para>
/// The template's OWN infrastructure entities deliberately stay out of <c>core</c>: Identity,
/// <c>Tenant</c>, <c>AuditTrail</c>, <c>Document</c>, <c>PicklistSet</c>, the branch, posting and approval
/// tables and <c>__EFMigrationsHistory</c> keep their existing names and columns in the default schema
/// (owner decision D2). That draws a visible line in pgAdmin between the framework's tables and this
/// business's tables, and it means upgrading the template never forces a rename migration on an existing
/// project. Those that derive from <see cref="BaseEntity"/> opt out by naming their table explicitly in their
/// <c>IEntityTypeConfiguration</c>, which the convention yields to, and are listed in <c>TemplateEntities</c>.
/// </para>
/// </remarks>
public interface IBusinessEntity
{
}

/// <summary>
/// Marks a lookup: a reference list that users pick from (GX convention §3, owner decision Q-A1). Table name
/// <c>TBL_LK_</c> + its plural, in <c>core</c>: <c>core."TBL_LK_VEHICLE_MAKES"</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The fixed columns</b> are MNEFleets' <c>BaseLkEntity</c>: an <c>int</c> key <c>Id</c>, a required
/// <c>Name</c> and an optional <c>Slug</c> (owner decision D4). Derive from <see cref="BaseLkEntity"/>, or from
/// <see cref="BaseVersionedLkEntity"/> for a lookup users edit; <c>DataModelGuard</c> refuses a lookup without
/// them at startup.
/// </para>
/// <para>
/// <b>Further columns are allowed</b> (decision Q-A2): a code, an active flag, a sort order, a description, a
/// foreign key to another lookup, the audit and version columns. A <c>decimal</c> column is not: a rate or an
/// amount makes the table master data with rules, a <c>TBL_</c> table, and the guard says so.
/// </para>
/// <para>
/// Lookups the application ships are seeded by MIGRATION with explicit ids, and the identity sequence is reset in
/// the same migration (<c>SeedLookup</c>, <c>ResetIdentitySequence</c>); lookups users maintain are edited on the
/// module's own page. The template ships no lookup editor (decision Q-A4).
/// </para>
/// </remarks>
public interface ILookupEntity : IBusinessEntity
{
    /// <summary>The key: always an <c>int</c> identity.</summary>
    int Id { get; }

    /// <summary>What users see and pick. Required.</summary>
    string Name { get; }

    /// <summary>An optional stable, URL- and code-friendly form of the name.</summary>
    string? Slug { get; }
}
