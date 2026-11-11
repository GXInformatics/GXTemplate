using System.Linq.Expressions;
using System.Reflection;
using CleanArchitecture.Blazor.Domain.Common.Entities;
using CleanArchitecture.Blazor.Domain.Common.Precision;

namespace CleanArchitecture.Blazor.Infrastructure.Persistence.Rules;

/// <summary>The Pass 59 model conventions applied by <see cref="ApplicationDbContext"/>.</summary>
internal static class DataModelConventions
{
    /// <summary>
    /// Sets precision and scale on every <c>decimal</c> property that declares a kind. Undeclared ones are
    /// left alone, for <c>DataModelGuard</c> to refuse.
    /// </summary>
    public static void ApplyDecimalKinds(ModelBuilder builder)
    {
        foreach (var entityType in builder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetDeclaredProperties())
            {
                var kind = KindOf(property.PropertyInfo);
                if (kind is null || !IsDecimal(property.ClrType)) continue;

                property.SetPrecision(kind.Precision);
                property.SetScale(kind.Scale);
            }
        }
    }

    /// <summary>
    /// Maps <see cref="IVersioned.Version"/> to <c>xmin</c> as a row version on PostgreSQL (Npgsql's supported
    /// mapping: a <c>uint</c> property configured with <c>IsRowVersion()</c>), and ignores it elsewhere.
    /// </summary>
    public static void ApplyVersioning(ModelBuilder builder, string? providerName)
    {
        var postgres = providerName == Transactions.PostingSql.NpgsqlProvider;
        foreach (var entityType in builder.Model.GetEntityTypes().ToList())
        {
            if (entityType.IsOwned() || entityType.BaseType is not null) continue;
            if (!typeof(IVersioned).IsAssignableFrom(entityType.ClrType)) continue;

            var entity = builder.Entity(entityType.ClrType);
            if (postgres) entity.Property(nameof(IVersioned.Version)).IsRowVersion();
            else entity.Ignore(nameof(IVersioned.Version));
        }
    }

    public static bool IsDecimal(Type type) => (Nullable.GetUnderlyingType(type) ?? type) == typeof(decimal);

    public static bool IsBinaryFloat(Type type) =>
        (Nullable.GetUnderlyingType(type) ?? type) is var t && (t == typeof(double) || t == typeof(float));

    /// <summary>
    /// The property's kind, or null when it declares none OR more than one - the guard reports both, in its own
    /// words, rather than a reflection error from the middle of model building.
    /// </summary>
    public static DecimalKindAttribute? KindOf(MemberInfo? member)
    {
        var kinds = member?.GetCustomAttributes<DecimalKindAttribute>(inherit: true).Take(2).ToList();
        return kinds is { Count: 1 } ? kinds[0] : null;
    }

    /// <summary>
    /// The <c>VisibleTo</c> rule of a type implementing <see cref="IBranchVisibility{TSelf}"/> for itself, or
    /// null when it does not.
    /// </summary>
    public static LambdaExpression? BranchVisibilityRuleOf(Type clr)
    {
        var contract = typeof(IBranchVisibility<>).MakeGenericTypeOrNull(clr);
        if (contract is null || !contract.IsAssignableFrom(clr)) return null;

        const string name = nameof(IBranchVisibility<Stub>.VisibleTo);
        var getter = clr.GetProperty(name, BindingFlags.Public | BindingFlags.Static)?.GetMethod
                     ?? clr.GetInterfaceMap(contract).TargetMethods
                         .Single(m => m.Name.EndsWith("get_" + name, StringComparison.Ordinal));
        return (LambdaExpression)getter.Invoke(null, null)!;
    }

    private static Type? MakeGenericTypeOrNull(this Type definition, Type argument)
    {
        try
        {
            return definition.MakeGenericType(argument);
        }
        catch (ArgumentException)
        {
            return null; // the constraint TSelf : IBranchVisibility<TSelf> is not met
        }
    }

    private sealed class Stub : IBranchVisibility<Stub>
    {
        public static Expression<Func<Stub, int[], bool>> VisibleTo => (_, _) => false;
        public IEnumerable<int> InvolvedBranchIds => [];
    }
}
