using System.Linq.Expressions;
using System.Reflection;
using CleanArchitecture.Blazor.Application.Common.Branches;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Transactions;
using CleanArchitecture.Blazor.Application.Common.Numerics;
using CleanArchitecture.Blazor.Domain.Common.Entities;
using CleanArchitecture.Blazor.Infrastructure.Persistence.Rules;
using Microsoft.EntityFrameworkCore.Metadata;

namespace CleanArchitecture.Blazor.Infrastructure.Persistence.Transactions;

/// <summary>
/// <c>INSERT … ON CONFLICT (key) DO NOTHING</c> on the unit of work's connection and transaction (Pass 60,
/// amendment C4). See <see cref="IInsertIfAbsent"/>.
/// </summary>
/// <remarks>
/// <para><b>Bypasses <c>SaveChanges</c></b>, so neither the Pass 59 save rules nor the save interceptors see the row.
/// What that means, precisely:</para>
/// <list type="table">
/// <item><term>Run here, as at save</term><description>the branch grant (an <see cref="IMustHaveBranch"/> row needs a grant
/// covering its branch; an <see cref="IBranchVisibility{TSelf}"/> row one of its branches; the system context is
/// exempt); the decimal kinds (no value with more places than its kind); the creation audit fields
/// (<c>CreatedAt</c>/<c>CreatedById</c>, as <c>AuditableEntityInterceptor</c> stamps them).</description></item>
/// <item><term>Refused outright</term><description><see cref="IAuditable"/> types (their audit-trail row is written only by
/// the interceptor in <c>SaveChanges</c>); <see cref="IPostable"/> and <see cref="IPostableLine{THeader}"/> types (a
/// document is never created by key, and a line needs its header touched); tenant-scoped types (the tenant stamp is
/// the interceptor's); an entity with pending domain events (they would never be dispatched); a type in an inheritance
/// hierarchy, or with owned or complex members (one table, plain columns only).</description></item>
/// <item><term>Need no check</term><description><see cref="IAppendOnly"/> (an insert is the one change it allows) and
/// <see cref="IVersioned"/> (a new row has no version to compare).</description></item>
/// <item><term>Not intercepted</term><description>the command is created on the connection, like <see cref="PostingSql"/>'s,
/// so <c>GuardedTableCommandInterceptor</c> does not see it; it allows <c>INSERT</c> anyway.</description></item>
/// </list>
/// </remarks>
internal sealed class InsertIfAbsent(IUnitOfWork unitOfWork, IUserContextAccessor userContextAccessor, IDateTime dateTime)
    : IInsertIfAbsent
{
    public async Task<bool> EnsureAsync<TEntity>(TEntity candidate, Expression<Func<TEntity, object?>> key, CancellationToken cancellationToken = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(key);
        if (!unitOfWork.IsActive)
            throw new InvalidOperationException(
                $"InsertIfAbsent for {typeof(TEntity).Name} runs inside the unit of work only. Send the command as an ITransactionalRequest.");

        var context = PostingSql.AsDbContext(unitOfWork.Context);
        PostingSql.RequirePostgreSql(context, "InsertIfAbsent");
        var entityType = context.Model.FindEntityType(typeof(TEntity))
                         ?? throw new InvalidOperationException($"{typeof(TEntity).Name} is not in the model.");

        RefuseUnsupported(entityType, candidate);
        var keyProperties = KeyProperties(entityType, key);
        CheckBranch(candidate);
        CheckPrecision(entityType, candidate);
        StampCreation(candidate);

        var (table, column) = PostingSql.Names<TEntity>(context);
        var columns = entityType.GetProperties().Where(p => IsWritten(p, candidate)).ToList();

        var sql = $"INSERT INTO {table} ({string.Join(", ", columns.Select(p => column(p.Name)))}) " +
                  $"VALUES ({string.Join(", ", columns.Select((_, i) => "@p" + i))}) " +
                  $"ON CONFLICT ({string.Join(", ", keyProperties.Select(p => column(p.Name)))}) DO NOTHING";

        await using var command = await PostingSql.CommandAsync(context, sql, cancellationToken);
        for (var i = 0; i < columns.Count; i++)
        {
            var property = columns[i];
            var value = property.GetGetter().GetClrValue(candidate);
            var converter = property.GetTypeMapping().Converter;
            PostingSql.AddParameter(command, "p" + i, converter is null || value is null ? value : converter.ConvertToProvider(value));
        }

        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static void RefuseUnsupported(IEntityType entityType, object candidate)
    {
        var clr = entityType.ClrType;
        string? reason = null;
        if (typeof(IAuditable).IsAssignableFrom(clr))
            reason = "it is IAuditable, and its audit-trail row is written only by SaveChanges";
        else if (typeof(IPostable).IsAssignableFrom(clr) || DataModelGuard.LineHeaderTypeOf(clr) is not null)
            reason = "a document or its line is never created by key; save it through the context";
        else if (typeof(IMustHaveTenant).IsAssignableFrom(clr) || typeof(IMayHaveTenant).IsAssignableFrom(clr))
            reason = "it is tenant-scoped, and the tenant is stamped by SaveChanges";
        else if (candidate is IHasDomainEvents { DomainEvents.Count: > 0 })
            reason = "it has pending domain events, which this path would never dispatch";
        else if (entityType.BaseType is not null || entityType.GetDirectlyDerivedTypes().Any())
            reason = "it is part of an inheritance hierarchy";
        else if (entityType.GetNavigations().Any(n => n.TargetEntityType.IsOwned()) || entityType.GetComplexProperties().Any())
            reason = "it has owned or complex members";

        if (reason is not null)
            throw new InvalidOperationException($"InsertIfAbsent cannot insert {clr.Name}: {reason}.");
    }

    private static IReadOnlyList<IProperty> KeyProperties<TEntity>(IEntityType entityType, Expression<Func<TEntity, object?>> key)
    {
        var body = key.Body is UnaryExpression { NodeType: ExpressionType.Convert } convert ? convert.Operand : key.Body;
        var names = body switch
        {
            MemberExpression member => [member.Member.Name],
            NewExpression created => created.Arguments.Select(a => (a as MemberExpression)?.Member.Name
                                                                   ?? throw new ArgumentException("A key lists properties only.", nameof(key))).ToArray(),
            _ => throw new ArgumentException("The key is x => x.Code or x => new { x.A, x.B }.", nameof(key))
        };

        var properties = names.Select(n => entityType.FindProperty(n)
                                           ?? throw new ArgumentException($"{entityType.ClrType.Name} has no property {n}.", nameof(key))).ToList();
        var set = properties.ToHashSet();
        var unique = entityType.GetKeys().Select(k => k.Properties)
            .Concat(entityType.GetIndexes().Where(i => i.IsUnique && i.GetFilter() is null).Select(i => i.Properties))
            .Any(p => p.Count == set.Count && p.All(set.Contains));
        if (!unique)
            throw new InvalidOperationException(
                $"InsertIfAbsent for {entityType.ClrType.Name}: ({string.Join(", ", names)}) is not its primary key, an alternate key or an " +
                "unfiltered unique index, so ON CONFLICT has nothing to arbitrate on. Declare the unique index in the configuration.");
        return properties;
    }

    private void CheckBranch(object candidate)
    {
        if (userContextAccessor.Current?.Branches?.IsSystem == true) return;
        var name = candidate.GetType().Name;
        if (candidate is IMustHaveBranch scoped)
        {
            var grant = BranchGrant.Current
                        ?? throw new InvalidOperationException($"{name} is branch-scoped and was ensured outside a branch command. Implement IBranchCommand on the request.");
            if (!grant.Contains(scoped.BranchId))
                throw new InvalidOperationException($"{name} belongs to branch {scoped.BranchId}, but this command was granted branch(es) {string.Join(", ", grant)}.");
            return;
        }

        var involved = candidate.GetType().GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IBranchVisibility<>))
            ?.GetProperty("InvolvedBranchIds")?.GetValue(candidate) as IEnumerable<int>;
        if (involved is null) return;
        var current = BranchGrant.Current
                      ?? throw new InvalidOperationException($"{name} is branch-scoped and was ensured outside a branch command. Implement IBranchCommand on the request.");
        if (!involved.Any(current.Contains))
            throw new InvalidOperationException($"{name} involves none of the branches this command was granted ({string.Join(", ", current)}).");
    }

    private static void CheckPrecision(IEntityType entityType, object candidate)
    {
        foreach (var property in entityType.GetProperties())
        {
            if (property.PropertyInfo is not { } info || DataModelConventions.KindOf(info) is not { } kind) continue;
            if (property.GetGetter().GetClrValue(candidate) is decimal value && GxRound.SignificantScale(value) > kind.Scale)
                throw new InvalidOperationException(
                    $"{entityType.ClrType.Name}.{property.Name} is {value}, which has more than the {kind.Scale} decimal places a " +
                    $"{kind.KindName} value holds. Round it when you compute it (GxRound).");
        }
    }

    private void StampCreation(object candidate)
    {
        if (candidate is not IAuditableEntity auditable) return;
        auditable.CreatedAt = dateTime.UtcNow;
        auditable.CreatedById = userContextAccessor.Current?.UserId;
    }

    /// <summary>Every column but the store-generated ones: the row version always, a generated key while unset.</summary>
    private static bool IsWritten(IProperty property, object candidate)
    {
        if (property.IsShadowProperty()) return false;
        if (property.IsConcurrencyToken && property.ValueGenerated == ValueGenerated.OnAddOrUpdate) return false;
        if (property.ValueGenerated == ValueGenerated.OnAdd)
        {
            var value = property.GetGetter().GetClrValue(candidate);
            if (value is null || Equals(value, property.ClrType.IsValueType ? Activator.CreateInstance(property.ClrType) : null)) return false;
        }
        return true;
    }
}
