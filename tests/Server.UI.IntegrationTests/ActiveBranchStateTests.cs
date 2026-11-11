using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Features.Branches;
using CleanArchitecture.Blazor.Server.UI.Services.Branches;
using FluentAssertions;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Server.UI.IntegrationTests;

/// <summary>
/// Pass 59 plan §5 row 29: the session's active branch. It starts at the default branch, survives a
/// reconnect (the same circuit), is restored on a new circuit only while still writable, and falls back to
/// the default when its assignment is removed.
/// </summary>
public class ActiveBranchStateTests
{
    private sealed class Directory : IUserBranchDirectory
    {
        public List<BranchDto> Writable { get; } = new();
        public int? Default { get; set; }

        public Task<IReadOnlyList<BranchDto>> WritableAsync(string userId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BranchDto>>(Writable.ToList());

        public Task<int?> DefaultBranchIdAsync(string userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Default is { } d && Writable.Any(b => b.Id == d) ? Default : null);
    }

    /// <summary>The tab's session storage: one entry, keyed by user, as the real store keeps it.</summary>
    private sealed class Store : IActiveBranchStore
    {
        public (string UserId, int? BranchId)? Entry { get; set; }

        public Task<int?> GetAsync(string userId) => Task.FromResult(Entry is { } e && e.UserId == userId ? e.BranchId : null);

        public Task SetAsync(string userId, int? branchId)
        {
            Entry = (userId, branchId);
            return Task.CompletedTask;
        }
    }

    private static BranchDto B(int id) => new() { Id = id, Code = "B" + id, Name = "Branch " + id, IsActive = true };

    private static Directory TwoBranchesDefaultingTo2()
    {
        var directory = new Directory { Default = 2 };
        directory.Writable.AddRange([B(1), B(2)]);
        return directory;
    }

    [Test]
    public async Task T29_ANewSignInStartsAtTheDefaultBranch()
    {
        var state = new ActiveBranchState(TwoBranchesDefaultingTo2(), new Store());
        await state.LoadAsync("u1");
        state.BranchId.Should().Be(2);
    }

    [Test]
    public async Task T29_AReconnectKeepsTheCircuit_AndWithItTheChosenBranch()
    {
        var store = new Store();
        var state = new ActiveBranchState(TwoBranchesDefaultingTo2(), store);
        await state.LoadAsync("u1");
        (await state.SelectAsync(1)).Should().BeTrue();

        // A brief drop keeps the circuit: same object, same branch, nothing reloaded.
        state.BranchId.Should().Be(1);
        store.Entry.Should().Be(("u1", (int?)1));
    }

    [Test]
    public async Task T29_ANewCircuitRestoresTheStoredBranch_OnlyWhileItIsStillWritable()
    {
        var directory = TwoBranchesDefaultingTo2();
        var store = new Store { Entry = ("u1", 1) };

        var restored = new ActiveBranchState(directory, store);
        await restored.LoadAsync("u1");
        restored.BranchId.Should().Be(1);

        directory.Writable.RemoveAll(b => b.Id == 1);
        var fallback = new ActiveBranchState(directory, store);
        await fallback.LoadAsync("u1");
        fallback.BranchId.Should().Be(2, "a branch no longer writable is not restored");

        var otherUser = new ActiveBranchState(TwoBranchesDefaultingTo2(), new Store { Entry = ("someone-else", 1) });
        await otherUser.LoadAsync("u1");
        otherUser.BranchId.Should().Be(2, "another user's entry in the same tab is never inherited");
    }

    [Test]
    public async Task T29_WhenTheAssignmentIsRemoved_RefreshMovesToTheDefault_AndSaysSo()
    {
        var directory = TwoBranchesDefaultingTo2();
        var state = new ActiveBranchState(directory, new Store());
        await state.LoadAsync("u1");
        await state.SelectAsync(1);

        (await state.RefreshAsync()).Should().BeFalse("nothing changed");
        directory.Writable.RemoveAll(b => b.Id == 1);
        (await state.RefreshAsync()).Should().BeTrue();
        state.BranchId.Should().Be(2);

        (await state.SelectAsync(1)).Should().BeFalse("a branch that is no longer writable cannot be chosen");
        state.BranchId.Should().Be(2);

        directory.Writable.Clear();
        (await state.RefreshAsync()).Should().BeTrue();
        state.Branch.Should().BeNull("a user with no writable branch has no active branch");
    }
}
