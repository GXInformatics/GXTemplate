using CleanArchitecture.Blazor.Domain.Common.Entities;
using CleanArchitecture.Blazor.Domain.Entities;
using CleanArchitecture.Blazor.Domain.Identity;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
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

/// <summary>Names its table with the DataAnnotation, not ToTable - and must win the same way (Pass 55).</summary>
[System.ComponentModel.DataAnnotations.Schema.Table("annotated_things", Schema = "reporting")]
public class SampleAnnotatedThing : BaseEntity
{
    public string? Note { get; set; }
}

/// <summary>TPT root (Pass 55).</summary>
public class SampleVehicle : BaseEntity
{
    public string? Registration { get; set; }
}

/// <summary>TPT derived - has a table of its own, so it needs a name of its own.</summary>
public class SampleTruck : SampleVehicle
{
    public decimal PayloadTonnes { get; set; }
}

/// <summary>Abstract TPC root - stored nowhere, so it must not be given a table.</summary>
public abstract class SampleShape : BaseEntity
{
    public string? Colour { get; set; }
}

public class SampleCircle : SampleShape
{
    public decimal Radius { get; set; }
}

public class SampleSquare : SampleShape
{
    public decimal Side { get; set; }
}

/// <summary>Many-to-many with SampleStudent through an implicit join table (Pass 55).</summary>
public class SampleCourse : BaseEntity
{
    public string? Title { get; set; }
    public ICollection<SampleStudent> Students { get; set; } = new List<SampleStudent>();
    public ICollection<SampleMentor> Mentors { get; set; } = new List<SampleMentor>();
}

public class SampleStudent : BaseEntity
{
    public string? Name { get; set; }
    public ICollection<SampleCourse> Courses { get; set; } = new List<SampleCourse>();
}

/// <summary>Many-to-many whose join table is named by hand - which must win.</summary>
public class SampleMentor : BaseEntity
{
    public string? Name { get; set; }
    public ICollection<SampleCourse> Courses { get; set; } = new List<SampleCourse>();
}

internal static class SampleShapes
{
    /// <summary>
    /// The project-shaped additions (Pass 55): a TPT pair, an abstract-root TPC hierarchy, and two
    /// many-to-many joins, one named by hand. Shared by the convention tests and the
    /// "everything outside core is a template table" test, which runs them inside the real context.
    /// </summary>
    public static void Configure(ModelBuilder builder)
    {
        builder.Entity<SampleVehicle>().UseTptMappingStrategy();
        builder.Entity<SampleTruck>();

        builder.Entity<SampleShape>().UseTpcMappingStrategy();
        // TPC rows of one hierarchy share a key space across tables, so the key cannot be an
        // identity column per table; never-generated keeps model validation out of the test.
        builder.Entity<SampleShape>().Property(s => s.Id).ValueGeneratedNever();
        builder.Entity<SampleCircle>();
        builder.Entity<SampleSquare>();

        builder.Entity<SampleCourse>().HasMany(c => c.Students).WithMany(s => s.Courses);
        builder.Entity<SampleCourse>().HasMany(c => c.Mentors).WithMany(m => m.Courses)
            .UsingEntity("CourseMentor", j => j.ToTable("COURSE_MENTORS", GxNamingConventions.BusinessSchema));
    }
}

internal sealed class SampleContext(DbContextOptions<SampleContext> options) : DbContext(options)
{
    public DbSet<SampleWidget> Widgets => Set<SampleWidget>();
    public DbSet<SampleWidgetKind> WidgetKinds => Set<SampleWidgetKind>();
    public DbSet<SamplePinnedThing> PinnedThings => Set<SamplePinnedThing>();
    public DbSet<SampleAnimal> Animals => Set<SampleAnimal>();
    public DbSet<SampleAnnotatedThing> AnnotatedThings => Set<SampleAnnotatedThing>();

    /// <summary>How many times to run the convention, to prove it is idempotent.</summary>
    public int ApplyCount { get; init; } = 1;

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<SampleWidget>().OwnsOne(w => w.Price);
        builder.Entity<SampleDog>();
        builder.Entity<SamplePinnedThing>().ToTable("legacy_things", "reporting");
        SampleShapes.Configure(builder);

        for (var i = 0; i < ApplyCount; i++)
        {
            builder.ApplyGxTableNaming();
        }
    }
}

/// <summary>
/// Keys the model cache on <see cref="SampleContext.ApplyCount"/> as well as the context type.
/// </summary>
/// <remarks>
/// Pass 55. EF builds a model once per context TYPE, so without this the "apply twice" context
/// reused the model built by "apply once" and the idempotence test compared a model with itself.
/// </remarks>
internal sealed class ApplyCountModelCacheKeyFactory : Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime) =>
        context is SampleContext sample
            ? (context.GetType(), sample.ApplyCount, designTime)
            : (object)(context.GetType(), designTime);
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
        new(new DbContextOptionsBuilder<SampleContext>()
            .UseNpgsql("Host=none")
            .ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory, ApplyCountModelCacheKeyFactory>()
            .Options)
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
        Assert.Equal(Mapping<SampleTruck>(once), Mapping<SampleTruck>(twice));
        Assert.Equal(Mapping<SampleCircle>(once), Mapping<SampleCircle>(twice));
        Assert.Equal(JoinMapping(once, "SampleCourseSampleStudent"), JoinMapping(twice, "SampleCourseSampleStudent"));
    }

    // ---- Pass 55: the gaps the handover listed --------------------------------------------------

    private static (string? Table, string? Schema) JoinMapping(DbContext db, string joinName)
    {
        var join = db.Model.FindEntityType(joinName)!;
        return (join.GetTableName(), join.GetSchema());
    }

    [Fact]
    public void ATableAttribute_WinsOnBothNameAndSchema_LikeToTable()
    {
        // [Table] is recorded as ConfigurationSource.DataAnnotation, not Explicit. The convention used
        // to honour only Explicit, so this entity was renamed TBL_SAMPLE_ANNOTATED_THING in core.
        using var db = Sample();

        Assert.Equal(("annotated_things", "reporting"), Mapping<SampleAnnotatedThing>(db));
    }

    [Fact]
    public void ATptPair_GetsATblNameEach()
    {
        // In TPT the derived type HAS a table. It used to be skipped as if it were a TPH leaf, so it
        // kept EF's default name ("SampleTruck") in the default schema.
        using var db = Sample();

        Assert.Equal(("TBL_SAMPLE_VEHICLE", GxNamingConventions.BusinessSchema), Mapping<SampleVehicle>(db));
        Assert.Equal(("TBL_SAMPLE_TRUCK", GxNamingConventions.BusinessSchema), Mapping<SampleTruck>(db));
    }

    [Fact]
    public void AnAbstractTpcRoot_GetsNoTable_AndItsConcreteTypesGetOneEach()
    {
        using var db = Sample();

        Assert.Null(Mapping<SampleShape>(db).Table);
        Assert.Equal(("TBL_SAMPLE_CIRCLE", GxNamingConventions.BusinessSchema), Mapping<SampleCircle>(db));
        Assert.Equal(("TBL_SAMPLE_SQUARE", GxNamingConventions.BusinessSchema), Mapping<SampleSquare>(db));
    }

    [Fact]
    public void AnImplicitManyToManyJoin_IsNamedIntoCore()
    {
        // The join is a property bag with no CLR type of its own, so the IBusinessEntity test never
        // saw it: the project's own link table sat in the default schema as "SampleCourseSampleStudent".
        using var db = Sample();

        Assert.Equal(("TBL_SAMPLE_COURSE_SAMPLE_STUDENT", GxNamingConventions.BusinessSchema),
            JoinMapping(db, "SampleCourseSampleStudent"));
    }

    [Fact]
    public void AJoinNamedByHand_KeepsItsName()
    {
        using var db = Sample();

        // Named by hand (ToTable on the join), so it keeps that name - not TBL_COURSE_MENTOR.
        Assert.Equal(("COURSE_MENTORS", GxNamingConventions.BusinessSchema), JoinMapping(db, "CourseMentor"));
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
    // PostgreSQL only (pass 47). The provider only has to build a model; nothing opens a connection.
    //
    // Since Pass 55 this is the application's context PLUS a project's shapes - a TPT pair, an
    // abstract-root TPC hierarchy and two many-to-many joins (SampleShapes) - because "every table
    // outside core is a template table" is a claim about what a GENERATED project's model looks like,
    // and the bare template model has none of those shapes to get wrong.
    private sealed class ProjectShapedContext(DbContextOptions<ApplicationDbContext> options) : ApplicationDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            SampleShapes.Configure(builder);
            base.OnModelCreating(builder);
        }
    }

    private static ApplicationDbContext BusinessContext() =>
        new ProjectShapedContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql("Host=none").Options);

    /// <summary>
    /// Every entity type the template itself ships. Listed, not derived: a generated project's model
    /// adds its own types, and those belong in core.
    /// </summary>
    private static readonly Type[] TemplateEntityTypes =
    [
        typeof(ApplicationUser), typeof(ApplicationRole), typeof(ApplicationUserClaim), typeof(ApplicationRoleClaim),
        typeof(ApplicationUserLogin), typeof(ApplicationUserRole), typeof(ApplicationUserToken),
        typeof(IdentityUserPasskey<string>), typeof(Tenant), typeof(TenantUser), typeof(AuditTrail), typeof(Document),
        typeof(PicklistSet), typeof(SecurityPolicy), typeof(DataProtectionKey)
    ];

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
        // Core holds the project's own business models and nothing of the template's.
        // HasDefaultSchema("core") - the wrong way to do this - fails here by sweeping Identity in
        // with everything else. (Before pass 47 this asserted that core was EMPTY, which every
        // generated project failed on its first table: CO-166.)
        using var db = BusinessContext();

        var templateTypesInCore = db.Model.GetEntityTypes()
            .Where(e => e.GetSchema() == GxNamingConventions.BusinessSchema && TemplateEntityTypes.Contains(e.ClrType))
            .Select(e => e.ClrType.Name)
            .ToArray();

        Assert.Empty(templateTypesInCore);
    }

    [Fact]
    public void EveryTableOutsideCore_IsOneOfTheTemplates()
    {
        // The other direction, and what keeps the list above honest: a project's own table belongs in
        // core, and a table the template adds must be listed there, or this names it.
        //
        // Pass 55: over a PROJECT-shaped model (see ProjectShapedContext), and counting TABLES:
        //   - a type with no table of its own - the abstract TPC root, a TPH leaf - owns nothing to
        //     misplace, so it is skipped;
        //   - a TPT derived type and a many-to-many join ARE tables, and were exactly the two this
        //     test could not see before, because the bare template model contains neither. A join is
        //     reported by its EF name, since its CLR type is a shared Dictionary.
        using var db = BusinessContext();

        var unlisted = db.Model.GetEntityTypes()
            .Where(e => e.GetTableName() is not null && !e.IsOwned())
            .Where(e => e.GetSchema() != GxNamingConventions.BusinessSchema && !TemplateEntityTypes.Contains(e.ClrType))
            .Select(e => e.HasSharedClrType ? e.Name : e.ClrType.Name)
            .ToArray();

        Assert.Empty(unlisted);
    }

    [Fact]
    public void TheProjectShapedModel_ReallyContainsATptPairAndAJoin()
    {
        // Guards the test above from passing vacuously: if SampleShapes stopped producing these, the
        // "nothing outside core" assertion would be true of a model that no longer has them.
        using var db = BusinessContext();

        Assert.Equal(GxNamingConventions.BusinessSchema, db.Model.FindEntityType(typeof(SampleTruck))!.GetSchema());
        Assert.Equal("TBL_SAMPLE_TRUCK", db.Model.FindEntityType(typeof(SampleTruck))!.GetTableName());
        Assert.Equal("TBL_SAMPLE_COURSE_SAMPLE_STUDENT", db.Model.FindEntityType("SampleCourseSampleStudent")!.GetTableName());
    }

    [Fact]
    public void OnPostgres_TheBusinessModelIsNotSnakeCased()
    {
        // EFCore.NamingConventions must not reach this context. Beyond rewriting the GX table names,
        // it rewrites EF's own migration-history model: __EFMigrationsHistory gets migration_id /
        // product_version columns, and the day the plugin is removed EF queries "MigrationId" and
        // fails with 42703, leaving a database that can be neither migrated forward nor inspected.
        // This test is cheap; recovering from that means hand-editing EF's bookkeeping table.
        using var db = BusinessContext();

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
