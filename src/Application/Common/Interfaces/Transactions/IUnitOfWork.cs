namespace CleanArchitecture.Blazor.Application.Common.Interfaces.Transactions;

/// <summary>
/// The ambient unit of work, for services that must run inside one (Pass 58).
/// </summary>
public interface IUnitOfWork
{
    /// <summary>True while a transactional request is running on this logical call chain.</summary>
    bool IsActive { get; }

    /// <summary>The unit of work's context. Throws when <see cref="IsActive"/> is false.</summary>
    IApplicationDbContext Context { get; }
}

/// <summary>
/// Begins and inspects units of work. Used by <c>TransactionBehaviour</c>; application code uses
/// <see cref="IUnitOfWork"/>.
/// </summary>
public interface IUnitOfWorkManager
{
    /// <summary>The active session on this logical call chain, or null.</summary>
    IUnitOfWorkSession? Current { get; }

    /// <summary>
    /// Opens a context and a READ COMMITTED transaction and makes them ambient. Disposing the
    /// session rolls back anything not committed, empties the ambient holder and disposes the
    /// context.
    /// </summary>
    Task<IUnitOfWorkSession> BeginAsync(CancellationToken cancellationToken);

    /// <summary>
    /// True for a failure worth retrying the whole attempt for: a PostgreSQL deadlock (40P01) or
    /// serialisation failure (40001), anywhere in the inner-exception chain.
    /// </summary>
    bool IsRetryable(Exception exception);
}

/// <summary>One unit of work: its context, transaction, event buffers and failure state.</summary>
public interface IUnitOfWorkSession : IAsyncDisposable
{
    IApplicationDbContext Context { get; }

    /// <summary>Removes and returns the posting events buffered since the last call, in raise order.</summary>
    IReadOnlyList<PostingEvent> TakePostingEvents();

    /// <summary>Removes and returns the ordinary events to publish after the commit, in raise order.</summary>
    IReadOnlyList<DomainEvent> TakeAfterCommitEvents();

    /// <summary>
    /// Marks the unit of work as failed by a nested request. The outer behaviour rolls back even if
    /// its handler ignored the nested result. <paramref name="cause"/> is the nested exception, if
    /// any, so a retryable one can still retry the outer attempt.
    /// </summary>
    void Fail(IReadOnlyList<string> errors, Exception? cause);

    bool HasFailed { get; }
    IReadOnlyList<string> FailureErrors { get; }
    Exception? FailureCause { get; }

    Task CommitAsync(CancellationToken cancellationToken);
}
