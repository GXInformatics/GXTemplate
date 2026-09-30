using CleanArchitecture.Blazor.TestSupport;
using FluentAssertions;
using NUnit.Framework;

// Deliberately NOT under CleanArchitecture.Blazor.Application.IntegrationTests: the [SetUpFixture]
// there needs a database, and these tests of the guard need none, so they run even when it is missing.
namespace CleanArchitecture.Blazor.IntegrationHarness.Tests;

[TestFixture]
public class TestDatabaseGuardTests
{
    // The label PostgresTestDatabase passes to the guard.
    private const string Variable = "This test assembly";

    private static string? Refusal(string connectionString) => TestDatabaseGuard.Refusal(Variable, connectionString);

    [TestCase("Host=localhost;Port=5434;Database=" + TestDatabaseNames.AppInt + ";Username=postgres")]
    [TestCase("Host=localhost;Database=gx_test_;Username=postgres")]
    public void ADatabaseStartingWithGxTest_IsAccepted(string connectionString)
    {
        Refusal(connectionString).Should().BeNull();
    }

    [Test]
    public void TheApplicationsOwnDatabase_IsRefused()
    {
        // GXTemplateDatabase is the application's local database name, replaced at generation.
        var refusal = Refusal("Host=localhost;Port=5434;Database=GXTemplateDatabase;Username=postgres");

        refusal.Should().NotBeNull();
        refusal.Should().Contain(Variable).And.Contain("'GXTemplateDatabase'").And.Contain("gx_test_");
    }

    [TestCase("Host=localhost;Database=postgres;Username=postgres", TestName = "the maintenance database")]
    [TestCase("Host=localhost;Database=my_gx_test_db;Username=postgres", TestName = "the prefix in the middle")]
    [TestCase("Host=localhost;Database=GX_TEST_appint;Username=postgres", TestName = "the prefix in another case")]
    [TestCase("Host=localhost;Username=postgres", TestName = "no database named")]
    public void AnyOtherDatabase_IsRefused(string connectionString)
    {
        Refusal(connectionString).Should().NotBeNull();
    }

    [Test]
    public void AnUnparseableConnectionString_IsRefused_NotThrown()
    {
        Refusal("this is not a connection string").Should().Contain(Variable);
    }

    [Test]
    public void EveryDatabaseTheSuitesUse_PassesTheGuard_AndIsNamedAfterTheProject()
    {
        string[] names =
        [
            TestDatabaseNames.Unit, TestDatabaseNames.UnitLogs, TestDatabaseNames.Infra, TestDatabaseNames.InfraLogs,
            TestDatabaseNames.AppInt, TestDatabaseNames.Ui, TestDatabaseNames.UiLogs
        ];

        names.Should().OnlyHaveUniqueItems("no two test assemblies may share a database");
        foreach (var name in names)
        {
            Refusal($"Host=localhost;Database={name};Username=postgres").Should().BeNull(name);
            name.Should().StartWith("gx_test_" + TestDatabaseNames.Project + "_");
            name.Length.Should().BeLessThanOrEqualTo(63, "PostgreSQL truncates identifiers longer than 63 bytes");
        }
    }
}
