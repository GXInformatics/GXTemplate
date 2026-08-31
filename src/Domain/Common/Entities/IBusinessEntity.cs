// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace CleanArchitecture.Blazor.Domain.Common.Entities;

/// <summary>
/// Marks an entity as a project business model: schema <c>core</c>, table name
/// <c>TBL_UPPER_SNAKE</c>.
/// </summary>
/// <remarks>
/// <see cref="BaseEntity"/> implements this, so a project entity inherits the convention simply by
/// deriving from the template's base - there is no per-entity opt-in to forget.
/// <para>
/// The template's OWN infrastructure entities deliberately stay out of <c>core</c>: Identity,
/// <c>Tenant</c>, <c>AuditTrail</c>, <c>Document</c>, <c>PicklistSet</c> and
/// <c>__EFMigrationsHistory</c> keep their existing names in the default schema. That draws a
/// visible line in pgAdmin between the framework's tables and this business's tables, and it means
/// upgrading the template never forces a rename migration on an existing project. The two that do
/// derive from <see cref="BaseEntity"/> - Document and PicklistSet - opt out by naming their table
/// explicitly in their <c>IEntityTypeConfiguration</c>, which the convention yields to.
/// </para>
/// </remarks>
public interface IBusinessEntity
{
}

/// <summary>
/// Marks a pure code/description lookup: table name <c>TBL_LK_UPPER_SNAKE</c>.
/// </summary>
/// <remarks>
/// The test is one question: <b>does any code branch on this row's value?</b>
/// <list type="bullet">
/// <item><description><b>No</b> - it is a lookup. The application treats every row identically; it
/// is code plus description and nothing else, and a client can safely add rows without a
/// developer.</description></item>
/// <item><description><b>Yes</b> - it is not a lookup, however small or dropdown-ish it looks. It
/// has rules attached and the code cares which row it is. A tax code carrying rates and GL
/// mappings is <c>TBL_</c>, not <c>TBL_LK_</c>.</description></item>
/// </list>
/// <para>
/// Deliberately a two-way split. Resist adding a third category for "master data with behaviour":
/// the boundary between a master and a transactional document is a spectrum (versioned price lists,
/// BOMs with lifecycle states), so a third category produces per-entity judgement calls and
/// therefore inconsistency. The question above has exactly one defensible answer per table.
/// </para>
/// <para>
/// Having <b>no</b> lookup tables at all is a normal outcome - a status or a movement type that the
/// code switches on belongs in a C# enum stored as a string, not in a table. <c>TBL_LK_</c> exists
/// for when a client genuinely needs to add rows at runtime.
/// </para>
/// </remarks>
public interface ILookupEntity : IBusinessEntity
{
}
