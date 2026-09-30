using System;
using CleanArchitecture.Blazor.TestSupport;
using FluentAssertions;
using Npgsql;
using NUnit.Framework;

// Deliberately NOT under CleanArchitecture.Blazor.Application.IntegrationTests: these tests connect to
// nothing, so they run even when GX_TEST_PG is missing and the [SetUpFixture] fails.
namespace CleanArchitecture.Blazor.IntegrationHarness.Tests;

/// <summary>
/// What <see cref="PostgresTestDatabase.Create"/> accepts before it connects to anything.
/// </summary>
[TestFixture]
public class PostgresTestDatabaseConfigurationTests
{
    /// <summary>A server-only connection string. Never opened by these tests.</summary>
    private const string Server = "Host=localhost;Port=5434;Username=postgres";

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void AMissingServer_FailsNamingTheVariable(string? server)
    {
        var act = () => PostgresTestDatabase.Create(TestDatabaseNames.AppInt, server);

        act.Should().Throw<InvalidOperationException>().WithMessage("*GX_TEST_PG is not set*");
    }

    [Test]
    public void AServerThatNamesADatabase_IsRefused()
    {
        var act = () => PostgresTestDatabase.Create(TestDatabaseNames.AppInt, Server + ";Database=GXTemplateDatabase");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*GX_TEST_PG names a database ('GXTemplateDatabase')*");
    }

    [Test]
    public void ADatabaseNameTheGuardRefuses_IsRefused()
    {
        var act = () => PostgresTestDatabase.Create("GXTemplateDatabase", Server);

        act.Should().Throw<InvalidOperationException>().WithMessage("*'GXTemplateDatabase'*gx_test_*");
    }

    [Test]
    public void AnUnparseableServer_IsRefused()
    {
        var act = () => PostgresTestDatabase.Create(TestDatabaseNames.AppInt, "this is not a connection string");

        act.Should().Throw<InvalidOperationException>().WithMessage("*GX_TEST_PG is not a valid*");
    }

    [Test]
    public void AValidServerAndName_GiveTheAssemblysConnectionString()
    {
        var database = PostgresTestDatabase.Create(TestDatabaseNames.AppInt, Server);

        database.DatabaseName.Should().Be(TestDatabaseNames.AppInt);
        var built = new NpgsqlConnectionStringBuilder(database.ConnectionString);
        built.Database.Should().Be(TestDatabaseNames.AppInt);
        built.Host.Should().Be("localhost");
        built.Port.Should().Be(5434);
    }
}
