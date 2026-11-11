using System.Data.Common;
using System.Text.RegularExpressions;
using CleanArchitecture.Blazor.Application.Common.Transactions;
using CleanArchitecture.Blazor.Infrastructure.Persistence.Transactions;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CleanArchitecture.Blazor.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Turns a query tagged by <see cref="QueryableLockExtensions.ForUpdate{T}"/> into
/// <c>SELECT … FOR UPDATE</c> (Pass 58).
/// </summary>
/// <remarks>
/// It refuses rather than guesses. Each refusal is a statement that would lock nothing, or lock
/// something other than the caller meant:
/// <list type="bullet">
/// <item><description>no transaction - the lock would end with the statement;</description></item>
/// <item><description>not PostgreSQL - owner decision, Pass 58;</description></item>
/// <item><description>a JOIN - PostgreSQL rejects <c>FOR UPDATE</c> on the nullable side of an outer
/// join, and a lock across joined tables is not what the rule describes;</description></item>
/// <item><description>neither ORDER BY nor LIMIT - rows are locked in the order returned, and the
/// global lock order needs that order to be stated in the query;</description></item>
/// <item><description>more than one statement.</description></item>
/// </list>
/// Stateless; registered once on the context options.
/// </remarks>
public sealed class ForUpdateCommandInterceptor : DbCommandInterceptor
{
    public static readonly ForUpdateCommandInterceptor Instance = new();

    private static readonly Regex Join = new(@"\bJOIN\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex OrderOrLimit = new(@"\bORDER\s+BY\b|\bLIMIT\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly string Tag = "-- " + QueryableLockExtensions.ForUpdateTag;

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Apply(command, eventData);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Apply(command, eventData);
        return ValueTask.FromResult(result);
    }

    private static void Apply(DbCommand command, CommandEventData eventData)
    {
        var sql = command.CommandText;
        if (!sql.Contains(Tag, StringComparison.Ordinal)) return;

        var context = eventData.Context;
        if (context is null || context.Database.ProviderName != PostingSql.NpgsqlProvider)
            throw new NotSupportedException("ForUpdate() is implemented for PostgreSQL only.");
        if (context.Database.CurrentTransaction is null || command.Transaction is null)
            throw new InvalidOperationException(
                "ForUpdate() needs a transaction: outside one the lock ends with the statement. " +
                "Run it inside an ITransactionalRequest.");

        var body = StripComments(sql);
        if (Join.IsMatch(body))
            throw new InvalidOperationException(
                "ForUpdate() locks one table: this query joins (an Include, or an auto-included navigation - " +
                "use IgnoreAutoIncludes()). Lock the rows by key in a query of their own.");
        if (!OrderOrLimit.IsMatch(body))
            throw new InvalidOperationException(
                "ForUpdate() needs the lock order stated in the query: add an OrderBy following the global lock order " +
                "(document header, document number, item cost rows by ItemId, stock balance rows by ItemId, WarehouseId), " +
                "or fetch a single row.");
        if (body.TrimEnd().TrimEnd(';').Contains(';'))
            throw new InvalidOperationException("ForUpdate() cannot be applied to a batch of statements.");

        command.CommandText = sql.TrimEnd().TrimEnd(';') + Environment.NewLine + "FOR UPDATE";
    }

    private static string StripComments(string sql) =>
        string.Join('\n', sql.Split('\n').Where(line => !line.TrimStart().StartsWith("--", StringComparison.Ordinal)));
}
