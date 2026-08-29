using Xunit;

namespace CleanArchitecture.Blazor.Infrastructure.UnitTests.Logging;

/// <summary>
/// Groups the test classes that work against real SQLite files, so they never run at the same time.
/// </summary>
/// <remarks>
/// xUnit parallelises across collections, and these classes share process-global state whether they
/// like it or not: <c>SqliteConnection.ClearAllPools()</c> clears pools for the whole process, not
/// for one connection string. So a class calling it during its cleanup can close the connection
/// another class's Serilog sink is in the middle of writing through, and that class loses a row it
/// then waits for until it times out.
/// <para>
/// That is what produced the one unreproducible failure recorded as Pass 11C anomaly 2. It became
/// reproducible in Pass 11D as soon as a second clearing call was added, which is how it was finally
/// identified: two classes, one global switch, and a race that only sometimes lands badly. Sharing a
/// collection serialises them and removes the race rather than making it rarer.
/// </para>
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public class SqliteFileCollection
{
    public const string Name = "sqlite-files";
}
