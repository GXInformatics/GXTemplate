using Mediator;
using MudBlazor;

namespace CleanArchitecture.Blazor.Server.UI.Components.Dialogs;

/// <summary>
/// Pass 52: the work a confirmation dialog's own button does. An action returns null when it succeeded, or the sentence
/// that says why it did not; the dialog then stays open and shows that sentence (<see cref="ConfirmationDialog"/>,
/// <c>Pages/Identity/Users/Components/ResetPasswordDialog.razor</c>, <see cref="DeleteConfirmation"/>).
/// </summary>
public static class DialogAction
{
    /// <summary>
    /// Runs <paramref name="action"/>. A validation failure (thrown by the mediator's validation behaviour before the
    /// handler runs) is a refusal like any other: its message is returned, not thrown.
    /// </summary>
    public static async Task<string?> RunAsync(Func<Task<string?>> action)
    {
        try
        {
            return await action();
        }
        catch (FluentValidation.ValidationException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// <see cref="RunAsync"/>, with <paramref name="dialog"/> held open meanwhile: no close button, backdrop click or
    /// Escape, so the only ways out are the dialog's own buttons (and Cancel is disabled while the work runs).
    /// </summary>
    public static async Task<string?> RunHeldOpenAsync(IMudDialogInstance dialog, Func<Task<string?>> action)
    {
        var options = dialog.Options;
        await dialog.SetOptionsAsync(options with { CloseButton = false, BackdropClick = false, CloseOnEscapeKey = false });
        try
        {
            return await RunAsync(action);
        }
        finally
        {
            await dialog.SetOptionsAsync(options);
        }
    }

    /// <summary>
    /// Sends <paramref name="request"/>; null when it succeeded, else its errors. A refused domain rule and a stale row
    /// version both arrive here as a failed result carrying their sentence (ResultExceptionBehavior).
    /// </summary>
    public static async Task<string?> FailureOf<T>(this IMediator mediator, IRequest<T> request) where T : CleanArchitecture.Blazor.Application.Common.Interfaces.IResult
    {
        var result = await mediator.Send(request);
        return result.Succeeded ? null : string.Join(", ", result.Errors);
    }
}
