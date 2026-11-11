using CleanArchitecture.Blazor.Application.Features.Branches;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Options;

namespace CleanArchitecture.Blazor.Server.UI.Services.Branches;

/// <summary>
/// Which screens maintain branches (Pass 60b, item 4d). An application replaces the template's dialog, page or both
/// from its module's (or the application level's) Server.UI registrations:
/// <code>
/// services.Configure&lt;BranchScreenOptions&gt;(o =&gt;
/// {
///     o.FormDialog = typeof(OrgBranchDialog);   // a component with [Parameter] AddEditBranchCommand Model
///     o.PageRoute  = "/org/branches";           // the module's own page
/// });
/// </code>
/// </summary>
/// <remarks>
/// With a <see cref="PageRoute"/>, the template's page at <see cref="TemplateRoute"/> redirects there and the menu entry
/// points there, so no template screen offers a way around the application's. The rules themselves are server-side
/// either way: every save goes through <see cref="AddEditBranchCommand"/>, its validators and its event handlers.
/// Startup fails if either setting is wrong (<see cref="BranchScreenOptionsValidator"/>).
/// </remarks>
public sealed class BranchScreenOptions
{
    /// <summary>The template's own page.</summary>
    public const string TemplateRoute = "/system/branches";

    /// <summary>The dialog the branch page opens to add or edit a branch. Default: the template's.</summary>
    public Type FormDialog { get; set; } = typeof(Pages.Branches.BranchFormDialog);

    /// <summary>The page that lists branches, or null for the template's.</summary>
    public string? PageRoute { get; set; }

    /// <summary>Where the branch page is: <see cref="PageRoute"/> or the template's route.</summary>
    public string EffectiveRoute => string.IsNullOrWhiteSpace(PageRoute) ? TemplateRoute : PageRoute;

    /// <summary>True when the template's page should send its visitors elsewhere.</summary>
    public bool Redirects => !string.Equals(EffectiveRoute.TrimEnd('/'), TemplateRoute, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Fails startup on a dialog that cannot take the command, or a route no page answers (Pass 60b).</summary>
public sealed class BranchScreenOptionsValidator : IValidateOptions<BranchScreenOptions>
{
    public ValidateOptionsResult Validate(string? name, BranchScreenOptions options)
    {
        var problems = new List<string>();

        var dialog = options.FormDialog;
        var model = dialog?.GetProperty("Model");
        if (dialog is null || !typeof(IComponent).IsAssignableFrom(dialog))
            problems.Add($"BranchScreenOptions.FormDialog ({dialog?.FullName ?? "null"}) is not a Razor component.");
        else if (model is null || model.PropertyType != typeof(AddEditBranchCommand) || !model.IsDefined(typeof(ParameterAttribute), true))
            problems.Add($"BranchScreenOptions.FormDialog {dialog.FullName} has no public [Parameter] AddEditBranchCommand Model; " +
                         "the branch page passes the command to it under that name.");

        if (!string.IsNullOrWhiteSpace(options.PageRoute) && !RouteExists(options.PageRoute))
            problems.Add($"BranchScreenOptions.PageRoute '{options.PageRoute}' matches no @page in {typeof(BranchScreenOptions).Assembly.GetName().Name}.");

        return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
    }

    private static bool RouteExists(string route) =>
        typeof(BranchScreenOptions).Assembly.GetTypes()
            .Where(t => typeof(IComponent).IsAssignableFrom(t))
            .SelectMany(t => System.Reflection.CustomAttributeExtensions.GetCustomAttributes<RouteAttribute>(t))
            .Any(r => string.Equals(r.Template.TrimEnd('/'), route.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
}
