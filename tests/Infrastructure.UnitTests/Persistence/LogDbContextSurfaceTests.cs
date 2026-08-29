using System.Reflection;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CleanArchitecture.Blazor.Infrastructure.UnitTests.Persistence;

/// <summary>
/// The read-only posture of <see cref="ILogDbContext"/>, asserted structurally.
/// </summary>
/// <remarks>
/// "Nothing in the business layer can write to the log database" is a claim about the shape of an
/// interface, not about anyone's discipline, and it is only worth making if it is checked. If a
/// later change hands out a <c>DbSet&lt;SystemLog&gt;</c> for convenience, every mutation on it
/// becomes expressible again and this test is the thing that notices.
/// </remarks>
public class LogDbContextSurfaceTests
{
    private static readonly Type Contract = typeof(ILogDbContext);

    [Fact]
    public void TheInterface_ExposesNoDbSet()
    {
        var dbSets = Contract.GetProperties()
            .Where(p => p.PropertyType.IsGenericType &&
                        p.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>))
            .Select(p => p.Name)
            .ToArray();

        Assert.Empty(dbSets);
    }

    [Fact]
    public void TheLogRows_AreExposedAsAQueryable()
    {
        var property = Contract.GetProperty(nameof(ILogDbContext.SystemLogs));

        Assert.NotNull(property);
        Assert.Equal(typeof(IQueryable<>), property!.PropertyType.GetGenericTypeDefinition());
        Assert.Null(property.SetMethod);
    }

    [Theory]
    [InlineData("Add")]
    [InlineData("AddAsync")]
    [InlineData("AddRange")]
    [InlineData("Attach")]
    [InlineData("Update")]
    [InlineData("UpdateRange")]
    [InlineData("Remove")]
    [InlineData("RemoveRange")]
    [InlineData("SaveChanges")]
    [InlineData("SaveChangesAsync")]
    public void TheInterface_OffersNoWriteOperation(string forbidden)
    {
        var found = Contract.GetMethods().Select(m => m.Name);

        Assert.DoesNotContain(forbidden, found);
    }

    [Fact]
    public void TheOnlyWriteIsTheNamedPurge()
    {
        // The purge is a real capability - the Clear Logs button, behind Permissions.Logs.Purge - so
        // it is declared by name rather than smuggled in as a side effect of exposing something
        // writable. Deleting everything should be the one destructive thing that is easy to find.
        var declared = Contract.GetMethods()
            .Where(m => !m.IsSpecialName)
            .Select(m => m.Name)
            .ToArray();

        Assert.Equal([nameof(ILogDbContext.PurgeAsync)], declared);
    }

    [Fact]
    public void NoHandlerReachesTheLogTableThroughTheBusinessContext()
    {
        // The paired negative for the model test: even the type name must be gone from the business
        // contract, or a handler could still write db.SystemLogs and compile.
        var members = typeof(IApplicationDbContext).GetProperties().Select(p => p.Name);

        Assert.DoesNotContain("SystemLogs", members);
    }
}
