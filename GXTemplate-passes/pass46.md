# Pass 46 — Carry-over reconciliation (read-only)

**Date:** 2026-09-30. **Start:** HEAD `72add5d1` ("pass45"), clean tree.
**Commit:** this report only, as `pass46`. Not pushed.

**Input (read-only):** `C:\Yoab\Projects\GXProjectTracking\docs\template-carry-over.md`, with ProjectTracking's `docs\passes\` and source read for detail.
- "CO-n" below is the bullet on line *n* of that document, which is its stable identifier.
- The document has 188 lines. Every bullet in it is given a status here.

**Decisions that override the carry-over document** (Yoab):
- **D1** PostgreSQL only.
- **D2** Genuine multi-tenancy, with no tenant and no role on self-registration or external login.
- **D3** `<Name>Dbx` / `<Name>Dbx_Logs` as the wizard default.
- **D4** Required: the busy button with its dialog-action contract, ThemeToggle, the pass 45 configuration layout, and `Bootstrap:AdministratorPassword`.
- **D5** Documents stays, with its defects fixed.

**Method, and what the evidence marks mean**
- Every status was established by reading the template at `72add5d1`. Seven read-only sub-agents did most of the reading, one per carry-over section.
- I re-read the load-bearing lines myself; the rows marked **V\*** below are the ones I re-read.
- **V** means the cited line was read in this repository during this pass. **I** means inferred, and the row says why.
- The only commands run were `dotnet build` and read-only `git`/`grep`. No test was run and no database was touched.

**Abbreviations:**
- Source: `SUI` = `src/Server.UI`, `INF` = `src/Infrastructure`, `APP` = `src/Application`, `DOM` = `src/Domain`.
- Tests: `AUT` = `tests/Application.UnitTests`, `AIT` = `tests/Application.IntegrationTests`, `IUT` = `tests/Infrastructure.UnitTests`, `UIT` = `tests/Server.UI.IntegrationTests`.
- Files: `EP` = `SUI/Services/IdentityComponentsEndpointRouteBuilderExtensions.cs`, `INIT` = `INF/Persistence/ApplicationDbContextInitializer.cs`, `IDI` = `INF/DependencyInjection.cs`.

---

## Summary

1. Of the 176 work-item bullets (CO-9 to CO-199): 13 already done, 113 needed, 27 partial, 8 in conflict with a decision (restated below), 15 not applicable. Of the 12 "Not carried over" claims, D1 overturns one (CO-208).
2. D1 is the largest dependency. The whole test suite runs on SQLite (44 test files, plus a SQLite-file default in the HTTP harness), so the PostgreSQL test harness must come **first**.
3. D2 changes onboarding (CO-15 to CO-24) from "the installation's one organisation" to "no tenant, no role, wait". It also exposes two places that are unsafe for a signed-in user with no tenant: `VisibleDocumentSpecification.cs:61-65` fails open (V\*), and `ApplicationDbContext.cs:72` treats that user like "no principal".
4. Everything D4 requires is absent: `GxSubmitButton`, `gx-busy-button.js`, `.gx-busy`, `DialogAction`, `ThemeToggle`, `BootstrapSettings` (0 hits each in `src` and `tests`).
5. Both D5 defects are confirmed: the edit only validates and closes (`DocumentFormDialog.razor:74-81`, V\*), and the menu item is ungated (`MenuService.cs:76-99`, V\*). The live CDN viewer is OpenSeadragon, from unpkg.
6. Baseline build: 0 errors, 18 warnings. The 8 NETSDK1206 warnings come from SQLite and go with D1; the other 10 are code warnings.
7. R4 proposes seven build passes, 47 to 53, starting with the PostgreSQL test harness.
8. R5 has five questions. The most consequential is whether every GX project adopts split database logins (CO-45 to CO-48).

---

## Baseline

| | |
|---|---|
| Last template pass | pass 45, `GXTemplate-passes/pass45.md`, commit `72add5d1`. ProjectTracking was generated from pass 44, `d7e8c31` (CO-3). |
| Build | `dotnet build CleanArchitecture.Blazor.slnx --no-incremental`: **0 errors, 18 warnings** (SDK 10.0.401). This matches pass 45's closing count (`pass45.md:30`). |
| Code warnings (10) | `APP/Common/Extensions/DescriptionAttributeExtensions.cs:12,20` CS8600, `:23,33` CS8603; `APP/Common/Mappings/MapsterConfiguration.cs:26,28` CS8601; `SUI/Components/Inputs/Select/TenantSelect.razor:13` CS8603; `SUI/Pages/SystemManagement/AuditTrails.razor:107` CS8602; `SUI/Pages/Dashboard/Dashboard.razor:202` CS8604; `SUI/Components/Inputs/Select/MudDateTimeField.razor:1` MUD0002. |
| NETSDK1206 (8) | "Affected libraries: SQLite" (win7 RIDs). One each for Infrastructure, the three Migrators and the four test projects. The source is `DOM/Domain.csproj:30`, an unconditional `EntityFrameworkCore.Sqlite`. |
| Providers | `.template.config/template.json:20-43` offers the choice postgresql/mssql/sqlite (default postgresql); `:69-77` defines `UseSqlServer`/`UsePostgreSql`. `Directory.Build.props:4-15` defines both constants for the template's own build. There are three migrator projects: `src/Migrators/Migrators.{MSSQL,PostgreSQL,SqLite}`. SQLite ships in every generated project (`Directory.Build.props:10`) because the test harnesses run on it. CI runs on SQL Server (`.github/workflows/build.yml:22-36`). |
| DB naming today | `template.json:94-117`: `DatabaseName` falls back to the project name, is sanitised, and replaces `GXTemplateDatabase`. `.template.config/local-settings/appsettings.Development.json:13-14` gives `Host=localhost;Port=5434;Database=<Name>` and `<Name>_Logs`. It is not enforced at runtime. D3's `Dbx` suffix is **not** yet applied. |
| Test projects | `AUT`: NUnit 4.6.1, Nullable off. `AIT`: NUnit, Nullable off, `Respawn 7.0.0` (`AIT/*.csproj:32`). `IUT`: xUnit 2.9.2, Nullable on, references all three migrators (`:34-40`). `UIT`: NUnit, bUnit 2.9.0, Nullable on. There is no TestSupport library and no Testcontainers. Every provider reaches the tests through `DOM/Domain.csproj:19-35`. |
| Harnesses | `AIT/Testing.cs`:<ul><li>`GX_TEST_DBPROVIDER` / `GX_TEST_CONNECTIONSTRING` / `GX_TEST_LOGCONNECTIONSTRING` (`:46-48`).</li><li>`Assert.Ignore` when they are unset (`:71-77`) and on SQLite (`:184`).</li><li>Respawn (`:151-157`).</li><li>`EnsureDeleted` on a stale history (`:227`).</li><li>A mock user context with only id and name (`:126-141`).</li><li>Every user is given every permission (`:313`).</li></ul>`UIT/GxWebApplicationFactory.cs`:<ul><li>Falls back to SQLite files (`:68,82,88`).</li><li>Settings go through `ConfigureAppConfiguration` (`:94`), not `UseSetting`.</li></ul>`IUT/Logging/SqliteFileCollection.cs:21` is a serial xUnit collection for SQLite files. |
| SQLite in tests | 44 files use `UseSqlite`/`SqliteConnection`: 26 in AUT, 12 in IUT and 6 in UIT, plus the factory. 34 of them are in-memory `EnsureCreated` classes. CO-148's "roughly 23" is an undercount. |
| Smoke | `tooling/smoke-generate.ps1` (463 lines):<ul><li>Parameters: `-Database`, `-ProjectName`, `-DefaultTimeZone`, `-AllowSelfRegistration`, `-KeepOutput`, `-NoBuild`, `-NoTests` (`:44-69`).</li><li>It asserts `replaces`/`guids` literals, pack, generate, provider stripping, and that no conditionals are left.</li><li>It asserts the four appsettings files, with empty secrets and the required structure, and the Development connection strings.</li><li>It asserts the csproj publish settings, a unique UserSecretsId, and the absence of template-only folders.</li><li>It builds and runs AUT, IUT and UIT (`:403-432`), and checks git-ignore after `git init`.</li></ul>D1 and D3 break `:47`, `:79`, `:182-184`, `:271-272`, `:319-336`. |

---

## R2 — Item-by-item status

Statuses are **DONE**, **NEEDED**, **PARTIAL**, **N/A** (not applicable) and **CONFLICT** (conflicts with a decision; the adapted form is given).
- Effort: S ≈ under ½ day, M ≈ 1–2 days, L ≈ more than 2 days.
- "Pass" is the build pass proposed in R4.
- The evidence column is V unless a row says I.

### Security (CO-9 to CO-49)

| CO | Item | Status | Evidence | Eff. | Deps | Pass | Note or recommendation |
|---|---|---|---|---|---|---|---|
| 9 | `/files` deny-by-default | **DONE** | `SUI/Endpoints/FileEndpoints.cs:38-43` `ServedUploadTypes`, `:132-136` refuse unlisted; `UIT/FileEndpointMatrixTests.cs:186,197` (unlisted prefix, leading space) | — | — | — | The per-prefix rule is stronger than ProjectTracking's. The Document rule leans on `VisibleDocumentSpecification`; see R3-8. |
| 10 | Register external logins only when configured | **NEEDED** | `IDI:574-585` registers `.AddMicrosoftAccount`/`.AddGoogle` unconditionally (V\*); `:662-666` falls back to `"disabled"` | S | — | 49 | — |
| 11 | `/performexternallogin` validates the provider | **NEEDED** | `EP:348-371`: `Challenge(properties, [provider??string.Empty])` with no scheme check | S | 10 | 49 | — |
| 12 | Picker renders nothing when there are no schemes | **NEEDED** | `SUI/Pages/Identity/Login/ExternalLoginPicker.razor:6-9` "There are no external authentication services configured." | S | 10 | 49 | Drop the string from its 4 resx files. |
| 13 | Don't map callback/link routes when off | **PARTIAL** | `SUI/Middlewares/SelfRegistrationMiddleware.cs:42-43` already 404s the link routes when registration is off. `EP:348`, `:374-440` are always mapped. | S | 10 | 49 | Recommend carrying it: it is cheap and removes a dead surface. |
| 14 | `AllowSelfRegistration` defaults to false | **DONE** | `INF/Configurations/AppConfigurationSettings.cs:52`; `SUI/appsettings.json:81`; `AUT/Configurations/AppConfigurationSettingsValidationTests.cs:47`, `:96-108` `SelfRegistrationCanBeTurnedOn_ByConfigurationAlone`; `template.json:137-143` | — | — | — | Matches D2. |
| 15 | Self-registered accounts active, no role, at both doors | **NEEDED** | `SUI/Pages/Identity/Register/Register.razor:155` `IsActive = false`, `:176` `AddToRoleAsync(user, Roles.Basic)` (V\*); `EP:446` RoleManager param, `:484` `IsActive = false`, `:509` AddToRole (V\*) | M | 16–21 | 49 | Extended by D2: also `TenantId = null` and no `TenantUsers` row (R3-1). It must ship with CO-20/21; otherwise an active user with no role lands on the dashboard. Extract `SUI/Services/Access/SelfRegistration.cs` so it can be tested. |
| 16 | Remove the tenant field from Register | **NEEDED** | `Register.razor:30-35` `<TenantSelect … Required="true">`, `:286-305` `[Required]`; `INF/Services/MultiTenant/TenantDataSourceService.cs:78-79` returns empty for anonymous, so registration can never succeed | S | 15 | 49 | Consistent with D2. |
| 17 | Server assigns the tenant; refuse when ambiguous | **CONFLICT** (D2) | `Register.razor:139` `user.TenantId = _formModel.TenantId` (client value, V\*); `:185-192` writes `TenantUsers` | S | 15, 16 | 49 | Adapted: assign **no** tenant and write no membership, and ignore any client value. Do not port `SelfRegistration.OrganisationAsync`. See R3-2. |
| 18 | `/performlinkexternallogin` trusts a query `tenantId` | **CONFLICT** (D2) | `EP:449` `[FromQuery] string? tenantId`, `:460` required, `:467`, `:487` membership (V\*); `SUI/Pages/Identity/Login/LinkExternalLogin.razor:28-34` TenantSelect, `:108` sends it | S–M | 15, 19 | 49 | The defect is real when registration is on: an anonymous caller chooses its tenant. Adapted: remove the field and the parameter, with `TenantId = null` and no membership (R3-3). `AUT/Identity/Provisioning/NewUserTimeZoneDefaultTests.cs:49-53` reflects on that page's `InputModel`. |
| 19 | Account and membership in one transaction | **PARTIAL** | Form door: `Register.razor:168`, `:176`, `:185-192` are three writes, and the context at `:185` is never disposed. External door: user and membership already go in one save (`EP:487`), but `:501`, `:509`, `:517` are separate. | M | 15, 18 | 49 | Under D2 the form door becomes one `CreateAsync`, so it is atomic for free. The external door still needs `CreateAsync` + `AddLoginAsync` in one transaction, with an injected-failure test. |
| 20 | Hold users on `/account/awaiting-access` | **NEEDED** | `AwaitingAccess` has 0 hits in src/tests. Patterns to copy: `ForcePasswordChangeMiddleware` at `SUI/DependencyInjection.cs:154`, `<ForcePasswordChangeGuard />` at `SUI/Layouts/AppLayout.razor:43`, refresh-signin at `EP:587-607`. | L | 21, 23, 24 | 49 | Adapted rule: waiting = **no role OR no tenant** (R3-4). |
| 21 | Show a user with no role no menu section | **NEEDED** | `SUI/Components/AppShell/NavigationMenu.razor:18`; the "Application" section has no `Roles` (`SUI/Services/Navigation/MenuService.cs:76-99`, V\*) | S | 20 | 49 | "No role" becomes "waiting" (R3-4). |
| 22 | Optional `AllowedRegistrationDomains` | **NEEDED** (optional) | absent; `AppConfigurationSettings.cs:11-78` | S–M | 15, 18 | 49 | Recommend carrying it: generic, and inert when the list is empty. |
| 23 | First grant doesn't bump the stamp; clear caches | **NEEDED** | `SUI/Pages/Identity/Users/Components/UserFormDialog.razor:292-301` bumps on any role change; the profile cache `INF/Services/Identity/UserProfileState.cs:136-154` is not cleared; `AUT/Identity/Users/UserRoleChangeSecurityStampTests.cs:295` pins the current behaviour | M | 20, 24 | 49 | Adapted: "first grant" is the edit that ends waiting, whether it sets the role, the tenant or both (R3-5). |
| 24 | Application port `IUserContextCache.Clear` | **PARTIAL** | Exists only in Infrastructure: `INF/Services/Identity/IUserContextLoader.cs:13`, `UserContextLoader.cs:132-139`. Gap: `UserFormDialog.razor:251-263` rewrites `TenantUsers`, but `:271` clears only on a primary-tenant change. `SUI/Pages/Identity/Roles/Roles.razor:429,470` and `Users.razor:978` clear nothing. | S–M | — | 49 | The `AllowedTenantIds` cache lasts up to 1 h (`UserContextLoader.cs:13`). |
| 25 | `RetiredGrants` reconciliation | **NEEDED** | `INIT:138-141` "Grant-only, never revoke", `:192-209`; `AUT/Persistence/ProvisioningTests.cs:315` must still pass | M | — | 49 | The first entry is `Dashboards.View` (CO-26). |
| 26 | Retire `Dashboards.View` | **NEEDED** | `SUI/Pages/Dashboard/Dashboard.razor:1`; `APP/Common/Security/Permissions/Dashboards.cs:13`; `AdministratorPermissionRegistry.cs:43,209` | S | 25, 104 | 52 | Only with CO-104, which is recommended. |
| 27 | Reconcile role descriptions on every start | **NEEDED** | `INIT:170-176` writes `Description` only inside `if (role is null)` | S | — | 49 | — |
| 28 | Move Documents into MANAGEMENT (admin-gated) | **CONFLICT** (D5) | `MenuService.cs:76-99`; the page policy is `SUI/Pages/Documents/Documents.razor:2`; Basic **holds** `Documents.View`/`Download` (`INIT:110-114`, pinned by `ProvisioningTests.cs:160`) | S | 29, 21 | 49 | Adapted: keep Documents where Basic can reach it, and gate the item on `Permissions.Documents.View` (needs CO-29). Guard it with a menu component test. |
| 29 | Permission field on the menu models | **NEEDED** | `SUI/Models/NavigationMenu/MenuSectionItemModel.cs:9`, `MenuSectionSubItemModel.cs:7` have `Roles` only; `MenuService.cs:13-38` documents role-only gating as deliberate; `UIT/SystemMenuGateComponentTests.cs:175-191,194-205` pin it | M | — | 49 | ProjectTracking never built this, so there is no reference implementation. The two pinned tests must be rewritten deliberately. |
| 30 | Remove `SeedSampleTenantAsync` ("Europe") | **CONFLICT** (D2) | `INIT:340-362`; `:352-358` adds every administrator; runs in Development only (`INF/Extensions/HostExtensions.cs:33-35`); `ProvisioningTests.cs:381,392,405` | S | — | 49 | Under D2 a second tenant is a fair demo; the defect is the silent membership. Recommend removing it: the administrator already holds `Users.SwitchToAnyTenant` (`AdministratorPermissionRegistry.cs:176`, V\*). Add a README note on creating a second tenant. R5-4. |
| 31 | Tenant filter coverage by marker | **NEEDED** | `INF/Persistence/ApplicationDbContext.cs:113-150` is an explicit list (AuditTrail `:144-146`, PicklistSet `:148-150`); `DOM/Entities/Document.cs:11` `IMayHaveTenant` is unfiltered; stamped at `AuditableEntityInterceptor.cs:327-328` | M | — | 49 | Adapted: a model-walking coverage test with a reasoned exemption list (Document is scoped by specification). Also fail closed for principal-present-tenant-null (R3-7). |
| 32 | Document TenantSwitchService / null ambient context [borderline] | **DONE** | `README.md:468-469`, `:509-512`; `INF/Services/TenantSwitchService.cs:106-112`; `APP/Common/Interfaces/Identity/IUserContextAccessor.cs:17-45` | — | — | — | — |
| 33 | Generic record-level access [borderline] | **N/A** | No owner marker or access behaviour exists; the template has no consuming entity | (L) | 31, 34 | — | Recommend **not** carrying it. It is shaped for ProjectTracking; CO-31's coverage test gives the reusable half. At most, a README pattern note (pass 53). |
| 34 | Command/query reflection guard | **NEEDED** | `APP/Common/Security/RequestAuthorizationRegistry.cs:24-30`; `AUT/Security/RequestAuthorizationRegistryTests.cs:33` count 24, no classification | S | — | 49 | Carry only the neither/both half; the access-marker half belongs to CO-33. |
| 35 | Pin data protection (`SetApplicationName`) | **PARTIAL** | `IDI:646` `services.AddDataProtection().PersistKeysToDbContext<ApplicationDbContext>();` (V\*) | S | — | 50 | Use `"CleanArchitecture.Blazor"`: `sourceName` rewrites it per project. It must land before any generated project ships its first release, because adding it later signs everyone out once. |
| 36 | README: keys stored unencrypted [deliberate] | **NEEDED** | `README.md:928` names the table only | S | 35 | 50 | — |
| 37 | Empty connection strings in appsettings.json | **DONE** | `SUI/appsettings.json:12,21`; `AUT/Configurations/CommittedAppSettingsTests.cs:31-33,77` | — | — | — | pass 45 G1. |
| 38 | Development settings never published | **DONE** | `SUI/Server.UI.csproj:33`; `AUT/Configurations/ServerUiPublishTests.cs:46-50` | — | — | — | pass 45 G4. |
| 39 | `.gitignore` the server `web.config` | **NEEDED** | no `web.config` rule in `.gitignore`; `git log --all -- **/web.config` is empty, so nothing needs rotating | S | — | 50 | Add a `git check-ignore` assertion next to `CommittedAppSettingsTests.cs:101`. |
| 40 | Fresh `UserSecretsId` per project | **DONE** | `SUI/Server.UI.csproj:12`; `template.json:16` `guids`; `tooling/smoke-generate.ps1:372-387` | — | — | — | pass 45 G8. |
| 41 | Committed-credentials guard over every tracked file | **PARTIAL** | `CommittedAppSettingsTests.cs:24-33` covers three appsettings files only. A full scan would fail today on `AUT/Logging/LogDatabaseCreationAcceptanceTests.cs:50`, `AUT/Logging/SinkTimestampAcceptanceTests.cs:60` (`Password=postgres`) and `.github/workflows/build.yml:26,37` | M | 42, 43 | 50 | Needs a placeholder allow-list for the harmless strings in unit tests. |
| 42 | No passwords in test connection strings | **NEEDED** | `LogDatabaseCreationAcceptanceTests.cs:49-53`, `SinkTimestampAcceptanceTests.cs:59-63` (hard-coded `localhost:5433`, `postgres/postgres`); CI `sa` password at `build.yml:26-37` | S–M | 145 | 47 | — |
| 43 | Optional `Bootstrap:AdministratorPassword` | **NEEDED** (D4) | no `BootstrapSettings`; `INIT:238` always calls `GenerateCompliantPassword()` | M | — | 50 | The key must be present and empty in all four appsettings files, including `.template.config/local-settings/…`, and in `RequiredStructure`. |
| 44 | The bootstrap password never reaches a log | **PARTIAL** | `BootstrapSecret` scope at `INIT:274-289`; persistent sinks exclude it (`INF/Extensions/SerilogExtensions.cs:53-55,96-113`); only a predicate test exists (`IUT/Logging/LogDatabaseDiagnosticRoutingTests.cs:59`) | M | 43, 161 | 50 | Add `BootstrapAdministratorPasswordTests` on the real pipeline. |
| 45 | Everyday login plus a separate migration login | **NEEDED** | `INF/Configurations/DatabaseSettings.cs:19-47` has one connection string; `INIT:49-61` migrates through it | L | D1 | 51 | R5-1. |
| 46 | Grant the everyday login after each migration | **NEEDED** | no `DatabaseLogins.cs` | M | 45 | 51 | ProjectTracking reference: `src/Infrastructure/Persistence/DatabaseLogins.cs`. |
| 47 | `DatabaseLoginCheck` refusals | **NEEDED** | absent | M | 45, 46, 63 | 51 | — |
| 48 | Effective-privilege check [borderline] | **NEEDED** | no `has_table_privilege` anywhere | M | 46, 47 | 51 | Recommend carrying it with CO-47. With one dialect it is cheap, and it closes the PUBLIC and role-inheritance gap. |
| 49 | Restrict the `web.config` ACL | **NEEDED** (docs) | no ACL guidance; `README.md:85,135-149` | S | 45, 188 | 51 | — |

### Configuration (CO-53 to CO-75)

| CO | Item | Status | Evidence | Eff. | Deps | Pass | Note or recommendation |
|---|---|---|---|---|---|---|---|
| 53 | GX configuration layout | **DONE** | `SUI/appsettings.json:2-9` header; `appsettings.Staging.json`/`Production.json:1-39`; `.gitignore:373`; `README.md:78-85,134-144` | — | — | — | pass 45. D1 leftovers: the `.template.config/local-settings/appsettings.Development.json:9-11,15-18` mssql/SQLite arms go in pass 48. |
| 54 | Every server-supplied key listed empty | **PARTIAL** | Done: `appsettings.json:75,99,100,112`. Missing: `DatabaseSettings:MigrationConnectionString`, `AppConfigurationSettings:DataRoot`, `Bootstrap:AdministratorPassword`. The class default `INF/Configurations/MailSettings.cs:76` is still `"noreply@example.com"`. | S | 43, 45, 66 | 50/51 | Each key arrives with the feature that owns it. |
| 55 | Missing-connection-string message names both sources | **DONE** | `DatabaseSettings.cs:84-88` | — | — | — | pass 45 G6. |
| 56 | Log-database comment and README agree | **PARTIAL** | They now agree on local behaviour (`appsettings.json:13-20`, `README.md:111-129`). But `README.md:126` has the DBA create a separate `ims_log` role, where ProjectTracking has the DBA create the log database owned by the everyday login (no CREATEDB). | S | 45 | 51 | — |
| 57 | Replace "GX Application" with the project name | **NEEDED** | `appsettings.json:84,101,144`; `AppConfigurationSettings.cs:46`; `UIT/GxWebApplicationFactory.cs:124`; `AIT/appsettings.json:15,53`; `AUT/Logging/LogDatabaseCreationAcceptanceTests.cs:334`; `AUT/Configurations/AppConfigurationSettingsValidationTests.cs:43`. No `replaces` in `template.json`. | S | — | 50 | A global replace also moves the test assertions, as it should. Extend the smoke's literal-absence check. |
| 58 | Company, time-zone and tenant defaults from the wizard | **PARTIAL** | The time zone is a wizard parameter (`template.json:53-59,119-132`). Company and Copyright have GX fallbacks (`AppConfigurationSettings.cs:25,30`, `appsettings.json:85-86`). The tenant is still `"Default"` (`INIT:121`), and never-rename (`INIT:214`) is undocumented. | S | — | 50 | Recommend keeping `"Default"` (D2: one tenant among many) and documenting never-rename. Adding a Company wizard parameter is not needed. |
| 59 | `LocalTimeOffset` ignores daylight saving | **PARTIAL** | Documentation done (`APP/Common/Interfaces/IApplicationSettings.cs:12-21`). `BaseUtcOffset` is still used at `APP/Common/Security/UserProfile.cs:32-34`, `APP/Features/Identity/DTOs/ApplicationUserDto.cs:64-66`, `SUI/Components/Time/UtcToLocal.razor:31` | S–M | — | 50 | Use `GetUtcOffset(instant)`. |
| 60 | IIS settings in `Server.UI.csproj` | **DONE** | `SUI/Server.UI.csproj:16-17,23-26` | — | — | — | pass 45 G4. |
| 61 | Keep the empty `Authentication` keys | **DONE** | `appsettings.json:55-64`; `CommittedAppSettingsTests.cs:43-46` | — | — | — | Its premise ("harmless once conditional") holds only after CO-10. |
| 62 | Mailgun keys fatal in `MailSettings.Validate` | **NEEDED** | `MailSettings.cs:137-148` "deliberately not an error", `:149-181`; dead branch `INF/Services/Mail/MailStartupCheck.cs:31-33,53-56`; `README.md:338` | M | 63 | 50 | Production hosts in `GxWebApplicationFactory` must then supply mail keys or use the sink (I). |
| 63 | `GuardedEnvironments` (Production or Staging) | **NEEDED** | absent; guards key on `IsDevelopment()` (`MailStartupCheck.cs:63`, `IDI:436`, `HostExtensions.cs:33`, `SUI/DependencyInjection.cs:132`) | S | — | 50 | The foundation for CO-47, 64 and 66. |
| 64 | Require `ApplicationUrl` in guarded environments | **NEEDED** | `AppConfigurationSettings.cs:57-77` validates only the time zone | S | 63, 65 | 50 | — |
| 65 | Environment-dependent settings in post-configuration | **PARTIAL** | The pattern exists for mail (`IDI:433-437`). There is no `DatabaseSettings.ApplyEnvironment` or `RequiresSeparateMigrationLogin`. | M | 45, 63 | 51 | — |
| 66 | `AppConfigurationSettings:DataRoot` | **NEEDED** | log `SerilogExtensions.cs:55` (`./log`, working directory); storage `INF/Services/Storage/LocalDiskFileStorage.cs:23-25` (`GetCurrentDirectory`); mail sink `INF/Services/Mail/SinkMailService.cs:43-45` (`AppContext.BaseDirectory`) | M | 63, 54 | 50 | Three different roots today. |
| 67 | README: `RootPath` resolves against the working directory | **NEEDED** | `README.md:294` says "content root", but the code at `LocalDiskFileStorage.cs:25` uses the working directory | S | 66 | 50 | — |
| 68 | Startup refusals reach the file log | **NEEDED** | `SUI/Program.cs:28-29` is a plain `InitializeDatabaseAsync(); RunAsync();` (V\*) | S–M | — | 50 | `Program` is used by `WebApplicationFactory`, so keep that path working. |
| 69 | `MigrationConnectionString` requirement is host-specific | **N/A** | No console tools exist; only `SUI` is a host | — | 45 | 51 | Design CO-45 host-specific from the start, at no extra cost. |
| 70 | App-local ICU | **NEEDED** | no `ICU4C`/`AppLocalIcu`; `Server.UI.csproj:48-65` | S–M | — | 50 | `Africa/Lagos` validation (`AppConfigurationSettings.cs:70`) depends on ICU on the server. |
| 71 | Remove `<InvariantGlobalization>false` | **NEEDED** | `SUI/Server.UI.csproj:9` | S | 70 | 50 | — |
| 72 | Globalization in the "Process-wide state" line | **PARTIAL** | The line exists at `SUI/Program.cs:86-114`, but has no ICU/NLS source or version and no time-zone resolution | S–M | 70 | 50 | Its provider branch (`:96-99`) is simplified in pass 48. |
| 73 | USENLS documented | **NEEDED** | no `docs/deployment.md`; `template.json:213` excludes `docs/**` from generation | S | 70, 72 | 51 | Adapted: the doc must live where generation keeps it (see CO-188). |
| 74 | Placeholders and arguments one-to-one | **PARTIAL** | Holds for the bootstrap banner (`INIT:281-289`). **Violated** at `Register.razor:193-197`: 2 placeholders, 4 arguments, so `UserId` logs the user name and `TenantId` logs the user id (V\*). | S | 15 | 49 | That code is rewritten by CO-15. Keep the rule in the README conventions. |
| 75 | Relative URLs in development mail [borderline] | **N/A** | the templates have no images (`INF/Resources/EmailTemplates/*.sbn`); `base_url` is unused (`MailTemplateRenderer.cs:97`) | — | 129 | — | Recommend skipping it. It only arises with CO-129's `logo_url`. |

### Database (CO-79 to CO-99)

| CO | Item | Status | Evidence | Eff. | Deps | Pass | Note or recommendation |
|---|---|---|---|---|---|---|---|
| 79 | Strip unselected providers at generation | **CONFLICT** (D1) | `template.json:20-43,69-92,149-190,207-209`; `DOM/Domain.csproj:19-35`; `INF/Infrastructure.csproj:21-29`; `SUI/Server.UI.csproj:69-75`; `IUT/*.csproj:34-40`; `APP/Common/Constants/DbProviderKeys.cs:6-7`; `IDI:34-41,289-297,311-325`; `SerilogExtensions.cs:1-26,164-176,181-307,400-435`; `INF/Persistence/Logging/LogTableDdl.cs` (12 `#if`), `LogDatabaseDdl.cs` (24 `#if`), `LogDatabaseStartupCheck.cs` (6), `LogDbContext.cs:82-91`; `SUI/Program.cs:94-99`; `Directory.Build.props:4-15`; `CleanArchitecture.Blazor.slnx:13-19`; `.gitignore:12-14,270-273`; `.gitattributes:52`; `GX.Blazor.Template.nuspec:74`; `build.yml:22-36`; about 23 README passages; 14 test files with provider `#if`s | L | 144–149 | 48 | Adapted: **delete from the template**, not strip at generation. Remove the `Database` symbol, both computed symbols, `DbProviderSetting`, `Directory.Build.props`, `Migrators.MSSQL`/`Migrators.SqLite`, and every arm. Keep `DBProvider: "postgresql"` or remove the key (decide in pass 48; the `DbProviderKeys` validation must follow). |
| 80 | Remove "template.json"/"this template" comments and empty ItemGroups | **NEEDED** | `LogDatabaseDdl.cs:57,230`; `IDI:266,282`; "this template" appears 33 times in 21 src files; **user-visible** `[Description("Not implemented in this template …")]` at `APP/Common/Constants/Roles.cs:66-78` | S–M | 79 | 48 | Reword only what misleads a generated project, plus the `Roles.cs` strings. |
| 81 | Delete provider dead code | **NEEDED** | `LogDatabaseDdl.cs:279` `QuoteLiteral` (only caller at `:218`, SQL Server), `:71` `RequiresExplicitCreation` + `LogDatabaseStartupCheck.cs:157`, `:375-392` SQLite directory helper; `DOM/Domain.csproj:35` InMemory (no `UseInMemoryDatabase` anywhere); `IDI:34` stale comment | S | 79 | 48 | — |
| 82 | Concurrency sentence in `DbExceptionHandler` | **NEEDED** | `APP/Common/ExceptionHandlers/DbExceptionHandler.cs:37-46` has no `DbUpdateConcurrencyException` arm, so it falls to `:76` | S | — | 48 | Pairs with CO-85. |
| 83 | Distinguish reference-constraint delete from insert/update | **NEEDED** | `DbExceptionHandler.cs:66-69` | S | — | 48 | — |
| 84 | Unique-constraint message names the field | **PARTIAL** | `DbExceptionHandler.cs:56-58` uses CLR property names when present; `tableName` at `:53` and `GetConstraintName` at `:121` are unused | S | — | 48 | — |
| 85 | `xmin` optimistic concurrency (`IRowVersioned`) | **NEEDED** | no `IRowVersioned`/`IsRowVersion`/`xmin` anywhere | L | 79, 89 | 48 | Recommend carrying it. PostgreSQL-only makes it unconditional, and D4's dialogs can show the conflict sentence. Entities: Document, PicklistSet, Tenant, SecurityPolicy. |
| 86 | `IAppendOnly` + `AppendOnlyInterceptor` | **NEEDED** | absent; `AUT/Common/Interceptors/InterceptorOrderingTests.cs` to extend | S–M | — | 48 | `AuditTrail` is the natural first user. |
| 87 | Database triggers and grants for append-only tables [borderline] | **NEEDED** | none | M–L | 45, 46, 86 | — | Recommend **deferring** it, with a README pattern note in pass 53. The REVOKE half needs CO-45/46 first; revisit after pass 51. |
| 88 | Domain-rule exception becomes a result [borderline] | **NEEDED** | `APP/Pipeline/ResultExceptionBehavior.cs:53-56` logs at Error with "An unexpected error occurred"; no domain-exception type exists | S | — | 48 | Recommend carrying it with a small `DomainRuleException`. Every GX business app has domain rules, and D4's dialogs will show the sentence. |
| 89 | `DatabaseFacade` on `IApplicationDbContext` [borderline] | **NEEDED** | `APP/Common/Interfaces/IApplicationDbContext.cs:12-20` | S | — | 48 | Recommend carrying it with CO-85's `Entry<T>`. It is cheap. |
| 90 | `DateOnly`/`TimeOnly` in audit `SerializeValue` | **NEEDED** | `INF/Persistence/Interceptors/AuditableEntityInterceptor.cs:478` lacks them and falls to `:485` | S | — | 48 | — |
| 91 | Audit-trail limits documented [borderline] | **NEEDED** | `README.md:436` does not mention them; the bypass is noted only at `IDI:185` | S | — | 53 | Recommend carrying it (README). |
| 92 | `varchar(450)` and dormant naming conventions [borderline] | **PARTIAL** | dormancy documented at `README.md:921-925`; the 450 default (`ApplicationDbContext.cs:163`) is not | S | — | 53 | Recommend carrying it (one paragraph). |
| 93 | SoftDelete unused [borderline] | **PARTIAL** | only in code comments (`ApplicationDbContext.cs:107-109`, `QueryFilters.cs:46`, `ModelBuilderExtensions.cs:23`) | S | — | 53 | Recommend a README note, without a sample entity. |
| 94 | `IgnoreQueryFilters` over `Concat`, soft-deleted keys [borderline] | **NEEDED** | undocumented; named filters are used (`APP/Features/AuditTrails/AuditTrailTenantScope.cs:76`) | S | 93 | 53 | Recommend a README note. |
| 95 | Gap-free `RefIssuer` counter [borderline] | **N/A** | absent; no template consumer | — | — | — | Recommend **not** carrying it. At most a pattern note. |
| 96 | Immutable keys and named CHECK constraints [borderline] | **NEEDED** | no `SetAfterSaveBehavior`/`HasCheckConstraint` | S–M | — | 53 | Recommend documenting the pattern only. |
| 97 | Migration practice [borderline] | **NEEDED** | `README.md:225-238` covers only adding a migration | S | — | 53 | Recommend carrying it (README). |
| 98 | Raw-SQL backfill changes `xmin` | **NEEDED** | no deployment notes exist | S | 85, 188 | 51 | — |
| 99 | First-boot ERR from the `__EFMigrationsHistory` probe | **NEEDED** | not filtered (`SerilogExtensions.cs:38`, `appsettings.json:70`); not documented. That it is emitted is **I** (seen in ProjectTracking; not reproduced, since no database is allowed in this pass). | S | — | 51 | A README/deployment note; a filter only if it can be precise. |

### UI (CO-103 to CO-139)

| CO | Item | Status | Evidence | Eff. | Deps | Pass | Note or recommendation |
|---|---|---|---|---|---|---|---|
| 103 | Remove `IPDFService`/`PDFService` | **NEEDED** | `APP/Common/Interfaces/IPDFService.cs`, `INF/Services/PDFService.cs:11` (Arial), registered at `IDI:352`, no consumer | S | — | 52 | QuestPDF has no other consumer (`INF/Infrastructure.csproj:19`, `SUI/Program.cs:5-6,67,108-113`). R5-5. |
| 104 | Remove the fake dashboard and the Home item | **NEEDED** | `SUI/Pages/Dashboard/Dashboard.razor:1,13,30`; 4 resx; `MenuService.cs:81`; `Pages/Dashboard/Components/Carousel.razor:2` | M | 105, 26 | 52 | "/" must stay routable for any authenticated user. It is the target of `EP:70` (`Home = "/"`), `TenantSelector.razor:193` (forceLoad after a switch), `AuthLayout.razor:92` and 5 error pages, and of `UIT/CookieLoginTests.cs:49,63,84` and `ForcedPasswordChangeTests.cs:65,183,212,218`. Adapted: a small neutral Home page (app name, current tenant, links the user may follow), gated only by the fallback policy. |
| 105 | Delete dead front-end code (CDN) | **NEEDED** | Swiper: `Carousel.razor:108` → `Swiper.cs:16` → `carousel.js:12`. `appInterop.js:3` (openseadragon from cdnjs). `Breadcrumbs.razor:51` → `HistoryGo.cs`. `FileUploadZone.razor:40-41,233` (Thumbnail, FileSizeFormatter, Fancybox). `fancybox.js:1,3` (jsdelivr, unpkg) and `app.css:8,155-162`. `JSInteropConstants.cs:7,9,12`. **Also dead, and not in the carry-over:** `SUI/Pages/Documents/Documents.razor.js:5`, which loads fabric from unpkg. | S–M | 107, 114 | 52 | **D5 caution:** `OpenSeadragon.cs`/`openseadragon.js` are **live** in Documents (`DocumentFormDialog.razor:46,71`). Delete them only together with CO-107. |
| 106 | Remove the QR-code CDN script | **NEEDED** | `SUI/App.razor:56` cdnjs `qrcode.min.js`; `GenerateQrCode.cs` and `generateQrCode.js` have no callers | S | — | 52 | — |
| 107 | Document edit saves nothing | **NEEDED** (D5) | `SUI/Pages/Documents/Components/DocumentFormDialog.razor:74-81` validates and closes; `Documents.razor:273-277` only reloads (V\*) | S–M | 110, 115 | 52 | Its primary action becomes a `GxSubmitButton` that sends `AddEditDocumentCommand` inside the dialog. Replace the OpenSeadragon viewer (`:44-49,69-72`) with an `<img>` from the authenticated `/files` endpoint, or remove it. |
| 108 | Bundle one web font | **NEEDED** | `SUI/Themes/Theme.cs:132-133` "Inter"; `App.razor:17` Google Fonts Roboto; no `wwwroot/fonts` | M | — | 52 | — |
| 109 | Roles delete race during reload | **NEEDED** | `SUI/Pages/Identity/Roles/Roles.razor:73,134` not disabled while loading; `OnDelete:406`, `OnDeleteChecked:442` have no `_isLoading` return (`OnSearch:285` has one) | S | 116 | 52 | — |
| 110 | `GxSubmitButton` | **NEEDED** (D4) | 0 hits; no `SUI/Components/Common/` | M | 111, 112 | 52 | — |
| 111 | `gx-busy-button.js` | **NEEDED** (D4) | not in `SUI/wwwroot/js`; `App.razor:53-56` | S | 110 | 52 | — |
| 112 | `.gx-busy` style | **NEEDED** (D4) | `SUI/wwwroot/css/app.css` has none; `MudLoadingButton.razor:6` uses `Disabled` | S | — | 52 | — |
| 113 | Use `GxSubmitButton` everywhere; drop the flags | **NEEDED** (D4) | 13 submit buttons in 12 files: `Login:58`, `Register:98`, `Forgot:35`, `ResetPassword:47`, `LoginWith2fa:43`, `LoginWithRecoveryCode:33`, `LinkExternalLogin:65`, `ExternalLoginPicker:19`, `ChangePassword:66,70`, `ProfileInformationTab:85`, `ChangePasswordTab:44`, `UserInfoCard:87`. Flags: `_saving` at `TenantFormDialog:49`, `CreatePicklistDialog:79`, `SecuritySettings:111`, `SecurityTab:75`; `_submitting` at `ChangePassword:85`; `_processing` at `UploadFilesFormDialog:63`. | L | 110–112, 115 | 52 | Adds the D5 dialogs (`DocumentFormDialog:56`, `UploadFilesFormDialog:53`) and the waiting page (CO-20). |
| 114 | Delete `MudLoadingButton` | **NEEDED** | `SUI/Components/Inputs/Button/MudLoadingButton.razor`; live in `TenantFormDialog:39`, `CreatePicklistDialog:60`, `UploadFilesFormDialog:43,53`; dead in `Breadcrumbs:11`, `FileUploadZone:26`; `SUI/_Imports.razor:46` | S | 105, 113 | 52 | Caution: `UploadFilesFormDialog:43` sits inside `MudFileUpload` `CustomContent`, and `GXTemplate-passes/pass19-report.md:878` warns against moving it to `ActivatorContent`. |
| 115 | `DialogAction` contract | **NEEDED** (D4) | no `DialogAction.cs`; `SUI/Services/DialogServiceHelper.cs:129-146` has no `action`/`busyLabel`; `ConfirmationDialog.razor:18-21` only closes; `DeleteConfirmation.razor:24-39` has no busy state and reports errors in a snackbar (`:35`) | M | 110 | 52 | — |
| 116 | Row actions run inside their confirmation dialog | **NEEDED** (D4) | `Users.razor:554-598` (single delete, last-admin guard `:580`), `:611-638` (bulk); `Roles.razor:421-438,461-479`; `SystemLogs.razor:362-387` (`_clearing` at `:270`) | M | 115, 109 | 52 | Documents delete already runs in `DeleteConfirmation` (`Documents.razor:284,299`) and gains the busy state. |
| 117 | `ResetPasswordDialog` does the work itself | **NEEDED** | `ResetPasswordDialog.razor:61-69`; the work runs after close at `Users.razor:715-747` | S | 115 | 52 | — |
| 118 | Caller-supplied busy label; busy group | **NEEDED** | `DeleteConfirmation.razor` has no busy label at all | S | 115 | 52 | Build `BusyLabel` in from the start, defaulting to `AppStrings` "Deleting". Recommend skipping the busy group until a page needs it. |
| 119 | Close localisation gaps | **NEEDED** | `SUI/Pages/Identity/Login/ChangePassword.razor:11` has no resx; **also** `SUI/Pages/SystemManagement/SecuritySettings.razor:6` (not in the carry-over); `APP/Common/Constants/AppStrings.cs` lacks Saving/Deleting/Adding/Submitting; nothing guards a missing resx | M | 113, 20 | 52 | Add a missing-resx guard test. |
| 120 | Case-insensitive resource-key collisions | **N/A** | a scripted check found no case-insensitive duplicates in `SUI/Resources` | — | — | — | Guidance for CO-119: `Login.resx:120` and `Register.resx:67` already use "Sign In". |
| 121 | Remove unused localisation | **CONFLICT** (D5) | ListView `Documents.razor:42`, Download `:107`, DeleteTheItem `:286,301`, DeleteConfirmWithSelected `:298`, Clear `UploadFilesFormDialog:40` and Submit `:53` are **used by Documents**. The tenant keys are used at `Register.razor:31,33,288,304` and `LinkExternalLogin.razor:132,151` | S | 16, 18, 105 | 52 | Adapted: keep the keys Documents uses. The Register "Tenant" / "Please select a tenant" keys and `ValidationMessages.TenantRequired` go with CO-16/18. Print goes with Breadcrumbs (CO-105). Uploading has 0 uses and goes now. |
| 122 | `Logo.razor` disposal | **NEEDED** | `SUI/Components/Branding/Logo.razor:20` subscribes, `:28` unsubscribes the wrong event, and there is no `IDisposable` | S | — | 52 | — |
| 123 | Logo alt text | **NEEDED** | `Logo.razor:5,9` `alt="Logo"` | S | — | 52 | — |
| 124 | Centralise branding (`Brand.cs`) | **PARTIAL** | `Logo.razor:3` already follows dark mode; paths hard-coded at `:5,9`; no `Brand.cs` | M | — | 52 | — |
| 125 | TenantSelector inline style | **NEEDED** | `SUI/Components/AppShell/TenantSelector.razor:17` `height32px` | S | — | 52 | — |
| 126 | AuthLayout `min-height` | **NEEDED** | `SUI/Layouts/AuthLayout.razor:14` `height: 100vh` | S | — | 52 | — |
| 127 | Favicon and icon links | **NEEDED** | `App.razor:13-48` has no icon links; `favicon-96x96.png`, `favicon-152x152.png` unreferenced | S–M | 124 | 52 | The `favicon.ico` frame count was not checked (I). |
| 128 | Second app-bar logo while the drawer is open | **N/A** | `SUI/Components/AppShell/HeaderMenu.razor` has no `<Logo>` (company-name text at `:45-47`); the logo is in the drawer (`NavigationMenu.razor:14`) | — | 124 | — | It applies only if CO-124 adds an app-bar logo. |
| 129 | Logo in the email templates | **NEEDED** | no "logo" in `MailTemplateRenderer.cs` or `*.sbn` | M | 124 | 52 | — |
| 130 | Theme colours are template defaults | **NEEDED** | `App.razor:33` `#1976d2` disagrees with `Theme.cs:11` `Primary = "#0f172a"` | S | 124, 139 | 52 | — |
| 131 | Missing static file gets 302 instead of 404 | **NEEDED** | (I) fallback policy `IDI:555-557` and `SUI/DependencyInjection.cs:166` `MapStaticAssets().AllowAnonymous()` covers manifest files only | S–M | — | 52 | Not reproduced in this pass. |
| 132 | User pickers offer inactive accounts | **NEEDED** | `INF/Services/Identity/UserDataSourceService.cs:85-90` has no `IsActive` filter; the only picker is `PickSuperiorAutocomplete` (`UserFormDialog.razor:80`) | M | — | 52 | Recommend active-only, keeping a linked inactive user labelled "(inactive)". Defer `GetUserOptionsQuery` until a domain picker needs it. |
| 133 | `MudSelect` shows the raw id | **NEEDED** | `SUI/Pages/Identity/Users/Users.razor:50-57` has no `ToStringFunc` (`TenantSelect.razor:13` already has one) | S | — | 52 | — |
| 134 | "Awaiting access: N" chip | **NEEDED** | absent (depends on CO-20) | M | 20 | 49 | Adapted count: active, confirmed, and (no role OR no tenant) (R3-4). Only a `Users.ViewAllTenants` holder sees users with no tenant (R3-6). |
| 135 | One guard per write control [borderline] | **NEEDED** | **Live instance:** `Documents.razor:87` `@if (_accessRights.Edit \|\| _accessRights.Delete)` wraps Download at `:104`, so a user holding Download alone cannot download (V\*) | S | — | 52 | Recommend carrying it: fix Documents and state the rule. |
| 136 | Clear stale results on reload [borderline] | **N/A** | no `?tab=` pattern; `SupplyParameterFromQuery` only on identity pages | — | — | — | Guidance only (README, pass 53). |
| 137 | QuestPDF font hygiene [borderline] | **N/A** | QuestPDF's only consumer is `PDFService` (CO-103) | — | 103 | — | N/A if QuestPDF is removed with CO-103 (recommended; R5-5). |
| 138 | Busy button as one unit, with the dialog contract | **NEEDED** (D4) | `GxSubmitButtonComponentTests` and `DialogActionComponentTests` absent; every dialog closes first and acts afterwards | L | 110–118 | 52 | This is the umbrella for CO-110 to CO-118. |
| 139 | ThemeToggle replaces the theme drawer | **NEEDED** (D4) | `SUI/Components/Theming/ThemesMenu.razor`, `ThemesButton.razor`, `PrimaryColorPicker.razor`, their resx; `wwwroot/js/theme.js` (imported at `SUI/Services/Layout/LayoutService.cs:103`); Settings item `UserInfoCard.razor:76-79`; RTL toggle `HeaderMenu.razor:27-32`; `LayoutService.cs:23,45,120,194-220`; `UserPreference.cs:33-40`; flash risk at `MainLayout.razor:11-12,25-33` | L | 108, 130 | 52 | Also breaks `LanguageSelector.razor:45-46` `SetRightToLeft()`; no supported culture is RTL, so drop it. `OnlineUsersTracker` (hosted only at `ThemesMenu.razor:158`) goes with its test, and `ServerHub.GetOnlineUsers` loses its caller. |

### Tests (CO-143 to CO-180)

| CO | Item | Status | Evidence | Eff. | Deps | Pass | Note or recommendation |
|---|---|---|---|---|---|---|---|
| 143 | `TestDatabaseGuard` | **NEEDED** | absent | S | 144 | 47 | — |
| 144 | Framework-neutral `tests/TestSupport` | **NEEDED** | `tests/` has only the four projects | M | — | 47 | Required: the suites mix NUnit (3 projects) and xUnit (IUT). |
| 145 | `PostgresTestDatabase` (`GX_TEST_PG`) | **NEEDED** | no `GX_TEST_PG`; `AIT/Testing.cs:46-48` uses provider-plus-string variables; the stale history is **silently dropped** (`:227` `EnsureDeleted`) | M | 143, 144 | 47 | — |
| 146 | `DELETE`-batch reset; remove Respawn | **NEEDED** | `AIT/*.csproj:32` Respawn; `Testing.cs:151-157,348` | M | 145 | 47 | The trigger and lookup-table clauses apply only once CO-86/87 exist. |
| 147 | A database per assembly; serial; `HarnessTests` | **PARTIAL** | `AIT/AssemblyInfo.cs:3` `NonParallelizable`; UIT has no assembly attributes; the xUnit collection is SQLite-only (`IUT/Logging/SqliteFileCollection.cs:21`) | S | 145 | 47 | — |
| 148 | Move SQLite `EnsureCreated` classes to migrated PostgreSQL | **NEEDED** | 34 classes (list in Baseline; e.g. `UIT/SuperiorBoundComponentTests.cs:71,82`) | L | 144–147 | 47 | The largest single item in the reconciliation. |
| 149 | Fail, never skip, without the server variable | **NEEDED** | `Testing.cs:73,184` `Assert.Ignore`; `CanConnect` fallbacks at `SinkTimestampAcceptanceTests.cs:111,182,247`, `LogDatabaseCreationAcceptanceTests.cs:73,112,153,186,214,253,389`; SQLite fallback `GxWebApplicationFactory.cs:68,82,88`; `README.md:1124` promises "skipped" (V\*) | M | 145, 151 | 47 | Delete the LocalDB tests rather than converting them (D1). |
| 150 | `GX_TEST_CREATE_DATABASES` opt-in | **NEEDED** | absent; `LogDatabaseCreationAcceptanceTests.cs:71,110,151` gate on `CanConnect` | S–M | 145, 149 | 47 | — |
| 151 | `GxWebApplicationFactory` changes | **NEEDED** | `:94` `ConfigureAppConfiguration`; `:44-46` no hooks; SQLite fallback | M | 145, 146 (47, 63 for sub-parts) | 47/50/51 | `UseSetting`, reset before boot and `configureServices` go in pass 47. The guarded-environment defaults go in 50. The `IDatabaseLoginCheck` stub goes in 51. |
| 152 | `PostgreSqlCanaryTests` | **NEEDED** | absent | S | 145 | 47 | — |
| 153 | `AnonymousMatrixTests` expects 404 when off | **DONE** | `UIT/AnonymousMatrixTests.cs:87-88` | — | — | — | Both flag states are tested (pass 45 G7). |
| 154 | `UserSecretsIdTests` | **DONE** | Equivalent coverage: `template.json:16` `guids` + `smoke-generate.ps1:372-387` (plus the twin generation) | — | — | — | A unit test cannot run in the template itself, where the id is the template's by definition. The smoke is the right place. |
| 155 | `CommittedAppSettingsTests` | **PARTIAL** | `AUT/Configurations/CommittedAppSettingsTests.cs:31-33,39-53,101-136` | S | 43, 45, 66 | 50/51 | `RequiredStructure` grows with each new key. |
| 156 | `ModelMatchesMigrations` with Identity options | **DONE** | `IUT/Persistence/ModelMatchesMigrationsTests.cs:115,180`; no `PendingModelChangesWarning` suppression | — | — | 48 | Trim its SQLite leg (`:128,175`) in pass 48. |
| 157 | Real PostgreSQL log-table test; `tenant_id` round trip | **NEEDED** | `IUT/Logging/LogTableDdlTests.cs:182-212` runs on SQLite; the SQLite-era assertion is still at `IUT/Logging/LogTenantStampingTests.cs:189` | M | 145, 149 | 47 | — |
| 158 | Remove vacuous tests | **NEEDED** | `AIT/Picklist/Commands/DeletePicklistTests.cs:37` `FindAsync<Document>`, **and** `:20-21` `Should().ThrowAsync` never awaited (V\*); `IUT/Logging/SinkColumnDriftTests.cs:211` empty for every provider except SQLite; `UIT/ServerHubTenantIsolationTests.cs:566` `mock?.…Should()` | S | — | 47 | — |
| 159 | No test depends on a fresh database | **NEEDED** | `UIT/CookieLoginTests.cs:31-40`; `LogDatabaseSeparationTests.cs:177,205-207`; `pragma_*` at `AUT/Common/Interceptors/TenantStampingTests.cs:217,241`, `LogTableDdlTests.cs:200-216`, `SinkTimestampTests.cs:221` | M | 145, 151 | 47 | — |
| 160 | bUnit practice | **PARTIAL** | store doubles already used (`UIT/AuthLayoutComponentTests.cs:94`); shared open connection at `SuperiorBoundComponentTests.cs:71,82,140`, `UserDeactivationPermissionComponentTests.cs:178`, `UserTenantScopeComponentTests.cs:182`; `RoleDefinitionComponentTests.cs:183` uses `new Mock<IDialogService>()` | M | 148 | 47 | — |
| 161 | `UseSerilog` with `ReadFrom.Services` | **NEEDED** | `INF/Extensions/SerilogExtensions.cs:36-37` uses the two-argument form | S | — | 47 | CO-44 needs it. |
| 162 | Harness uses the real `IUserContextLoader`; `UseUser` | **NEEDED** | `AIT/Testing.cs:126-141` mock with id and name only | M | 145 | 47 | — |
| 163 | A harness user with limited grants | **NEEDED** | `Testing.cs:313` `GrantAllPermissionsAsync` for every user | S | 162 | 49 | Needed for the refusal tests in pass 49. |
| 164 | Clear the ambient user after reset; `ResetIsolationTests` | **PARTIAL** | `Testing.cs:354` already clears it; no `ResetIsolationTests` | S | 146 | 47 | — |
| 165 | Settable `TestClock` | **NEEDED** | absent; `APP/Common/Interfaces/IDateTime.cs` exists | M | 162 | 47 | — |
| 166 | `NoTemplateEntity_IsMappedIntoTheCoreSchema` breaks on the first project table | **NEEDED** | `IUT/Persistence/GxTableNamingTests.cs:215,227` `Assert.Empty(inCore)` | S | — | 47 | A generated project fails this on its first entity. |
| 167 | Name the Basic test after its grant | **CONFLICT** (D2) | `AUT/Persistence/ProvisioningTests.cs:160` `TheBasicRole_HoldsExactlyTheDocumentsReadGrant` | S | 15 | 49 | Adapted: rename it (shared `BasicGrant`), **and** assert that no new account receives Basic automatically. |
| 168 | Concurrency tests make a real change; positive controls | **NEEDED** | no concurrency test (CO-85 absent) | S | 85 | 48 | A practice rule that applies once CO-85 lands. |
| 169 | Split-login tests | **NEEDED** | no `SplitLoginDatabase`/`DatabaseLoginSeparationTests` | L | 45–48, 150 | 51 | Conditional on R5-1. |
| 170 | Startup-guard tests | **NEEDED** | no `StartupGuardTests`/`ProductionStartupCheckTests`; `UIT/ProcessWideStateTests.cs:61` checks only the Npgsql switch | M | 63, 64, 66, 68, 72 | 50 | — |
| 171 | `IHostEnvironment` in test service collections | **NEEDED** | (I) `IDI:433` `PostConfigure<IHostEnvironment>`; `Testing.cs:99` registers only `IWebHostEnvironment`; `InterceptorOrderingTests.cs:55` etc. register none | S | 65 | 47 | It bites only when `MailSettings` is resolved, hence I. |
| 172 | `ExternalRequestTests` | **NEEDED** | absent; `App.razor:17` (Google Fonts) and `:56` (cdnjs) would fail it | S | 105, 106, 108 | 52 | — |
| 173 | `RouteAuditTests`; pin confirmed-email sign-in | **PARTIAL** | pin exists: `UIT/IdentityLifecyclePolicyTests.cs:134-141`; no `RouteAuditTests` | M | 20, 151 | 49 | Adapted: the audit's roleless user **and** a role-but-no-tenant user must land on awaiting-access. |
| 174 | Access-gating component tests | **PARTIAL** | `UIT/SystemMenuGateComponentTests.cs:61,130-194` cover the admin section only; no `AwaitingAccessComponentTests` | M | 20, 21, 28, 29 | 49 | Adapted: add "a role but no tenant stays waiting" and "Basic is offered Documents, gated by permission". |
| 175 | Drive Register end to end to awaiting-access | **CONFLICT** (D2) | `Register.razor:30-35,139` | M | 15–20, 161 | 49 | Adapted: when enabled, the created user has **no tenant, no membership and no role** and lands on awaiting-access. A crafted `TenantId` is ignored. "Several organisations" becomes "never assigned". |
| 176 | `BrandingTests` | **NEEDED** | no `Brand` class; `neozhu` is kept deliberately in `DOM/Common/Entities/IAuditable.cs` (CO-213), so the scan covers UI markup only | S | 124, 127 | 52 | — |
| 177 | `AppBarLogoComponentTests` | **NEEDED** | absent | S | 128, 139 | 52 | Assert one logo, whatever CO-124 decides. |
| 178 | `SubmitButtonGuardTests` | **NEEDED** | would fail today on the 6 `MudLoadingButton` tags and 13 submit buttons (CO-113/114) | S | 113, 114 | 52 | — |
| 179 | `GxSubmitButtonComponentTests` | **NEEDED** (D4) | absent | M | 110–112 | 52 | — |
| 180 | `DialogActionComponentTests`; stubs run `Action` | **NEEDED** (D4) | absent; the stub at `RoleDefinitionComponentTests.cs:183` must run the action | M | 115–118 | 52 | — |

### Tooling (CO-184 to CO-199)

| CO | Item | Status | Evidence | Eff. | Deps | Pass | Note or recommendation |
|---|---|---|---|---|---|---|---|
| 184 | `[Rr]eleases/` negation + `GitIgnoreGuardTests` | **NEEDED** | `.gitignore:26` `[Rr]eleases/`. `git check-ignore -v --no-index src/Application/Features/Releases/x.cs` → `.gitignore:26`. | S–M | — | 53 | The guard needs named exclusions (e.g. `SUI/log/`, ignored by `.gitignore:34`). |
| 185 | Name local-only folders explicitly [borderline] | **PARTIAL** | `.gitignore:371` `**/Files/` catches any `Files` folder (`check-ignore` on `src/Application/Features/Files/x.cs` → `:371`); `:45-46` already anchors `Mail` | S | 184 | 53 | Recommend carrying it: the same trap as CO-184, and it costs one line. |
| 186 | `tools/Check-Publish.ps1` | **NEEDED** | no `tools/`; `tooling/` is excluded from generation (`template.json:215`); smoke already checks part of it (`smoke-generate.ps1:289-293,347-370`) | M | 70, 187 | 51 | It must live under a folder that generation keeps. Under D1, "removed-provider libraries absent" means no SqlServer or Sqlite DLLs. |
| 187 | `tools/Check-Icu.ps1` | **NEEDED** | absent | S–M | 70 | 51 | — |
| 188 | `docs/deployment.md` | **PARTIAL** | the README covers `web.config` variables and SkipWebConfig/app_offline (`README.md:85,135-149`); no logins/SQL, first-start lines, "Remove additional files", backup, or DataRoot; `template.json:213` excludes `docs/**` | M | 45–49, 66 | 51 | Adapted: change the `docs/**` exclude to a narrower one, or place the file elsewhere. |
| 189 | Strip template-only README content at generation | **NEEDED** | no conditionals in `README.md`; no `*.md` custom operation (the only one is `**/*.slnx`, `template.json:149-166`). Ships: `:1` title, `:15-16`, `:43-74`, `:192-244`, `:1140-1241` | M | — | 53 | Recommend `<!--#if (false)-->` blocks via a `*.md` custom operation. They are invisible when rendered in the template repository. Move the migration recipe (`:227-240`) into its own section. |
| 190 | README: running tests, and configuration | **PARTIAL** | `README.md:1109-1138` and `:76-98,246+` exist; `:1124` says "skipped"; no PowerShell/Bash recipes; SQL Server/SQLite text at `:1122,1135-1137` | S–M | 149, 79 | 48/53 | Provider text goes in 48; recipes and fail-loud wording in 53. |
| 191 | Clear build warnings | **NEEDED** | the 10 code warnings in Baseline | S–M | 79, 104 | 53 | Pass 48 removes 8 (NETSDK1206); CO-104 removes `Dashboard.razor:202`. |
| 192 | `<Nullable>enable</Nullable>` in the test projects | **PARTIAL** | on in `IUT/*.csproj:6`, `UIT/*.csproj:9`; off in AUT (`:3-11`) and AIT (`:3-11`) | S | — | 47 | It may surface new warnings in those two projects. Do it while pass 47 rewrites them. |
| 193 | Clean stale binaries after removals | **N/A** | a process note; the RID is already `win-x64` (`smoke-generate.ps1:352`) | — | — | — | Add it to the pass checklist, especially for pass 48 (removed Migrators). |
| 194 | Mutation-testing procedure [borderline] | **N/A** | nothing ships | — | — | — | Recommend keeping it as maintainer guidance in `GXTemplate-passes/` (a checklist), not as template content. |
| 195 | Console-tool host conventions [borderline] | **N/A** | the template ships no console tool | — | — | — | Recommend not carrying it now. A README pattern note, if anything. |
| 196 | `tools/DevSeed` [borderline] | **N/A** | absent | — | — | — | Recommend not carrying it. Its data is product-specific (CO-206). |
| 197 | All-or-nothing import (`ImportSession`) [borderline] | **N/A** | absent | — | — | — | Recommend not carrying it: heavy, with no template consumer. |
| 198 | Safety rules for data-writing tools [borderline] | **N/A** | absent | — | — | — | Recommend a short README "operational scripts" section, with no code. |
| 199 | psql script conventions [borderline] | **N/A** | absent | — | — | — | Recommend a two-paragraph note in the deployment document (CO-188), now cheap under D1. |

### "Not carried over" (CO-203 to CO-214), checked against the decisions

| CO | Claim | Status | Evidence |
|---|---|---|---|
| 203 | Tracking domain is business logic | **Holds** | `APP/Features` holds only AuditTrails, Documents, Identity, PicklistSets, SecuritySettings, SystemLogs, Tenants |
| 204 | Product features | **Holds** | — |
| 205 | Project reporting | **Holds**. Only the font, QuestPDF and external-request mechanisms were listed. | CO-108, 137, 172 |
| 206 | Client data | **Holds** | — |
| 207 | GX brand choices | **Holds** | — |
| 208 | "PostgreSQL-only is a product decision; the template keeps its provider choice" | **CONFLICT** with D1: overturned | `template.json:20-43`; `src/Migrators/*`; `Directory.Build.props:4-15` |
| 209 | Documents: only its defects | **Holds**, reinforced by D5 | CO-28 (adapted), CO-107, CO-105 (OpenSeadragon), CO-135 (Download nesting) |
| 210 | Local environment details | **Holds**, with notes | Port 5434 is already the template's local default (`.template.config/local-settings/appsettings.Development.json:13-14`, smoke `:322`). D3 changes the name to `<Name>Dbx`. |
| 211 | Pass 0 generation defects are absent | **Holds** (V as code; the smoke itself was not run here) | Wizard values `template.json:79-146` + smoke `:194-214,300-302`; no Docker (smoke `:389-395`); `IAuditable.cs:19` + smoke `:397-401` |
| 212 | Idle timeout and `RequestAuthorizationRegistry` exist | **Holds** | `APP/Common/Security/RequestAuthorizationRegistry.cs:17`; `INF/Services/Security/IdleTimeoutPolicyProvider.cs:32`; `IDI:146`; `AUT/Security/RequestAuthorizationRegistryTests.cs:32` |
| 213 | Attribution comments kept; no PWA manifest | **Holds** | `DOM/Common/Entities/IAuditable.cs:1-2`; `APP/Common/Interfaces/IPermissionService.cs:1-2` |
| 214 | The pass-workflow rule and the Blob design are project-specific | **Holds** | — |

### Found in this pass, not in the carry-over document

| # | Finding | Evidence | Where it goes |
|---|---|---|---|
| F1 | A principal with no tenant sees **every tenant's** public documents | `APP/Features/Documents/Specifications/VisibleDocumentSpecification.cs:61-65` (V\*); the rule is also used by `/files` (`FileEndpoints.cs:173-196`) | R3-8, pass 49 |
| F2 | A registration log event has shifted arguments (2 placeholders, 4 arguments) | `Register.razor:193-197` (V\*) | CO-74, pass 49 |
| F3 | Registration creates a DbContext that is never disposed | `Register.razor:185` | CO-19, pass 49 |
| F4 | A secondary-membership change leaves `AllowedTenantIds` stale for up to 1 h | `UserFormDialog.razor:251-263,271`; `UserContextLoader.cs:13` | CO-24, pass 49 |
| F5 | Download is hidden from a user who holds Download alone | `Documents.razor:87,104` (V\*) | CO-135, pass 52 |
| F6 | Dead script `Documents.razor.js` loads fabric from unpkg | `SUI/Pages/Documents/Documents.razor.js:5` | CO-105, pass 52 |
| F7 | `SecuritySettings` has a localizer but no resx | `SUI/Pages/SystemManagement/SecuritySettings.razor:6` | CO-119, pass 52 |
| F8 | An un-awaited `Should().ThrowAsync` makes a test vacuous | `AIT/Picklist/Commands/DeletePicklistTests.cs:20-21` (V\*) | CO-158, pass 47 |
| F9 | User-visible "Not implemented in this template" role descriptions | `APP/Common/Constants/Roles.cs:66-78` | CO-80, pass 48 |
| F10 | `theme-color` meta and the theme's primary colour disagree | `App.razor:33` vs `Theme.cs:11` | CO-130, pass 52 |

---

## R3 — Adapting to multi-tenancy (D2)

**The template rule, restated for the build passes:**
- A self-registered or external-login account is created **active**. It has `TenantId = null`, **no `TenantUsers` row** and **no role**.
- The form door has `EmailConfirmed = false`; the external door keeps `true`, because the provider has verified the address (`EP:485`).
- **A signed-in user is *waiting* when they have no role OR no tenant.**
- A waiting user can reach only: awaiting-access, profile, change-password, sign-in/sign-out, refresh-signin and the status pages.
- An administrator ends the wait by assigning **both**. That act clears the user-context and profile caches, and sends the user through `refresh-signin` once.
- Self-registration stays off by default. That is already the case (CO-14).

**Why "or no tenant", not only "no role":**
- `ApplicationUser.TenantId` is nullable (`DOM/Identity/ApplicationUser.cs:19`).
- Two data rules treat a missing tenant as "no restriction" or as "installation scope" instead of "nothing":
  - `VisibleDocumentSpecification.cs:61-65` (F1);
  - `ApplicationDbContext.cs:72`, where a null `CurrentTenantId` becomes `TenantId IS NULL`.
- A roleful user without a tenant would therefore see more, not less. Holding such users at the gate closes it for today. R3-7 and R3-8 close it in depth.

| # | Carry-over item(s) | Single-organisation assumption | Adapted form | Files it would touch |
|---|---|---|---|---|
| R3-1 | CO-15, 16 | Register assigns a tenant and Basic | Create active, no role, `TenantId = null`, no membership; remove the tenant field | `SUI/Pages/Identity/Register/Register.razor` (+4 resx); new `SUI/Services/Access/SelfRegistration.cs`; tests |
| R3-2 | CO-17 | "Assign the installation's one organisation; refuse when ambiguous" (`SelfRegistration.OrganisationAsync`) | **Never assign.** Ignore any client value and do not port the helper. The "ambiguous" refusal disappears. | `Register.razor`, `SelfRegistration.cs` |
| R3-3 | CO-18, 19 | External door "assigns the installation's one organisation, refusing with none or several" | Remove the `tenantId` query parameter and the page field. Create with no tenant and no role. `CreateAsync` + `AddLoginAsync` go in one transaction. | `EP:443-530`; `SUI/Pages/Identity/Login/LinkExternalLogin.razor` (+resx); `AUT/Identity/Provisioning/NewUserTimeZoneDefaultTests.cs:49-53` |
| R3-4 | CO-20, 21, 134, 173, 174 | "Waiting" = no role | Waiting = **no role OR no tenant**, one rule in `AwaitingAccess.cs` read by the middleware, the guard, the menu (`VisibleSections`) and the Users chip. Side effect: an administrator-created user saved without a role or tenant (`UserFormDialog.razor:312,330`) is also held, which is correct. | new `SUI/Services/Access/AwaitingAccess.cs`, `SUI/Middlewares/AwaitingAccessMiddleware.cs`, `SUI/Components/Routing/AwaitingAccessGuard.razor`, `SUI/Pages/Identity/Login/WaitingForAccess.razor` (+4 resx); `SUI/DependencyInjection.cs`; `SUI/Layouts/AppLayout.razor`; `NavigationMenu.razor`; `Users.razor`; new `APP/Features/Identity/AwaitingAccessFilter.cs` |
| R3-5 | CO-23, 24 | "First grant" = first role | The **edit that ends waiting** (a role or a tenant added while the other is present) bumps no stamp and clears both caches. Any membership change clears the context cache (F4). | new `SUI/Services/Access/UserRoleAssignment.cs`; `UserFormDialog.razor:251-301`; new `APP/Common/Interfaces/Identity/IUserContextCache.cs` + implementation; `Roles.razor:429,470`; `Users.razor:978`; `UserRoleChangeSecurityStampTests.cs:295` |
| R3-6 | CO-134, 22 (approval) | One organisation, so every administrator sees every sign-up | With several tenants, a user with no tenant matches no tenant id and is visible **only** to `Users.ViewAllTenants` holders (`APP/Features/Identity/UserTenantVisibility.cs:59-62`, V), today the installation Administrator (`AdministratorPermissionRegistry.cs:183`). The chip, the grid and the assignment follow from that. | none beyond R3-4, unless R5-2 decides otherwise |
| R3-7 | CO-31 | No tenant = "one installation" | Tenant filters fail closed for a **present principal with a null tenant**. Keep "no principal" (seeding, background work) as installation scope. Add a model-walking coverage test with a reasoned exemption list (Document → specification). | `INF/Persistence/ApplicationDbContext.cs:72,113-150`; new `AUT/Persistence/TenantFilterCoverageTests.cs` |
| R3-8 | CO-9 (via F1), D5 | — | `VisibleDocumentSpecification` with an empty tenant returns **own private documents only** (or nothing), never other tenants' public documents. `/files` inherits this. | `VisibleDocumentSpecification.cs:61-65`; `AUT/Features/Documents/DocumentTenantIsolationTests.cs`; `UIT/FileEndpointMatrixTests.cs` |
| R3-9 | CO-30 | Sample tenant; every administrator silently joins it | Remove the seeder. The administrator already holds `SwitchToAnyTenant` (`AdministratorPermissionRegistry.cs:176`). Add a README note on creating a second tenant (R5-4). | `INIT:88-95,340-362`; `ProvisioningTests.cs:381-412` |
| R3-10 | CO-167, 175 | Basic is the new user's role; "several organisations" refusal | Rename the Basic test to its grant. Assert that no new account is given Basic or a tenant. The E2E test ends on awaiting-access with no tenant and no role. | `ProvisioningTests.cs:160`; new Register E2E test in UIT |
| R3-11 | CO-58 | The seeded tenant is named after the company | Keep `"Default"`, as the first of many. Document that it is never renamed (`INIT:214`). | `INIT:121,212-223` |
| R3-12 | CO-43 | — (check only) | The bootstrap administrator is placed in the first tenant (`INIT:237-254`), so it is never waiting. No change. | — |

**Not affected by D2:**
- CO-32 (`TenantSwitchService` returns empty for `SwitchScope.None`, `TenantSwitchService.cs:219-220`, so a waiting user has an empty switcher).
- CO-33, which is not carried.

---

## R4 — Proposed build passes

The order follows the dependencies, and each pass stays small enough to review.
- The PostgreSQL harness comes **first**, although the brief lists security first. Every later pass writes database tests, and writing them on SQLite would mean moving them again in pass 48.
- Standing controls for every pass:
  - build with 0 errors, and report the warning count against the previous pass;
  - all suites green against `GX_TEST_PG`, with the per-suite test counts before and after, and every removed test named with its reason;
  - `dotnet pack` succeeds;
  - `tooling/smoke-generate.ps1` passes;
  - every mutation is shown applied (a diff) and restored byte for byte.

### Pass 47 — PostgreSQL test infrastructure (L)
- **Scope:** CO-143 to 150, 151 (`UseSetting`, reset before boot, `configureServices`, no SQLite fallback), 152, 157 to 162, 164, 165, 166, 171, 42, 192; F8; `build.yml` moves to a PostgreSQL service container.
- The providers stay in `src` for one more pass. No test uses SQLite or LocalDB when this pass ends.
- **Load-bearing mutations:**
  1. `GX_TEST_PG` names a database without the throwaway prefix → the guard refuses **before connecting**.
  2. Remove one child table from the reset batch → `ResetIsolationTests` fails.
  3. Unset `GX_TEST_PG` → every server suite **fails** (none skipped), with a message naming the variable.
  4. Tamper with `__EFMigrationsHistory` → the harness fails with the psql DROP command and no password.
- **Controls:**
  - `grep -r "UseSqlite\|SqliteConnection\|Assert.Ignore\|CanConnect" tests/` returns only the Azurite skip (`AUT/Storage/AzureBlobFileStorageTests.cs:43`), which is kept as documented.
  - The Respawn package is gone.
- **The generated project must show:** the smoke gains `-TestServer` (or reads `GX_TEST_PG`) and runs the three suites against it. Without it, the generated suites fail loud rather than silently passing on SQLite.

### Pass 48 — PostgreSQL-only template and PostgreSQL data behaviour (L)
- **Scope:** CO-79 (as deletion), 80, 81, 208, D3 naming, the CO-53 leftovers, CO-156 trim, CO-190 provider text, 82, 83, 84, 85, 86, 88, 89, 90, 168; F9.
- This pass has two halves. If it grows beyond review size, split it after the removal half.
- **Load-bearing mutations:**
  1. Re-add `EntityFrameworkCore.Sqlite` to `Domain.csproj` → a ported `ProviderPurityTests` fails **and** the smoke fails.
  2. Change the fallback so it drops `Dbx` → the smoke's name assertion fails.
  3. Remove `.IsRowVersion()` from one entity type → `RowVersionCoverageTests` fails.
  4. Remove the `DbUpdateConcurrencyException` arm → the handler test expecting "This record was changed by someone else…" fails.
  5. Mark an `AuditTrail` modified → `AppendOnlyInterceptor` refuses.
- **Controls:**
  - Warnings 18 → 10 (all NETSDK1206 gone).
  - `grep -ri "sqlite\|sqlserver\|mssql\|UseSqlServer" src tests .template.config` returns only a named allow-list (e.g. `*.VC.db` Visual Studio rules).
  - The PostgreSQL model snapshot is unchanged by `xmin`.
- **The generated project must show:**
  - `dotnet new gxblazor --Database mssql` is refused as an unknown option;
  - no `Migrators.MSSQL`/`Migrators.SqLite` and no `Sqlite` in any `project.assets.json`;
  - the Development file holds `Database=<Name>Dbx` and `<Name>Dbx_Logs`;
  - `--DatabaseName Foo` gives `Foo` / `Foo_Logs`.

### Pass 49 — Access and onboarding (L)
- **Scope:** CO-10 to 13, 15 to 25, 27, 28 (adapted), 29, 30, 31, 34, 74, 134, 163, 167, 173, 174, 175; R3-1 to R3-10; F1 to F4.
- **Load-bearing mutations:**
  1. Restore `AddToRoleAsync(Basic)` on registration → "new account has no role and no tenant" fails.
  2. Accept the query `tenantId` → the crafted-tenant test fails.
  3. The waiting rule checks roles only → "a role but no tenant stays waiting" fails.
  4. Remove the middleware registration → `RouteAuditTests` (a user with no role, over HTTP) reaches a page and fails.
  5. Restore the fail-open branch in `VisibleDocumentSpecification` → a user with no tenant sees another tenant's public document, and the test fails.
  6. Register Google unconditionally → the picker renders nothing, and `/performexternallogin?provider=Nope` returns 404; both tests fail.
  7. Gate Documents by role instead of permission → "Basic is offered Documents" fails.
- **Controls:**
  - The administrator's sign-in and menu are unchanged (positive control on each gated page).
  - `IdentityLifecyclePolicyTests` is still green.
  - `Provisioning_DoesNotRevokeAGrantAnOperatorAdded` is still green next to `RetiredGrants`.
- **The generated project must show:**
  - the smoke with default flags gives `/account/register` 404 (existing);
  - the smoke with `-AllowSelfRegistration true` runs the Register E2E test green in the generated suites.

### Pass 50 — Configuration, startup guards and bootstrap (M–L)
- **Scope:** CO-35, 36, 39, 41, 43, 44, 54 (Bootstrap, DataRoot), 57, 58, 59, 62, 63, 64, 66, 67, 68, 70, 71, 72, 151 (guarded defaults), 155, 170.
- **Load-bearing mutations:**
  1. Log the configured bootstrap password → the capturing-sink test fails.
  2. Set an invalid `Bootstrap:AdministratorPassword` → startup is refused, naming the key and never the value.
  3. `GuardedEnvironments` without Staging → the Staging refusal test fails.
  4. Remove the `Program.cs` try/flush → "Startup refused" is missing from the DataRoot file log.
  5. Plant `Password=realvalue` in a tracked `.md` → `CommittedCredentialsTests` fails, reporting only file and line.
  6. Remove `System.Globalization.AppLocalIcu` → `AppLocalIcuTests` fails.
  7. Remove `SetApplicationName` → the data-protection test fails.
- **Controls:** Development still boots with no keys set. The Development mail sink is still exempt.
- **The generated project must show:**
  - `Bootstrap:AdministratorPassword` and `AppConfigurationSettings:DataRoot` are present and empty in all four appsettings files;
  - "GX Application" is absent and the project name is present;
  - after `git init`, `src/Server.UI/web.config` is ignored.

### Pass 51 — Database logins and deployment (L; conditional on R5-1)
- **Scope:** CO-45 to 49, 54 (migration key), 56, 65, 69, 73, 98, 99, 151 (`IDatabaseLoginCheck` stub), 169, 186, 187, 188 (adapted location), 199.
- **Load-bearing mutations:**
  1. Grant TRUNCATE to the everyday login → the privilege test fails (and a 42501 test fails).
  2. Make the everyday login a superuser → startup is refused.
  3. Empty `MigrationConnectionString` in Production → refused, naming the key.
  4. Skip `DatabaseLogins` after migration → the default-privileges test fails.
  5. Remove `CopyToPublishDirectory="Never"` → `Check-Publish` fails.
- **Controls:** the split-login tests run only with `GX_TEST_CREATE_DATABASES=1` and report as skipped otherwise, as CO-150 allows. In Development, one login still works.
- **The generated project must show:** the smoke publishes the generated project in Release and runs the shipped `Check-Publish.ps1` and `Check-Icu.ps1` green. The deployment document is present in the generated tree.

### Pass 52 — UI (L)
- **Scope:** CO-26, 103 to 133 (except 120, 128), 135, 137, 138, 139, 172, 176 to 180; D5 fixes (CO-107, 105 OpenSeadragon, 121 adapted, 135/F5); F6, F7, F10.
- **Load-bearing mutations:**
  1. Put `disabled` on the busy button → `GxSubmitButtonComponentTests` fails.
  2. Remove the re-entry guard → a double click runs twice, and the test fails.
  3. `DialogAction` closes on failure → `DialogActionComponentTests` fails.
  4. Add a `ButtonType.Submit` `MudButton` → `SubmitButtonGuardTests` fails.
  5. `DocumentFormDialog` stops sending `AddEditDocumentCommand` → the Documents edit test fails.
  6. Add a CDN `<script>` → `ExternalRequestTests` fails.
  7. Invert the ThemeToggle icon → `ThemeToggleComponentTests` fails.
- **Controls:**
  - "/" answers 200 for any authenticated user and for a user switching tenant.
  - No `Theming/ThemesMenu`, `theme.js`, `MudLoadingButton` or Dashboard remain.
  - Warnings go down by one (`Dashboard.razor:202`).
- **The generated project must show:** the smoke asserts that the generated `App.razor` references no off-origin `src`/`href`, that `wwwroot/js/gx-busy-button.js` and `wwwroot/fonts/` exist, and that `theme.js` does not.

### Pass 53 — Tooling, README and warnings (M)
- **Scope:** CO-184, 185, 189, 190 (recipes, fail-loud wording), 191, 193 (checklist), 194 (checklist), the README pattern notes for 33, 87, 91 to 97, 136, 195, 198.
- **Load-bearing mutations:**
  1. Add `src/Application/Features/Releases/X.cs` with the negation removed → `GitIgnoreGuardTests` fails.
  2. Remove one `<!--#if (false)-->` guard → the smoke finds "Packaging the template" in the generated README.
  3. Reintroduce one CS8603 → the warning-count control fails, if pass 53 turns on `TreatWarningsAsErrors` for nullability (decide in the pass).
- **Controls:** **0 build warnings** in the template and in the generated project.
- **The generated project must show:**
  - a README without the template-only sections, and with a "Changing the model" section;
  - `git status --ignored` in the generated tree ignores nothing under `src/`, `tests/` or `tools/` except the named exclusions.

---

## R5 — Open questions for Yoab (ranked)

1. **Split database logins in every GX project (CO-45 to 49, 169; pass 51)?**
   - They cost L, and every deployment then needs a DBA to create two logins.
   - The code cannot say whether GX hosting always has that separation.
   - Recommendation: **yes**. It is the only thing that stops the running application from dropping its own tables, and D1 makes it one dialect. If no, pass 51 shrinks to the deployment document and the publish/ICU tools.
2. **Who may approve a waiting account?**
   - Today, only holders of `Users.ViewAllTenants` can even see a user with no tenant (`UserTenantVisibility.cs:59-62`), which in practice means the installation Administrator.
   - Recommendation: **keep it that way**. Letting tenant administrators see waiting accounts would let any tenant claim any sign-up.
3. **Is "a role but no tenant" ever a legitimate state, for example an installation-level operator?**
   - The proposed rule (R3-4) makes it a waiting state.
   - Recommendation: **no**. Every principal, including administrators, has a home tenant (the seeded administrator does, `INIT:237-254`). The alternative requires R3-7 and R3-8 to define what "installation scope" may see.
4. **The Development sample tenant "Europe" (CO-30):** remove it (recommended), or keep it without the automatic administrator membership as a tenancy demonstration?
5. **QuestPDF (CO-103, 137):** remove the package with the unused `PDFService` (recommended, since nothing uses it), or keep it for future reports? Keeping it makes the CO-137 font hygiene required.
