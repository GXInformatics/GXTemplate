// Copyright (c) MudBlazor 2021
// MudBlazor licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Security.Cryptography;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;

namespace CleanArchitecture.Blazor.Server.UI.Services.UserPreferences;

public interface IUserPreferencesService
{
    /// <summary>
    ///     Saves UserPreference in local storage
    /// </summary>
    /// <param name="userPreferences">The userPreferences to save in the local storage</param>
    public Task SaveUserPreferences(UserPreference userPreferences);

    /// <summary>
    ///     Loads UserPreference from local storage
    /// </summary>
    /// <returns>The user's saved choice. Null when they have made none, so the device's preference applies.</returns>
    public Task<UserPreference?> LoadUserPreferences();
}

public class UserPreferencesService : IUserPreferencesService
{
    private const string Key = "userPreferences";
    private readonly ProtectedLocalStorage _localStorage;

    public UserPreferencesService(ProtectedLocalStorage localStorage)
    {
        _localStorage = localStorage;
    }

    public async Task SaveUserPreferences(UserPreference userPreferences)
    {
        await _localStorage.SetAsync(Key, userPreferences);
    }

    public async Task<UserPreference?> LoadUserPreferences()
    {
        try
        {
            var result = await _localStorage.GetAsync<StoredPreference>(Key);
            if (!result.Success || result.Value is null) return null;

            // Pass 52: a value saved by the old theme drawer carries its other fields too; they are ignored. Its mode
            // (0 System, 1 Light, 2 Dark) is the reliable part: choosing System stored IsDarkMode = true whatever the
            // device said, so System reads as no choice and the device decides.
            return result.Value.DarkLightTheme switch
            {
                null => new UserPreference { IsDarkMode = result.Value.IsDarkMode },
                1 => new UserPreference { IsDarkMode = false },
                2 => new UserPreference { IsDarkMode = true },
                _ => null
            };
        }
        catch (CryptographicException)
        {
            await _localStorage.DeleteAsync(Key);
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>What may be stored under <see cref="Key"/>: this pass's one field, or the old drawer's value.</summary>
    private sealed class StoredPreference
    {
        public bool IsDarkMode { get; set; }

        /// <summary>The old drawer's mode. Never written since pass 52.</summary>
        public int? DarkLightTheme { get; set; }
    }
}
