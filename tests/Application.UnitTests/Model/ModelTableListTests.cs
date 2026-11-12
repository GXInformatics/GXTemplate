#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using CleanArchitecture.Blazor.Domain.Common.Modules;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using CleanArchitecture.Blazor.Infrastructure.Persistence.Rules;
using CleanArchitecture.Blazor.TestSupport;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.Model;

/// <summary>
/// Every table of the real model, checked against <c>tables.approved.txt</c> beside this file (Pass 60c, owner
/// decisions D3 and Q-D). One line per table: <c>schema.table &lt;- Entity [Module]</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> Table names are worked out by rules - <c>core</c>, <c>TBL_</c>, the plural of the entity name - and a
/// rule can give a surprising word (a plural you did not expect, a lookup prefix you forgot). This makes every table
/// name, and the module that owns it, a line in a file that changes in review the moment the model does, before a
/// migration exists to notice.
/// </para>
/// <para>
/// <b>In a generated project the file is the APPLICATION'S data, not part of this test.</b> The template ships it
/// listing its own tables only, so the first module entity fails this test once, by design: read the difference,
/// check each name, then approve it with <c>GX_APPROVE_TABLES=1 dotnet test --filter ModelTableListTests</c>, which
/// rewrites the file. Editing the file is not editing a template test.
/// </para>
/// </remarks>
public class ModelTableListTests
{
    public const string ApproveVariable = "GX_APPROVE_TABLES";

    [Test]
    public void TheModelsTables_AreTheApprovedList()
    {
        var actual = TableList();
        var path = ApprovedPath();

        if (Environment.GetEnvironmentVariable(ApproveVariable) == "1")
        {
            File.WriteAllText(path, actual);
            Assert.Pass($"{path} rewritten from the model; review the difference before committing it.");
        }

        var approved = File.Exists(path) ? File.ReadAllText(path).Replace("\r\n", "\n") : "";
        if (approved == actual) return;

        var was = approved.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var now = actual.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        Assert.Fail(
            "The model's tables differ from tests/Application.UnitTests/Model/tables.approved.txt." + Environment.NewLine +
            string.Concat(now.Except(was).Order().Select(l => "  + " + l + Environment.NewLine)) +
            string.Concat(was.Except(now).Order().Select(l => "  - " + l + Environment.NewLine)) +
            "Check every name (plural, TBL_ / TBL_LK_, core, the module), then approve the list:" + Environment.NewLine +
            $"  {ApproveVariable}=1 dotnet test tests/Application.UnitTests --filter ModelTableListTests" + Environment.NewLine +
            "(README \"Database naming\"). The file is your application's data; this test is the template's.");
    }

    /// <summary>The model's tables, one sorted line each, ending in a newline.</summary>
    public static string TableList(ApplicationDbContext? model = null)
    {
        using var context = model ?? new ApplicationDbContext(ApplicationModel.NpgsqlOptions("Host=model-only"));
        var lines = context.Model.GetEntityTypes()
            .Where(e => !e.IsOwned() && e.GetTableName() is not null && OwnsTable(e))
            .Select(e => $"{e.GetSchema() ?? "public"}.{e.GetTableName()} <- {Name(e)} [{Owner(e)}]")
            .Distinct()
            .Order(StringComparer.Ordinal);
        return string.Concat(lines.Select(l => l + "\n"));
    }

    /// <summary>A table is listed once, by the entity that maps it first in its hierarchy (TPH leaves share the root's).</summary>
    private static bool OwnsTable(IEntityType entityType) =>
        entityType.BaseType is null || entityType.GetTableName() != entityType.BaseType.GetTableName()
                                    || entityType.GetSchema() != entityType.BaseType.GetSchema();

    private static string Name(IEntityType entityType) =>
        entityType.HasSharedClrType ? entityType.Name + " (join)" : entityType.ClrType.Name;

    private static string Owner(IEntityType entityType)
    {
        if (entityType.IsPropertyBag)
        {
            var side = entityType.GetForeignKeys().Select(fk => ModuleDefinition.Of(fk.PrincipalEntityType.GetRootType().ClrType))
                .FirstOrDefault(m => m is not null);
            return side?.Name ?? "template";
        }
        if (TemplateEntities.Contains(entityType.ClrType)) return "template";
        return ModuleDefinition.Of(entityType.GetRootType().ClrType)?.Name ?? "no module";
    }

    private static string ApprovedPath([CallerFilePath] string here = "") =>
        Path.Combine(Path.GetDirectoryName(here)!, "tables.approved.txt");
}

[Module<ListedModule>] public class ListedWarehouse : CleanArchitecture.Blazor.Domain.Common.Entities.BaseEntity { }
public sealed class ListedModule : ModuleDefinition { public override string Name => "Listed"; }

public sealed class PlantedTableContext() : ApplicationDbContext(ApplicationModel.NpgsqlOptions("Host=model-only"))
{
    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<ListedWarehouse>();
        base.OnModelCreating(builder);
    }
}

public class ModelTableListPlantedTests
{
    [Test]
    public void APlantedModuleEntity_IsANewLine_WithItsCorePluralNameAndItsModule()
    {
        // What a generated project's first module entity does to the list: one new line, ready to review.
        var planted = ModelTableListTests.TableList(new PlantedTableContext());
        var template = ModelTableListTests.TableList();

        planted.Split('\n').Except(template.Split('\n')).Should().Equal("core.TBL_LISTED_WAREHOUSES <- ListedWarehouse [Listed]");
    }
}
