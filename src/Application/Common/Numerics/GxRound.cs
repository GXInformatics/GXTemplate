using CleanArchitecture.Blazor.Domain.Common.Precision;

namespace CleanArchitecture.Blazor.Application.Common.Numerics;

/// <summary>
/// The one rounding rule for stored decimals (Pass 59, owner decision 1).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description><b>Midpoint away from zero</b> at the kind's scale: 2.345 → 2.35, −2.345 → −2.35. This is
/// what commercial invoices do, and what PostgreSQL itself does with <c>numeric</c>.</description></item>
/// <item><description><b>Round once</b>, when a value is computed for storage: a line amount is
/// <c>GxRound.Money(quantity * unitCost)</c>. Never round intermediate arithmetic.</description></item>
/// <item><description><b>A total is the sum of stored values</b>, never a recomputation from unrounded parts.</description></item>
/// <item><description>A value with more decimals than its kind allows is <b>refused at save</b>, so a forgotten
/// call here fails loudly instead of being rounded by the database.</description></item>
/// </list>
/// </remarks>
public static class GxRound
{
    public const MidpointRounding Rule = MidpointRounding.AwayFromZero;

    private static readonly int MoneyScale = new MoneyAttribute().Scale;
    private static readonly int QuantityScale = new QuantityAttribute().Scale;
    private static readonly int UnitCostScale = new UnitCostAttribute().Scale;
    private static readonly int RateScale = new RateAttribute().Scale;

    /// <summary>Rounds to a <see cref="MoneyAttribute"/> value (2 places).</summary>
    public static decimal Money(decimal value) => Math.Round(value, MoneyScale, Rule);

    /// <summary>Rounds to a <see cref="QuantityAttribute"/> value (4 places).</summary>
    public static decimal Quantity(decimal value) => Math.Round(value, QuantityScale, Rule);

    /// <summary>Rounds to a <see cref="UnitCostAttribute"/> value (4 places).</summary>
    public static decimal UnitCost(decimal value) => Math.Round(value, UnitCostScale, Rule);

    /// <summary>Rounds to a <see cref="RateAttribute"/> value (6 places).</summary>
    public static decimal Rate(decimal value) => Math.Round(value, RateScale, Rule);

    /// <summary>Rounds to the scale of <paramref name="kind"/>.</summary>
    public static decimal To(DecimalKindAttribute kind, decimal value) => Math.Round(value, kind.Scale, Rule);

    /// <summary>
    /// The number of decimal places <paramref name="value"/> actually needs, ignoring trailing zeros
    /// (1.50m needs 1). Used by the save check.
    /// </summary>
    public static int SignificantScale(decimal value)
    {
        int scale = value.Scale;
        while (scale > 0 && decimal.Round(value, scale - 1) == value) scale--;
        return scale;
    }
}
