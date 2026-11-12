using CleanArchitecture.Blazor.Domain.Common.Entities;
using CleanArchitecture.Blazor.Domain.Common.Modules;
using CleanArchitecture.Blazor.Domain.Entities;
using CleanArchitecture.Blazor.Domain.Identity;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using CleanArchitecture.Blazor.Infrastructure.Persistence.Extensions;
using CleanArchitecture.Blazor.Infrastructure.Persistence.Rules;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CleanArchitecture.Blazor.Infrastructure.UnitTests.Persistence;

#region Sample model

// Every sample is in one module. Pass 60 put a module's tables in a schema of its own ("smp"); Pass 60c
// (owner decision D1) put every business table back in core, whatever its module.
public sealed class SampleModule : ModuleDefinition
{
    public override string Name => "Sample";
}

// Stands in for the "throwaway project with two entities" the standard asks you to verify against,
// except that this one runs on every build instead of once by hand.

[Module<SampleModule>]
public class SampleWidget : BaseAuditableEntity
{
    public string? Name { get; set; }
    public SampleMoney? Price { get; set; }
    public int? KindId { get; set; }
    public SampleWidgetKind? Kind { get; set; }
}

/// <summary>A lookup (Pass 60c): BaseLkEntity's fixed columns plus a code and a description.</summary>
[Module<SampleModule>]
public class SampleWidgetKind : BaseLkEntity
{
    public string? Code { get; set; }
    public string? Description { get; set; }
}

/// <summary>Names its own table; the convention must leave BOTH name and schema alone.</summary>
[Module<SampleModule>]
public class SamplePinnedThing : BaseEntity
{
    public string? Note { get; set; }
}

/// <summary>TPH root.</summary>
[Module<SampleModule>]
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
[Module<SampleModule>]
public class SampleAnnotatedThing : BaseEntity
{
    public string? Note { get; set; }
}

/// <summary>TPT root (Pass 55).</summary>
[Module<SampleModule>]
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
[Module<SampleModule>]
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
[Module<SampleModule>]
public class SampleCourse : BaseEntity
{
    public string? Title { get; set; }
    public ICollection<SampleStudent> Students { get; set; } = new List<SampleStudent>();
    public ICollection<SampleMentor> Mentors { get; set; } = new List<SampleMentor>();
}

[Module<SampleModule>]
public class SampleStudent : BaseEntity
{
    public string? Name { get; set; }
    public ICollection<SampleCourse> Courses { get; set; } = new List<SampleCourse>();
}

/// <summary>Many-to-many whose join table is named by hand - which must win.</summary>
[Module<SampleModule>]
public class SampleMentor : BaseEntity
{
    public string? Name { get; set; }
    public ICollection<SampleCourse> Courses { get; set; } = new List<SampleCourse>();
}

/// <summary>Named by hand WITHOUT a schema, inside a module: it stays where it was put (Pass 60c, owner decision D1).</summary>
[Module<SampleModule>]
public class SampleNamedInModule : BaseEntity
{
    public string? Note { get; set; }
}

/// <summary>Pass 60c: a column and a unique index named by hand - both kept, the index under the ux_tbl_ rule.</summary>
[Module<SampleModule>]
public class SampleHandNamedColumn : BaseEntity
{
    public string Label { get; set; } = "";
}

/// <summary>A business entity with no module: core like any other (Pass 60c); DataModelGuard refuses it for the missing module.</summary>
public class SampleOrphan : BaseEntity
{
    public string? Note { get; set; }
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
        builder.Entity<SampleNamedInModule>().ToTable("legacy_plain");
        builder.Entity<SampleOrphan>();
        builder.Entity<SampleHandNamedColumn>().Property(x => x.Label).HasColumnName("Label");
        builder.Entity<SampleHandNamedColumn>().HasIndex(x => x.Label).IsUnique().HasDatabaseName("ux_tbl_sample_hand_named_columns_label");
        SampleShapes.Configure(builder);

        for (var i = 0; i < ApplyCount; i++)
        {
            builder.ApplyGxTableNaming();
        }
    }

    // Pass 60c: the columns and constraint names, as ApplicationDbContext registers them.
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        configurationBuilder.Conventions.Add(_ => new GxSnakeCaseConvention());
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
    private const string Core = GxNamingConventions.BusinessSchema;

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
    public void ABusinessEntity_BecomesCoreTblPluralUpperSnake()
    {
        using var db = Sample();

        Assert.Equal(("TBL_SAMPLE_WIDGETS", Core), Mapping<SampleWidget>(db));
    }

    [Fact]
    public void ALookupEntity_BecomesCoreTblLkPluralUpperSnake()
    {
        using var db = Sample();

        Assert.Equal(("TBL_LK_SAMPLE_WIDGET_KINDS", Core),
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

        Assert.Equal(("TBL_SAMPLE_ANIMALS", Core), Mapping<SampleAnimal>(db));
        Assert.Equal(("TBL_SAMPLE_ANIMALS", Core), Mapping<SampleDog>(db));
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

        Assert.Equal(("TBL_SAMPLE_VEHICLES", Core), Mapping<SampleVehicle>(db));
        Assert.Equal(("TBL_SAMPLE_TRUCKS", Core), Mapping<SampleTruck>(db));
    }

    [Fact]
    public void AnAbstractTpcRoot_GetsNoTable_AndItsConcreteTypesGetOneEach()
    {
        using var db = Sample();

        Assert.Null(Mapping<SampleShape>(db).Table);
        Assert.Equal(("TBL_SAMPLE_CIRCLES", Core), Mapping<SampleCircle>(db));
        Assert.Equal(("TBL_SAMPLE_SQUARES", Core), Mapping<SampleSquare>(db));
    }

    [Fact]
    public void AnImplicitManyToManyJoin_IsNamedIntoCore_WithItsLastWordPlural()
    {
        // The join is a property bag with no CLR type of its own, so the IBusinessEntity test never
        // saw it: the project's own link table sat in the default schema as "SampleCourseSampleStudent".
        using var db = Sample();

        Assert.Equal(("TBL_SAMPLE_COURSE_SAMPLE_STUDENTS", Core),
            JoinMapping(db, "SampleCourseSampleStudent"));
    }

    [Fact]
    public void AJoinNamedByHand_KeepsItsName()
    {
        using var db = Sample();

        // Named by hand (ToTable on the join), so it keeps that name - not TBL_COURSE_MENTOR.
        Assert.Equal(("COURSE_MENTORS", Core), JoinMapping(db, "CourseMentor"));
    }

    [Fact]
    public void ATableNamedByHandWithoutASchema_StaysWhereItWasPut()
    {
        // Pass 60c (owner decision D1): as before Pass 60. ToTable("legacy_plain") is public."legacy_plain", module or
        // not; DataModelGuard then refuses a MODULE entity there (decision Q-B), which ModuleGuardTests asserts.
        using var db = Sample();

        Assert.Equal(("legacy_plain", (string?)null), Mapping<SampleNamedInModule>(db));
    }

    [Fact]
    public void AnEntityWithNoModule_IsInCoreLikeAnyOther()
    {
        // Pass 60c: the module plays no part in naming. DataModelGuard fails startup for this entity's missing module.
        using var db = Sample();

        Assert.Equal(("TBL_SAMPLE_ORPHANS", Core), Mapping<SampleOrphan>(db));
    }

    // ---- Pass 60c: snake_case columns and constraint names (owner decision D5) --------------------

    private static string Column<T>(DbContext db, string property) =>
        db.Model.FindEntityType(typeof(T))!.FindProperty(property)!.GetColumnName();

    [Fact]
    public void BusinessColumns_AreSnakeCase()
    {
        using var db = Sample();

        Assert.Equal("id", Column<SampleWidget>(db, "Id"));
        Assert.Equal("name", Column<SampleWidget>(db, nameof(SampleWidget.Name)));
        Assert.Equal("created_by_id", Column<SampleWidget>(db, nameof(SampleWidget.CreatedById)));
        Assert.Equal("last_modified_at", Column<SampleWidget>(db, nameof(SampleWidget.LastModifiedAt)));
        Assert.Equal("good_boy", Column<SampleDog>(db, nameof(SampleDog.GoodBoy)));
        Assert.Equal("payload_tonnes", Column<SampleTruck>(db, nameof(SampleTruck.PayloadTonnes)));
        Assert.Equal("discriminator", db.Model.FindEntityType(typeof(SampleAnimal))!.FindDiscriminatorProperty()!.GetColumnName());
    }

    [Fact]
    public void AnOwnedTypesColumns_AreSnakeCase_WithTheOwnersPrefix()
    {
        using var db = Sample();
        var price = db.Model.FindEntityType(typeof(SampleWidget))!.FindNavigation(nameof(SampleWidget.Price))!.TargetEntityType;

        Assert.Equal("price_amount", price.FindProperty(nameof(SampleMoney.Amount))!.GetColumnName());
        Assert.Equal("price_currency", price.FindProperty(nameof(SampleMoney.Currency))!.GetColumnName());
    }

    [Fact]
    public void ALookupsFixedColumns_AreIdNameSlug()
    {
        using var db = Sample();

        Assert.Equal("id", Column<SampleWidgetKind>(db, "Id"));
        Assert.Equal("name", Column<SampleWidgetKind>(db, nameof(SampleWidgetKind.Name)));
        Assert.Equal("slug", Column<SampleWidgetKind>(db, nameof(SampleWidgetKind.Slug)));
    }

    [Fact]
    public void KeysForeignKeysAndIndexes_AreEfsDefaultNames_InLowerCase()
    {
        using var db = Sample();

        Assert.Equal("pk_tbl_sample_widgets", db.Model.FindEntityType(typeof(SampleWidget))!.FindPrimaryKey()!.GetName());

        var join = db.Model.FindEntityType("SampleCourseSampleStudent")!;
        Assert.Equal(["courses_id", "students_id"], join.GetProperties().Select(p => p.GetColumnName()).Order().ToArray());
        // fk_<table>_<principal table>_<column>, lower case. These two are longer than PostgreSQL's 63 characters, so EF
        // cuts them to 63, exactly as MNEFleets' fk_tbl_accessory_charges_tbl_lk_approval_status_approval_statu.
        var foreignKeys = join.GetForeignKeys().Select(fk => fk.GetConstraintName()!).Order().ToArray();
        Assert.Equal(2, foreignKeys.Length);
        Assert.All(foreignKeys, name =>
        {
            Assert.StartsWith("fk_tbl_sample_course_sample_students_tbl_sample_", name);
            Assert.True(name.Length <= 63, name);
            Assert.Equal(name.ToLowerInvariant(), name);
        });
        Assert.Equal("fk_tbl_sample_widgets_tbl_lk_sample_widget_kinds_kind_id",
            db.Model.FindEntityType(typeof(SampleWidget))!.GetForeignKeys().Single().GetConstraintName());
        Assert.Equal(["ix_tbl_sample_course_sample_students_students_id"],
            join.GetIndexes().Select(i => i.GetDatabaseName()!).ToArray());
    }

    [Fact]
    public void AColumnNamedByHand_IsKept()
    {
        using var db = Sample();

        // A hand-named table keeps its name; its columns are still a business entity's, so snake_case.
        Assert.Equal("note", Column<SamplePinnedThing>(db, nameof(SamplePinnedThing.Note)));
        Assert.Equal("Label", Column<SampleHandNamedColumn>(db, nameof(SampleHandNamedColumn.Label)));
        Assert.Equal("ux_tbl_sample_hand_named_columns_label",
            db.Model.FindEntityType(typeof(SampleHandNamedColumn))!.GetIndexes().Single().GetDatabaseName());
    }

    [Theory]
    [InlineData("Id", "id")]
    [InlineData("CreatedById", "created_by_id")]
    [InlineData("EmployeesId", "employees_id")]          // MNEFleets, S:68
    [InlineData("RaisedById", "raised_by_id")]           // MNEFleets, S:78
    [InlineData("TripKitsId", "trip_kits_id")]
    [InlineData("BHOrderId", "bh_order_id")]             // an acronym run, then a word
    [InlineData("Address_Street", "address_street")]     // EF's owned-type column
    [InlineData("SKU", "sku")]
    [InlineData("Line2Total", "line2total")]             // the plug-in's own rule: no break after a digit
    [InlineData("xmin", "xmin")]
    public void ToSnake_IsTheNamingPluginsRewrite(string name, string expected)
    {
        Assert.Equal(expected, GxSnakeCaseConvention.ToSnake(name));
    }

    [Fact]
    public void GxSqlColumn_QuotesTheSnakeColumn()
    {
        Assert.Equal("\"is_main\"", GxSql.Column("IsMain"));
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
/// Business models go to <c>core</c> (Pass 60 moved them to a schema per module; Pass 60c moved them back); the template's own
/// infrastructure tables stay where they are.
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
    /// Every entity type the template itself ships. Since Pass 60 the list lives in the template
    /// (<see cref="TemplateEntities"/>), because DataModelGuard needs it at startup to refuse an
    /// application entity that declares no module; this test reads the same list.
    /// </summary>
    private static readonly IReadOnlyCollection<Type> TemplateEntityTypes = TemplateEntities.Types;

    [Theory]
    [InlineData(typeof(ApplicationUser), "AspNetUsers")]
    [InlineData(typeof(ApplicationRole), "AspNetRoles")]
    [InlineData(typeof(Tenant), "Tenants")]
    [InlineData(typeof(AuditTrail), "AuditTrails")]
    [InlineData(typeof(Document), "Documents")]
    [InlineData(typeof(PicklistSet), "PicklistSets")]
    [InlineData(typeof(SecurityPolicy), "SecurityPolicies")]
    [InlineData(typeof(ProcessedRequest), "ProcessedRequests")]
    [InlineData(typeof(DocumentSequence), "DocumentSequences")]
    [InlineData(typeof(Branch), "Branches")]
    [InlineData(typeof(UserBranch), "UserBranches")]
    [InlineData(typeof(ApprovalLimit), "ApprovalLimits")]
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
        // core holds the project's own business models and nothing of the template's (owner decision D2).
        // HasDefaultSchema("core") - the wrong way to do this - fails here by sweeping Identity in with
        // everything else. Every template table is in the DEFAULT schema, not merely out of core.
        using var db = BusinessContext();

        var templateTypesInCore = db.Model.GetEntityTypes()
            .Where(e => e.GetSchema() is not null && TemplateEntityTypes.Contains(e.ClrType))
            .Select(e => e.ClrType.Name)
            .ToArray();

        Assert.Empty(templateTypesInCore);
    }

    [Fact]
    public void EveryTableOutsideCore_IsOneOfTheTemplates()
    {
        // The other direction, and what keeps the list above honest: a project's own table belongs in
        // core, and a table the template adds must be listed in TemplateEntities, or this names it.
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
        Assert.Equal("TBL_SAMPLE_TRUCKS", db.Model.FindEntityType(typeof(SampleTruck))!.GetTableName());
        Assert.Equal("TBL_SAMPLE_COURSE_SAMPLE_STUDENTS", db.Model.FindEntityType("SampleCourseSampleStudent")!.GetTableName());
    }

    [Fact]
    public void OnPostgres_TheTemplateTablesAndTheHistoryAreNotSnakeCased_AndTheBusinessTablesAre()
    {
        // Pass 60c (owner decision D5): business tables get snake_case from GxSnakeCaseConvention, and NOTHING
        // else does. EFCore.NamingConventions must still not reach this context: beyond the business tables it
        // rewrites Identity, the template's tables and EF's own migration-history model - __EFMigrationsHistory gets
        // migration_id / product_version columns, and the day the plugin is removed EF queries "MigrationId" and
        // fails with 42703, leaving a database that can be neither migrated forward nor inspected.
        using var db = BusinessContext();

        var templateSnakeCased = db.Model.GetEntityTypes()
            .Where(e => TemplateEntityTypes.Contains(e.ClrType))
            .SelectMany(e => e.GetProperties().Select(p => $"{e.ClrType.Name}.{p.GetColumnName()}"))
            .Where(name => name.Split('.')[1] is var column && column.Contains('_') && column == column.ToLowerInvariant())
            .ToArray();
        Assert.Empty(templateSnakeCased);

        var businessNotSnake = db.Model.GetEntityTypes()
            .Where(e => !e.IsPropertyBag && !TemplateEntityTypes.Contains(e.ClrType) && e.GetTableName() is not null)
            .SelectMany(e => e.GetProperties().Select(p => p.GetColumnName()))
            .Where(column => column != column.ToLowerInvariant())
            .ToArray();
        Assert.Empty(businessNotSnake);

        var history = Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<Microsoft.EntityFrameworkCore.Migrations.IHistoryRepository>(db).GetCreateScript();
        Assert.Contains("\"MigrationId\"", history);
        Assert.Contains("\"ProductVersion\"", history);
    }

    [Fact]
    public void AHandNamedUniqueIndexOrCheckConstraint_WithoutTheGxPrefix_FailsTheModelBuild()
    {
        // Pass 60c (decision Q-E): on a business table a unique index named by hand starts ux_tbl_, a check
        // constraint ck_tbl_. Both offenders are named in one failure.
        using var db = new BadConstraintContext(new DbContextOptionsBuilder<BadConstraintContext>().UseNpgsql("Host=none").Options);

        var thrown = Assert.Throws<InvalidOperationException>(() => db.Model);
        Assert.Contains("'IX_Bad_Label'", thrown.Message);
        Assert.Contains("'label_positive'", thrown.Message);
        Assert.Contains("ux_tbl_", thrown.Message);
        Assert.Contains("ck_tbl_", thrown.Message);
    }

    private sealed class BadConstraintContext(DbContextOptions<BadConstraintContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            builder.Entity<SampleHandNamedColumn>().HasIndex(x => x.Label).IsUnique().HasDatabaseName("IX_Bad_Label");
            builder.Entity<SampleHandNamedColumn>().ToTable(t => t.HasCheckConstraint("label_positive", "length(\"Label\") > 0"));
            builder.ApplyGxTableNaming();
        }

        protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
            configurationBuilder.Conventions.Add(_ => new GxSnakeCaseConvention());
    }
}
