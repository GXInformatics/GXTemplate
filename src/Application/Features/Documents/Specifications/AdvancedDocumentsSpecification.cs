namespace CleanArchitecture.Blazor.Application.Features.Documents.Specifications;
#nullable disable warnings

/// <summary>
/// The documents listing: what this principal may see, narrowed by the chosen list view and keyword.
/// </summary>
/// <remarks>
/// <b>Visibility is applied once, to every list view, before any of them narrows anything.</b> It
/// used to be stated inside the branches instead - spelled out for <c>All</c>, spelled out again for
/// <c>My</c>, and simply absent from <c>TODAY</c> and <c>LAST_30_DAYS</c>, which filtered on date
/// alone. Selecting "Created today" therefore listed every tenant's documents, including other
/// users' private ones, from a page whose own download button would refuse to open them.
/// <para>
/// The fix is structural rather than a third and fourth copy of the clause: a list view now says
/// only what it adds. <c>All</c> adds nothing, <c>My</c> adds an owner, the two date views add a
/// window - and none of them can subtract, because the rule is no longer theirs to restate.
/// </para>
/// <para>
/// <b>One behaviour change falls out of this,</b> and it is a correction. The old <c>All</c> branch
/// applied its tenant test only to the public half, so a principal's own private document in another
/// tenant was listed - while <c>VisibleDocumentSpecification</c>, which governs the download and the
/// <c>/files</c> endpoint, refused to serve it. The listing and the download now agree, which is the
/// point of having one definition.
/// </para>
/// </remarks>
public class AdvancedDocumentsSpecification : Specification<Document>
{
    public AdvancedDocumentsSpecification(AdvancedDocumentsFilter filter)
    {
        DateTime today = DateTime.UtcNow;
        var todayrange = today.GetDateRange("TODAY", filter.CurrentUser.LocalTimeOffset);
        var last30daysrange = today.GetDateRange("LAST_30_DAYS", filter.CurrentUser.LocalTimeOffset);

        // Unconditional, and first. Every list view is a narrowing of this.
        Query.Where(VisibleDocumentSpecification.IsVisibleTo(
                filter.CurrentUser.UserId, filter.CurrentUser.TenantId))

            // "Created by me" adds an owner. Visibility above already grants a principal their own
            // private documents, so this narrows rather than widens.
            .Where(p => p.CreatedById == filter.CurrentUser.UserId, filter.ListView == DocumentListView.My)

            .Where(x => x.CreatedAt >= todayrange.Start && x.CreatedAt < todayrange.End.AddDays(1), filter.ListView == DocumentListView.TODAY)
            .Where(x => x.CreatedAt >= last30daysrange.Start, filter.ListView == DocumentListView.LAST_30_DAYS)
            .Where(
                x => x.Title.Contains(filter.Keyword) || x.Description.Contains(filter.Keyword),
                !string.IsNullOrEmpty(filter.Keyword));
    }
}
