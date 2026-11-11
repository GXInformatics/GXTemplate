namespace CleanArchitecture.Blazor.Application.Modules.Sample.Contracts;

/// <summary>Published query: how many bins the current user can see in a branch. Implemented in Infrastructure.</summary>
public interface ISampleBinLookup
{
    Task<int> CountAsync(int branchId, CancellationToken cancellationToken);
}
