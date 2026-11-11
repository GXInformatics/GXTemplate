namespace CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;

/// <summary>
/// Drops cached user contexts so the next hub invocation reloads them (Pass 59).
/// </summary>
/// <remarks>
/// A user's branch read set lives in their <see cref="UserContext"/>, which is cached for an hour and, since
/// Pass 59, re-read from the cache on every hub invocation. Anything that changes a user's branches or the
/// permissions behind them (<c>Branches.ViewAll</c>, <c>Branches.WriteAll</c>) calls this, so the change
/// reaches reads at that user's next interaction. Writes do not depend on it: the branch check re-reads
/// assignments and <c>WriteAll</c> from the database on every send.
/// </remarks>
public interface IUserContextInvalidator
{
    /// <summary>One user's assignments or own permissions changed.</summary>
    void Invalidate(string userId);

    /// <summary>A role's permissions changed, which can affect any of its members.</summary>
    void InvalidateAll();
}
