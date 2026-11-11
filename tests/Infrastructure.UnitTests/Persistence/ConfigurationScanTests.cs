using System.Reflection;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using CleanArchitecture.Blazor.Infrastructure.Persistence.Logging.Configurations;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CleanArchitecture.Blazor.Infrastructure.UnitTests.Persistence
{
    /// <summary>
    /// Pass 60 plan §5 row 18 (P60): every entity configuration in Infrastructure is applied to the business
    /// context except the log model's, so a module's configurations under <c>Infrastructure/Modules/&lt;M&gt;</c>
    /// are applied, where the old namespace-equality predicate silently dropped them.
    /// </summary>
    public class ConfigurationScanTests
    {
        private static ApplicationDbContext Context() =>
            new(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql("Host=model-only").Options);

        private static IEnumerable<(Type Configuration, Type Entity)> Configurations() =>
            typeof(ApplicationDbContext).Assembly.GetTypes()
                .Where(t => t is { IsAbstract: false, IsGenericTypeDefinition: false })
                .SelectMany(t => t.GetInterfaces()
                    .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEntityTypeConfiguration<>))
                    .Select(i => (t, i.GetGenericArguments()[0])));

        [Fact]
        public void EveryConfigurationIsApplied_ExceptTheLogModels()
        {
            using var db = Context();
            var configurations = Configurations().ToList();
            Assert.NotEmpty(configurations);

            foreach (var (configuration, entity) in configurations)
            {
                var inModel = db.Model.FindEntityType(entity) is not null;
                if (ApplicationDbContext.IsLogConfiguration(configuration))
                    Assert.False(inModel, $"{configuration.Name} configures the log model, which must not be in the business database.");
                else
                    Assert.True(inModel, $"{configuration.Name} is not applied to ApplicationDbContext, so {entity.Name} falls back to conventions.");
            }
        }

        [Fact]
        public void AModuleConfigurationsNamespace_IsApplied_AndTheLogNamespaceIsNot()
        {
            Assert.False(ApplicationDbContext.IsLogConfiguration(typeof(CleanArchitecture.Blazor.Infrastructure.Modules.Sample.Configurations.SampleModuleConfigurationProbe)));
            Assert.False(ApplicationDbContext.IsLogConfiguration(typeof(CleanArchitecture.Blazor.Infrastructure.Persistence.Configurations.AuditTrailConfiguration)));
            Assert.True(ApplicationDbContext.IsLogConfiguration(typeof(SystemLogConfiguration)));
        }
    }
}

namespace CleanArchitecture.Blazor.Infrastructure.Modules.Sample.Configurations
{
    /// <summary>Stands in for a configuration where a module keeps it, to test the scan's predicate.</summary>
    public sealed class SampleModuleConfigurationProbe;
}
