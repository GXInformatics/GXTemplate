using CleanArchitecture.Blazor.Domain.Common.Entities;
using CleanArchitecture.Blazor.Domain.Entities;
using CleanArchitecture.Blazor.Domain.Identity;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using CleanArchitecture.Blazor.Infrastructure.Persistence.Extensions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CleanArchitecture.Blazor.Infrastructure.UnitTests.Persistence;

#region Sample model

// Stands in for the "throwaway project with two entities" the standard asks you to verify against,
// except that this one runs on every build instead of once by hand.

public class SampleWidget : BaseAuditableEntity
{
    public string? Name { get; set; }
    public SampleMoney? Price { get; set; }
}

/// <summary>Code and description, nothing branches on it - the lookup test, answered "no".</summary>
public class SampleWidgetKind : BaseEntity, ILookupEntity
{
    public string? Code { get; set; }
    public string? Description { get; set; }
}

/// <summary>Names its own table; the convention must leave BOTH name and schema alone.</summary>
public class SamplePinnedThing : BaseEntity
{
    public string? Note { get; set; }
}

/// <summary>TPH root.</summary>
public class SampleAnimal : BaseEntity
{
    public string? Name { get; set; }
}

/// <summary>TPH leaf - shares the root's table, and must keep sharing it.</summary>
public class SampleDog : SampleAnimal
{
    public bool GoodBoy { get; set; }
}

/// <summary>Owned value object - table-split into SampleWidget, and must stay split.</summary>
public class SampleMoney : IBusinessEntity
{
    public decimal Amount { get; set; }
    public string? Currency { get; set; }
}

internal sealed class SampleContext(DbContextOptions<SampleContext> options) : DbContext(options)
{
    public DbSet<SampleWidget> Widgets => Set<SampleWidget>();
    public DbSet<SampleWidgetKind> WidgetKinds => Set<SampleWidgetKind>();
    public DbSet<SamplePinnedThing> PinnedThings => Set<SamplePinnedThing>();
    public DbSet<SampleAnimal> Animals => Set<SampleAnimal>();

    /// <summary>How many times to run the convention, to prove it is idempotent.</summary>
    public int ApplyCount { get; init; } = 1;

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<SampleWidget>().OwnsOne(w => w.Price);
        builder.Entity<SampleDog>();
        builder.Entity<SamplePinnedThing>().ToTable("legacy_things", "reporting");

        for (var i = 0; i < ApplyCount; i++)
        {
            builder.ApplyGxTableNaming();
        }
    }
}

#endregion

/// <summary>
/// The GX naming standard, asserted on a model rather than trusted to a migration nobody reads.
/// </summary>
/// <remarks>
/// Every case here is one the convention gets wrong SILENTLY if it regresses. A predicate that stops
/// matching leaves tables in the default schema under EF's pluralised default, and nothing fails -
/// the application builds, boots and runs, against table names nobody asked for.
/// </remarks>
public class GxTableNamingTests
{
    // The provider only has to be enough to build a model; nothing here opens a connection.
    private static SampleContext Sample(int applyCount = 1) =>
        new(new DbContextOptionsBuilder<SampleContext>().UseSqlite("Data Source=:memory:").Options)
        { ApplyCount = applyCount };

    private static (string? Table, string? Schema) Mapping<T>(DbContext db)
    {
        var entity = db.Model.GetEntityTypes().Single(e => e.ClrType == typeof(T));
        return (entity.GetTableName(), entity.GetSchema());
    }

    [Fact]
    public void ABusinessEntity_BecomesCoreTblUpperSnake()
    {
        using var db = Sample();

        Assert.Equal(("TBL_SAMPLE_WIDGET", GxNamingConventions.BusinessSchema), Mapping<SampleWidget>(db));
    }

    [Fact]
    public void ALookupEntity_BecomesCoreTblLkUpperSnake()
    {
        using var db = Sample();

        Assert.Equal(("TBL_LK_SAMPLE_WIDGET_KIND", GxNamingConventions.BusinessSchema),
            Mapping<SampleWidgetKind>(db));
    }

    [Fact]
    public void AnExplicitToTable_WinsOnBothNameAndSchema()
    {
        // The schema half is the easy one to lose. A convention that skips the NAME but still calls
        // SetSchema moves a deliberately-pinned table into core while leaving it named as pinned -
        // which is exactly how this template's own Documents table would go missing.
        using var db = Sample();

        Assert.Equal(("legacy_things", "reporting"), Mapping<SamplePinnedThing>(db));
    }

    [Fact]
    public void ATphLeaf_KeepsSharingItsRootsTable()
    {
        // Naming a derived type in a TPH hierarchy silently converts the mapping to TPT - a
        // schema-strategy change wearing a rename's clothes.
        using var db = Sample();

        Assert.Equal(("TBL_SAMPLE_ANIMAL", GxNamingConventions.BusinessSchema), Mapping<SampleAnimal>(db));
        Assert.Equal(("TBL_SAMPLE_ANIMAL", GxNamingConventions.BusinessSchema), Mapping<SampleDog>(db));
    }

    [Fact]
    public void AnOwnedType_StaysTableSplitIntoItsOwner()
    {
        // Same shape of failure as TPH: naming it splits the value object out into a table of its
        // own, which is not what "apply a naming convention" is supposed to mean.
        using var db = Sample();

        Assert.Equal(Mapping<SampleWidget>(db), Mapping<SampleMoney>(db));
    }

    [Fact]
    public void ApplyingTheConventionTwice_ChangesNothing()
    {
        // Names derive from the CLR type, never from the current table name, so a second
        // `dotnet ef migrations add` yields an empty migration rather than TBL_TBL_SAMPLE_WIDGET.
        using var once = Sample();
        using var twice = Sample(applyCount: 2);

        Assert.Equal(Mapping<SampleWidget>(once), Mapping<SampleWidget>(twice));
        Assert.Equal(Mapping<SampleWidgetKind>(once), Mapping<SampleWidgetKind>(twice));
    }

    [Theory]
    [InlineData("StockMovement", "STOCK_MOVEMENT")]
    [InlineData("UomConversion", "UOM_CONVERSION")]     // not U_OM_CONVERSION
    [InlineData("IMSSetting", "IMS_SETTING")]           // acronym run, not IMSSETTING
    [InlineData("Item", "ITEM")]
    [InlineData("PurchaseOrderLine2", "PURCHASE_ORDER_LINE2")]
    public void ToUpperSnake_HandlesAcronymsAndCamelBoundaries(string clrName, string expected)
    {
        Assert.Equal(expected, GxNamingConventions.ToUpperSnake(clrName));
    }
}

/// <summary>
/// The other half of the standard: what the convention must NOT touch.
/// </summary>
/// <remarks>
/// Business models go to <c>core</c>; the template's own infrastructure tables stay where they are.
/// That line is the practical value of the schema split when someone opens pgAdmin, and it is what
/// keeps a template upgrade from handing every existing GX project a rename migration.
/// </remarks>
public class TemplateTablesStayOutOfCoreTests
{
    private static ApplicationDbContext BusinessContext(bool postgres = false) =>
        new(postgres
            ? new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql("Host=none;Database=none;").Options
            : new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite("Data Source=:memory:").Options);

    [Theory]
    [InlineData(typeof(ApplicationUser), "AspNetUsers")]
    [InlineData(typeof(ApplicationRole), "AspNetRoles")]
    [InlineData(typeof(Tenant), "Tenants")]
    [InlineData(typeof(AuditTrail), "AuditTrails")]
    [InlineData(typeof(Document), "Documents")]
    [InlineData(typeof(PicklistSet), "PicklistSets")]
    [InlineData(typeof(SecurityPolicy), "SecurityPolicies")]
    public void ATemplateTable_KeepsItsNameAndTheDefaultSchema(Type clrType, string expectedTable)
    {
        // Document and PicklistSet derive from BaseAuditableEntity and are therefore IBusinessEntity
        // exactly like a project entity; they stay put only because their configurations name their
        // table explicitly. Delete that line and this test is what notices.
        using var db = BusinessContext();

        var entity = db.Model.FindEntityType(clrType)!;

        Assert.Equal(expectedTable, entity.GetTableName());
        Assert.Null(entity.GetSchema());
    }

    [Fact]
    public void NoTemplateEntity_IsMappedIntoTheCoreSchema()
    {
        // The template ships no business models of its own, so core stays empty until a project adds
        // one. HasDefaultSchema("core") - the wrong way to do this - fails here by sweeping Identity
        // in with everything else.
        using var db = BusinessContext();

        var inCore = db.Model.GetEntityTypes()
            .Where(e => e.GetSchema() == GxNamingConventions.BusinessSchema)
            .Select(e => e.ClrType.Name)
            .ToArray();

        Assert.Empty(inCore);
    }

    [Fact]
    public void OnPostgres_TheBusinessModelIsNotSnakeCased()
    {
        // EFCore.NamingConventions must not reach this context. Beyond rewriting the GX table names,
        // it rewrites EF's own migration-history model: __EFMigrationsHistory gets migration_id /
        // product_version columns, and the day the plugin is removed EF queries "MigrationId" and
        // fails with 42703, leaving a database that can be neither migrated forward nor inspected.
        // This test is cheap; recovering from that means hand-editing EF's bookkeeping table.
        using var db = BusinessContext(postgres: true);

        var snakeCased = db.Model.GetEntityTypes()
            .SelectMany(e => e.GetProperties()
                .Select(p => p.GetColumnName())
                .Append(e.GetTableName() ?? string.Empty))
            .Where(name => name.Contains('_') && name == name.ToLowerInvariant())
            .Distinct()
            .ToArray();

        Assert.Empty(snakeCased);
    }
}
