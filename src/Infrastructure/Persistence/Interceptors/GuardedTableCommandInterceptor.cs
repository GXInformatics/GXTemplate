using System.Collections.Concurrent;
using System.Data.Common;
using System.Text.RegularExpressions;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Caching;
using CleanArchitecture.Blazor.Domain.Common.Entities;
using CleanArchitecture.Blazor.Infrastructure.Persistence.Rules;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;

namespace CleanArchitecture.Blazor.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Refuses bulk <c>UPDATE</c>, <c>DELETE</c> and <c>TRUNCATE</c> against guarded tables (Pass 59).
/// </summary>
/// <remarks>
/// <b>Why.</b> <c>ExecuteUpdate</c>, <c>ExecuteDelete</c> and raw SQL do not pass through
/// <c>SaveChanges</c>, so the save rules never see them. Guarded tables are those of <c>IAppendOnly</c>,
/// <c>IPostable</c>, <c>IPostableLine&lt;&gt;</c> and branch-scoped types: none may be bulk-written,
/// because a bulk statement cannot be checked row by row.
/// <para>
/// <b>Limits, stated.</b> It acts on commands whose <c>CommandSource</c> is <c>ExecuteUpdate</c>,
/// <c>ExecuteDelete</c> or <c>ExecuteSqlRaw</c>; <c>SaveChanges</c> and migrations are
/// untouched. The raw-SQL check matches text, so it is a safety net, not a proof. SQL sent on the raw
/// connection or from outside the application is invisible here; the append-only triggers stop that.
/// </para>
/// Added with <c>AddInterceptors</c> beside <c>ForUpdateCommandInterceptor</c>, not as an
/// <c>ISaveChangesInterceptor</c> service, so the pinned interceptor count is unchanged.
/// </remarks>
public sealed class GuardedTableCommandInterceptor : DbCommandInterceptor
{
    public static readonly GuardedTableCommandInterceptor Instance = new();

    private static readonly ConcurrentDictionary<IModel, Regex?> Patterns = new();

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Check(command, eventData);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Check(command, eventData);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Check(command, eventData);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Check(command, eventData);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Check(command, eventData);
        return result;
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        Check(command, eventData);
        return ValueTask.FromResult(result);
    }

    private static readonly ConcurrentDictionary<IModel, Regex?> CachedReadPatterns = new();

    private static void Check(DbCommand command, CommandEventData eventData)
    {
        CheckCachedRead(command, eventData);

        if (eventData.CommandSource is not (CommandSource.ExecuteUpdate or CommandSource.ExecuteDelete
            or CommandSource.ExecuteSqlRaw)) return;
        if (eventData.Context is null) return;

        var pattern = Patterns.GetOrAdd(eventData.Context.Model, BuildPattern);
        var match = pattern?.Match(command.CommandText);
        if (match is { Success: true })
            throw new DataRuleViolationException(
                $"A bulk {match.Groups["verb"].Value.ToUpperInvariant()} against {match.Groups["table"].Value} was refused: the table holds " +
                "append-only, posted-document or branch-scoped rows, which change only through SaveChanges and its rules.");
    }

    /// <summary>
    /// Architecture rule 3 (Pass 60): while a cacheable request fills its cache entry (<see cref="CacheFill"/>), SQL
    /// that reads an append-only or branch-scoped table is refused. Such a figure would be served stale, or served
    /// to a user with another branch read set.
    /// </summary>
    private static void CheckCachedRead(DbCommand command, CommandEventData eventData)
    {
        if (CacheFill.Current is not { } request || eventData.Context is null) return;

        var pattern = CachedReadPatterns.GetOrAdd(eventData.Context.Model, BuildCachedReadPattern);
        var match = pattern?.Match(command.CommandText);
        if (match is { Success: true })
            throw new DataRuleViolationException(
                $"{request} is cacheable but reads {match.Groups["table"].Value}, which holds append-only or branch-scoped rows. " +
                "A cached ledger or stock figure is stale until a write clears it, and a cached branch-scoped list reaches users " +
                "with other branches; remove ICacheableRequest from this request (architecture rule 3).");
    }

    private static bool IsUncacheable(Type clr) =>
        typeof(IAppendOnly).IsAssignableFrom(clr)
        || typeof(IMustHaveBranch).IsAssignableFrom(clr)
        || clr.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IBranchVisibility<>));

    private static Regex? BuildCachedReadPattern(IModel model)
    {
        var tables = model.GetEntityTypes()
            .Where(t => !t.IsOwned() && t.GetTableName() is not null && IsUncacheable(t.ClrType))
            .Select(t => t.GetTableName()!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (tables.Count == 0) return null;

        var names = string.Join("|", tables.Select(Regex.Escape));
        return new Regex(
            $"""\b(?:FROM|JOIN)\s+(?:"?\w+"?\s*\.\s*)?"?(?<table>{names})"?(?![\w"])""",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    internal static bool IsGuarded(Type clr) =>
        typeof(IAppendOnly).IsAssignableFrom(clr)
        || typeof(IPostable).IsAssignableFrom(clr)
        || DataModelGuard.LineHeaderTypeOf(clr) is not null
        || typeof(IMustHaveBranch).IsAssignableFrom(clr)
        || clr.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IBranchVisibility<>));

    private static Regex? BuildPattern(IModel model)
    {
        var tables = model.GetEntityTypes()
            .Where(t => !t.IsOwned() && t.GetTableName() is not null && IsGuarded(t.ClrType))
            .Select(t => t.GetTableName()!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (tables.Count == 0) return null;

        // UPDATE "s"."T" / UPDATE "T" / DELETE FROM … / TRUNCATE [TABLE] …, quoted or not, any schema.
        var names = string.Join("|", tables.Select(Regex.Escape));
        return new Regex(
            $"""\b(?<verb>UPDATE|DELETE\s+FROM|TRUNCATE(?:\s+TABLE)?(?:\s+ONLY)?)\s+(?:"?\w+"?\s*\.\s*)?"?(?<table>{names})"?(?![\w"])""",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }
}
