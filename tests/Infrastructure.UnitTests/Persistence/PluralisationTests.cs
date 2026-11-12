using CleanArchitecture.Blazor.Domain.Common.Entities;
using CleanArchitecture.Blazor.Infrastructure.Persistence.Extensions;
using Xunit;

namespace CleanArchitecture.Blazor.Infrastructure.UnitTests.Persistence;

[PluralName("TestCriteria")] public class TestCriterion : BaseEntity { }
[PluralName("StockHistory")] public class StockHistory : BaseEntity { }
[PluralName("stock history")] public class BadlyOverridden : BaseEntity { }
public class GoodsVatRate : BaseEntity { }
public class VehicleMake : BaseLkEntity { }
public class PurchaseOrderLine2 : BaseEntity { }

/// <summary>
/// Pass 60c (owner decision D3): table names are TBL_ + the plural entity name in upper snake case. Every rule of
/// <see cref="GxNamingConventions.Pluralise"/> and every entry of its fixed lists is pinned here, so a change to a
/// plural is a visible test change, never a silent table rename.
/// </summary>
public class PluralisationTests
{
    [Theory]
    // The owner's own example: singular and plural class names give one table.
    [InlineData("StoreMovement", "StoreMovements")]
    [InlineData("StoreMovements", "StoreMovements")]
    [InlineData("StockMovement", "StockMovements")]
    // Rule 9: + s.
    [InlineData("Warehouse", "Warehouses")]
    [InlineData("Bin", "Bins")]
    [InlineData("Item", "Items")]
    [InlineData("Uom", "Uoms")]
    [InlineData("UomConversion", "UomConversions")]
    [InlineData("PriceList", "PriceLists")]
    [InlineData("JournalVoucher", "JournalVouchers")]
    [InlineData("Employee", "Employees")]
    [InlineData("Route", "Routes")]
    [InlineData("TripKit", "TripKits")]
    [InlineData("Company", "Companies")]                 // rule 8
    // Rule 8: consonant + y -> ies; vowel + y -> + s.
    [InlineData("Category", "Categories")]
    [InlineData("Inventory", "Inventories")]
    [InlineData("History", "Histories")]
    [InlineData("Day", "Days")]
    [InlineData("Key", "Keys")]
    [InlineData("Survey", "Surveys")]
    [InlineData("Journey", "Journeys")]
    // Rule 7: s, x, z, ch, sh -> + es.
    [InlineData("Status", "Statuses")]
    [InlineData("Address", "Addresses")]
    [InlineData("Process", "Processes")]
    [InlineData("Bus", "Buses")]
    [InlineData("Gas", "Gases")]
    [InlineData("Box", "Boxes")]
    [InlineData("Tax", "Taxes")]
    [InlineData("Batch", "Batches")]
    [InlineData("Branch", "Branches")]
    [InlineData("Dispatch", "Dispatches")]
    [InlineData("Wash", "Washes")]
    [InlineData("Waltz", "Waltzes")]
    // Rule 6: sis -> ses.
    [InlineData("Analysis", "Analyses")]
    [InlineData("Basis", "Bases")]
    [InlineData("CostAnalysis", "CostAnalyses")]
    // Rule 5: ends in s (not ss/us/is/as/os): already plural.
    [InlineData("Invoices", "Invoices")]
    [InlineData("Categories", "Categories")]
    [InlineData("Days", "Days")]
    [InlineData("Settings", "Settings")]
    [InlineData("TripRequests", "TripRequests")]
    // Rule 4: an acronym.
    [InlineData("VAT", "VATS")]
    [InlineData("GoodsVAT", "GoodsVATS")]
    [InlineData("IMS", "IMS")]
    [InlineData("IMSSetting", "IMSSettings")]
    // Rules 1-2: the fixed irregular list, and the plural forms it gives (unchanged).
    [InlineData("Person", "People")]
    [InlineData("SalesPerson", "SalesPeople")]
    [InlineData("Child", "Children")]
    [InlineData("Man", "Men")]
    [InlineData("Woman", "Women")]
    [InlineData("Criterion", "Criteria")]
    [InlineData("Datum", "Data")]
    [InlineData("Medium", "Media")]
    [InlineData("Index", "Indices")]
    [InlineData("PriceIndex", "PriceIndices")]
    [InlineData("Matrix", "Matrices")]
    [InlineData("Vertex", "Vertices")]
    [InlineData("Leaf", "Leaves")]
    [InlineData("Shelf", "Shelves")]
    [InlineData("Half", "Halves")]
    [InlineData("Knife", "Knives")]
    [InlineData("Life", "Lives")]
    [InlineData("Wife", "Wives")]
    [InlineData("Foot", "Feet")]
    [InlineData("Tooth", "Teeth")]
    [InlineData("Goose", "Geese")]
    [InlineData("Mouse", "Mice")]
    [InlineData("Ox", "Oxen")]
    [InlineData("Quiz", "Quizzes")]
    [InlineData("People", "People")]
    [InlineData("Children", "Children")]
    // Rule 3: uncountable words, unchanged.
    [InlineData("Equipment", "Equipment")]
    [InlineData("OfficeEquipment", "OfficeEquipment")]
    [InlineData("Information", "Information")]
    [InlineData("Data", "Data")]
    [InlineData("Series", "Series")]
    [InlineData("Species", "Species")]
    [InlineData("News", "News")]
    [InlineData("Staff", "Staff")]
    [InlineData("Personnel", "Personnel")]
    [InlineData("Feedback", "Feedback")]
    [InlineData("Software", "Software")]
    [InlineData("Metadata", "Metadata")]
    [InlineData("Sheep", "Sheep")]
    [InlineData("Deer", "Deer")]
    // Only the LAST word changes.
    [InlineData("CategoryItem", "CategoryItems")]
    [InlineData("StatusHistory", "StatusHistories")]
    [InlineData("BoxLabel", "BoxLabels")]
    public void Pluralise_GivesTheFixedPlural(string singular, string expected)
    {
        Assert.Equal(expected, GxNamingConventions.Pluralise(singular));
    }

    [Theory]
    [InlineData("StoreMovement", "TBL_STORE_MOVEMENTS")]
    [InlineData("StoreMovements", "TBL_STORE_MOVEMENTS")]
    [InlineData("UomConversion", "TBL_UOM_CONVERSIONS")]
    [InlineData("IMSSetting", "TBL_IMS_SETTINGS")]
    [InlineData("VAT", "TBL_VATS")]
    [InlineData("SampleCourseSampleStudent", "TBL_SAMPLE_COURSE_SAMPLE_STUDENTS")]
    public void TheTableName_IsTblPlusThePluralInUpperSnake(string name, string expected)
    {
        Assert.Equal(expected, "TBL_" + GxNamingConventions.ToUpperSnake(GxNamingConventions.Pluralise(name)));
    }

    [Fact]
    public void ALookup_IsTblLkPlusThePlural()
    {
        Assert.Equal("TBL_LK_VEHICLE_MAKES", GxNamingConventions.TableNameFor(typeof(VehicleMake)));
    }

    [Fact]
    public void PluralName_OverridesTheRules_ForTheWholeClassName()
    {
        Assert.Equal("TBL_TEST_CRITERIA", GxNamingConventions.TableNameFor(typeof(TestCriterion)));
        Assert.Equal("TBL_STOCK_HISTORY", GxNamingConventions.TableNameFor(typeof(StockHistory)));   // kept singular on purpose
        Assert.Equal("TBL_GOODS_VAT_RATES", GxNamingConventions.TableNameFor(typeof(GoodsVatRate)));
    }

    [Fact]
    public void APluralNameThatIsNotAPascalIdentifier_FailsLoudly()
    {
        var thrown = Assert.Throws<InvalidOperationException>(() => GxNamingConventions.TableNameFor(typeof(BadlyOverridden)));
        Assert.Contains("BadlyOverridden", thrown.Message);
        Assert.Contains("PascalCase", thrown.Message);
    }

    [Fact]
    public void ANameEndingInADigit_FailsLoudly_AskingForPluralName()
    {
        var thrown = Assert.Throws<InvalidOperationException>(() => GxNamingConventions.TableNameFor(typeof(PurchaseOrderLine2)));
        Assert.Contains("PurchaseOrderLine2", thrown.Message);
        Assert.Contains("[PluralName", thrown.Message);
    }

    [Fact]
    public void TheFixedLists_AreAllPinnedAbove()
    {
        // A word added to either list without a row above fails here, so the lists cannot grow unreviewed.
        Assert.Equal(22, GxNamingConventions.IrregularPlurals.Count);
        Assert.Equal(34, GxNamingConventions.UnchangedWords.Count);
    }
}
