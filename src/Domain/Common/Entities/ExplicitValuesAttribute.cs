namespace CleanArchitecture.Blazor.Domain.Common.Entities;

/// <summary>
/// Declares that every member of this enum has an explicit numeric value (Pass 60c, owner decision Q-H).
/// </summary>
/// <remarks>
/// Enums on entities are stored as <c>integer</c> (GX convention §6.3), so the stored number IS the contract: a
/// member inserted in the middle of an enum without values silently renumbers every row after it.
/// <code>[ExplicitValues] public enum WarehouseKind { Main = 1, InTransit = 2, Quarantine = 3 }</code>
/// <c>DataModelGuard</c> refuses at startup an enum used by a business entity without this attribute, and the shipped
/// test <c>EnumValuesAreExplicitTests</c> reads the source of every such enum and fails on a member without
/// <c>= value</c>, so the attribute cannot be put on an enum that does not deserve it.
/// </remarks>
[AttributeUsage(AttributeTargets.Enum, Inherited = false, AllowMultiple = false)]
public sealed class ExplicitValuesAttribute : Attribute
{
}
