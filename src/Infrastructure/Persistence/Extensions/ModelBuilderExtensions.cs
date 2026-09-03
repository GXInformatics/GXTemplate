// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;

namespace CleanArchitecture.Blazor.Infrastructure.Persistence.Extensions;

public static class ModelBuilderExtensions
{
    /// <summary>
    /// Applies one NAMED query filter to every entity implementing <typeparamref name="TInterface"/>.
    /// </summary>
    /// <remarks>
    /// <b>Named, since Pass 29 - and the name is what makes a second filter safe.</b> The
    /// single-argument <c>HasQueryFilter</c> REPLACES any filter already configured on an entity.
    /// While this helper had exactly one caller that did not matter; with a tenant filter as well
    /// it matters a great deal, because whichever call ran second would silently discard the other.
    /// EF 10's named overload composes them instead, and lets a caller drop one by name while the
    /// rest keep applying - see <c>AuditTrailsWithPaginationQuery</c>.
    /// <para>
    /// <b>The soft-delete filter this is called with currently matches nothing.</b> No entity in the
    /// template derives from <c>BaseAuditableSoftDeleteEntity</c>, so <c>ISoftDelete</c> has no
    /// implementors and the call below is a no-op. It is kept, and named, rather than deleted: the
    /// moment a generated project adds a soft-deletable entity the filter starts applying, and it
    /// should already be composing correctly with the tenant filter when it does.
    /// </para>
    /// </remarks>
    public static void ApplyGlobalFilters<TInterface>(this ModelBuilder modelBuilder,
        string filterName,
        Expression<Func<TInterface, bool>> expression)
    {
        var entities = modelBuilder.Model
            .GetEntityTypes()
            .Where(e => e.ClrType.GetInterface(typeof(TInterface).Name) != null)
            .Select(e => e.ClrType);
        foreach (var entity in entities)
        {
            var newParam = Expression.Parameter(entity);
            var newBody = ReplacingExpressionVisitor.Replace(expression.Parameters.Single(), newParam, expression.Body);
            modelBuilder.Entity(entity).HasQueryFilter(filterName, Expression.Lambda(newBody, newParam));
        }
    }
}
