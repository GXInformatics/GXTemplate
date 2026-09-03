namespace CleanArchitecture.Blazor.Infrastructure.Services.Identity;

/// <summary>
/// Implementation of IUserContextAccessor using AsyncLocal for call chain isolation.
/// </summary>
public class UserContextAccessor : IUserContextAccessor
{
    private sealed class Node
    {
        public UserContext? Value;
        public Node? Parent;
    }


    /// <summary>
    /// The ambient context, per async call chain.
    /// </summary>
    /// <remarks>
    /// <b>Static, deliberately</b>, and this is the same design ASP.NET Core's own
    /// <see cref="Microsoft.AspNetCore.Http.HttpContextAccessor"/> uses for the same reason: the
    /// value being tracked belongs to the CALL CHAIN, not to the accessor object, so which instance
    /// you happen to hold should not change what you observe.
    /// <para>
    /// It was an instance field, which was indistinguishable from this in the application - the type
    /// is registered as a singleton, so exactly one ever existed - but it made the ambient value
    /// unreachable from anywhere that cannot resolve services. <c>UserInfoEnricher</c> is exactly
    /// that place: Serilog constructs enrichers itself, through a parameterless constructor, and the
    /// logger is configured in <c>Program.cs</c> BEFORE <c>AddInfrastructure</c> has registered
    /// anything - so there is no container to ask. It already reaches the request the same way, by
    /// newing up an <c>HttpContextAccessor</c>, and this makes the user context reachable on the
    /// same terms.
    /// </para>
    /// <para>
    /// Nothing relied on per-instance isolation: no test constructs this type (the one test double,
    /// <c>MutableUserContextAccessor</c>, implements the interface independently), and the
    /// registration has always been a singleton.
    /// </para>
    /// </remarks>
    private static readonly AsyncLocal<Node?> _current = new();
    /// <summary>
    /// Gets the current user context.
    /// </summary>
    public UserContext? Current => _current.Value?.Value;

    /// <summary>
    /// Pushes a new user context onto the stack.
    /// </summary>
    /// <param name="context">The user context to push.</param>
    /// <returns>A disposable object that will pop the context when disposed.</returns>
    public IDisposable Push(UserContext context)
    {
        var node = new Node
        {
            Value = context,
            Parent = _current.Value
        };
        _current.Value = node;
        return new Pop(node.Parent);
    }

    /// <remarks>
    /// No longer carries the accessor that created it. It used to, because the stack it restores
    /// lived on that instance; the stack is now static - see <see cref="_current"/> - so the owning
    /// object was a reference to something this type no longer needs, and holding it would have
    /// implied a per-instance scoping that no longer exists.
    /// </remarks>
    private sealed class Pop : IDisposable
    {
        private readonly Node? _restore;

        public Pop(Node? restore)
        {
            _restore = restore;
        }

        public void Dispose()
        {
            _current.Value = _restore;
        }
    }

    /// <summary>
    /// Sets the current user context.
    /// </summary>
    /// <param name="context">The user context to set.</param>
    public void Set(UserContext context)
    {
        _current.Value = new Node
        {
            Value = context,
            Parent = null
        };
    }

    /// <summary>
    /// Clears the current user context.
    /// </summary>
    public void Clear()
    {
        _current.Value = null;
    }
} 
