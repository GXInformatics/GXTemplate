using System.Runtime.CompilerServices;

namespace CleanArchitecture.Blazor.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Marks the window during which <see cref="AuditableEntityInterceptor"/> is writing its audit rows
/// with a second <c>SaveChanges</c> on the same context.
/// <para>
/// That inner save runs the whole interceptor pipeline again. Without this marker
/// <see cref="DispatchDomainEventsInterceptor"/> would publish the pending domain events from inside
/// it - that is, <b>before</b> the audit transaction commits - so handlers would observe data no
/// other connection can see yet, and events would already be published if the audit write then
/// failed and rolled the business change back. The marker keeps domain-event dispatch on the outer
/// save, after the commit.
/// </para>
/// <para>
/// State is keyed to the context instance rather than held on either interceptor, because both are
/// registered scoped and are shared by every context created within a scope.
/// </para>
/// </summary>
internal static class AuditWriteScope
{
    private static readonly ConditionalWeakTable<DbContext, StrongBox<bool>> _active = new();

    /// <summary>True while the audit rows for <paramref name="context"/> are being written.</summary>
    public static bool IsActive(DbContext context) =>
        _active.TryGetValue(context, out var flag) && flag.Value;

    /// <summary>Opens the window; dispose the result to close it.</summary>
    public static IDisposable Enter(DbContext context)
    {
        var flag = _active.GetValue(context, static _ => new StrongBox<bool>(false));
        flag.Value = true;
        return new Scope(flag);
    }

    private sealed class Scope : IDisposable
    {
        private readonly StrongBox<bool> _flag;
        public Scope(StrongBox<bool> flag) => _flag = flag;
        public void Dispose() => _flag.Value = false;
    }
}
