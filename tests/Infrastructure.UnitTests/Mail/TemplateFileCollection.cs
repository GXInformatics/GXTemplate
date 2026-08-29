using Xunit;

namespace CleanArchitecture.Blazor.Infrastructure.UnitTests.Mail;

/// <summary>
/// Groups the test classes that write template files into the output directory, so they never run at
/// the same time.
/// </summary>
/// <remarks>
/// The renderer and the guard both resolve templates against <c>AppContext.BaseDirectory</c>, which
/// is one shared directory for the whole test process. A class that adds a deliberately broken
/// template so the guard will reject it would, running in parallel, be seen by a class asserting the
/// shipped set is clean. Sharing a collection serialises them.
/// <para>
/// The same reasoning produced <c>SqliteFileCollection</c> in Pass 11D, for the same class of
/// problem: process-global state that xUnit's per-collection parallelism would otherwise interleave.
/// </para>
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public class TemplateFileCollection
{
    public const string Name = "mail-template-files";
}
