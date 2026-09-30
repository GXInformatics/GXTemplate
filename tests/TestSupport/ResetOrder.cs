namespace CleanArchitecture.Blazor.TestSupport;

/// <summary>
/// Orders tables so that deleting them in sequence never violates a foreign key: every table
/// comes before any table it references (children first).
/// </summary>
/// <remarks>
/// Pure, so the ordering rules are tested without a database (<c>ResetOrderTests</c>). A
/// self-reference (for example <c>AspNetUsers.SuperiorId</c>) does not constrain the order:
/// <c>DELETE FROM t</c> removes the whole table in one statement, and PostgreSQL checks a
/// NO ACTION constraint at the end of that statement. A true cycle between two or more tables
/// cannot be ordered at all, so it fails with the tables named, rather than resetting in an order
/// that fails later with an unrelated-looking foreign-key error.
/// </remarks>
public static class ResetOrder
{
    public static IReadOnlyList<string> ChildrenFirst(
        IEnumerable<string> tables, IEnumerable<(string Child, string Parent)> foreignKeys)
    {
        var remaining = new List<string>(tables);
        var edges = foreignKeys.Where(e => !string.Equals(e.Child, e.Parent, StringComparison.Ordinal)).ToList();
        var ordered = new List<string>(remaining.Count);

        while (remaining.Count > 0)
        {
            // A table nothing still in the list references can go now.
            var next = remaining.FirstOrDefault(t => !edges.Any(e =>
                string.Equals(e.Parent, t, StringComparison.Ordinal) && remaining.Contains(e.Child, StringComparer.Ordinal)));

            if (next is null)
            {
                throw new InvalidOperationException(
                    "The test database cannot be reset table by table: these tables reference each other " +
                    $"in a foreign-key cycle: {string.Join(", ", remaining.OrderBy(t => t, StringComparer.Ordinal))}. " +
                    "Break the cycle (a nullable key with ON DELETE SET NULL, or a deferrable constraint) or " +
                    "teach PostgresTestDatabase to reset it.");
            }

            ordered.Add(next);
            remaining.Remove(next);
        }

        return ordered;
    }
}
