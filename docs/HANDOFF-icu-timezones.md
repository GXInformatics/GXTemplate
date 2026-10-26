# GXTemplate — handoff: app-local ICU, so IANA time zones resolve on every Windows server (template 1.4.0)

**Why.** An app generated from this template (FMS, first staging deploy, 2026-10-26) answered **HTTP 500.30 — app failed
to start** on IIS. The log database was created; the business database was not. Yoab has met the same fault before.

The cause is the template's own `AppConfigurationSettings:DefaultTimeZone`, the IANA id `Africa/Lagos`. On Windows, .NET
resolves an IANA id only through ICU, and Windows ships ICU only from Windows 10 1903 / Windows Server 2022. On Windows
Server 2019 and older:
- `TimeZoneInfo.TryFindSystemTimeZoneById` returns false;
- `AppConfigurationSettings.Validate` refuses the id when the settings are first read (the
  `ApplicationDbContextInitializer` constructor);
- startup stops after `PrepareLogDatabaseAsync` and before `MigrateAsync`.

The fix is the template's, so every app generated from it carries it. FMS has the same change (its pass 17c).

## Done in the code

| # | Change | Where |
|---|---|---|
| 1 | `Microsoft.ICU.ICU4C.Runtime` 72.1.0.3 and `<RuntimeHostConfigurationOption Include="System.Globalization.AppLocalIcu" Value="72.1.0.3" />`: the app publishes `icuuc72.dll`, `icuin72.dll`, `icudt72.dll` for win-x64 and loads them instead of the system's | `src/Server.UI/Server.UI.csproj` |
| 2 | The process-wide-state line adds "ICU app-local 72.1.0.3; default time zone Africa/Lagos resolves: yes", written before the databases are touched | `src/Server.UI/Program.cs` |
| 3 | The `DefaultTimeZone` refusal names the likely cause (no ICU) and the three DLLs to look for | `src/Infrastructure/Configurations/AppConfigurationSettings.cs` |
| 4 | `AppLocalIcuTests`: the evaluated project references the package and sets the option with the same version, and never sets `System.Globalization.UseNls` | `tests/Application.UnitTests/Configurations/` |

## Do this, in order

1. **The version.** Take the latest stable `Microsoft.ICU.ICU4C.Runtime` on nuget.org (the code names 72.1.0.3). If you
   change it, change both numbers in `Server.UI.csproj` and the DLL names in row 3's message.
2. **Build** `CleanArchitecture.Blazor.slnx`; fix compile errors without changing behaviour. If a test asserts the exact
   process-wide-state line, update it for the two new fields.
3. **Run locally**: the startup line reads `ICU app-local 72.1.0.3` and `resolves: yes`.
4. **Publish** `src/Server.UI -c Release`: the three ICU DLLs sit beside the app, and the runtimeconfig carries
   `System.Globalization.AppLocalIcu`. Run the published exe once from a console against a scratch database.
5. **All four test projects**, each alone, as the template's passes do.
6. **Template 1.3.0 → 1.4.0**: the nuspec `<version>`, a 1.4.0 entry at the top of `<releaseNotes>` ("Apps carry their own
   ICU (Microsoft.ICU.ICU4C.Runtime, System.Globalization.AppLocalIcu), so IANA time zone ids such as Africa/Lagos
   resolve on Windows Server 2019 and older; the startup log says which ICU is in force"), `AppConfigurationSettings.Version`,
   and the README where it describes deployment or the version. Pack `GX.Blazor.Template.1.4.0.nupkg` as before.
7. **Generate a fresh app from the 1.4.0 package** into a scratch folder, build and publish it, and confirm the three
   DLLs and the startup line there too.
8. **Report** to `GXTemplate-passes/pass53-icu.md` (the next number after the last report; use the next free one if 53
   is taken): the version used, the startup line, the publish's ICU files, the tests, the generation check. Leave the
   work uncommitted for review, as the template's passes do.
