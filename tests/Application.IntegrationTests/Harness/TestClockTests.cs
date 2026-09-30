using System;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Domain.Entities;
using FluentAssertions;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.IntegrationTests.Harness;

using static Testing;

/// <summary>
/// The harness clock is the application's clock (pass 47, CO-165): a test that sets it sees the
/// application stamp that instant, and the next test gets real time back.
/// </summary>
[TestFixture]
public class TestClockTests : TestBase
{
    private static readonly DateTime Fixed = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    [Test, Order(1)]
    public async Task ASetClock_IsTheTimeTheApplicationStamps()
    {
        Clock.Set(Fixed);

        var row = new PicklistSet { Name = Picklist.Brand, Value = "clocked", Text = "t" };
        await AddAsync(row);

        row.CreatedAt.Should().Be(Fixed, "the audit interceptor reads IDateTime, which the harness replaced with its clock");
    }

    [Test, Order(2)]
    public void TheReset_GivesRealTimeBack()
    {
        // ResetState ran in TestBase's [SetUp]: the fixed instant from the test before must be gone.
        Clock.UtcNow.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }
}
