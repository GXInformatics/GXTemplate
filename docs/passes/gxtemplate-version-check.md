# GXTemplate version check

**Date:** 2026-10-10. Read-only: nothing in any repository, the template cache or the generated project was modified. This file is the only output.
**Template repo:** HEAD `1693dccb` ("pass48", 2026-10-09 09:23 +0100), branch `main`, clean tree.
**Generated tree:** `C:\Yoab\Projects\InventoryMS\InventoryMS`, generated 2026-10-10 11:40 and committed as `8c02163` ("Add project files.", 11:46). Its `src/Server.UI/Components/Theming/` holds `ThemesMenu.razor`, `ThemesButton.razor` and `PrimaryColorPicker.razor`: the theme drawer.

## Verdict

**Both are true. HEAD is missing the toggle, and the package is behind HEAD.**

- **HEAD is missing the toggle.** The app-bar light/dark toggle (`ThemeToggle`) and `GxSubmitButton` have never been ported to the template. HEAD still ships the theme drawer. The port is planned as pass 46's "Pass 52 — UI" (CO-110 to 118 and CO-138/139) and has not been done.
- **The package is behind HEAD.** The installed package is `GX.Blazor.Template` **1.0.0**, packed on 2026-09-26 from **`135ed419` (pass44)**. It is four commits behind: pass45, pass46, pass47 and pass48. Compared with HEAD, it is missing 117 template files: 77 modified, 27 added and 13 deleted. Those changes are listed in §3.
- **A 1.1.0 package matching HEAD exists but was never installed.** `GX.Blazor.Template.1.1.0.nupkg` in the repo root was packed today at 11:38. Its content is byte-identical to HEAD. The InventoryMS generation two minutes later still came from 1.0.0, because 1.0.0 is the version registered with `dotnet new`.
- **Installing 1.1.0 would not bring back the toggle.** HEAD has no toggle either, so a 1.1.0 generation still shows the drawer.

## 1. Where the toggle and GxSubmitButton came from

### Template repo

`git log --oneline -30 main` runs from `c807b55a` "Fin" (2026-08-29) to `1693dccb` "pass48" (2026-10-09). Commit subjects are pass numbers only.

| Check | Result |
|---|---|
| `git grep -E "ThemeToggle\|GxSubmitButton\|gx-busy\|\bDialogAction\b" HEAD -- src tests .template.config` | **0 hits** |
| `git log --all -S ThemeToggle -- src tests` | no commits |
| `git log --all -S GxSubmitButton` | only `fe69fe1f` pass46, which adds the audit report `GXTemplate-passes/pass46.md` and no code |
| HEAD `src/Server.UI/Components/Theming/` | `ThemesMenu.razor`, `ThemesButton.razor`, `PrimaryColorPicker.razor` (the drawer) |
| Branches, stashes | `main` only, no stash. The `template/*` remotes are upstream CleanArchitectureWithBlazorServer. |

The only mention is pass 46's audit (`GXTemplate-passes/pass46.md:35`), which reports "`GxSubmitButton`, `gx-busy-button.js`, `.gx-busy`, `DialogAction`, `ThemeToggle` … 0 hits each in `src` and `tests`". The audit made them required (decision D4) and scheduled them for "Pass 52 — UI" (`pass46.md:433-445`, CO-110 to 118, 138 and 139). Since then, pass 47 delivered the PostgreSQL test infrastructure and pass 48 removed default outbound call-outs. Pass 52 has not run.

### Where they live

**GXProjectTracking** (`C:\Yoab\Projects\GXProjectTracking`) is the origin of both. The template's carry-over list (`docs/template-carry-over.md:110,138,139`) cites these passes.

| Feature | Commit | Date | Files added |
|---|---|---|---|
| `GxSubmitButton` and the busy script | `a514c89` pass18c | 2026-09-30 13:06 | `Components/Common/GxSubmitButton.razor`, `wwwroot/js/gx-busy-button.js`, `GxSubmitButtonComponentTests.cs` |
| Rolled out everywhere | `1c5cbdf` pass18d, `23b1949` pass19a | 2026-09-30 15:25, 16:47 | — |
| App-bar light/dark toggle | `2948e0d` pass20 ("one light/dark toggle in the app bar; the theme panel removed") | 2026-09-30 18:51 | `Components/Theming/ThemeToggle.razor` and 3 resx files, `ThemeToggleComponentTests.cs`; 12 drawer files deleted |

**MNEFleets** (`C:\Yoab\Projects\MNEFleets\FrontEnd`; the folder's other 8 repos have no hits):

| Feature | Commit | Date |
|---|---|---|
| `GxSubmitButton`, `gx-busy-button.js`, tests, ported from GXProjectTracking | `05fa2596` "Busy button, pass A: GX ProjectTracker's GxSubmitButton…" | 2026-09-30 17:33 |
| `ThemeToggle.razor`, MNEFleets' own simpler toggle that keeps `DarkLightTheme`; not the GXProjectTracking design | `3096a018` "UI/UXThemeFixes" | 2026-08-10 |
| Theme first-paint follow-up | `a5843703` "Fixes30Sept" | 2026-09-30 22:57 |

**Were they ported to the template?** No. On 30 September, GXProjectTracking shipped the busy button (13:06) and the toggle (18:51), and MNEFleets ported the busy button (17:33). The template's pass46 audit was committed at 19:18 that same evening and recorded both as absent. Pass47 (23:04) and pass48 (2026-10-09) did not touch them.

The older IMS repo at `C:\Yoab\Projects\IMSB\IMS` (last commit 2026-08-31) also has the drawer (`ThemesMenu.razor`).

## 2. The installed package

| | |
|---|---|
| `dotnet new list` | `GX Blazor Server Solution (Clean A...)`, short name `gxblazor`, `[C#]` |
| `dotnet new uninstall` | `GX.Blazor.Template`, **Version: 1.0.0**, author GX Informatics Limited |
| Source | **Local package**, not a feed. `~/.templateengine/packages.json` records `"LocalPackage":"True"`, `LastChangeTime 2026-09-26T15:19:24Z`, mount point `C:\Users\yoab\.templateengine\packages\GX.Blazor.Template.1.0.0.nupkg` |
| Origin | The template repo's own `GX.Blazor.Template.1.0.0.nupkg`, which is gitignored by `*.nupkg`. Both files have SHA-256 `4cda0874…ede516`. |
| Build date | All 842 zip entries are stamped 2026-09-26 15:19 UTC (16:19 +0100). Same file mtime. |
| Version/commit marker | **None.** The nuspec carries only `<version>1.0.0</version>`, with no `<repository commit=…>`, and no file in the package records a commit. |
| Packed from | **`135ed419` (pass44, committed 2026-09-26 16:02 +0100)**, identified by content. Every file under `content/` was hashed with the repo's own clean filters (`git hash-object --path`) and compared with each candidate commit's tree. |

| Commit | Same | Differ | 1.0.0 verdict |
|---|---|---|---|
| `4af9566a` pass43 | 801 | 33 | — |
| **`135ed419` pass44** | **834** | **0** | **exact match** |
| `72add5d1` pass45 | 820 | 14 | — |
| `fe69fe1f` pass46 | 820 | 14 | — |
| `6ebe4004` pass47 | 759 | 74 | — |
| `1693dccb` pass48 (HEAD) | 743 | 78 | — |

The 2 files in the package that no commit tracks are the stray `.claude/scheduled_tasks.lock` (see §5) and an empty-folder placeholder, `Resources/Components/Presence/_._`.

The same test on `GX.Blazor.Template.1.1.0.nupkg` (packed 2026-10-10 10:38 UTC) gives `1693dccb` HEAD: **848 same, 0 differ**. That package is current, but it is not installed.

## 3. Generated tree versus template HEAD

**Method.** `dotnet new` could not be re-run into the session scratchpad because the paths exceed 260 characters with long paths disabled. Instead, the comparison works on git objects alone. For each file in the generated commit `8c02163`, the script maps its name back to the template path and compares content with both `135ed419` and HEAD. "CleanArchitecture.Blazor" maps to "InventoryMS", and the local-settings file maps to `src/Server.UI/appsettings.Development.json`. Before comparing, it applies the template's generated replacements: names, database name, `DefaultTimeZone` "UTC" and `AllowSelfRegistration` true. It also evaluates the `#if` blocks for PostgreSQL. It ignores CRLF and BOM differences.

**Result.** The generated tree has 829 files. Every file that comes from the template matches **the pass44 rendering exactly**. The one residual is `appsettings.json`, where pass44's connection-string replacement wrote `InventoryMSDbx`, a generated name. The 732 files the template has not changed since pass44 also match HEAD. The only file in the generated tree that the template did not supply is `src/Server.UI/Properties/launchSettings.json`, created by Visual Studio at 11:45.

Below is every file that differs from HEAD: the changes the package is missing. They match `git diff --no-renames 135ed419 1693dccb` restricted to packaged paths. That diff also changes `.template.config/template.json` and `.github/workflows/build.yml`, which are not emitted into a generated project.

**None of the 117 is the toggle, GxSubmitButton or any theming file.** The UI-side changes are all from pass 48's removal of CDN code: `App.razor` (−3 lines), `_Imports.razor` (−1), `app.css` (−9) and the 12 deleted JS/interop files.

### 3a. Modified at HEAD; the generated tree has the pass44 version (77)

| Area | Files |
|---|---|
| Solution / docs | `InventoryMS.slnx` (template `CleanArchitecture.Blazor.slnx`), `README.md` |
| `src/Infrastructure` | `Configurations/AppConfigurationSettings.cs`, `Configurations/DatabaseSettings.cs`, `Configurations/MailSettings.cs`, `DependencyInjection.cs`, `Extensions/SerilogExtensions.cs`, `Infrastructure.csproj`, `Persistence/Logging/LogDatabaseDdl.cs` |
| `src/Server.UI` | `App.razor`, `Server.UI.csproj`, `_Imports.razor`, `appsettings.json`, `wwwroot/css/app.css` |
| `tests/Application.IntegrationTests` | `Application.IntegrationTests.csproj`, `Picklist/Commands/AddEditPicklistCommandTests.cs`, `Picklist/Commands/DeletePicklistTests.cs`, `Testing.cs`, `appsettings.json` |
| `tests/Application.UnitTests` | `Application.UnitTests.csproj`; `Common/Interceptors/` `InterceptorOrderingTests.cs`, `SaveChangesInterceptorRegressionTests.cs`, `TenantStampingTests.cs`, `TransactionalAuditTests.cs`; `Common/Mappings/ApplicationUserProjectionTests.cs`; `Common/PublishStrategies/PublisherAmbientContextTests.cs`; `Configurations/AppConfigurationSettingsValidationTests.cs`, `Configurations/DatabaseSettingsValidationTests.cs`; `Endpoints/FileEndpointsAuthorizationTests.cs`; `Features/AuditTrails/AuditTrailTenantFilterTests.cs`; `Features/Documents/DocumentTenantIsolationTests.cs`, `Features/Documents/Queries/GetFileStreamQueryHandlerTests.cs`; `Features/PicklistSets/` `PicklistSetTenantFilterTests.cs`, `PicklistTenantUniquenessTests.cs`, `SharedPicklistCreationTests.cs`, `SharedPicklistWriteTests.cs`; `Features/SecuritySettings/InstallationPolicyWriteTests.cs`; `Identity/` `AdministratorProtectionTests.cs`, `MustChangePasswordTests.cs`, `PermissionAssignmentGuardTests.cs`, `Roles/RoleDefinitionRightTests.cs`, `UserContextAllowedTenantsTests.cs`, `Users/SwitchableTenantsTests.cs`, `Users/TenantSwitchAuthorizationTests.cs`, `Users/UserRoleChangeSecurityStampTests.cs`, `Users/UserTenantConsistencyTests.cs`; `Logging/LogDatabaseCreationAcceptanceTests.cs`, `Logging/SinkTimestampAcceptanceTests.cs`; `Persistence/ProvisioningTests.cs` |
| `tests/Infrastructure.UnitTests` | `Infrastructure.UnitTests.csproj`; `Logging/` `LogDatabaseDdlTests.cs`, `LogTableDdlTests.cs`, `LogTenantStampingTests.cs`, `PublishedNotificationLogTenantTests.cs`, `SinkColumnDriftTests.cs`, `SinkTimestampTests.cs`; `Persistence/` `GxTableNamingTests.cs`, `LogModelSeparationTests.cs`, `ModelMatchesMigrationsTests.cs`; `Security/IdleTimeoutPolicyTests.cs`; `Services/` `PicklistDataSourceScopeTests.cs`, `TenantVisibilityTests.cs`, `UserVisibilityTests.cs` |
| `tests/Server.UI.IntegrationTests` | `AnonymousMatrixTests.cs`, `ChangePasswordNavigationComponentTests.cs`, `CookieLoginTests.cs`, `ForcedPasswordChangeTests.cs`, `GxWebApplicationFactory.cs`, `IdentityLifecyclePolicyTests.cs`, `LogDatabaseSeparationTests.cs`, `ProcessWideStateTests.cs`, `RoleDefinitionComponentTests.cs`, `Server.UI.IntegrationTests.csproj`, `ServerHubTenantIsolationTests.cs`, `SuperiorBoundComponentTests.cs`, `UserDeactivationPermissionComponentTests.cs`, `UserTenantScopeComponentTests.cs` |

### 3b. Added at HEAD; absent from the generated tree (27)

| Area | Files |
|---|---|
| `src/Server.UI` | `appsettings.Development.json` (from `.template.config/local-settings/`), `appsettings.Production.json`, `appsettings.Staging.json` |
| `tests/Application.IntegrationTests` | `Harness/PostgresTestDatabaseConfigurationTests.cs`, `Harness/ResetOrderTests.cs`, `Harness/SharedDatabaseTests.cs`, `Harness/TestClockTests.cs`, `Harness/TestDatabaseGuardTests.cs`, `PostgreSqlCanaryTests.cs`, `TestClock.cs` |
| `tests/Application.UnitTests` | `Configurations/CommittedAppSettingsTests.cs`, `Configurations/OutboundAddressTests.cs`, `Configurations/ServerUiPublishTests.cs`, `UnitTestDatabase.cs` |
| `tests/Infrastructure.UnitTests` | `InfraTestDatabase.cs`, `Logging/LogTablePostgresTests.cs` |
| `tests/Server.UI.IntegrationTests` | `HarnessTests.cs`, `InMemoryRoleStore.cs`, `InMemoryUserStore.cs`, `SerilogPipelineCaptureTests.cs`, `UiTestDatabase.cs` |
| `tests/TestSupport` (new project) | `ApplicationModel.cs`, `PostgresTestDatabase.cs`, `ResetOrder.cs`, `TestDatabaseGuard.cs`, `TestDatabaseNames.cs`, `TestSupport.csproj` |

### 3c. Deleted at HEAD; still in the generated tree (13)

`src/Server.UI/Components/Inputs/Upload/FileSizeFormatter.razor`, `…/Upload/FileUploadZone.razor`, `…/Upload/Thumbnail.razor`, `src/Server.UI/Pages/Dashboard/Components/Carousel.razor`, `src/Server.UI/Pages/Documents/Documents.razor.js`, `src/Server.UI/Services/JsInterop/Fancybox.cs`, `…/JsInterop/GenerateQrCode.cs`, `…/JsInterop/Swiper.cs`, `src/Server.UI/wwwroot/js/appInterop.js`, `…/js/carousel.js`, `…/js/fancybox.js`, `…/js/generateQrCode.js`, `tests/Infrastructure.UnitTests/Logging/SqliteFileCollection.cs`.

## 4. Plainly

| Question | Answer |
|---|---|
| Is the package behind HEAD? | **Yes.** The installed 1.0.0 = pass44 `135ed419`, four commits and 117 template files behind HEAD `1693dccb`. |
| Is HEAD missing the toggle? | **Yes.** No commit in the template has ever contained `ThemeToggle` or `GxSubmitButton`. They live in GXProjectTracking (pass18c/18d/19a/20, 2026-09-30) and, for the busy button, MNEFleets `05fa2596`. They are scheduled for template pass 52 and not yet ported. |
| Which one explains the drawer? | **HEAD.** Regenerating from the existing 1.1.0 nupkg (= HEAD) would pick up the 117 files above, and the drawer would still be there. |

## 5. Side findings (not acted on)

- **A stray file was packed into 1.0.0 and generated into InventoryMS.** `.claude/scheduled_tasks.lock` (124 bytes, a Claude Code lock file) is untracked in the template repo and excluded only via `.git/info/exclude`. `.\**` in the nuspec packed it, so InventoryMS now carries and has committed `.claude/scheduled_tasks.lock` in `8c02163`. 1.1.0 no longer contains the lock file. The repo's `.claude/` folder is now empty, but the nuspec still packs it, as the `content/.claude/_._` placeholder. Neither the nuspec exclude list nor `template.json`'s `exclude` has a `.claude` pattern.
- **A second template install points at a missing folder.** `packages.json` also holds a folder-mounted template at `C:\Yoab\Projects\GXProjectTrackingTemplate` (installed 2026-09-26). That folder no longer exists.
- **The nuspec version did not move with the passes.** 1.0.0 was packed at pass44, and 1.1.0 first appears in the nuspec at pass48. Neither package records its source commit. A `<repository type="git" commit="…"/>` element in the nuspec, filled at pack time, would make this check a one-liner next time.
