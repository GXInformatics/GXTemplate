#nullable enable
using System.Linq;
using CleanArchitecture.Blazor.Application.UnitTests.DataFoundations;
using CleanArchitecture.Blazor.Domain.Entities;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using CleanArchitecture.Blazor.Infrastructure.Persistence.Rules;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.Extensions
{
    /// <summary>What a module writes to make branch names unique: a configuration of the template's Branch adding only the index.</summary>
    public sealed class ModuleBranchNameIndex : IEntityTypeConfiguration<Branch>
    {
        public void Configure(EntityTypeBuilder<Branch> builder) => builder.HasIndex(b => b.Name).IsUnique();
    }

    /// <summary>The template's model plus the configuration above, applied by the same assembly scan the context uses.</summary>
    public sealed class BranchNameIndexContext() : GuardContext(Options())
    {
        protected override void Add(ModelBuilder builder) { }

        protected override void After(ModelBuilder builder) =>
            builder.ApplyConfigurationsFromAssembly(typeof(ModuleBranchNameIndex).Assembly, t => t == typeof(ModuleBranchNameIndex));
    }

    /// <summary>Pass 60b plan §5 row 13 (item 4c): a second configuration of the template's Branch applies beside the template's.</summary>
    public class BranchConfigurationExtensionTests
    {
        [Test]
        public void T13_AModuleConfigurationOfBranch_AddsItsUniqueIndex_KeepsTheTemplatesOwn_AndPassesTheGuard()
        {
            using var context = new BranchNameIndexContext();
            var branch = context.Model.FindEntityType(typeof(Branch))!;

            branch.GetIndexes().Should().Contain(i => i.IsUnique && i.Properties.Single().Name == nameof(Branch.Name));
            branch.GetIndexes().Should().Contain(i => i.IsUnique && i.Properties.Single().Name == nameof(Branch.Code));
            (branch.GetTableName(), branch.GetSchema()).Should().Be(("Branches", null), "the table stays the template's");
            DataModelGuard.FindProblems(context.Model).Should().BeEmpty();
        }
    }
}
