using Npgsql;

namespace CleanArchitecture.Blazor.TestSupport;

/// <summary>
/// Refuses any test database whose name does not start with <see cref="RequiredPrefix"/>.
/// </summary>
/// <remarks>
/// The suites empty every table before each test. A connection string copied from the wrong place,
/// such as one naming the application's own database, would wipe a real database. The prefix is the
/// convention for throwaway test databases on a shared server, so anything else is refused before
/// the harness opens a connection.
/// <para>
/// Pure, so it is tested on its own (<c>TestDatabaseGuardTests</c>) without a database.
/// </para>
/// </remarks>
public static class TestDatabaseGuard
{
    public const string RequiredPrefix = "gx_test_";

    /// <summary>
    /// Why <paramref name="connectionString"/> (read from <paramref name="variable"/>) may not be used,
    /// or null when it names a database starting with <see cref="RequiredPrefix"/>.
    /// </summary>
    public static string? Refusal(string variable, string connectionString)
    {
        string? database;
        try
        {
            database = new NpgsqlConnectionStringBuilder(connectionString).Database;
        }
        catch (ArgumentException ex)
        {
            return $"{variable} is not a valid PostgreSQL connection string ({ex.Message}).";
        }

        if (string.IsNullOrWhiteSpace(database))
        {
            return $"{variable} names no database. Name a throwaway one starting with '{RequiredPrefix}'.";
        }

        if (!database.StartsWith(RequiredPrefix, StringComparison.Ordinal))
        {
            return $"{variable} names database '{database}'. The test suites write to their databases and empty " +
                   $"them before every test, so they only run against throwaway databases whose names start " +
                   $"with '{RequiredPrefix}'.";
        }

        return null;
    }
}
