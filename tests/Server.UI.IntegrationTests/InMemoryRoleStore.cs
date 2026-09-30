#nullable enable
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Domain.Identity;
using Microsoft.AspNetCore.Identity;

namespace CleanArchitecture.Blazor.Server.UI.IntegrationTests;

/// <summary>
/// A queryable <see cref="RoleManager{TRole}"/> store with no database (pass 47, CO-160).
/// </summary>
/// <remarks>
/// <see cref="Roles"/> supports EF's async operators (<c>ToListAsync</c> and friends), because the
/// components under test query <c>RoleManager.Roles</c> exactly as they would over EF. A plain
/// <c>AsQueryable()</c> would make them throw "the source IQueryable doesn't implement
/// IAsyncEnumerable".
/// </remarks>
internal sealed class InMemoryRoleStore : IQueryableRoleStore<ApplicationRole>
{
    private readonly ConcurrentDictionary<string, ApplicationRole> _roles = new(StringComparer.Ordinal);

    public IQueryable<ApplicationRole> Roles => new AsyncQueryable<ApplicationRole>(_roles.Values.ToList().AsQueryable());

    public Task<IdentityResult> CreateAsync(ApplicationRole role, CancellationToken cancellationToken)
    {
        _roles[role.Id] = role;
        return Task.FromResult(IdentityResult.Success);
    }

    public Task<IdentityResult> UpdateAsync(ApplicationRole role, CancellationToken cancellationToken)
    {
        _roles[role.Id] = role;
        return Task.FromResult(IdentityResult.Success);
    }

    public Task<IdentityResult> DeleteAsync(ApplicationRole role, CancellationToken cancellationToken)
    {
        _roles.TryRemove(role.Id, out _);
        return Task.FromResult(IdentityResult.Success);
    }

    public Task<string> GetRoleIdAsync(ApplicationRole role, CancellationToken cancellationToken) =>
        Task.FromResult(role.Id);

    public Task<string?> GetRoleNameAsync(ApplicationRole role, CancellationToken cancellationToken) =>
        Task.FromResult(role.Name);

    public Task SetRoleNameAsync(ApplicationRole role, string? roleName, CancellationToken cancellationToken)
    {
        role.Name = roleName;
        return Task.CompletedTask;
    }

    public Task<string?> GetNormalizedRoleNameAsync(ApplicationRole role, CancellationToken cancellationToken) =>
        Task.FromResult(role.NormalizedName);

    public Task SetNormalizedRoleNameAsync(ApplicationRole role, string? normalizedName, CancellationToken cancellationToken)
    {
        role.NormalizedName = normalizedName;
        return Task.CompletedTask;
    }

    public Task<ApplicationRole?> FindByIdAsync(string roleId, CancellationToken cancellationToken) =>
        Task.FromResult(_roles.TryGetValue(roleId, out var role) ? role : null);

    public Task<ApplicationRole?> FindByNameAsync(string normalizedRoleName, CancellationToken cancellationToken) =>
        Task.FromResult(_roles.Values.FirstOrDefault(r => r.NormalizedName == normalizedRoleName));

    public void Dispose()
    {
    }

    /// <summary>LINQ to Objects that EF's async operators accept.</summary>
    private sealed class AsyncQueryable<T> : IQueryable<T>, IAsyncEnumerable<T>
    {
        private readonly IQueryable<T> _inner;

        public AsyncQueryable(IQueryable<T> inner) => _inner = inner;

        public Type ElementType => _inner.ElementType;
        public Expression Expression => _inner.Expression;
        public IQueryProvider Provider => new AsyncQueryProvider(_inner.Provider);

        public IEnumerator<T> GetEnumerator() => _inner.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public async IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            foreach (var item in _inner)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
            }

            await Task.CompletedTask;
        }
    }

    /// <summary>Keeps every derived query (a <c>Select</c>, a <c>Where</c>) async-capable.</summary>
    private sealed class AsyncQueryProvider(IQueryProvider inner) : IQueryProvider
    {
        public IQueryable CreateQuery(Expression expression) =>
            (IQueryable)Activator.CreateInstance(
                typeof(AsyncQueryable<>).MakeGenericType(expression.Type.GetGenericArguments()[0]),
                inner.CreateQuery(expression))!;

        public IQueryable<TElement> CreateQuery<TElement>(Expression expression) =>
            new AsyncQueryable<TElement>(inner.CreateQuery<TElement>(expression));

        public object? Execute(Expression expression) => inner.Execute(expression);

        public TResult Execute<TResult>(Expression expression) => inner.Execute<TResult>(expression);
    }
}
#nullable restore
