namespace CleanArchitecture.Blazor.Domain.Common.Entities;

/// <summary>
/// Overrides the plural the GX naming convention would give an entity's table (Pass 60c, owner decision D3).
/// </summary>
/// <remarks>
/// Table names are <c>TBL_</c> + the plural entity name in upper snake case: <c>StockMovement</c> →
/// <c>core."TBL_STOCK_MOVEMENTS"</c>. The plural is worked out by fixed rules (README "Database naming"); use this
/// attribute only where those rules give the wrong word, and give the PLURAL OF THE WHOLE CLASS NAME, in PascalCase:
/// <code>[PluralName("Criteria")] public class Criterion : BaseEntity { … }              // core."TBL_CRITERIA"
/// [PluralName("StockHistory")] public class StockHistory : BaseEntity { … }      // keeps a singular on purpose</code>
/// The table list (<c>tables.approved.txt</c>) shows the result in review either way.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class PluralNameAttribute(string plural) : Attribute
{
    /// <summary>The plural of the whole class name, PascalCase, e.g. <c>Criteria</c>.</summary>
    public string Plural { get; } = plural;
}
