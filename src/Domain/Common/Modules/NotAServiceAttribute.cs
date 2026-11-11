namespace CleanArchitecture.Blazor.Domain.Common.Modules;

/// <summary>
/// Declares that an interface in a module's <c>Contracts</c> namespace is not a service (Pass 60b, amendment E1): a
/// shape other modules implement on their own types, or a marker, never resolved from the container.
/// </summary>
/// <remarks>
/// Every other public interface in a <c>…Modules.&lt;M&gt;.Contracts</c> namespace must be registered exactly once,
/// or startup fails naming it (<c>ModuleServiceGuard</c>). Nothing is inferred from the interface's members: an
/// empty interface is checked like any other unless it carries this attribute. A marked interface that is
/// registered anyway also fails startup, because the marking would then be wrong.
/// <code>
/// [NotAService("Implemented by each module's document headers; read by type tests, never resolved.")]
/// public interface IHasStockEffect { … }
/// </code>
/// </remarks>
[AttributeUsage(AttributeTargets.Interface, Inherited = false, AllowMultiple = false)]
public sealed class NotAServiceAttribute(string reason) : Attribute
{
    /// <summary>Why the interface is never resolved. Must not be blank.</summary>
    public string Reason { get; } = reason;
}
