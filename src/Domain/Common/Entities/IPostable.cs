namespace CleanArchitecture.Blazor.Domain.Common.Entities;

/// <summary>
/// A ledger row (Pass 59): inserted, never updated or deleted. A mistake is undone by posting a
/// reversing entry. Any Modified or Deleted entry fails the save; bulk <c>UPDATE</c>/<c>DELETE</c> through
/// the context is refused; and the table's database triggers (created with
/// <c>migrationBuilder.CreateAppendOnlyTrigger</c>, checked at startup) refuse <c>UPDATE</c>, <c>DELETE</c>
/// and <c>TRUNCATE</c> from any path.
/// </summary>
public interface IAppendOnly
{
}

/// <summary>
/// A document header that becomes immutable once posted (Pass 59).
/// </summary>
/// <remarks>
/// <b>Posted</b> means the <i>stored</i> value of <see cref="PostedAt"/> is not null. Once posted, the
/// document cannot be deleted, and only properties marked <see cref="AllowedAfterPostingAttribute"/> (and
/// the audit fields) may change: a reversal sets its status and link, nothing else. <c>PostedAt</c> itself
/// never changes back. A header posted in the current unit of work stays editable until it commits, so
/// that posting-event handlers can finish the posting.
/// <para>
/// A postable header must also be <see cref="IVersioned"/>: a change to its lines touches the header, so
/// the line edit and a concurrent post conflict on the header's version instead of a line slipping into
/// a document that was posted without it.
/// </para>
/// </remarks>
public interface IPostable
{
    DateTime? PostedAt { get; }
}

/// <summary>
/// A line of a postable document (Pass 59). Once the header is posted, lines cannot be added or deleted,
/// and a line may change only in its <see cref="AllowedAfterPostingAttribute"/> properties. Each line
/// change also touches the tracked header, so it carries the header's version check.
/// </summary>
/// <typeparam name="THeader">The header type; the line must have a navigation to it.</typeparam>
public interface IPostableLine<THeader> where THeader : class, IPostable
{
    THeader? Header { get; }
}

/// <summary>
/// Marks a property of an <see cref="IPostable"/> header, or of an <see cref="IPostableLine{THeader}"/>,
/// that may still change after the document is posted: a status moving to Reversed, a link to the
/// reversing entry, a returned quantity on a line. Not allowed on <c>PostedAt</c> or on a key.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class AllowedAfterPostingAttribute : Attribute;
