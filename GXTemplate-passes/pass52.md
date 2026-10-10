# Pass 52 — The app-bar light/dark toggle and GxSubmitButton

**Suites (template solution, each project alone, foreground, PostgreSQL 15.7 at 127.0.0.1:5432):**
Application.UnitTests **531 passed + 14 skipped** · Infrastructure.UnitTests **221** ·
Application.IntegrationTests **42** · Server.UI.IntegrationTests **283** (was 261) — 0 failed.

**Date:** 2026-10-10. **Start:** HEAD `1693dccb` ("pass48"), clean tree.
**Commit:** none. The work is left uncommitted for review.
**Template version:** 1.1.0 → **1.2.0**.
**Origin:** `docs/passes/gxtemplate-version-check.md`. A generation from the installed package still showed the
theme drawer, and the version check found that the toggle and GxSubmitButton had never reached the template.
**Name:** this is pass 46's "Pass 52 — UI", narrowed to what was asked for. The next sequential number would
have been 49.

---

## Summary

1. **Scope: the D4 UI items of pass 46.** Covered: CO-110 to CO-118, CO-138 and CO-139, with their tests CO-178
   to CO-180, `ThemeToggleComponentTests`, and CO-122 (needed to compile).
   - The rest of planned pass 52 is **not done** (§4): dashboard, fonts, branding, favicon, CDN tests, and the
     Documents fixes.
2. **Ported from GX Project Tracking**, which was generated from this template at pass44 (`d7e8c31`, "baseline:
   generated from GXTemplate pass44 (135ed419)"). Each commit's diff was applied file by file, with
   `GXProjectTracking` rewritten to `CleanArchitecture.Blazor`:

   | GXPT commit | What | Taken |
   |---|---|---|
   | `a514c89` pass18c | `GxSubmitButton`, `gx-busy-button.js`, Login, the first tests | all |
   | `1c5cbdf` pass18d | the button everywhere, `.gx-busy`, `MudLoadingButton` deleted, `SubmitButtonGuardTests`, busy labels in 4 cultures | all, except the Projects pages, `FileUploadZone` (deleted in pass 48) and `WaitingForAccess` (not in the template; pass 49) |
   | `23b1949` pass19a | `DialogAction`, the dialog contract, row actions inside their dialog | the dialog subset only (9 files). Its Documents removal, logo/divider and Projects changes were left out (D5 keeps Documents). |
   | `2948e0d` pass20 | `ThemeToggle` replaces the theme drawer; first-paint fix | all, except `AppBarLogoComponentTests` (the template has no app-bar logo, CO-128) |

   Applied cleanly: 113 file patches. Merged by hand: `App.razor`, both `_Imports.razor`, `app.css` and
   `Register.razor` (context drift). `HeaderMenu.razor` and `NavigationMenu.razor` applied with reduced context and
   were then checked by eye.
3. **Written here, not in GXPT:**
   - **CO-117:** `ResetPasswordDialog` runs the reset itself, with progress. A refusal (say, a password the
     policy rejects) stays in the dialog. `Users.razor` passes the work as `Action`.
   - Three tests for it in `DialogActionComponentTests`. They replace GXPT's `WorkItemDateDialog` test, which has
     no counterpart here.
   - **CO-122:** `Logo.razor` implements `IDisposable` and unsubscribes `DarkModeChanged`. It used to unsubscribe
     `MajorUpdateOccurred`, which no longer exists, so without this the build fails.
   - **A test defect in the ported `ASecondClickWhileBusy_RunsTheActionOnce`:** with the re-entry guard removed, it
     **hung** instead of failing, because it awaited the second click before the work could end. It now fires the
     extra clicks, completes the work, and awaits all of them under a 10 s timeout.
   - **Pass references:** the ported comments cited GXPT's pass numbers (18c, 18d, 19a, 20), which clash with the
     template's own passes 18 to 20. 48 lines now say "Pass 52". One attribution, in `app.css`, keeps "measured in
     GX Project Tracking pass 18d".
   - **The smoke** (`tooling/smoke-generate.ps1`) now asserts what pass 46 asked the generated project to show (§3).
   - **The nuspec** is 1.2.0, with release notes.
4. **Mutations: 6 of 6 red**, each on the test it targets. Every file was restored and checked with `cmp` (§2).
5. **Build:** 0 errors. The 10 distinct code warnings are pass 46's 10. The `SixLabors.ImageSharp` NuGet audit
   warnings are pass 48's, and are unchanged.

---

## 1. What changed

**Added (11):** `Components/Common/GxSubmitButton.razor`, `Components/Dialogs/DialogAction.cs`,
`Components/Theming/ThemeToggle.razor` (+ neutral, de-DE and zh-CN resx), `wwwroot/js/gx-busy-button.js`; tests
`GxSubmitButtonComponentTests`, `DialogActionComponentTests`, `SubmitButtonGuardTests`, `ThemeToggleComponentTests`.

**Deleted (12):** `ThemesMenu.razor`, `ThemesButton.razor`, `PrimaryColorPicker.razor` and their 5 resx files,
`wwwroot/js/theme.js`, `Inputs/Button/MudLoadingButton.razor`, `Presence/OnlineUsersTracker.razor`, and
`OnlineUsersTrackerComponentTests`.

**Changed (90), by kind:**
- **Shell:**
  - `HeaderMenu`: the toggle in both breakpoints; the right-to-left toggle is gone.
  - `NavigationMenu`: no RTL branches, no settings callback.
  - `UserInfoCard`: no "Settings" item; sign-out is a native post with a busy state.
  - `AppLayout`, `MainLayout`: nothing renders until the theme is known.
  - `LanguageSelector`: no `SetRightToLeft`.
  - `LogsLineCharts`: follows `IsDarkMode` only.
- **Theme state:**
  - `LayoutService` keeps `IsDarkMode`, `ToggleDarkMode` and `DarkModeChanged`.
  - `UserPreference` keeps `IsDarkMode`. `UserPreferencesService` reads an old drawer value and ignores its other
    fields; its mode decides, and System follows the device.
  - `Theme.cs`: the 4px radius the drawer used to impose. `app.css`: the 15px base font and the blank page in the
    device's scheme.
- **Busy button on every form submit:**
  - the pages Login, Register, Forgot, ResetPassword, ChangePassword, LoginWith2fa, LoginWithRecoveryCode,
    LinkExternalLogin and ExternalLoginPicker;
  - the tabs ProfileInformationTab, ChangePasswordTab and SecurityTab;
  - the dialogs RoleFormDialog, UserFormDialog, TenantFormDialog, CreatePicklistDialog and UploadFilesFormDialog;
  - SecuritySettings and Breadcrumbs.

  The `_saving`, `_submitting`, `_processing` and `_clearing` flags are gone: `grep` finds none in `src`.
- **Dialog contract:**
  - `ConfirmationDialog` and `DeleteConfirmation` take `Action` and `BusyLabel`, show a failure in a `MudAlert`
    inside the dialog, and disable Cancel while the work runs.
  - `DialogServiceHelper.ShowConfirmationDialogAsync` gains `action` and `busyLabel`.
  - Users Delete and bulk Delete (the last-administrator refusal stays in the dialog), Roles Delete and bulk Delete,
    and SystemLogs Clear Logs now run inside their dialog. So does the password reset (CO-117).
  - `RoleDefinitionComponentTests`' dialog stub runs the action it is given (CO-180).
- **Localisation:** `AppStrings` Saving, Deleting, Adding and Submitting; busy labels in each touched page's resx
  (neutral, en, de-DE, zh-CN).
- **Packaging:** `App.razor` loads `js/gx-busy-button.js` after `blazor.web.js`; `.mud-button-root.gx-busy` in
  `app.css`; the nuspec is 1.2.0.

## 2. Mutations

| # | Mutation | Red test |
|---|---|---|
| M1 | `GxSubmitButton` puts `disabled` on while busy (`Disabled="@(Disabled \|\| _busy)"`) | `GxSubmitButtonComponentTests`: 2 failed (busy class, not the disabled style; stays busy through navigation) |
| M2 | Re-entry guard removed (`if (_busy) return;` → `if (false) return;`) | `ASecondClickWhileBusy_RunsTheActionOnce`. The first run **hung** (defect above); after the fix it fails in 0.4 s. |
| M3 | `ConfirmationDialog` closes on failure | `DialogActionComponentTests`: 2 failed (failure stays in the dialog; validation refusal shown) |
| M4 | A `ButtonType.Submit` `MudButton` added to `Forgot.razor` | `SubmitButtonGuardTests.NoRazorFile_HasASubmitButtonThatIsNotAGxSubmitButton_OrAMudLoadingButton` |
| M5 | `ThemeToggle` icon inverted | `ThemeToggleComponentTests`: 11 of 11 failed |
| M6 | `ResetPasswordDialog` closes on failure | `TheResetPasswordDialog_ResetsInsideTheDialog_AndKeepsARefusalThere` |

The first M5 run passed, but its build had failed on a missing `using System;` introduced by the M2 test fix, so it
ran a stale binary. It was rerun after a 0-error build. Every result above comes from a 0-error build.

## 3. Generated project

`tooling/smoke-generate.ps1` (defaults: postgresql, custom hive, short path under `%TEMP%`, `GX_TEST_PG` as above):
**SMOKE PASSED**, with 106 checks ok and 0 FAIL.
- **Package:** packed `GX.Blazor.Template.1.2.0.nupkg` into the work folder. The repository-root nupkgs were not
  touched.
- **New pass 52 checks, all ok:**
  - `ThemeToggle.razor`, `GxSubmitButton.razor`, `DialogAction.cs` and `wwwroot/js/gx-busy-button.js` are
    generated.
  - `ThemesMenu`, `ThemesButton`, `PrimaryColorPicker`, `MudLoadingButton`, `OnlineUsersTracker` and `theme.js`
    are not.
  - `App.razor` loads `js/gx-busy-button.js`, and `app.css` styles `.mud-button-root.gx-busy`.
  - The four new test files are generated.
- **Build:** the generated solution builds with 0 errors.
- **Without `GX_TEST_PG`,** the suites fail loud, naming the variable.
- **Against the server,** the generated suites pass: Application.UnitTests 531 + 14 skipped,
  Infrastructure.UnitTests 200, Application.IntegrationTests 42, Server.UI.IntegrationTests 283.

**Not done:** booting the generated app in a browser to watch the toggle and a busy button live. The bUnit tests
drive both components, including the first paint (the theme is resolved before anything renders) and the busy state
through navigation.

## 4. Planned for pass 52 and not done

- **CO-107 (D5):** editing a document still saves nothing. `DocumentFormDialog`'s OK validates and closes, and is a
  plain `MudButton` with `OnClick`. It is not a submit button, so the guard does not see it.
- **CO-105 rest:** the OpenSeadragon viewer in `DocumentFormDialog` still loads from unpkg.
- **CO-109:** Roles delete race during reload.
- **CO-26, 103, 104:** Dashboard and Home.
- **CO-108:** bundle a font.
- **CO-119 to 121:** localisation gaps; `ChangePassword` still has no resx.
- **CO-123 to 127, 129 to 133:** logo alt text, `Brand.cs`, `TenantSelector` and `AuthLayout` styles, favicon,
  theme colours.
- **CO-135, 137** and the tests **CO-172** (`ExternalRequestTests`), **176** and **177**.
- **The busy group from CO-118** was skipped, as pass 46 recommended.

## 5. Found but not fixed

1. **`ServerHub.GetOnlineUsers` has no UI caller any more.** `OnlineUsersTracker` was its only one. The hub method
   and `ServerHubTenantIsolationTests` remain (pass 46 CO-139 note).
2. **`.claude/` is packed into the template** (version check §5). The nuspec has no `**\.claude\**` exclusion, and
   `template.json` has no `.claude` exclusion either.
3. **`SixLabors.ImageSharp` 3.1.12 advisories** (pass 48, Found but not fixed 2) are unchanged.
