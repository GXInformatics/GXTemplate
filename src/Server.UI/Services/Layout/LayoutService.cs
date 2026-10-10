using CleanArchitecture.Blazor.Server.UI.Services.UserPreferences;
using CleanArchitecture.Blazor.Server.UI.Themes;

namespace CleanArchitecture.Blazor.Server.UI.Services.Layout;

/// <summary>
/// The application's theme: the GX theme (<c>Themes/Theme.cs</c>), light or dark.
/// </summary>
/// <remarks>
/// Pass 52: the theme drawer is gone, and with it the primary colour, border radius, font size and right-to-left
/// settings. What is left is the one choice the app bar's toggle makes, saved through
/// <see cref="IUserPreferencesService"/>.
/// </remarks>
public class LayoutService
{
    private readonly IUserPreferencesService _userPreferencesService;

    public LayoutService(IUserPreferencesService userPreferencesService)
    {
        _userPreferencesService = userPreferencesService;
    }

    public MudTheme CurrentTheme { get; } = Theme.ApplicationTheme();
    public bool IsDarkMode { get; private set; }

    /// <summary>Raised when <see cref="IsDarkMode"/> changes.</summary>
    public event Func<Task>? DarkModeChanged;

    /// <summary>
    /// Applies the user's saved choice or, when they have made none, the device's preference.
    /// </summary>
    public async Task ApplyUserPreferences(bool deviceIsDark)
    {
        var saved = await _userPreferencesService.LoadUserPreferences();
        await SetDarkMode(saved?.IsDarkMode ?? deviceIsDark);
    }

    /// <summary>
    /// Switches between light and dark, and saves the choice.
    /// </summary>
    public async Task ToggleDarkMode()
    {
        await SetDarkMode(!IsDarkMode);
        await _userPreferencesService.SaveUserPreferences(new UserPreference { IsDarkMode = IsDarkMode });
    }

    private async Task SetDarkMode(bool isDarkMode)
    {
        if (IsDarkMode == isDarkMode) return;

        IsDarkMode = isDarkMode;
        if (DarkModeChanged is not null)
        {
            foreach (Func<Task> handler in DarkModeChanged.GetInvocationList())
            {
                await handler();
            }
        }
    }
}
