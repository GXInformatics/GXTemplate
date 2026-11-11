using System.Data;
using System.Runtime.CompilerServices;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Transactions;
using CleanArchitecture.Blazor.Domain.Common;
using Microsoft.EntityFrameworkCore.Storage;
#if (UsePostgreSql)
using Npgsql;
#endif

namespace CleanArchitecture.Blazor.Infrastructure.Persistence.Transactions;

/// <summary>
/// Where the active unit of work lives (Pass 58).
/// </summary>
/// <remarks>
/// <para>
/// <b>An AsyncLocal holding a HOLDER, not the session</b> - the <c>HttpContextAccessor</c> pattern.
/// An <see cref="ExecutionContext"/> captured while the unit of work is active (the background
/// notification publisher captures one per publish, Pass 39) copies the reference to the holder.
/// Ending the unit of work EMPTIES the holder, so every such copy sees no unit of work from then on
/// - never a disposed context.
/// </para>
/// <para>
/// <b>And a context registry</b>, like <c>AuditWriteScope</c>: the events interceptor is handed a
/// context, not a call chain, and asks the registry whether that context belongs to a unit of work.
/// </para>
/// <para>
/// <b>The holder is installed SYNCHRONOUSLY, in the caller's flow, and filled later.</b> A value an
/// <c>async</c> method assigns to an AsyncLocal is reverted when that method returns to its caller,
/// so installing it from inside an async <c>BeginAsync</c> would leave the caller - the behaviour,
/// and through it the handler - seeing nothing. <see cref="InstallHolder"/> runs in the non-async
/// part of <c>BeginAsync</c>, i.e. in the behaviour's own execution context; the session is attached
/// to the same holder object once the context and transaction are open.
/// </para>
/// </remarks>
internal static class UnitOfWorkScope
{
    internal sealed class Holder
    {
        public UnitOfWorkSession? Session;
    }

    private static readonly AsyncLocal<Holder?> Ambient = new();
    private static readonly ConditionalWeakTable<DbContext, UnitOfWorkSession> ByContext = new();

    public static UnitOfWorkSession? Current => Ambient.Value?.Session;

    /// <summary>The session that owns <paramref name="context"/>, if any.</summary>
    public static UnitOfWorkSession? Find(DbContext context) =>
        ByContext.TryGetValue(context, out var session) && !session.IsEnded ? session : null;

    /// <summary>Installs an empty holder in the CALLER's execution context. Must not be called from an async method.</summary>
    public static Holder InstallHolder()
    {
        var holder = new Holder();
        Ambient.Value = holder;
        return holder;
    }

    public static void Attach(Holder holder, UnitOfWorkSession session)
    {
        ByContext.AddOrUpdate(session.DbContext, session);
        holder.Session = session;
    }

    /// <summary>Empties the holder - for this call chain and every execution context that copied it.</summary>
    public static void Exit(Holder holder, UnitOfWorkSession session)
    {
        if (ReferenceEquals(holder.Session, session)) holder.Session = null;
        ByContext.Remove(session.DbContext);
    }
}

/// <summary>One unit of work (Pass 58). See <see cref="IUnitOfWorkSession"/>.</summary>
internal sealed class UnitOfWorkSession : IUnitOfWorkSession
{
    private readonly ApplicationDbContext _context;
    private readonly IDbContextTransaction _transaction;
    private readonly UnitOfWorkScope.Holder _holder;
    private readonly ILogger _logger;
    private readonly List<PostingEvent> _postingEvents = new();
    private readonly List<DomainEvent> _afterCommitEvents = new();
    private readonly List<string> _failureErrors = new();
    private bool _committed;

    public UnitOfWorkSession(ApplicationDbContext context, IDbContextTransaction transaction, UnitOfWorkScope.Holder holder, ILogger logger)
    {
        _context = context;
        _transaction = transaction;
        _holder = holder;
        _logger = logger;
    }

    public IApplicationDbContext Context => _context;
    internal DbContext DbContext => _context;
    internal bool IsEnded { get; private set; }

    /// <summary>Called by the events interceptor in place of publishing.</summary>
    internal void Buffer(IEnumerable<DomainEvent> events)
    {
        foreach (var domainEvent in events)
        {
            if (domainEvent is PostingEvent postingEvent) _postingEvents.Add(postingEvent);
            else _afterCommitEvents.Add(domainEvent);
        }
    }

    public IReadOnlyList<PostingEvent> TakePostingEvents()
    {
        var taken = _postingEvents.ToList();
        _postingEvents.Clear();
        return taken;
    }

    public IReadOnlyList<DomainEvent> TakeAfterCommitEvents()
    {
        var taken = _afterCommitEvents.ToList();
        _afterCommitEvents.Clear();
        return taken;
    }

    public void Fail(IReadOnlyList<string> errors, Exception? cause)
    {
        HasFailed = true;
        _failureErrors.AddRange(errors);
        FailureCause ??= cause;
    }

    public bool HasFailed { get; private set; }
    public IReadOnlyList<string> FailureErrors => _failureErrors;
    public Exception? FailureCause { get; private set; }

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        if (HasFailed) throw new InvalidOperationException("A failed unit of work cannot be committed.");
        await _transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        _committed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (IsEnded) return;
        IsEnded = true;
        UnitOfWorkScope.Exit(_holder, this);

        try
        {
            if (!_committed)
            {
                _postingEvents.Clear();
                _afterCommitEvents.Clear();
                await _transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            // A failed commit or a broken connection can leave nothing to roll back; the transaction
            // is discarded with the connection either way, so this must not mask the original error.
            _logger.LogWarning(exception, "Rolling back a unit of work failed; the transaction is discarded with its connection.");
        }
        finally
        {
            await _transaction.DisposeAsync().ConfigureAwait(false);
            _context.LeasedToUnitOfWork = false;
            await _context.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Begins units of work, and answers for the active one (Pass 58). Registered scoped as both
/// <see cref="IUnitOfWorkManager"/> and <see cref="IUnitOfWork"/>.
/// </summary>
internal sealed class UnitOfWorkManager : IUnitOfWorkManager, IUnitOfWork
{
    /// <summary>PostgreSQL deadlock_detected and serialization_failure.</summary>
    private static readonly HashSet<string> RetryableSqlStates = ["40P01", "40001"];

    private readonly IDbContextFactory<ApplicationDbContext> _contextFactory;
    private readonly ILogger<UnitOfWorkManager> _logger;

    public UnitOfWorkManager(IDbContextFactory<ApplicationDbContext> contextFactory, ILogger<UnitOfWorkManager> logger)
    {
        _contextFactory = contextFactory;
        _logger = logger;
    }

    public IUnitOfWorkSession? Current => UnitOfWorkScope.Current;

    public bool IsActive => UnitOfWorkScope.Current is not null;

    public IApplicationDbContext Context =>
        UnitOfWorkScope.Current?.Context
        ?? throw new InvalidOperationException("There is no active unit of work. Send the request as an ITransactionalRequest.");

    /// <remarks>
    /// <b>Deliberately not <c>async</c>.</b> The holder must be installed in the CALLER's execution
    /// context; an async method's AsyncLocal assignments are undone when it returns. See
    /// <see cref="UnitOfWorkScope"/>.
    /// </remarks>
    public Task<IUnitOfWorkSession> BeginAsync(CancellationToken cancellationToken)
    {
        if (UnitOfWorkScope.Current is not null)
            throw new InvalidOperationException("A unit of work is already active; a nested request joins it instead.");

        var holder = UnitOfWorkScope.InstallHolder();
        return OpenAsync(holder, cancellationToken);
    }

    private async Task<IUnitOfWorkSession> OpenAsync(UnitOfWorkScope.Holder holder, CancellationToken cancellationToken)
    {
        var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var transaction = await context.Database
                .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
            context.LeasedToUnitOfWork = true;
            var session = new UnitOfWorkSession(context, transaction, holder, _logger);
            UnitOfWorkScope.Attach(holder, session);
            return session;
        }
        catch
        {
            await context.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public bool IsRetryable(Exception exception)
    {
#if (UsePostgreSql)
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres && RetryableSqlStates.Contains(postgres.SqlState)) return true;
        }
#endif
        return false;
    }
}
