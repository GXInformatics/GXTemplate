namespace CleanArchitecture.Blazor.TestSupport;

/// <summary>
/// The name of every test database, one per test assembly: <c>gx_test_&lt;project&gt;_&lt;suffix&gt;</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>One database per assembly.</b> <c>dotnet test</c> runs test assemblies side by side, and every
/// suite empties its database before each test, so two assemblies sharing one would empty each
/// other's tables mid-test.
/// </para>
/// <para>
/// <b>Named after the project.</b> <see cref="Project"/> is replaced at generation (template.json,
/// <c>TestDatabaseProject</c>) by the generated project's name, lower-cased, with everything but
/// letters, digits and underscore removed, and cut to 40 characters so that the longest name stays
/// within PostgreSQL's 63. So two GX projects tested against one server never share a database.
/// </para>
/// </remarks>
public static class TestDatabaseNames
{
    /// <summary>The project token. In the template repository it is the source name, sanitised.</summary>
    public const string Project = "cleanarchitectureblazor";

    /// <summary><c>gx_test_&lt;project&gt;_</c>: every database the suites use starts with it.</summary>
    public const string ProjectPrefix = TestDatabaseGuard.RequiredPrefix + Project + "_";

    /// <summary>Application.UnitTests.</summary>
    public const string Unit = ProjectPrefix + "unit";

    /// <summary>Application.UnitTests' log database, for the tests of the log sink and its DDL.</summary>
    public const string UnitLogs = ProjectPrefix + "unit_logs";

    /// <summary>Infrastructure.UnitTests.</summary>
    public const string Infra = ProjectPrefix + "infra";

    /// <summary>Infrastructure.UnitTests' log database, for the tests of the log table and the sink.</summary>
    public const string InfraLogs = ProjectPrefix + "infra_logs";

    /// <summary>Application.IntegrationTests.</summary>
    public const string AppInt = ProjectPrefix + "appint";

    /// <summary>Server.UI.IntegrationTests, business database.</summary>
    public const string Ui = ProjectPrefix + "ui";

    /// <summary>Server.UI.IntegrationTests, log database.</summary>
    public const string UiLogs = ProjectPrefix + "ui_logs";
}
