namespace CleanArchitecture.Blazor.Domain.Common.Precision;

/// <summary>
/// What a <c>decimal</c> property holds, and therefore its precision and scale in the database (Pass 59).
/// </summary>
/// <remarks>
/// <b>Every decimal in the model declares one, or the application does not start.</b>
/// <c>DataModelGuard</c> refuses an undeclared decimal, a decimal with two kinds, a kind on a property
/// that is not a decimal, and a configuration that changed the precision a kind sets. The column types
/// are applied by <c>ApplicationDbContext</c>'s precision convention.
/// <para>
/// A value with more decimal places than its kind allows is <b>refused at save</b>, not rounded:
/// PostgreSQL would round it silently on insert, and the stored value would then differ from the one
/// the application computed. Round explicitly with <c>GxRound</c>, once, when the stored value is
/// computed.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public abstract class DecimalKindAttribute : Attribute
{
    protected DecimalKindAttribute(int precision, int scale)
    {
        Precision = precision;
        Scale = scale;
    }

    /// <summary>Total digits.</summary>
    public int Precision { get; }

    /// <summary>Digits after the decimal point.</summary>
    public int Scale { get; }

    /// <summary>The kind's name as it appears in messages, e.g. <c>[Money]</c>.</summary>
    public string KindName => "[" + GetType().Name.Replace("Attribute", string.Empty, StringComparison.Ordinal) + "]";
}

/// <summary>An amount in currency: totals, prices, balances. <c>numeric(18,2)</c>.</summary>
public sealed class MoneyAttribute() : DecimalKindAttribute(18, 2);

/// <summary>A quantity of something. <c>numeric(18,4)</c>.</summary>
public sealed class QuantityAttribute() : DecimalKindAttribute(18, 4);

/// <summary>A cost or price per unit. <c>numeric(18,4)</c>. Separate from <see cref="QuantityAttribute"/> so intent stays readable.</summary>
public sealed class UnitCostAttribute() : DecimalKindAttribute(18, 4);

/// <summary>A rate: tax rate, markup percent, exchange rate. <c>numeric(18,6)</c>.</summary>
public sealed class RateAttribute() : DecimalKindAttribute(18, 6);

/// <summary>
/// Allows a <c>float</c> or <c>double</c> property on an entity. Without it, <c>DataModelGuard</c>
/// refuses one: binary floating point cannot hold 0.1 exactly, so it has no place in amounts or
/// quantities. Use it only for genuinely approximate data, such as coordinates.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class ApproximateAttribute : Attribute;
