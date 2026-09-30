using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Features.PicklistSets.Commands.AddEdit;
using CleanArchitecture.Blazor.Application.Features.PicklistSets.Commands.Delete;
using CleanArchitecture.Blazor.Domain.Entities;
using FluentAssertions;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.IntegrationTests.KeyValues.Commands;

using static Testing;

public class DeletePicklistTests : TestBase
{
    [Test]
    public async Task DeletingAnUnknownId_Succeeds_AndDeletesNothing()
    {
        // Pass 47 (F8): this test used to expect a NotFoundException without awaiting the assertion, so
        // it passed whatever the handler did. Awaited, it showed the handler does not throw: it deletes
        // the rows it finds, and for an id it cannot find that is none. That is what is asserted now.
        await SendAsync(new AddEditPicklistSetCommand { Name = Picklist.Brand, Text = "Kept", Value = "Kept" });

        var result = await SendAsync(new DeletePicklistSetCommand(new[] { 99 }));

        result.Succeeded.Should().BeTrue();
        (await CountAsync<PicklistSet>()).Should().Be(1);
    }

    [Test]
    public async Task ShouldDeleteKeyValue()
    {
        var addCommand = new AddEditPicklistSetCommand
        {
            Name = Picklist.Brand,
            Text = "Word",
            Value = "Word",
            Description = "For Test"
        };
        var result = await SendAsync(addCommand);
        (await FindAsync<PicklistSet>(result.Data)).Should().NotBeNull("the row must exist before the delete");

        await SendAsync(new DeletePicklistSetCommand(new[] { result.Data }));

        // PicklistSet, not Document (CO-158): a Document with this id never existed, so the old
        // assertion held whether or not the delete ran.
        var item = await FindAsync<PicklistSet>(result.Data);

        item.Should().BeNull();
    }
}
