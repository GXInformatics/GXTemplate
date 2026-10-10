namespace CleanArchitecture.Blazor.Server.UI.Services.UserPreferences;

/// <summary>
/// The one preference the application keeps: whether the user chose the dark theme.
/// </summary>
/// <remarks>
/// Pass 52: the theme drawer and everything it stored (primary colour, border radius, font size, right-to-left and a
/// "system" mode) are gone. The theme is fixed to <c>Themes/Theme.cs</c>, and the app bar's toggle switches it between
/// light and dark.
/// </remarks>
public class UserPreference
{
    public bool IsDarkMode { get; set; }
}
