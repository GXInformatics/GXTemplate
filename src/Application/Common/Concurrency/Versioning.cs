using System.Runtime.CompilerServices;
using CleanArchitecture.Blazor.Domain.Common.Entities;

namespace CleanArchitecture.Blazor.Application.Common.Concurrency;

/// <summary>
/// An edit command for an <see cref="IVersioned"/> entity (Pass 59): it carries back the version the user
/// loaded. <see cref="Version"/> is nullable so that "not supplied" is distinguishable from a value;
/// <c>ValidationBehavior</c> refuses an edit (<see cref="IsEdit"/>) that does not supply one.
/// </summary>
public interface IVersionedEdit
{
    /// <summary>The <c>Version</c> of the row as the user loaded it. Required for an edit.</summary>
    uint? Version { get; }

    /// <summary>True when the command changes an existing row, false when it creates one.</summary>
    bool IsEdit { get; }
}

/// <summary>
/// The result of saving a versioned entity (Pass 59, amendment B4): its key and its <b>new</b> version,
/// which the form keeps in place of the one it loaded, so its next save does not conflict with its own
/// last one.
/// </summary>
/// <remarks>
/// <b>A reference type on purpose.</b> Inside a unit of work the handler returns before the last save:
/// <c>TransactionBehaviour</c> saves again, and posting-event rounds may update the same row. A value
/// copied into the result by the handler would then be stale. <see cref="VersionExtensions.SavedVersionOf"/>
/// registers this object with the context, and every successful save on that context refreshes
/// <see cref="Version"/> from the entity, so the result holds the committed version.
/// </remarks>
public sealed class Saved<TId>
{
    public TId Id { get; set; } = default!;

    public uint Version { get; set; }
}

/// <summary>Helpers for the concurrency token (Pass 59). See the README, "Concurrency".</summary>
public static class VersionExtensions
{
    /// <summary>The Npgsql provider name; the token is PostgreSQL's <c>xmin</c>.</summary>
    public const string NpgsqlProvider = "Npgsql.EntityFrameworkCore.PostgreSQL";

    /// <summary>
    /// Makes the save check against the version the user loaded: sets it as the original value of
    /// <c>Version</c>, so EF issues <c>… WHERE xmin = @loaded</c> and a row changed since then raises
    /// <c>DbUpdateConcurrencyException</c> ("This record was changed by someone else…").
    /// </summary>
    /// <exception cref="InvalidOperationException"><paramref name="loadedVersion"/> is null.</exception>
    /// <exception cref="NotSupportedException">The provider is not PostgreSQL.</exception>
    public static void ApplyLoadedVersion<TEntity>(this IApplicationDbContext db, TEntity entity, uint? loadedVersion)
        where TEntity : class, IVersioned
    {
        ArgumentNullException.ThrowIfNull(entity);
        RequirePostgreSql(db);
        if (loadedVersion is null)
            throw new InvalidOperationException(
                $"{typeof(TEntity).Name}: the edit did not carry the version the user loaded. " +
                "Put the DTO's Version on the command (IVersionedEdit).");

        db.Entry(entity).Property(nameof(IVersioned.Version)).OriginalValue = loadedVersion.Value;
        VersionChecks.Mark(entity);
    }

    /// <summary>
    /// For a server-side update with no user-loaded version (for example a Post handler changing a header
    /// it locked with <c>ForUpdate()</c>): checks against the version just read. Still a check - the save
    /// fails if the row changed between the read and the save.
    /// </summary>
    public static void UseCurrentVersion<TEntity>(this IApplicationDbContext db, TEntity entity)
        where TEntity : class, IVersioned
    {
        ArgumentNullException.ThrowIfNull(entity);
        RequirePostgreSql(db);
        VersionChecks.Mark(entity);
    }

    /// <summary>
    /// The result to return from a save of <paramref name="entity"/> (amendment B4). Its
    /// <see cref="Saved{TId}.Version"/> is refreshed after every save on <paramref name="db"/>, so it holds
    /// the committed version even when a unit of work saves after the handler has returned.
    /// </summary>
    public static Saved<TId> SavedVersionOf<TEntity, TId>(this IApplicationDbContext db, TEntity entity, TId id)
        where TEntity : class, IVersioned
    {
        ArgumentNullException.ThrowIfNull(entity);
        var saved = new Saved<TId> { Id = id, Version = entity.Version };
        SavedVersions.Register(db, entity, v => saved.Version = v);
        return saved;
    }

    private static void RequirePostgreSql(IApplicationDbContext db)
    {
        if (db.Database.ProviderName != NpgsqlProvider)
            throw new NotSupportedException("Concurrency tokens (IVersioned) are implemented for PostgreSQL only.");
    }
}

/// <summary>
/// Which versioned entities had their version applied (Pass 59). Read by the save check, which refuses a
/// Modified or Deleted <see cref="IVersioned"/> entity that is not marked here. Infrastructure only.
/// </summary>
/// <remarks>
/// <b>(P59, owner decision) The save check applies inside Mediator requests only.</b>
/// <c>ValidationBehavior</c>, which runs for every request, marks the call chain with
/// <see cref="EnterRequest"/>. Any handler that saves a Modified or Deleted versioned entity without applying a
/// version fails loudly, whatever its command type. Code that uses a context directly, outside Mediator
/// (seeding, infrastructure services, and the pinned interceptor suites, which edit a <c>PicklistSet</c> through
/// a plain context and must pass unmodified), is not checked; EF still compares against the version it read.
/// </remarks>
public static class VersionChecks
{
    private static readonly ConditionalWeakTable<object, object> Marked = new();
    private static readonly AsyncLocal<int> RequestDepth = new();

    public static void Mark(object entity) => Marked.AddOrUpdate(entity, Marked);

    public static bool IsMarked(object entity) => Marked.TryGetValue(entity, out _);

    /// <summary>True inside a Mediator request's call chain, where the save check applies.</summary>
    public static bool InRequest => RequestDepth.Value > 0;

    /// <summary>Marks the current call chain as a Mediator request until the result is disposed.</summary>
    public static IDisposable EnterRequest()
    {
        RequestDepth.Value++;
        return new Exit();
    }

    private sealed class Exit : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            RequestDepth.Value--;
        }
    }
}

/// <summary>
/// The <see cref="Saved{TId}"/> results registered against a context (amendment B4), refreshed after each
/// successful save by <c>ApplicationDbContext</c>. Infrastructure only.
/// </summary>
public static class SavedVersions
{
    private sealed class Registrations
    {
        public readonly List<(IVersioned Entity, Action<uint> Set)> Items = new();
    }

    private static readonly ConditionalWeakTable<object, Registrations> ByContext = new();

    public static void Register(object context, IVersioned entity, Action<uint> set)
    {
        var registrations = ByContext.GetOrCreateValue(context);
        lock (registrations) registrations.Items.Add((entity, set));
    }

    /// <summary>Copies each registered entity's current <c>Version</c> into its result.</summary>
    public static void Refresh(object context)
    {
        if (!ByContext.TryGetValue(context, out var registrations)) return;
        lock (registrations)
        {
            foreach (var (entity, set) in registrations.Items) set(entity.Version);
        }
    }
}

/// <summary>
/// Recognises the concurrency-conflict failure (Pass 59). Since Pass 60 the result carries
/// <c>ErrorCode.ConcurrencyConflict</c>; the message comparison stays for a result built without the code.
/// </summary>
public static class ConcurrencyConflict
{
    /// <summary>The message <c>ResultExceptionBehavior</c> returns for <c>DbUpdateConcurrencyException</c>.</summary>
    public const string Message = "This record was changed by someone else. Reload and try again.";

    /// <summary>True when <paramref name="result"/> failed because the row changed since it was loaded.</summary>
    public static bool Is(IResult result) =>
        !result.Succeeded
        && (string.Equals(result.ErrorCode, Models.ErrorCode.ConcurrencyConflict.Value, StringComparison.Ordinal)
            || result.Errors.Any(e => string.Equals(e, Message, StringComparison.Ordinal)));
}
