using System.Collections.Concurrent;
using System.Reflection;
using CleanArchitecture.Blazor.Domain.Common;

namespace CleanArchitecture.Blazor.Application.Common.Transactions;

/// <summary>
/// Refuses a posting event that captured an id before its row was saved (Pass 60c, owner decisions D6 and Q-J).
/// </summary>
/// <remarks>
/// <para>
/// Keys are integer identities (owner decision D6): a new row's id is 0 until <c>SaveChanges</c> assigns it. An event
/// raised while the row is being built and given <c>entity.Id</c> in its constructor therefore carries 0 for ever,
/// and its handlers write rows pointing at nothing. The pattern is <c>BranchCreatedEvent</c>'s: keep the entity and
/// read the id when asked, which is after the save, because <c>TransactionBehaviour</c> saves before it dispatches.
/// <code>public sealed class WarehouseCreated(Warehouse warehouse) : PostingEvent
/// {
///     private readonly Warehouse _warehouse = warehouse;
///     public int WarehouseId => _warehouse.Id;      // read at dispatch, after the save
/// }</code>
/// </para>
/// <para>
/// The check runs on every posting event just before it is dispatched: a public <c>int</c> or <c>long</c> property
/// whose name ends in <c>Id</c> and whose value is 0 fails, naming the event and the property. An id that may
/// legitimately be absent is <c>int?</c> / <c>long?</c>, which the check skips.
/// </para>
/// </remarks>
public static class PostingEventIdCheck
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> IdProperties = new();

    /// <summary>Throws <see cref="InvalidOperationException"/> when <paramref name="postingEvent"/> carries an id of 0.</summary>
    public static void Assert(PostingEvent postingEvent)
    {
        var type = postingEvent.GetType();
        foreach (var property in IdProperties.GetOrAdd(type, Find))
        {
            var value = property.GetValue(postingEvent);
            if (value is 0 or 0L)
                throw new InvalidOperationException(
                    $"{type.Name}.{property.Name} is 0: the event captured the id before the row was saved. Keep the entity in the " +
                    $"event and read its Id when asked (README \"Posting transactions\", the BranchCreatedEvent pattern); an id that " +
                    "may be absent is nullable.");
        }
    }

    private static PropertyInfo[] Find(Type type) =>
        type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.GetIndexParameters().Length == 0
                        && (p.PropertyType == typeof(int) || p.PropertyType == typeof(long))
                        && p.Name.EndsWith("Id", StringComparison.Ordinal))
            .ToArray();
}
