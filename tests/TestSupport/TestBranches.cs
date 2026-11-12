using System.Security.Cryptography;

namespace CleanArchitecture.Blazor.TestSupport;

/// <summary>
/// Branch codes and names for the template's own tests (Pass 60c, T9): ones an application's tightened branch rules
/// still accept.
/// </summary>
/// <remarks>
/// <para>
/// The integration harness runs every validator and posting handler of the application assembly, so a generated
/// project's module that tightens <c>AddEditBranchCommand</c> - as README "Extending branches" invites - also judges the
/// template's test branches. Until 1.7.1 those were codes like <c>"E3f9a1c2b"</c> (nine characters, lower case) and
/// names like "North" used by several tests, and Spare Parts' rule (at most six capitals and digits, unique name) could
/// not be added without failing them.
/// </para>
/// <para>
/// <b>The floor an application's rule must accept</b> (README "Extending branches"): a code of 1 to 6 upper-case letters
/// and digits starting with a letter (<see cref="CodeFloor"/>), and any name unique in the database. Every code here is
/// 6 such characters; every name carries a random tag. A test that needs a branch only as a fixture inserts it directly
/// and does not go through the command at all.
/// </para>
/// </remarks>
public static class TestBranches
{
    /// <summary>The code shape every application's branch rule must accept.</summary>
    public const string CodeFloor = "^[A-Z][A-Z0-9]{0,5}$";

    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    /// <summary>A random 6-character code: <c>T</c> and 5 upper-case letters or digits, e.g. <c>T7K2QX</c>.</summary>
    public static string NewCode() => "T" + RandomNumberGenerator.GetString(Alphabet, 5);

    /// <summary>A random tag, 8 upper-case letters or digits, to keep a test's branch names unique.</summary>
    public static string NewTag() => RandomNumberGenerator.GetString(Alphabet, 8);

    /// <summary><paramref name="label"/> with a random tag: a name no other test's branch has.</summary>
    public static string NewName(string label) => label + " " + NewTag();
}
