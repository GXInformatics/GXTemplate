using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Application.Features.Identity.DTOs;
using CleanArchitecture.Blazor.Infrastructure.Services.Identity;

namespace CleanArchitecture.Blazor.Server.UI.Components.Inputs.Autocomplete;
#nullable disable warnings
public class PickSuperiorAutocomplete<T> : MudAutocomplete<ApplicationUserDto>
{
    public PickSuperiorAutocomplete()
    {
        SearchFunc = SearchKeyValues;
        ToStringFunc = dto => dto?.UserName;
        Clearable = true;
        Dense = true;
        ResetValueOnEmptyText = true;
        ShowProgressIndicator = true;
        MaxItems = 200;
    }
    [Parameter] public string? TenantId { get; set; }
    [Parameter] public string? OwnerName { get; set; }

    [Inject] private IDataSourceService<ApplicationUserDto> UserService { get; set; } = default!;

    /// <summary>
    /// The users eligible to be somebody's superior: the same tenant, excluding the owner.
    /// </summary>
    /// <remarks>
    /// <b>An absent <see cref="TenantId"/> now matches NOTHING.</b> It used to match everything: the
    /// predicate read <c>(x.TenantId != null &amp;&amp; x.TenantId.Equals(TenantId) || TenantId == null)</c>,
    /// so with no tenant supplied the clause was <c>|| true</c> - and the component's only call site
    /// supplied none. The result was a live cross-tenant user directory, searchable by username or
    /// email, inside the user-edit dialog.
    /// <para>
    /// <b>The default is the important half of the fix.</b> Passing the tenant at the call site
    /// repairs that call site; failing closed here repairs every call site not yet written. A filter
    /// whose absent-parameter behaviour is "everything" is not a filter - it is a filter-shaped
    /// thing that defaults to the leak, which is exactly how this defect arose.
    /// </para>
    /// </remarks>
    private Task<IEnumerable<ApplicationUserDto>> SearchKeyValues(string? value, CancellationToken cancellation)
    {
        if (string.IsNullOrEmpty(TenantId))
        {
            return Task.FromResult(Enumerable.Empty<ApplicationUserDto>());
        }

        var result = UserService.DataSource.Where(x =>
            x.TenantId != null && x.TenantId.Equals(TenantId, StringComparison.Ordinal) &&
            !x.UserName.Equals(OwnerName));

        if (!string.IsNullOrWhiteSpace(value))
        {
            result = result.Where(x =>
                x.UserName.Contains(value, StringComparison.OrdinalIgnoreCase) ||
                x.Email.Contains(value, StringComparison.OrdinalIgnoreCase));
        }

        return Task.FromResult(result);
    }
    protected override void OnInitialized()
    {
        UserService.OnChange += TenantsService_OnChange;
    }
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            await UserService.InitializeAsync();
        }

    }
    private async Task TenantsService_OnChange()
    {
        await InvokeAsync(StateHasChanged);
    }
    protected override async ValueTask DisposeAsyncCore()
    {
        UserService.OnChange -= TenantsService_OnChange;
        await base.DisposeAsyncCore();
    }
}
