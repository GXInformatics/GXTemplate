using System.Linq.Expressions;

namespace CleanArchitecture.Blazor.Application.Common.Interfaces.Transactions;

/// <summary>
/// Creates a row if its key is absent, and does nothing otherwise, safely under concurrency (Pass 60,
/// amendment C4). PostgreSQL only, and only inside a unit of work.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it is for.</b> A posting handler must sometimes write to a row that may not exist yet - a stock balance
/// for an item in a branch, a cost row - while another transaction may be creating the same row. Read-then-insert
/// races: both read "absent", both insert, and one fails with a unique violation. This helper issues
/// <c>INSERT … ON CONFLICT (key) DO NOTHING</c>: the second transaction waits for the first, then does nothing.
/// Then lock and update the row as usual, in the lock order (README, "Posting transactions"):
/// </para>
/// <code>
/// await insertIfAbsent.EnsureAsync(new StockBalance { ItemId = id, BranchId = branch }, b =&gt; new { b.ItemId, b.BranchId }, ct);
/// var balance = await db.Set&lt;StockBalance&gt;().Where(b =&gt; b.ItemId == id &amp;&amp; b.BranchId == branch).ForUpdate().SingleAsync(ct);
/// balance.OnHand += quantity;
/// </code>
/// <para>
/// <b>It bypasses <c>SaveChanges</c></b>, so it runs the save rules that matter for a new row itself: the branch grant,
/// the decimal kinds' scale, and the creation audit fields. It refuses the types whose rules it cannot honour: see
/// <c>InsertIfAbsent</c> in Infrastructure and the README.
/// </para>
/// </remarks>
public interface IInsertIfAbsent
{
    /// <summary>
    /// Inserts <paramref name="candidate"/> unless a row with the same <paramref name="key"/> exists. Returns true when
    /// this call inserted it. <paramref name="key"/> is the primary key or exactly the columns of a unique index or
    /// alternate key: <c>x =&gt; x.Code</c> or <c>x =&gt; new { x.ItemId, x.BranchId }</c>.
    /// </summary>
    Task<bool> EnsureAsync<TEntity>(TEntity candidate, Expression<Func<TEntity, object?>> key, CancellationToken cancellationToken = default)
        where TEntity : class;
}
