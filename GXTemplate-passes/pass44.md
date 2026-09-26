# Pass 44 — Defect fixes from the first generated project

**Date:** 2026-09-26. **Start:** HEAD `4af9566a` ("pass43"), clean tree.
**Commit:** one squashed commit, `pass44`, not pushed.

## Summary

All six items were done. Three of them rested on a premise that turned out to be wrong; see the next section.

| | Result |
|---|---|
| **F1** | `/files` now refuses any key whose prefix is not listed. The old default was **exploitable, not just theoretical**: at HEAD, `GET /files/%20Documents/<key>` returned **200 with the bytes of a document the caller may not see**, because the check compared `" Documents"` against `"Documents"` and the storage layer then trimmed the space off. Both HTTP rows are mutation-checked. |
| **F2** | Application.IntegrationTests reads GX_TEST_*. It has no LocalDB default and no `BlazorDashboard.Test`. When the variables are unset, all 12 tests are **skipped** with a message naming them. They pass against local PostgreSQL 18 and against LocalDB. The project was **not** deleted (see F2). |
| **F3** | Docker is gone: 6 files, the csproj property, the solution items, `.gitattributes` lines and README mentions. |
| **F4** | The database name was lost because `appsettings.json` moved to `Port=5434` in `b184f547`, so the `template.json` replace text matched nothing. Provider exclusion **never existed**: it was documented design that everything shipped. It now exists for MSSQL and PostgreSQL. SQLite always ships (see Disproven premises). `tooling/smoke-generate.ps1` passes for all three providers, and three mutations make it fail. |
| **F5** | There was one over-replacement: `IAuditable.cs:19`. It now names the real upstream repository, whose name does not contain the token. The smoke script checks for it, and a mutation makes that check fail. |
| **F6** | The package route failed on a fresh clone because it never said to run `dotnet pack` first. The folder route was **worse**: generating from the clone root put `IMS/` inside the installed template, and every later project copied it. The README is fixed, and both routes run verbatim in a custom hive. |

**Controls** (template repo, after everything):
- `dotnet build CleanArchitecture.Blazor.slnx --no-incremental`: **0 errors**. There are 19 warnings, the same count as Pass 43.
- `dotnet pack build/pack.csproj`: succeeds, and its built-in content check passes.
- Tests with GX_TEST_* **unset**, compared with Pass 43:
  - Application.UnitTests: 517 passed, 12 skipped (+5 new F1 cases).
  - Infrastructure.UnitTests: 229 passed.
  - Server.UI.IntegrationTests: 254 passed. Pass 43 reported 251; the 2 new F1 rows account for 253. The one-test difference was already present before the fork's changes (it measured 254 then) and was not investigated.
  - Application.IntegrationTests: **0 passed, 12 skipped** (was 12 passed against LocalDB).
- Database tests that actually ran: Application.IntegrationTests with `GX_TEST_DBPROVIDER=postgresql` against local PostgreSQL 18 (port 5434), 12/12 passed; with `mssql` against LocalDB, 12/12 passed. Both throwaway databases (`gx_p44_appit`) were dropped afterwards. Server.UI.IntegrationTests ran only on its default SQLite.

Reports 19–43 are in the **in-repo** `GXTemplate-passes/` folder, not in `../GXTemplate-passes/`, which stops at pass 18. This pass is numbered 44 and written in the in-repo folder so that it lands in the commit like its predecessors.

---

## Disproven premises

1. **"Pass reports go in ../GXTemplate-passes/."** That folder holds passes 1–18. Passes 19–43 are committed in `GXTemplate-passes/` inside the repo, so the next number is **44** and this report is in that folder.

2. **F2: "`using static Testing;` sits outside the file-scoped namespace."** It sits *after* `namespace ...;`, i.e. inside it, in all six files (`TestBase.cs:6`, `HarnessPrincipalTests.cs:12`, `Services/PicklistServiceTests.cs:8`, `Services/TenantsServiceTests.cs:8`, `Picklist/Commands/AddEditPicklistCommandTests.cs:10`, `DeletePicklistTests.cs:11`). It has been there since `7267aa72`.
   - A CLI-generated project (`-n SmokeApp`) compiles Application.IntegrationTests with **0 errors**, both at HEAD and after this pass.
   - I **could not reproduce** the compile failure, so I did not change those lines.
   - The only build failure I reproduced was **MSB3021 (MAX_PATH)**, generating into a ~130-character folder. It hits Application.IntegrationTests, Infrastructure.UnitTests and Server.UI.IntegrationTests (SQLite's `runtimes\browser-wasm\...\e_sqlite3.a`). The README's Known limitations already covers this. It is plausible but unconfirmed for your VS project; see the manual checks.

3. **F4(c): "whether the conditional exclusions actually remove the providers."** There were none. `template.json`'s Database description and the README both said that **all three providers and migration projects ship regardless of the choice**, and gave reasons (mergeability, switching provider by configuration). This pass reverses that documented decision, because the brief treats it as a defect. The README now says so.

4. **F4 smoke: "assert the generated output contains no … SQLite migrator, provider package or provider test."** SQLite cannot be removed without redesigning the test harnesses, which is outside "`.template.config` and the conditional markers":
   - `GxWebApplicationFactory` boots the real application on SQLite by default. The application migrates at startup (`ApplicationDbContextInitializer.cs:54`), which needs `Migrators.SqLite`.
   - 48 test files use in-memory SQLite.

   SQLite therefore **always ships**, and the smoke asserts that it is present. The other server provider is removed completely.

5. **F4 background: "DefaultTimeZone came out UTC."** Through the CLI, `--DefaultTimeZone Africa/Lagos --AllowSelfRegistration false` reached `appsettings.json` correctly **at HEAD** (baseline generation). Only the database name was lost. Why VS produced `UTC` cannot be established from here; see the manual checks.

---

## F1 — `/files` fails closed

**Finding.** At HEAD, `IsPermittedAsync` returned `true` for any first segment other than `Documents` (`FileEndpoints.cs:99-103` at HEAD).
- The comparison uses the raw segment, but `StorageKeys.Split` (`StorageKeys.cs:85-88`) **trims** segments before the storage provider reads the key (`LocalDiskFileStorage.cs:78`).
- So `" Documents/x"` skipped the Documents rule and read `Documents/x`.
- `Images`, a real `UploadType` with no upload site, was also served to anyone authenticated.

**Fix.**
- `FileEndpoints.cs:38`: a `ServedUploadTypes` table. `ProfilePictures` maps to any authenticated user; `Documents` maps to Documents.Download plus `VisibleDocumentSpecification`.
- `:133`: an unlisted or missing prefix returns `false`, which the handler already reports as **404** (`:54`).
- `Images` is deliberately left unlisted, with a remark saying why.

**File map**

| File | Why |
|---|---|
| `src/Server.UI/Endpoints/FileEndpoints.cs` | Allow-list table, deny by default, and remarks. |
| `tests/Application.UnitTests/Endpoints/FileEndpointsAuthorizationTests.cs` | `AKeyUnderAnUnlistedPrefix_IsRefused_EvenWithEveryPermission` (`:251`), 5 cases: `Reports/x.pdf`, `Images/x.png`, `x.pdf`, `" Documents/…"`, `"Documents /…"`. |
| `tests/Server.UI.IntegrationTests/FileEndpointMatrixTests.cs` | Two rows: `AKeyUnderAnUnlistedPrefix_Is404_EvenThoughTheBytesExist` (`:186`) and `ADocumentKeyWithALeadingSpace_Is404_AndDoesNotSkipTheDocumentsRule` (`:197`). Real bytes are written under `Reports/`, because a row for an absent key is 404 under the old code too and would prove nothing. |

**Checks.** With the fix: unit tests 19/19 and matrix 10/10. Every existing Documents and avatar row stays green.

**Mutation:** the old `if (!string.Equals(firstSegment, "Documents"…)) return true;` restored.

```
Failed AKeyUnderAnUnlistedPrefix_IsRefused_EvenWithEveryPermission("Reports/x.pdf")   Expected ... to be False, but found True.
Failed AKeyUnderAnUnlistedPrefix_IsRefused_EvenWithEveryPermission("Images/x.png")    Expected ... to be False, but found True.
Failed AKeyUnderAnUnlistedPrefix_IsRefused_EvenWithEveryPermission("x.pdf")           Expected ... to be False, but found True.
Failed AKeyUnderAnUnlistedPrefix_IsRefused_EvenWithEveryPermission(" Documents/private-of-a.png")  ... found True.
Failed AKeyUnderAnUnlistedPrefix_IsRefused_EvenWithEveryPermission("Documents /private-of-a.png")  ... found True.
Failed!  - Failed: 5, Passed: 14, Total: 19 - Application.UnitTests
Failed ADocumentKeyWithALeadingSpace_Is404_AndDoesNotSkipTheDocumentsRule
   Expected response.StatusCode to be HttpStatusCode.NotFound {value: 404}, but found HttpStatusCode.OK {value: 200}.
Failed AKeyUnderAnUnlistedPrefix_Is404_EvenThoughTheBytesExist
   Expected response.StatusCode to be HttpStatusCode.NotFound {value: 404}, but found HttpStatusCode.OK {value: 200}.
Failed!  - Failed: 2, Passed: 8, Total: 10 - Server.UI.IntegrationTests
```

Restored. SHA-256 `df9c00af…13a4` matches the fixed file.

One mistake of mine, corrected before the mutation run: the new rows first asserted an **empty** body. A 404 from this app carries an HTML error page (`0x3C`), so they now assert the body is **not the stored bytes**.

---

## F2 — Application.IntegrationTests

**Finding.**
- `appsettings.json` pinned `mssql` to `(localdb)\mssqllocaldb`, with the databases `BlazorDashboard.Test` / `BlazorDashboard.Test_Logs`.
- Respawn used its default adapter, SQL Server.
- Without LocalDB the tests failed rather than skipped. The README called this deliberate (Known limitations).

**Delete or fix?** The project's 12 tests only partly overlap Application.UnitTests' SQLite-based picklist tests:
- It is the only suite that runs `AddInfrastructure` + `AddApplication` + Mediator in a plain container against a **server** database.
- It is also the only suite that runs `IDataSourceService<TenantDto>` / `<PicklistSetDto>` loading.

Fixing it cost about 60 lines. Deleting it is **not** clearly cheaper, so it was fixed.

**Fix.** In `Testing.cs`:
- It reads `GX_TEST_DBPROVIDER` / `GX_TEST_CONNECTIONSTRING` / `GX_TEST_LOGCONNECTIONSTRING` into `DatabaseSettings`.
- If the first two are unset, it calls `Assert.Ignore` (`:73`) with a message naming all three and warning that the database is emptied.
- `RespawnAdapterFor` (`:169`) chooses the Postgres or SqlServer adapter. For sqlite it calls Ignore, because Respawn has no SQLite adapter. For anything else it fails.
- Arms for providers that were not generated are conditioned (this part was done by the fork).

**File map**

| File | Why |
|---|---|
| `tests/Application.IntegrationTests/Testing.cs` | Env-var configuration, Ignore, Respawn adapter per provider. |
| `tests/Application.IntegrationTests/appsettings.json` | `DatabaseSettings` block removed, including LocalDB and `BlazorDashboard.Test`. |
| `README.md` | Known limitations paragraph removed. "Running the tests" and "Solution layout" document GX_TEST_*. |

**Checks**

| Variables | Result |
|---|---|
| unset | `Skipped! - Failed: 0, Passed: 0, Skipped: 12`, each with the message `OneTimeSetUp: Application.IntegrationTests need a database: set GX_TEST_DBPROVIDER … GX_TEST_CONNECTIONSTRING, and optionally GX_TEST_LOGCONNECTIONSTRING …` |
| postgresql → PG 18 on 5434 | Passed 12/12. |
| mssql → LocalDB | Passed 12/12. |

**Mutation:** the `Assert.Ignore` was disabled (`if (false)`). With the variables unset, the tests **fail** instead of skipping:

```
Failed AUserAskedForNoRoles_GetsNone
  Error Message: OneTimeSetUp: GX_TEST_DBPROVIDER='' is not a provider this suite knows: use postgresql or mssql.
Failed RunAsAdministratorAsync_ActuallyCreatesTheRoleAndAssignsIt   (same)
Failed RunAsAdministratorAsync_Succeeds                              (same)
```

Restored. SHA-256 `3d6d8d6b…64d9` matches the fixed file.

**Control:** the solution builds with 0 errors, both the template and all three generated providers.

---

## F3 — Docker removed

**File map**

| File | Why |
|---|---|
| `Dockerfile`, `docker-compose.yml`, `docker-compose.override.yml`, `docker-compose.dcproj`, `.dockerignore` | Deleted. |
| `launchSettings.json` (repo root) | Deleted. It held only the "Docker Compose" launch profile for the dcproj. It was not named in the brief. |
| `src/Server.UI/Server.UI.csproj` | `<DockerDefaultTargetOS>` removed. |
| `CleanArchitecture.Blazor.slnx` | `.dockerignore` and `Dockerfile` solution items removed. |
| `.gitattributes` | `Dockerfile`, `.dockerignore` and `*.dcproj` rules removed, with the comment adjusted. |
| `build/pack.csproj`, `README.md` | `.dockerignore` dropped from the list of dot-files NuGet would lose. The README had no Docker *section*; that was its only mention. |

**Check.** `git ls-files | xargs grep -il docker` (excluding pass reports) finds nothing. Build: 0 errors. `dotnet pack`: success. The smoke asserts that no Docker file or project property is generated.

---

## F4 — Wizard values reach the generated project

**Investigation**

**(a) Declarations.**
- `Database` (choice), `DatabaseName` (text, default `""`), `DefaultTimeZone` (text, default `UTC`) and `AllowSelfRegistration` (bool) are parameters.
- They reach `appsettings.json` through generated `replaces` symbols.
- `DatabaseName` falls back to `name` through a `coalesce`. I verified in a sandbox template that an **empty** value falls back to the project name.

**(b) Visual Studio.**
- `ide.host.json` lists all four with `isVisible: true`. The computed and generated symbols are not parameters, so VS never shows them. Nothing needed changing.
- `pack.csproj` already asserts that `ide.host.json` is in the package.
- I cannot drive the VS dialog; see the manual checks.

**(c) Exclusions.** There were none; see Disproven premise 3.

**(d) Database names.**
- `DbConnectionStringSetting` / `LogDbConnectionStringSetting` replaced `Host=localhost;Port=5432;Database=GXApplication…`.
- `appsettings.json:4,11` has said `Port=5434` since `b184f547` ("IdleTiemout", 2026-08-31).
- So the replace matched nothing and every project got `GXApplication` / `GXApplication_Logs`. Reproduced with the CLI at HEAD.

**Fix**
- `template.json:161,176`: the replace literals now match `Port=5434`. The generated value is still `Port=5432`, from `DbConnPrefix`.
- `template.json:68,73`: computed `UseSqlServer` / `UsePostgreSql`.
- `:218`: the `.slnx` conditional operation. `dotnet new` does not process `.slnx` comments without it; verified in a sandbox.
- `:243-259`: conditional excludes for `Migrators.MSSQL` and `Migrators.PostgreSQL`, and for the sqlite-only empty `SinkTimestampAcceptanceTests.cs`.
- `:284-286`: `tooling/`, `GXTemplate-passes/` and `Directory.Build.props` are excluded. **Before this pass, a folder install copied all 26 pass reports into every generated project.**
- `#if (UseSqlServer)` / `#if (UsePostgreSql)` markers in C#, and `<!--#if -->` markers in csproj/slnx.
- `Directory.Build.props` (template repo only) defines both symbols, so the template builds and tests every provider exactly as before.

**File map**

| File | Why |
|---|---|
| `.template.config/template.json` | See above. The Database description has been rewritten. |
| `Directory.Build.props` (new) | Defines `UseSqlServer;UsePostgreSql` for the template build only. Excluded from generation and the package. |
| `GX.Blazor.Template.nuspec` | Excludes `tooling\**` and `Directory.Build.props` from the package. |
| `CleanArchitecture.Blazor.slnx` | Migrator projects conditioned. |
| `src/Domain/Domain.csproj` | EF SqlServer/Npgsql, EF Exceptions SqlServer/PostgreSQL and EFCore.NamingConventions (used only under Npgsql) conditioned. |
| `src/Infrastructure/Infrastructure.csproj` | `Serilog.Sinks.MSSqlServer` and `Serilog.Sinks.Postgresql.Alternative` conditioned. |
| `src/Server.UI/Server.UI.csproj` | Migrator references conditioned. |
| `src/Infrastructure/DependencyInjection.cs` | Migration-assembly constants, `DatabaseProviders` arms and exception-processor arms conditioned. Validation reads this table, so a generated project **rejects** a provider it was not generated with. |
| `src/Infrastructure/Extensions/SerilogExtensions.cs` | Usings, sink selection, and the SQL Server and PostgreSQL sink members conditioned. |
| `src/Infrastructure/Persistence/Logging/LogDatabaseDdl.cs`, `LogDatabaseStartupCheck.cs`, `LogTableDdl.cs` | Per-provider arms, usings and DDL conditioned. |
| `src/Infrastructure/Persistence/Logging/LogDbContext.cs` | `Database.IsNpgsql()` table-name choice is conditioned, with a SystemLogs `#else`. |
| `tests/**` (16 files, done by a fork of this session, reviewed) | Provider-specific tests, cases and usings conditioned. |
| `tooling/smoke-generate.ps1` (new) | The smoke script. |
| `README.md` | Wizard options: provider section and table row. |

Details of the `tests/**` changes:
- `ModelMatchesMigrationsTests` lists only the generated providers, with one `HasPendingModelChanges` fact each.
- `LogDatabaseSeparationTests` polls with a budget derived from the slowest remaining sink: SQL Server 20 s; otherwise SQLite 10 s, read by reflection from Blazor.Serilog.Sinks.SQLite 1.1.0's `BatchProvider`; PostgreSQL's `DefaultPeriod` is 5 s.
- New `DatabaseSettingsValidationTests.AProviderThisProjectWasNotGeneratedWith_FailsValidation`, compiled only into generated projects that lack a provider.
- `SinkColumnDriftTests`: the four `provider == DbProviderKeys.Npgsql` branches became one conditioned `SpellsSnakeCase` helper. The smoke found them as residue.

**Checks**
- Template repo counts are unchanged; see Summary.
- The fork measured each generated project's tests with no GX_TEST_* set, 0 failures in every row:

| Provider | Application.UnitTests | Infrastructure.UnitTests | Server.UI.IntegrationTests |
|---|---|---|---|
| postgresql | 513 passed / 12 skipped | 207 / 0 | 254 / 0 |
| mssql | 509 / 12 | 201 / 0 | 254 / 0 |
| sqlite | 505 / 12 | 172 / 0 | 254 / 0 |

- `tooling/smoke-generate.ps1` packs, installs into `%TEMP%\gxsmoke-*\hive` and generates `SmokeApp`.
  - With `-Database postgresql` (defaults `--DefaultTimeZone Africa/Lagos --AllowSelfRegistration false`), the PostgreSQL run printed:

```
ok    no Migrators.MSSQL
ok    no mssql provider code in src/ or tests/
ok    Migrators.PostgreSQL present (chosen)
ok    Migrators.SqLite present (always: the test harnesses run on SQLite)
ok    DBProvider is postgresql / business database is named SmokeApp / log database is named SmokeApp_Logs
ok    DefaultTimeZone is Africa/Lagos / AllowSelfRegistration is false / no GXApplication database name
ok    0 errors
SMOKE PASSED (postgresql)
```
  - `-Database mssql` and `-Database sqlite` also end with **SMOKE PASSED**. Each generated solution builds with 0 errors.
  - The first run of all three failed on real residue plus two scanner problems. Both scanner problems are fixed:
    - `SqlConnection` matched inside `PostgreSqlConnection`.
    - `DatabaseSettingsValidationTests`' deliberate rejection cases are now a documented allowance.

**Mutation (a), as the brief asked:** the `Migrators.MSSQL` exclude modifier was removed.

```
FAIL  Migrators.MSSQL was generated although mssql was not chosen
FAIL  src\Migrators\Migrators.MSSQL\Migrators.MSSQL.csproj still references Migrators.MSSQL
ok    0 errors
SMOKE FAILED (postgresql): 2 failure(s)
```

The build alone stays green here, because the `.slnx` marker still drops the project. **Only the smoke catches the leftover migrator.** Restored; `template.json` SHA-256 `f812cec2…8b6c` matches the fixed file.

**Mutation (b):** the replace literal was set back to `Port=5432` (HEAD's state).

```
FAIL  DbConnectionStringSetting: its replaces literal 'Host=localhost;Port=5432;Database=GXApplication;...' occurs nowhere in the template content, so it replaces nothing
FAIL  LogDbConnectionStringSetting: ... occurs nowhere in the template content, so it replaces nothing
FAIL  business database is named SmokeApp - expected to find: Database=SmokeApp;
FAIL  log database is named SmokeApp_Logs - expected to find: Database=SmokeApp_Logs;
FAIL  appsettings.json still names the GXApplication database
SMOKE FAILED (postgresql): 5 failure(s)
```

Restored; SHA-256 matches as above.

**Mutation (new validation test),** in the generated PostgreSQL tree: the validator was widened with `.Append("mssql")`.

```
Failed AProviderThisProjectWasNotGeneratedWith_FailsValidation("mssql")
  Expected a <Microsoft.Extensions.Options.OptionsValidationException> to be thrown, but no exception was thrown.
Failed TheSupportedSetIsExactlyTheProvidersUseDatabaseCanDispatch   ... found one extraneous item at index 2: "mssql"
```

Restored: 10/10.

---

## F5 — sourceName over-replacement

**Finding.**
- The sourceName `CleanArchitecture.Blazor` is replaced everywhere, including comments.
- I searched every tracked file for occurrences that are not identifiers, and grepped the baseline-generated project for `SmokeApp`.
- There was **one** attribution hit: `src/Domain/Common/Entities/IAuditable.cs:19`, "divergence from upstream CleanArchitecture.Blazor".
- Every other occurrence is a real identifier that must follow the project name: namespaces, assembly names, `InternalsVisibleTo`, `/_content/<assembly>/…` in `MailPipelineTests.cs:56`, and the `MigrationsAssembly(...)` comment in `Infrastructure.UnitTests.csproj`.
- The README's lineage section names `neozhu/CleanArchitectureWithBlazorServer`, which does not contain the token.

**Fix.** The line now says "upstream neozhu/CleanArchitectureWithBlazorServer", which is also the more accurate name.

**File map**

| File | Why |
|---|---|
| `src/Domain/Common/Entities/IAuditable.cs` | Attribution names the upstream repository. |
| `tooling/smoke-generate.ps1` | Fails on any generated line matching `upstream|neozhu|Jason Taylor|CleanArchitectureWithBlazorServer` that contains the project name. |

**Control.** The smoke run prints `upstream attribution text does not carry 'SmokeApp' … ok none`.

**Mutation:** the old line was restored.

```
FAIL  src\Domain\Common\Entities\IAuditable.cs:19: /// the property. This is a deliberate divergence from upstream SmokeApp.
SMOKE FAILED (postgresql): 1 failure(s)
```

Restored; SHA-256 `f9144a27…d297` matches the fixed file.

---

## F6 — README Getting Started

I ran the steps verbatim, with custom hives, in a copy of the working tree under `%TEMP%\gxf6`, so that `cd ..` could not write into your folder.

| Step at HEAD | Result |
|---|---|
| `dotnet new install GX.Blazor.Template.1.0.0.nupkg` on a fresh clone | **Fails**: `could not be installed, the package does not exist`. The package is gitignored (`.gitignore:200`) and nothing said to pack it. |
| `dotnet new install .` then `dotnet new gxblazor -n IMS -o IMS` | Succeeds, but writes `IMS/` **inside the clone**. It is untracked and not ignored. The next generation from the folder install contained `IMS/`: `Second/` listed `IMS LICENSE README.md Second.slnx src tests`. The nuspec's `.\**` would also pack it. |

**Fix (README).**
- Step 1 now says to run from the clone root and to install one way only.
- The package route is `dotnet pack build/pack.csproj -o .` then `dotnet new install ./GX.Blazor.Template.1.0.0.nupkg`, with the output location stated.
- Step 2 is `cd ..` followed by `dotnet new gxblazor -n IMS -o IMS --Database postgresql`, with the reason given and `-o` explained.
- The maintainer's "install the built package" block also gains `cd ..`.
- A smoke-test paragraph was added under Packaging.

**Check (after), verbatim:**
- Package route: `Successfully created package …`, then `Template package contents verified …`. The install listed `gxblazor`, and generation succeeded. `appsettings.json` has `Database=IMS;` / `Database=IMS_Logs;`, and the Migrators are `PostgreSQL` and `SqLite`.
- Folder route: succeeded, `IMS/` is outside the template, and it contains no pass reports.

**File map:** `README.md` only.

---

## Manual VS checks for Yoab

**Before you start**
- Remove any older copy of the template: `dotnet new uninstall GX.Blazor.Template`, plus any folder registration, from both the CLI and VS.
- Then `dotnet pack build/pack.csproj -o .` and `dotnet new install ./GX.Blazor.Template.1.0.0.nupkg`.
- **Restart VS.** Its template cache is separate from the CLI's.

**Checks**
1. **File ▸ New ▸ Project** → search "GX Blazor". The template should be listed with its icon.
   - If it is missing, check that Tools ▸ Options ▸ Environment ▸ Preview Features ▸ "Show all .NET CLI templates in the New project dialog" is on (the wording varies by VS version).
2. **Configure your new project** page: name it (for example `IMS`), and use a **short location** such as `C:\src`, not a deep folder (MAX_PATH).
3. **Additional information** page. Four options should appear:

   | Option | Expected control |
   |---|---|
   | Database provider | Dropdown: PostgreSQL / SQL Server / SQLite |
   | Database name | Text box, empty |
   | Default time zone | Text box, pre-filled `UTC` |
   | Allow self-registration | Checkbox, ticked |

   Please tell me which of them appear and as what control.
   - I could not confirm from documentation whether VS renders **text** parameters. If "Database name" and "Default time zone" are missing, that explains a UTC result even when you intended otherwise.
   - These options have `persistenceScope: templateGroup`, so VS **remembers the last values used**. A previous run's `UTC` may be pre-filled.
4. Choose PostgreSQL, type `Africa/Lagos`, untick self-registration, and create the project. Then check:
   - No `src/Migrators/Migrators.MSSQL` folder and no MSSQL project in Solution Explorer. `Migrators.SqLite` **is expected**.
   - `src/Server.UI/appsettings.json` has `Database=IMS;`, `Database=IMS_Logs;`, `"DefaultTimeZone": "Africa/Lagos"` and `"AllowSelfRegistration": false`.
   - No `Dockerfile` or docker-compose items.
   - **Build ▸ Rebuild Solution** gives 0 errors. If Application.IntegrationTests fails again, **please send the Error List**; the `using static Testing;` failure did not reproduce from the CLI.
5. Test Explorer: Application.IntegrationTests should show **12 skipped**, with the GX_TEST_* message.

---

## Anything found but not fixed

- **Shared `UserSecretsId`.** `Server.UI.csproj` hard-codes `8118d19e-a6db-4446-bdb6-fa62b17f843d`. Every generated project therefore reads the **same** user-secrets store, so one project's secrets (connection strings, the Mailgun key) are visible to every other project on the machine. The fix is one line in `template.json`: `"guids": ["8118d19e-a6db-4446-bdb6-fa62b17f843d"]`. Left alone because it is outside this brief.
- **`template.json` excludes by deny-list.** A folder install copies anything new at the root, and `IMS/` and the pass reports both got in that way. An include-list (`src/**`, `tests/**`, the known root files) would fail closed. Recommended, not done.
- **Application.IntegrationTests in a sqlite-only project** can only ever skip, because Respawn has no SQLite adapter. It is honest, but it is noise; a `template.json` modifier could drop it for `sqlite`.
- **`Program.cs`'s Npgsql legacy-switch diagnostic** (`Program.cs:88-99`) ships in every project. It uses plain strings, has no package dependency, and prints `n/a (not PostgreSQL)` elsewhere. It was left unconditioned, and `ProcessWideStateTests` matches it.
- **`DbProviderKeys` keeps all three constants** in every project. Validation, not the constant, decides what is supported. The rejection test uses them.
- **The template's own `appsettings.json` uses `Port=5434`** (your local PostgreSQL 18). Generated projects get `5432`. The smoke's literal check now catches any future drift between the two.
- **Warnings are unchanged:** 19, including `NETSDK1206` for SQLite's `win7-*` RIDs.
