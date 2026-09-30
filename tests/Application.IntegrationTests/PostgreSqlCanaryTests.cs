using System.Threading.Tasks;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.IntegrationTests;

using static Testing;

/// <summary>
/// Proves the suite is talking to a PostgreSQL server, not just to something EF can open (CO-152).
/// </summary>
/// <remarks>
/// It asks the server itself rather than trusting the configured provider key, so a connection
/// string that reaches a different engine, or a harness that quietly falls back to another
/// provider, fails here instead of letting every other test pass on the wrong database.
/// </remarks>
public class PostgreSqlCanaryTests
{
    [Test]
    public async Task TheServer_IsPostgreSql()
    {
        using var scope = CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        context.Database.ProviderName.Should().Be("Npgsql.EntityFrameworkCore.PostgreSQL");

        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT version()";
            var version = (string?)await command.ExecuteScalarAsync();

            TestContext.Out.WriteLine($"Server: {version}");
            version.Should().StartWith("PostgreSQL ");
        }
        finally
        {
            await connection.CloseAsync();
        }
    }
}
