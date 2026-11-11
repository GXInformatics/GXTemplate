using CleanArchitecture.Blazor.Infrastructure.Persistence.Transactions;

namespace CleanArchitecture.Blazor.Infrastructure.Persistence;

/// <remarks>
/// <b>Inside a unit of work it returns the unit of work's context</b> (Pass 58), so a command handler,
/// the posting handlers it triggers and any request they send all write through one context, one
/// connection and one transaction. The context ignores <c>Dispose</c> while it is leased, so the
/// ordinary <c>await using var db = await factory.CreateAsync()</c> is correct in both cases. Outside a
/// unit of work every call returns a new context, as before.
/// </remarks>
internal sealed class ApplicationDbContextFactory(IDbContextFactory<ApplicationDbContext> efFactory) : IApplicationDbContextFactory
{
    public ValueTask<IApplicationDbContext> CreateAsync(CancellationToken ct = default)
    {
        if (UnitOfWorkScope.Current is { } session)
        {
            return new ValueTask<IApplicationDbContext>(session.Context);
        }

        var dbContext = efFactory.CreateDbContext();
        return new ValueTask<IApplicationDbContext>(dbContext);
    }
}
