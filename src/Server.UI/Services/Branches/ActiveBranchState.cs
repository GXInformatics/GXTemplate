using CleanArchitecture.Blazor.Application.Features.Branches;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;

namespace CleanArchitecture.Blazor.Server.UI.Services.Branches;

/// <summary>Where the active branch is remembered across a lost circuit (Pass 59).</summary>
public interface IActiveBranchStore
{
    Task<int?> GetAsync(string userId);

    Task SetAsync(string userId, int? branchId);
}

/// <summary>
/// The tab's <c>sessionStorage</c>, encrypted (<see cref="ProtectedSessionStorage"/>), keyed by user, so a
/// different user in the same tab never inherits it. The sign-out form and the idle sign-out remove the
/// key (<see cref="StorageKey"/>), so a new sign-in starts at the default branch.
/// </summary>
public sealed class ProtectedSessionActiveBranchStore(ProtectedSessionStorage storage) : IActiveBranchStore
{
    /// <summary>The <c>sessionStorage</c> key. The sign-out paths remove it by this name.</summary>
    public const string StorageKey = "gx.activeBranch";

    private sealed record Entry(string UserId, int? BranchId);

    public async Task<int?> GetAsync(string userId)
    {
        try
        {
            var result = await storage.GetAsync<Entry>(StorageKey);
            return result.Success && result.Value?.UserId == userId ? result.Value.BranchId : null;
        }
        catch (Exception)
        {
            // Unreadable (another key ring, tampering, storage blocked): start from the default instead.
            return null;
        }
    }

    public async Task SetAsync(string userId, int? branchId)
    {
        try
        {
            await storage.SetAsync(StorageKey, new Entry(userId, branchId));
        }
        catch (Exception)
        {
            // Storage unavailable: the circuit still holds the branch; only a lost circuit forgets it.
        }
    }
}

/// <summary>
/// The session's active branch (Pass 59): one per circuit, offered by the top-bar switcher.
/// </summary>
/// <remarks>
/// <b>A default for forms, not a security boundary.</b> A form puts <see cref="BranchId"/> on its command,
/// and <c>BranchAccessBehaviour</c> checks it against the database on every send. Reads are filtered by the
/// user's read set, not by this; whether a list also narrows to the active branch is each module's choice.
/// <list type="bullet">
/// <item><description><b>Reconnect:</b> a brief drop keeps the circuit, and this object with it.</description></item>
/// <item><description><b>A lost circuit:</b> the new one restores the branch from the tab's session storage, if it
/// is still writable.</description></item>
/// <item><description><b>A new sign-in:</b> starts at the default branch, else the first writable one, else none.</description></item>
/// <item><description><b>An assignment removed while signed in:</b> <see cref="RefreshAsync"/> (the switcher calls it
/// when opened) moves to the default branch and reports that it moved; the next branch command is refused
/// meanwhile by the server anyway.</description></item>
/// </list>
/// </remarks>
public sealed class ActiveBranchState(IUserBranchDirectory directory, IActiveBranchStore store)
{
    private string? _userId;

    /// <summary>The branches this user may write to, read fresh by the last load or refresh.</summary>
    public IReadOnlyList<BranchDto> Writable { get; private set; } = [];

    /// <summary>The active branch, or null when the user may write to none.</summary>
    public BranchDto? Branch { get; private set; }

    public int? BranchId => Branch?.Id;

    public bool IsLoaded => _userId is not null;

    public event Action? Changed;

    /// <summary>Loads the state for <paramref name="userId"/>: the stored branch if still writable, else the default.</summary>
    public async Task LoadAsync(string userId, CancellationToken cancellationToken = default)
    {
        _userId = userId;
        Writable = await directory.WritableAsync(userId, cancellationToken);
        var stored = await store.GetAsync(userId);
        var chosen = Pick(stored) ?? Pick(await directory.DefaultBranchIdAsync(userId, cancellationToken)) ?? Writable.FirstOrDefault();
        await SetAsync(chosen);
    }

    /// <summary>Makes <paramref name="branchId"/> active, if the user may still write to it (read fresh).</summary>
    public async Task<bool> SelectAsync(int branchId, CancellationToken cancellationToken = default)
    {
        if (_userId is null) return false;
        Writable = await directory.WritableAsync(_userId, cancellationToken);
        var chosen = Pick(branchId);
        if (chosen is null)
        {
            Changed?.Invoke();
            return false;
        }
        await SetAsync(chosen);
        return true;
    }

    /// <summary>
    /// Re-reads the writable branches. Returns true when the active branch was no longer writable and the
    /// state moved to the default (or to none).
    /// </summary>
    public async Task<bool> RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_userId is null) return false;
        Writable = await directory.WritableAsync(_userId, cancellationToken);
        if (Branch is not null && Pick(Branch.Id) is { } still)
        {
            Branch = still;
            Changed?.Invoke();
            return false;
        }

        var moved = Branch is not null;
        await SetAsync(Pick(await directory.DefaultBranchIdAsync(_userId, cancellationToken)) ?? Writable.FirstOrDefault());
        return moved;
    }

    private BranchDto? Pick(int? branchId) => branchId is null ? null : Writable.FirstOrDefault(b => b.Id == branchId);

    private async Task SetAsync(BranchDto? branch)
    {
        Branch = branch;
        if (_userId is not null) await store.SetAsync(_userId, branch?.Id);
        Changed?.Invoke();
    }
}
