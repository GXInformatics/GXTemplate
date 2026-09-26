# Pass 45 — GX configuration standard (ported from ProjectTracking)

**Date:** 2026-09-26. **Start:** HEAD `135ed419` ("pass44"), clean tree.
**Commit:** one squashed commit, `pass45`, not pushed.
**Reference (read-only, not modified):** `C:\Yoab\Projects\GXProjectTracking` at `db1d8b7` ("pass3b"). Its
`src/Server.UI/appsettings.Development.json` was neither read nor copied.

## Summary

All eight items are done, and every new guard was broken and seen to fail.

| | Result |
|---|---|
| **G1** | `appsettings.json` is structure only: every key is present, and every secret or environment-specific value is empty. `Mail:ApiKey` was added empty. The header comment is in place. **The external-login `ClientSecret`s were also emptied**; the brief's list missed them (Disproven premise 1). |
| **G2** | `appsettings.Development.json` is generated from **`.template.config/local-settings/`**, a second `sources` entry in `template.json`. It keeps its real name, so **no rename is needed**. The template's own `src/` never sees it. It holds `Host=localhost;Port=5434;Database=<name>;Username=postgres;Password=;`. The database-name replace now targets it. The nuspec now excludes the maintainer's local file, and pack **fails** if one is packed. That hole was real before this pass. |
| **G3** | `appsettings.Staging.json` and `appsettings.Production.json` are committed, with empty placeholders and `Serilog:Properties:Environment`. |
| **G4** | `RuntimeIdentifier`, `EnableMSDeployAppOffline`, `SkipWebConfig` and `Content Update … CopyToPublishDirectory="Never"` are in place. `ServerUiPublishTests` reads the **evaluated** item metadata. |
| **G5** | `CommittedAppSettingsTests` is ported. Its git check also works in a project that is **not yet a git repository**, which is where a generated project's tests first run. |
| **G6** | The startup message names both sources, and its validation tests are updated. |
| **G7** | The GX fallbacks and the wizard defaults are `Africa/Lagos` / `false`. The AnonymousMatrixTests change was **adapted, not copied** (Disproven premise 2): the suite is now green whichever way a project is generated. |
| **G8** | `"guids"` in `template.json`. Two generations get two different ids, and neither is the template's. |

**Verification.**
- The smoke passes with the template defaults. The generated solution builds with 0 errors.
- The generated project's non-database suites all pass: 519 passed + 12 skipped, 207, 255.
- The smoke also passes with `-Database mssql` and `-Database sqlite`; see Controls.
- The two required smoke mutations fail as expected, and so do four more.

**Controls.**
- Template build: 0 errors. Warnings went from 19 to 18: Server.UI's own `NETSDK1206` is gone, because of the concrete RID.
- Suites: 523 passed + 12 skipped / 229 / 255 / 12 skipped.
- `dotnet pack` succeeds, with the extended content check.

---

## Disproven premises

1. **G1: "every secret … value is empty: [the listed keys]" is not the full list for the template.**
   - The template still has the `Authentication` section, which ProjectTracking deleted in its pass 1. Its `Microsoft`/`Google` `ClientId`/`ClientSecret` held `"***"`.
   - The ported G5 test matches secret keys by the pattern `…|Secret|…$`. It flagged `Authentication:*:ClientSecret` on the first run.
   - **What I did:** emptied all four keys (none deleted), added them to Staging, Production and the test's required-key list, and made one code change so that empty behaves like missing (see G1).

2. **G7: "Port the AnonymousMatrixTests change": a verbatim port would recreate the bug in mirror image.**
   - ProjectTracking's `SelfRegistration_IsNotAvailable` expects **404** and reads the **shipped** `appsettings.json` value. That is right for a project whose value is fixed.
   - In the template, the value is a wizard choice. A verbatim port would make every project generated with `--AllowSelfRegistration true` ship a red test, which is exactly the ProjectTracking defect with the sign flipped.
   - **What I did:**
     - Removed `/account/register` from the always-reachable surface.
     - Added `TheRegistrationPages_FollowAllowSelfRegistration`, which checks **each** flag state in its own host: off gives 404, on gives 200.
   - It fails on the pre-port row and on a middleware that ignores the flag (Verification).

3. **G2: "Because the template repo ignores that file name too, ship it under a non-ignored source name and rename it."**
   - The template repo's rule, `.gitignore:373` `src/Server.UI/appsettings.Development.json`, is anchored to the root. The same **name** at any other path is not ignored (`git status` lists `.template.config/local-settings/` as untracked, not ignored).
   - So no rename is needed. The mechanism I used is under G2.
   - There is a trap next door, and it decided the folder name. A NuGet exclude cannot be root-anchored; see pass 9 and the nuspec comment. So `**\src\Server.UI\appsettings.Development.json` would also exclude a source kept at `.template.config/…/src/Server.UI/appsettings.Development.json`. The source therefore sits in `local-settings/` directly, whose path does not end in `src\Server.UI\`.

Two notes on the premises that did hold:
- **"The template repo's own local appsettings.Development.json must never be packed."** Before this pass, nothing prevented it: the nuspec packs `.\**` and excluded no such file. There is no such file on this machine today (`ls src/Server.UI` shows none), so no package has leaked one here. The G2 guard shows that it **would** have been packed.
- **G7 fallbacks.** Changing the `Company` and `Copyright` fallbacks alone changes nothing a generated project shows, because `appsettings.json` set `"Company"` and `"@2024 Copyright"` over them. I set the same GX values in `appsettings.json:85-86`. This is a small addition beyond the letter of G7.

---

## G1 — `appsettings.json` is structure only

| File:line | Change |
|---|---|
| `src/Server.UI/appsettings.json:2-9` | Header, ported: structure only; where values come from; never delete a key; the test that enforces it. It also says that the template generates the Development file. |
| `:12`, `:21` | `ConnectionString` / `LogConnectionString`: the `Port=5434 … Password=postgres` strings are now `""`. |
| `:13-20` | The `LogConnectionString` comment is corrected as in ProjectTracking pass 3b C4: the application **does** create the log database when it is absent and the login may. The README already said so, and the old comment ("Create it before first run") was wrong. |
| `:52-64` | `Authentication:*:ClientId/ClientSecret` changed from `"***"` to `""`, with a comment (Disproven premise 1). |
| `:74-75` | `ApplicationUrl` changed from `"https://example.com"` to `""`, with a comment. |
| `:78`, `:81` | `DefaultTimeZone` is `"Africa/Lagos"` and `AllowSelfRegistration` is `false` (G7). These are also the new `replaces` literals. |
| `:85-86` | `Company` / `Copyright` are the GX values (see the note under Disproven premises). |
| `:92-100` | `Mail`: the comment is rewritten, `FromAddress` changed from `"noreply@example.com"` to `""`, and **`ApiKey` was added as `""`**. `Domain` was already `""`. |
| `:112` | `MaxMind:LicenseKey` changed from `"your license key"` to `""`. |
| `:129` | `Storage:ConnectionString` was already `""`. |
| `src/Infrastructure/DependencyInjection.cs:576-583, 653-667` | New `ExternalLoginSetting`: an empty or blank id/secret falls back to `"disabled"`, as a missing one always did. Without it, an empty `ClientId` reaches the OAuth handler's validation and a sign-in attempt becomes a 500 instead of failing at the provider. This is behaviour-preserving, not a guard, so there is no mutation. |
| `src/Infrastructure/DependencyInjection.cs:428-430` | Comment: why the mail default is decided in code rather than in the Development file. |
| `src/Infrastructure/Configurations/MailSettings.cs:48-52` | Doc comment: the key is listed empty, and its value is never committed. |

`DBProvider` stays in `appsettings.json`. It is not a secret, and the wizard writes it (`template.json` `DbProviderSetting`, unchanged).

**The retained Pass 44 check** (every `replaces` literal must occur in the template content) is kept. The smoke's content scan now includes `.template.config/local-settings/` (`tooling/smoke-generate.ps1:196-203`) and excludes the maintainer's own local file, where a literal would be a false match. The check was extended to `guids` entries (`:211-214`). Mutation (c) below shows it still bites.

## G2 — the generated `appsettings.Development.json`

**Mechanism: a second template source, not a rename.**
- `template.json:221-225` adds `{ "source": "./.template.config/local-settings/", "target": "./src/Server.UI/", "include": [ "appsettings.Development.json" ] }`.
- The file keeps its real name, and it lives outside the template's `src/`. So it is never content in the template's own build or publish, and never loaded by the template's own host.
- The template engine processes it like any other content: `//#if` conditionals and `replaces`.

| File:line | Change |
|---|---|
| `.template.config/local-settings/appsettings.Development.json:1-20` (new) | A header (local only; gitignored; never published; the one place a local password goes). Then `//#if (UseSqlServer)` gives LocalDB, `//#elseif (UsePostgreSql)` gives `Host=localhost;Port=5434;Database=GXTemplateDatabase;Username=postgres;Password=;` (and `…_Logs`), and `//#else` gives SQLite files. |
| `.template.config/template.json:107` | `DatabaseNameSafe` (DatabaseName, falling back to the project name, then sanitised) gains `"replaces": "GXTemplateDatabase"`. `GXTemplateDatabase_Logs` therefore becomes `<name>_Logs`. |
| `.template.config/template.json` (was 118-187) | Removed `LogDatabaseName`, `DbConnPrefix`, `DbConnSuffix`, `DbConnectionStringSetting` and `LogDbConnectionStringSetting`. The provider shape is now visible in the source file itself, so its port can be read and mutated there, which is what the brief's port mutation needs. |
| `.template.config/template.json:218` | Main source excludes `src/Server.UI/appsettings.Development.json`. **Measured to be redundant, not a guard**; see below. |
| `GX.Blazor.Template.nuspec:67-71, 74` | `**\src\Server.UI\appsettings.Development.json` is excluded, with the reason. |
| `build/pack.csproj:48-49, 91-105` | Pack now **fails** if `content/.template.config/local-settings/appsettings.Development.json` is missing, **or if `content/src/Server.UI/appsettings.Development.json` is present**. |
| `.gitignore:373` | Unchanged and kept. The generated project inherits it. |

**The token is `GXTemplateDatabase`, not `GXApplication`.** A replace is global, and `GXApplication` still appears in README history text (`README.md:205`, `:1206`), which generated projects carry. The smoke asserts that `GXTemplateDatabase` is absent from every generated file.

**Guard: the maintainer's local file is never packed.** I created a dummy `src/Server.UI/appsettings.Development.json` holding only `{"Marker": "maintainer-local-file (mutation check; no credential)"}`; `git status --ignored` showed it as `!!`.
- With the exclude, pack succeeds, and the package lists only `content/.template.config/local-settings/appsettings.Development.json`.
- Mutation: the nuspec exclude is removed (a `sed` edit; line 74 changes from `log\**;**\src\Server.UI\appsettings.Development.json;` to `log\**;**\GXTemplate-passes\**;`):

```
Successfully created package '…\pk2\GX.Blazor.Template.1.0.0.nupkg'.
…\build\pack.csproj(101,5): error : The package CONTAINS content/src/Server.UI/appsettings.Development.json: the maintainer's own local settings file, with whatever password it holds. Check the nuspec exclude for **\src\Server.UI\appsettings.Development.json, and do not distribute this package.
```
The nuspec was restored; its git blob `918296117585` is identical before and after. The dummy file was deleted.

The first attempt at this mutation **did not apply**. In this Git Bash, `sed` does not match `\\` as a literal backslash (`echo 'x\sy' | sed 's|\\s|Z|'` prints `x\sy`). Pack passed, and I caught it only because the diff was empty. I re-ran it with `[\]`, as shown above. Every other mutation in this report prints its diff to prove it applied.

**Folder install.** I installed the working tree as a folder, with the dummy present, into a custom hive under `%TEMP%\gxfold`, then generated `FolderApp`. Its Development file held `Database=FolderApp`, not the marker. Then I removed the `template.json` exclude, reinstalled into a fresh hive and generated `FolderApp3`. The result was **still the template's file**: the second source overwrites the first. So that exclude is not load-bearing. It is kept as a statement of intent and listed under Found but not fixed.

## G3 — Staging and Production

| File | Change |
|---|---|
| `src/Server.UI/appsettings.Staging.json:1-39` (new) | Ported header. Empty `DatabaseSettings:ConnectionString/LogConnectionString`, `Authentication:*:ClientId/ClientSecret`, `AppConfigurationSettings:ApplicationUrl`, `Mail:Domain/FromAddress/ApiKey`, `MaxMind:LicenseKey` and `Storage:ConnectionString`. `Serilog:Properties:Environment` is `"Staging"`. |
| `src/Server.UI/appsettings.Production.json:1-39` (new) | The same, with `"Production"`. |

The HTTP harness boots some hosts as Production, so they now also load `appsettings.Production.json`. The factory's in-memory settings still win, and all 255 tests pass.

## G4 — `Server.UI.csproj`

| Line | Change |
|---|---|
| `src/Server.UI/Server.UI.csproj:14-17` | `<RuntimeIdentifier>win-x64</RuntimeIdentifier>` and `<EnableMSDeployAppOffline>true</EnableMSDeployAppOffline>`, with a comment. |
| `:19-27` | `<MsDeploySkipRules Include="SkipWebConfig">`, with `ObjectName` `filePath` and `AbsolutePath` `web\.config$`. |
| `:29-34` | `<Content Update="appsettings.Development.json" CopyToPublishDirectory="Never" />`, with a comment. |

**The test: `tests/Application.UnitTests/Configurations/ServerUiPublishTests.cs:33` `AppSettingsDevelopmentJson_IsNeverPublished`.**
- It runs `dotnet msbuild -getItem:Content` (via `DOTNET_HOST_PATH`, with inherited `MSBuild*` variables removed) and reads `CopyToPublishDirectory` from the **evaluated** item. It takes about 0.5 s.
- It evaluates a **copy** of `Server.UI.csproj` in a scratch folder, next to `{}` stand-ins for both settings files. The template repo has no Development file (it is gitignored), and an `Update` only applies to an item that exists, so evaluating in place would prove nothing.
- `appsettings.json` is asserted **not** `Never`, as a control.
- **Limitation:** a copy does not see `Directory.Build.*` above `src/Server.UI`. None of them touches `Content` today. The smoke evaluates the real, in-place project in the generated folder, where the file does exist, so both are covered.

**Mutation: the `Content Update` line removed.**
```
    33d32
    <         <Content Update="appsettings.Development.json" CopyToPublishDirectory="Never" />
    Failed AppSettingsDevelopmentJson_IsNeverPublished [521 ms]
    Error Message:
     Expected CopyToPublishDirectory(content, "appsettings.Development.json") to be a match with the expectation because the local settings file carries this machine's credentials and must never reach a server (Server.UI.csproj: <Content Update="appsettings.Development.json" CopyToPublishDirectory="Never" />), but it differs at index 0:
  Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 521 ms - CleanArchitecture.Blazor.Application.UnitTests.dll (net10.0)
    restored: 438970cb4a16
```
The same mutation in the smoke (d, below) reports `found 'PreserveNewest'`.

## G5 — `CommittedAppSettingsTests`

`tests/Application.UnitTests/Configurations/CommittedAppSettingsTests.cs` (new, 168 lines) is ported from ProjectTracking with three adaptations:
- **The root is found by layout** (`src/Server.UI/Server.UI.csproj`, `:61-71`), not by the solution file's name, which the template renames.
- **`RequiredStructure` (`:39-53`)** also lists the four `Authentication` keys.
- **`AppSettingsDevelopmentJson_IsIgnoredByGit` (`:100-139`) works outside a repository.**
  - A project straight out of `dotnet new` is not a git repository. There, ProjectTracking's version would fail with exit code 128, and the generated suite would ship red.
  - Inside a work tree, it asks `git check-ignore` in place, as ProjectTracking does; that also catches a file that is already tracked.
  - Outside one, it evaluates the project's `.gitignore` in a scratch repository.
  - Git runs with `-c core.excludesFile=<nonexistent>`, so a machine-wide ignore rule cannot make it pass.

The other tests are as in the reference: `EverySecretBearingKey_IsEmpty` ×3 (`:76-86`) and `AppSettingsJson_KeepsEveryKeyAServerMustSupply_AndLeavesItEmpty` (`:88-99`).

**Mutation (required): a value in a secret-bearing key** (`appsettings.Production.json`, `Mail:ApiKey`; the value is obviously fake).
```
    26c26
    <     "ApiKey": ""
    ---
    >     "ApiKey": "key-mutation-not-a-credential"
    Failed EverySecretBearingKey_IsEmpty("appsettings.Production.json") [26 ms]
    Error Message:
     Expected filled to be empty because appsettings.Production.json is committed: secrets belong in appsettings.Development.json locally and in the server's web.config, but found at least one item {"Mail:ApiKey"}.
  Failed!  - Failed:     1, Passed:     4, Skipped:     0, Total:     5, Duration: 90 ms - CleanArchitecture.Blazor.Application.UnitTests.dll (net10.0)
    restored: 10c3f41ef85d
```

**Mutation: the `.gitignore` entry removed** (template repo, in-repository path):
```
    373d372
    < src/Server.UI/appsettings.Development.json
    Failed AppSettingsDevelopmentJson_IsIgnoredByGit [62 ms]
    Error Message:
     Expected exitCode to be 0 because appsettings.Development.json holds local credentials and must be gitignored (git check-ignore exit code 1; 1 means not ignored), but found 1 (difference of 1).
  Failed!  - Failed:     1, Passed:     4, Skipped:     0, Total:     5, Duration: 80 ms - CleanArchitecture.Blazor.Application.UnitTests.dll (net10.0)
    restored: 4fec4dff6713
```

**Mutation: the same, in a generated project with no `.git`** (the scratch-repository path; the smoke's kept `SmokeApp`):
```
  Failed AppSettingsDevelopmentJson_IsIgnoredByGit [107 ms]
   Expected exitCode to be 0 because appsettings.Development.json holds local credentials and this project's .gitignore must ignore it (git check-ignore exit code 1; 1 means not ignored), but found 1 (difference of 1).
Failed!  - Failed:     1, Passed:     4, Skipped:     0, Total:     5, Duration: 122 ms - SmokeApp.Application.UnitTests.dll (net10.0)
```
Restored, and the diff against the backup is empty. After restoring: `Passed! - Failed: 0, Passed: 5`.

## G6 — the startup message

| File:line | Change |
|---|---|
| `src/Infrastructure/Configurations/DatabaseSettings.cs:82-88` | `DatabaseSettings.ConnectionString is not configured. Set it in src/Server.UI/appsettings.Development.json for local development; the server's web.config in deployment.`, with the reason in a comment. |
| `tests/Application.UnitTests/Configurations/DatabaseSettingsValidationTests.cs:55-76` | A `MissingConnectionStringMessage` constant. The test is renamed `AMissingConnectionString_FailsValidationNamingBothPlacesItComesFrom` and asserts the full wording. |
| same, `:103` | `ValidateIsStillTheSingleDefinitionOfTheRules` asserts the same constant. |

**Mutation: the old message restored.**
```
    <                 $"{nameof(DatabaseSettings)}.{nameof(ConnectionString)} is not configured. Set it in " +
    <                 "src/Server.UI/appsettings.Development.json for local development; the server's web.config in deployment.",
    >                 $"{nameof(DatabaseSettings)}.{nameof(ConnectionString)} is not configured" +
    >                 "",
    Failed AMissingConnectionString_FailsValidationNamingBothPlacesItComesFrom [35 ms]
     Expected OptionsValidationException.Failures {"DataAnnotation validation failed for 'DatabaseSettings' members: 'ConnectionString' with the error: 'DatabaseSettings.ConnectionString is not configured'."} to have an item matching f.Contains("DatabaseSettings.ConnectionString is not configured. Set it in src/Server.UI/appsettings.Development.json for local development; the server's web.config in deployment.").
    Failed ValidateIsStillTheSingleDefinitionOfTheRules [10 ms]
     Expected results.Select(r => r.ErrorMessage)[1] to be "DatabaseSettings.ConnectionString is not configured. Set it in … deployment." with a length of 167, but "DatabaseSettings.ConnectionString is not configured" has a length of 51, differs near "d" (index 50).
  Failed!  - Failed:     2, Passed:     8, Skipped:     0, Total:    10 - CleanArchitecture.Blazor.Application.UnitTests.dll (net10.0)
    after restore:     0 Error(s)
    restored: b2e70497ac8d
```

## G7 — GX defaults

| File:line | Change |
|---|---|
| `src/Infrastructure/Configurations/AppConfigurationSettings.cs:25, 30, 49, 52` | `Company` is `"GX Informatics Limited"`, `Copyright` is `"© 2026 GX Informatics Limited"`, `DefaultTimeZone` is `"Africa/Lagos"` and `AllowSelfRegistration` is `false`. |
| `.template.config/template.json:56, 64` | Wizard defaults: `DefaultTimeZone` `"Africa/Lagos"` and `AllowSelfRegistration` `"false"`. Descriptions are updated. |
| `.template.config/template.json:122, 137` | The `replaces` literals follow the template's own `appsettings.json` (`"Africa/Lagos"`, `false`). The switch still writes whichever value is chosen. |
| `tests/Application.UnitTests/Configurations/AppConfigurationSettingsValidationTests.cs:38-49` | `TheDefaults_AreValid_AndAreTheRatifiedGXOnes` asserts all five GX values. It used to assert `UTC` and `true` ("upstream's behaviour"). |
| same, `:95-107` | `SelfRegistrationCanBeTurnedOff…` is inverted to `…TurnedOn_ByConfigurationAlone`. Under a `false` default, configuring `"false"` would pass whether or not the key was read. This is the same fix as ProjectTracking pass 3b C6. |
| `tests/Server.UI.IntegrationTests/AnonymousMatrixTests.cs:63-75` | `/account/register` is removed from `TheAnonymousSurface_IsReachable`. |
| same, `:77-101` | New `TheRegistrationPages_FollowAllowSelfRegistration(false → 404, true → 200)`. Each state gets its own `GxWebApplicationFactory` with the flag set explicitly, and each checks `/account/register` and `/account/registerconfirmation`. See Disproven premise 2. |
| `tests/Server.UI.IntegrationTests/IdentityLifecyclePolicyTests.cs:27-29` | The doc comment no longer says the template ships `true`. |

**Mutation: the fallback reopens registration** (`AppConfigurationSettings.cs:52`).
```
    <     public bool AllowSelfRegistration { get; set; } = false;
    >     public bool AllowSelfRegistration { get; set; } = true;
    Failed TheDefaults_AreValid_AndAreTheRatifiedGXOnes [22 ms]
     Expected settings.AllowSelfRegistration to be False because GX accounts are created by an administrator; registration is opened by configuration, never by default, but found True.
  Failed!  - Failed:     1, Passed:     6, Skipped:     0, Total:     7 - CleanArchitecture.Blazor.Application.UnitTests.dll (net10.0)
    restored: eddfc247c20c
```

**Mutation: the middleware ignores the flag** (`SelfRegistrationMiddleware.cs:55`, changed to `ShouldBlock(context.Request.Path, true)`).
```
    Failed TheRegistrationPages_FollowAllowSelfRegistration(False,NotFound) [756 ms]
     Expected response.StatusCode to be HttpStatusCode.NotFound {value: 404} because /account/register with AllowSelfRegistration=False, but found HttpStatusCode.OK {value: 200}.
  Failed!  - Failed:     1, Passed:    22, Skipped:     0, Total:    23 - CleanArchitecture.Blazor.Server.UI.IntegrationTests.dll (net10.0)
    restored: 80bb3d23d4ad
```

**Demonstration: the pre-port row restored.** This is the defect ProjectTracking shipped, now visible in the template because its own default is `false`:
```
    Failed TheAnonymousSurface_IsReachable("/account/register") [80 ms]
     Expected response.StatusCode to be HttpStatusCode.OK {value: 200}, but found HttpStatusCode.NotFound {value: 404}.
  Failed!  - Failed:     1, Passed:    23, Skipped:     0, Total:    24 - CleanArchitecture.Blazor.Server.UI.IntegrationTests.dll (net10.0)
    restored: 2b134096001b
```
(The `diff` output for the last two runs spans the whole file only because `sed` rewrote CRLF as LF. The restored blobs are byte-identical, as the hashes show.)

## G8 — a UserSecretsId per project

| File:line | Change |
|---|---|
| `.template.config/template.json:16` | `"guids": [ "8118d19e-a6db-4446-bdb6-fa62b17f843d" ]`. The template engine replaces it, in every format it recognises, with a fresh GUID per generation. |

`Server.UI.csproj:12` still carries `8118d19e-…` in the template repo, as the token. The smoke asserts the result (below). ProjectTracking's `UserSecretsIdTests` was **not** ported: in the template repo the id *is* the template's, so that test would be red there.

---

## Verification

`tooling/smoke-generate.ps1` was extended; see the file map. **It now generates with the template's defaults**: an option is passed to `dotnet new` only when it is given to the script (`:45-67`, `:228-235`). The run printed `== generate: -n SmokeApp (template defaults)`.

| Step (smoke line) | Asserts |
|---|---|
| `:280-312` | All four files exist. In each **committed** file, every key matching the secret pattern is empty. `appsettings.json` keeps each G1 key, and keeps it empty. `DBProvider`, `DefaultTimeZone` and `AllowSelfRegistration` equal the defaults. The header is present. Staging and Production set `Serilog:Properties:Environment`. The JSON is parsed after stripping comments outside strings (`:130-156`). |
| `:313-337` | The Development file per provider. PostgreSQL: `Host=localhost`, **`Port=5434`**, `Database=SmokeApp` / `SmokeApp_Logs`, `Username=postgres`, **`Password=` empty**, each a named check. mssql: LocalDB. sqlite: `Data Source=<name>.db`. |
| `:339-345` | No `GXTemplateDatabase` in any file, and no `GXApplication` in any settings file. |
| `:347-357` | The csproj carries G4's four settings (XML). |
| `:359-370` | `dotnet msbuild -getItem:Content` on the **generated, in-place** project: `appsettings.Development.json` is `Never`, and the `appsettings.json` control is not. |
| `:372-387` | The UserSecretsId is a GUID; it differs from `8118d19e-…`; a **second generation** (`twin`) gets a different one; and the template id appears nowhere. |
| `:403-432` | Build with 0 errors, then the three non-database suites. This runs **before** `git init`, so G5's no-repository path is exercised. |
| `:434-448` | `git init`, then `git check-ignore` reports the Development file ignored. The three committed files are the control, and must be **not** ignored. |

**Result, defaults** (final run, excerpt):
```
== generate: -n SmokeApp (template defaults)
  ok    DBProvider is postgresql
  ok    DefaultTimeZone is Africa/Lagos
  ok    AllowSelfRegistration is false
  ok    Development business connection string: Port=5434
  ok    Development business connection string: Database=SmokeApp
  ok    Development business connection string: Password is EMPTY
  ok    Development log connection string: Database=SmokeApp_Logs
  ok    RuntimeIdentifier is win-x64
  ok    EnableMSDeployAppOffline is true
  ok    MsDeploySkipRules SkipWebConfig: ObjectName filePath, AbsolutePath web\.config$
  ok    Content Update="appsettings.Development.json" CopyToPublishDirectory="Never"
  ok    control: appsettings.json is publishable content
  ok    appsettings.Development.json has CopyToPublishDirectory=Never
  ok    UserSecretsId is a GUID (f210489b-066f-40b8-964f-32058a3a0bb7)
  ok    UserSecretsId differs from the template's 8118d19e-a6db-4446-bdb6-fa62b17f843d
  ok    a second generation of the same name gets a different UserSecretsId (0865bfe8-b537-4744-ad17-042219467e71)
  ok    the template's UserSecretsId occurs nowhere in the output
  ok    0 errors
  ok    Application.UnitTests: Passed! - Failed: 0, Passed: 519, Skipped: 12, Total: 531 - SmokeApp.Application.UnitTests.dll
  ok    Infrastructure.UnitTests: Passed! - Failed: 0, Passed: 207, Skipped: 0, Total: 207 - Infrastructure.UnitTests.dll
  ok    Server.UI.IntegrationTests: Passed! - Failed: 0, Passed: 255, Skipped: 0, Total: 255 - SmokeApp.Server.UI.IntegrationTests.dll
  ok    git check-ignore src/Server.UI/appsettings.Development.json: ignored
  ok    control: appsettings.json is NOT ignored
SMOKE PASSED (postgresql)
```

The generated `src/Server.UI/appsettings.Development.json`, verbatim:
```
  "DatabaseSettings": {
    "ConnectionString": "Host=localhost;Port=5434;Database=SmokeApp;Username=postgres;Password=;",
    "LogConnectionString": "Host=localhost;Port=5434;Database=SmokeApp_Logs;Username=postgres;Password=;"
  }
```

**Smoke mutations.** Each ran with `-NoBuild`, because every check involved runs before the build. Each file was restored, and its git blob matches the pre-mutation one.

(a) **Required: the `guids` replacement removed** (`template.json:16`).
```
    16d15
    <   "guids": [ "8118d19e-a6db-4446-bdb6-fa62b17f843d" ],
    smoke exit 1
  == the project has a UserSecretsId of its own
    ok    UserSecretsId is a GUID (8118d19e-a6db-4446-bdb6-fa62b17f843d)
    FAIL  UserSecretsId differs from the template's 8118d19e-a6db-4446-bdb6-fa62b17f843d - found '8118d19e-a6db-4446-bdb6-fa62b17f843d'
    FAIL  a second generation of the same name gets a different UserSecretsId (8118d19e-a6db-4446-bdb6-fa62b17f843d) - both are '8118d19e-a6db-4446-bdb6-fa62b17f843d'
    FAIL  the template's UserSecretsId occurs nowhere in the output - src\Server.UI\Server.UI.csproj:12
  SMOKE FAILED (postgresql): 3 failure(s)
    restored: fca975f6bc60
```
The first run of this mutation also exposed a smoke defect. With no `guids` array, the literal check printed `ok guids entry  occurs…` for a null entry. It is fixed at `:211` (null entries are filtered), and the output above is from the fixed script.

(b) **Required: `Port=5432` restored in the Development file source** (`.template.config/local-settings/appsettings.Development.json:13-14`).
```
    <     "ConnectionString": "Host=localhost;Port=5434;Database=GXTemplateDatabase;Username=postgres;Password=;",
    >     "ConnectionString": "Host=localhost;Port=5432;Database=GXTemplateDatabase;Username=postgres;Password=;",
    (and the log line)
smoke exit 1
  FAIL  Development business connection string: Port=5434 - found 'Host=localhost;Port=5432;Database=SmokeApp;Username=postgres;Password=;'
  FAIL  Development log connection string: Port=5434 - found 'Host=localhost;Port=5432;Database=SmokeApp_Logs;Username=postgres;Password=;'
SMOKE FAILED (postgresql): 2 failure(s)
restored: 5f6b4311c4c3
```

(c) **The retained Pass 44 literal check: the replace token drifts** (`GXTemplateDatabase` changed to `GXTemplateDb` in the source file).
```
    FAIL  DatabaseNameSafe: its replaces literal 'GXTemplateDatabase' occurs nowhere in the template content, so it replaces nothing
    FAIL  Development business connection string: Database=SmokeApp - found 'Host=localhost;Port=5434;Database=GXTemplateDb;Username=postgres;Password=;'
    FAIL  Development log connection string: Database=SmokeApp_Logs - found 'Host=localhost;Port=5434;Database=GXTemplateDb_Logs;Username=postgres;Password=;'
  SMOKE FAILED (postgresql): 3 failure(s)
    restored: 5f6b4311c4c3
```

(d) **The `Content Update` line removed** (`Server.UI.csproj:33`).
```
    FAIL  Content Update="appsettings.Development.json" CopyToPublishDirectory="Never"
    FAIL  appsettings.Development.json has CopyToPublishDirectory=Never - found 'PreserveNewest'
  SMOKE FAILED (postgresql): 2 failure(s)
    restored: 438970cb4a16
```

(e) **The `.gitignore` entry removed** (`.gitignore:373`).
```
    FAIL  git check-ignore src/Server.UI/appsettings.Development.json: ignored - exit code 1 (1 means NOT ignored)
  SMOKE FAILED (postgresql): 1 failure(s)
    restored: 4fec4dff6713
```

The first smoke run of this pass failed on a check of mine, not on the template. The "no `GXApplication` anywhere" check matched the README's own history sentences (`README.md:171`, `:1156` at the time). It is now scoped to the settings files; the replace token itself is still checked in every file.

---

## Controls

| Control | Result |
|---|---|
| `dotnet build CleanArchitecture.Blazor.slnx --no-incremental` | **0 errors, 18 warnings** (baseline 19). The set of distinct warning texts is identical (`diff` of normalised lines is empty). The one fewer is Server.UI's `NETSDK1206` (SQLite `win7-*` RIDs), which no longer applies with a concrete `RuntimeIdentifier`, as ProjectTracking measured. |
| Application.UnitTests | **523 passed, 12 skipped** (baseline 517 / 12). +6: `CommittedAppSettingsTests` ×5 and `ServerUiPublishTests` ×1. |
| Infrastructure.UnitTests | **229 passed** (baseline 229). |
| Server.UI.IntegrationTests | **255 passed** (baseline 254). −1 register row, +2 flag rows. |
| Application.IntegrationTests | **12 skipped**, `GX_TEST_*` unset, as in Pass 44. |
| `dotnet pack build/pack.csproj` (to scratch) | Succeeds. `Template package contents verified: template.json, ide.host.json, icon.png and local-settings/appsettings.Development.json are present under content/.template.config/, and no local src/Server.UI/appsettings.Development.json was packed.` |
| Smoke, defaults | **SMOKE PASSED (postgresql)**: 0 errors; generated suites 519/12, 207, 255. |
| Smoke, `-Database mssql` | **SMOKE PASSED (mssql)**: Development file has LocalDB `Database=SmokeApp` / `SmokeApp_Logs`; 0 errors; generated suites 515/12, 201, 255. |
| Smoke, `-Database sqlite` | **SMOKE PASSED (sqlite)**: `Data Source=SmokeApp.db` / `SmokeApp_Logs.db`; 0 errors; generated suites 511/12, 172, 255. |

No Docker was used. No credential was written anywhere. The only local settings file created was the dummy `{"Marker": …}` used for the pack mutation, which was deleted afterwards. The repository root's `GX.Blazor.Template.1.0.0.nupkg` was not rebuilt: every pack in this pass wrote to scratch or `%TEMP%`.

---

## Per-change file map

| File | Item | Why |
|---|---|---|
| `src/Server.UI/appsettings.json` | G1, G7 | Structure only; header; `Mail:ApiKey`; external-login keys emptied; GX `DefaultTimeZone` / `AllowSelfRegistration` / `Company` / `Copyright`; log-database comment corrected. |
| `src/Server.UI/appsettings.Staging.json` (new) | G3 | Committed placeholders, `Environment` `Staging`. |
| `src/Server.UI/appsettings.Production.json` (new) | G3 | Committed placeholders, `Environment` `Production`. |
| `.template.config/local-settings/appsettings.Development.json` (new) | G2 | The generated local file: provider conditionals, port 5434, empty password, name token. |
| `.template.config/template.json` | G2, G7, G8 | `guids`; wizard defaults; name replace on `DatabaseNameSafe`; five connection-string symbols removed; the second source; the main-source exclude. |
| `GX.Blazor.Template.nuspec` | G2 | Excludes the maintainer's local file. |
| `build/pack.csproj` | G2 | Asserts the local-settings file is present and the maintainer's file is absent. |
| `src/Server.UI/Server.UI.csproj` | G4 | RID, app-offline, `SkipWebConfig`, and the Development file never published. |
| `src/Infrastructure/Configurations/DatabaseSettings.cs` | G6 | The two-source message. |
| `src/Infrastructure/Configurations/AppConfigurationSettings.cs` | G7 | GX fallbacks. |
| `src/Infrastructure/DependencyInjection.cs` | G1 | `ExternalLoginSetting`; the mail-default comment. |
| `src/Infrastructure/Configurations/MailSettings.cs` | G1 | Doc comment on the API key. |
| `src/Infrastructure/Persistence/Logging/LogDatabaseDdl.cs:231`, `tests/Infrastructure.UnitTests/Logging/LogDatabaseDdlTests.cs:200` | G1 | Comments: "appsettings.json" becomes "the settings files" (the log connection string no longer lives in it). |
| `tests/Application.UnitTests/Configurations/CommittedAppSettingsTests.cs` (new) | G5 | Ported, with three adaptations. |
| `tests/Application.UnitTests/Configurations/ServerUiPublishTests.cs` (new) | G4 | Evaluated-metadata publish test. |
| `tests/Application.UnitTests/Configurations/DatabaseSettingsValidationTests.cs` | G6 | Message constant; renamed test. |
| `tests/Application.UnitTests/Configurations/AppConfigurationSettingsValidationTests.cs` | G7 | GX defaults asserted; the vacuous test inverted. |
| `tests/Server.UI.IntegrationTests/AnonymousMatrixTests.cs` | G7 | Register row removed; per-state flag rows. |
| `tests/Server.UI.IntegrationTests/IdentityLifecyclePolicyTests.cs` | G7 | Doc comment. |
| `tooling/smoke-generate.ps1` | Verification | Defaults-driven generation; layout, publish, csproj, UserSecretsId and gitignore checks; generated suites; `guids` literal check; null-entry fix. |
| `README.md` | all | Step 3 rewritten around the four-file layout, with the startup message and the `web.config` example and IIS settings. Wizard table (defaults, where values go); configuration reference intro; `ApplicationUrl`, `FromAddress` and API-key text; development-sink sentence; the smoke description; the maintainer's local file; the pack assertion. |
| `GXTemplate-passes/pass45.md` (new) | — | This report. |

---

## Found but not fixed

1. **`template.json`'s main-source exclude of `src/Server.UI/appsettings.Development.json` is redundant.** Measured under G2: the second source overwrites the first, so removing the exclude changes nothing. It is kept because it states the intent, and because it is what would protect a folder install if the sources were ever reordered. Nothing tests it, because nothing can currently fail.
2. **`RuntimeIdentifier` `win-x64` is unconditional.** A contributor on Linux or macOS still builds, as a framework-dependent `win-x64` output, but `dotnet run` would not start a Windows apphost there. GX is Windows/IIS, and the brief asks for the RID unconditionally. A `Condition="'$(OS)' == 'Windows_NT'"` would be the fix if that ever matters.
3. **`ServerUiPublishTests` evaluates a copy of the project**, so it does not see `Directory.Build.*` imports above `src/Server.UI`. The smoke's in-place evaluation of a generated project covers the real thing; the unit test covers the template repo, which has no Development file to evaluate in place.
4. **`Mail:FromAddress` is now empty by default.** It used to be `noreply@example.com`, via `appsettings.json`; the class default is still that. A server that forgets to supply it now sends with an empty from-address rather than a self-evidently fake one. That follows G1's rule. `MailStartupCheck`'s treatment of an empty address was not re-examined.
5. **External-login buttons still render with placeholder credentials.** This is ProjectTracking pass 1 C3, unchanged in the template: Google and Microsoft are registered unconditionally, and an empty id now falls back to `"disabled"` exactly as a missing one did. Registering a provider only when it is configured is the real fix; it is outside this brief.
6. **`CommittedAppSettingsTests` and `ServerUiPublishTests` shell out** to `git` and `dotnet`. A machine without `git` on `PATH` fails the git test outright, rather than skipping it. That was judged the honest outcome.
7. **The template repository's own `Server.UI` now needs a local `src/Server.UI/appsettings.Development.json`** (or `DatabaseSettings__*` variables) to run, like any generated project. It was **not** created: that would mean writing a credential. The README says so under Packaging.
8. **This environment's `sed` does not match `\\`.** One mutation silently failed to apply because of it (G2). Any future mutation script here should print its diff, as the runners in this pass do.
