#nullable enable
using System;
using System.Linq;
using CleanArchitecture.Blazor.Application.Common.Modules;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Server.UI.IntegrationTests.Architecture;

/// <summary>
/// Pass 60b plan §5 row 5: every service a module (or the application level) registers resolves from the real host,
/// which builds its provider with scope validation (Development), so a singleton that captures a scoped service fails
/// here, naming both. The host's own startup has already run the Contracts check (rule 5). Every generated project
/// runs this on its own modules; on the bare template there are none, and the test says so.
/// </summary>
[TestFixture]
public class ModuleServicesResolveTests
{
    [Test]
    public void T05_EveryModuleService_ResolvesUnderScopeValidation()
    {
        using var factory = new GxWebApplicationFactory();
        var ledger = factory.Services.GetService<ModuleServiceLedger>();

        if (ledger is null)
        {
            // The template ships no module. Vacuous, and asserted as such so a generated project's run is visibly
            // different: there, the ledger exists and every service below is resolved.
            Assert.Pass("No module or application-level registrations: nothing to resolve.");
            return;
        }

        ledger.Owners.Should().NotBeEmpty();
        using var scope = factory.Services.CreateScope();
        var failures = ledger.Added
            .Select(a => a.Descriptor)
            .Where(d => !d.ServiceType.IsGenericTypeDefinition && !d.IsKeyedService)
            .Select(d => d.ServiceType)
            .Distinct()
            .Select(service =>
            {
                try
                {
                    scope.ServiceProvider.GetServices(service).ToList();
                    return null;
                }
                catch (Exception ex)
                {
                    return $"{service.FullName}: {ex.GetType().Name}: {ex.Message}";
                }
            })
            .OfType<string>()
            .ToList();

        failures.Should().BeEmpty("each module service must resolve, with valid lifetimes:\n" + string.Join("\n", failures));
    }
}
