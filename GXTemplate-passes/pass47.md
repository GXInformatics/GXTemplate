# Pass 47 — PostgreSQL test infrastructure

**Date:** 2026-09-30. **Start:** HEAD `fe69fe1f` ("pass46"), clean tree.
**Commit:** one squashed commit, `pass47`. Not pushed.
**Scope:** pass 46 R4 "Pass 47", exactly: CO-143 to 150; CO-151's `UseSetting`, reset-before-boot, `configureServices` and no-SQLite-fallback parts; CO-152, 157 to 162, 164, 165, 166, 171, 42 and 192; F8; `build.yml`.
**Reference (read-only):** GX ProjectTracking (PT) `tests/TestSupport` and its pass reports `pass04a` to `pass04e`.

**Notation**
- "CO-n" is line *n* of `C:\Yoab\Projects\GXProjectTracking\docs\template-carry-over.md`, as in pass 46.
- Paths are repository-relative.
- No password appears in this report. The test server string was `Host=127.0.0.1;Port=5432;Username=postgres`, which has no password (see Disproven premise 1).

---

## Summary

1. **Every test suite runs on PostgreSQL, and no test uses SQLite or LocalDB.** There is a new framework-neutral library, `tests/TestSupport`, ported from PT with these parts:
   - `TestDatabaseGuard`: only `gx_test_` databases are accepted.
   - `TestDatabaseNames`: one database per assembly, named after the project.
   - `PostgresTestDatabase`:
     - takes the server from `GX_TEST_PG` alone;
     - creates the database when it is missing and never drops it;
     - migrates it once per run through the real migrations;
     - on a stale history, fails with the `psql` DROP command;
     - resets it with one children-first `DELETE` batch plus a restart of every sequence.
   - `ResetOrder` and `ApplicationModel`.
2. **Respawn is gone.** The three `GX_TEST_DBPROVIDER` / `GX_TEST_CONNECTIONSTRING` / `GX_TEST_LOGCONNECTIONSTRING` variables are retired. With `GX_TEST_PG` unset, every suite **fails** and names the variable; none skips (M3). The only skips are the 12 Azurite tests, plus the 2 opt-in create-and-drop tests when the server is set but the opt-in is not.
3. **Suites (final run):**

   | Suite | Before (SQLite, HEAD) | After (PostgreSQL) |
   |---|---|---|
   | Application.UnitTests | 523 passed + 12 skipped | **528 passed + 14 skipped** |
   | Infrastructure.UnitTests | 229 | **221** |
   | Application.IntegrationTests | 0 passed, **12 skipped** | **42 passed** |
   | Server.UI.IntegrationTests | 255 | **261** |

   Every suite passed on three runs with 0 failures and no test-host abort.
4. **Mutations.** 22 mutations were run in a separate `Mutation` configuration, plus M1's live scenario and the M3 and M4 scenarios.
   - Every load-bearing check (M1 to M4) and every new guard went red, except one: E17, `UseSetting`, which the template cannot observe yet (Disproven premise 8), so its test was removed.
   - E5 stayed green on its first run. The test was made deterministic, and its re-run (E5b) went red.
   - Every file was restored and checked byte for byte with `cmp`, the Mutation output was deleted (24 folders), and the solution was rebuilt with `--no-incremental` before the controls.
5. **Build:** 0 errors and **19 warnings** (18 before). The one extra is NETSDK1206 on the new TestSupport project, which inherits `Domain.csproj`'s SQLite reference; it goes in pass 48.
6. **`dotnet pack` succeeds.** **The smoke did not finish:** Claude Code stopped it because the machine was critically low on memory, and the stop notice says not to restart it unasked. Every step it reached passed except one false positive in a new check, since fixed (Controls). The remaining steps must be rerun on request.
7. **Two deviations you should know about:**
   - The suites ran on the **PostgreSQL 15.7 at 127.0.0.1:5432** (trust authentication), with your approval, not on PostgreSQL 18 at 5434, whose password was not available.
   - The two opt-in tests that drop a database were **not run**, because the rules forbid dropping databases (Disproven premise 6).

---

## Disproven premises

| # | Premise | What was found | What I did |
|---|---|---|---|
| 1 | "Local server: PostgreSQL 18 on localhost:5434" | `GX_TEST_PG` was not set; the 5434 instance requires scram (`pg_hba.conf`), and `%APPDATA%\postgresql\pgpass.conf` has an entry for 5433 only. | Asked. You chose the local **PostgreSQL 15.7 on 5432**, whose `pg_hba.conf` trusts 127.0.0.1. Every run in this report used it. **Nothing here was run on PostgreSQL 18.** |
| 2 | Each assembly's database is `_unit`, `_infra`, `_appint`, `_ui` or `_ui_logs` (five names) | The ported log tests need two more log databases, exactly as in PT: `SinkTimestampAcceptanceTests` and the lost-race test (`_unit_logs`), and `LogTablePostgresTests` (`_infra_logs`). | Added `TestDatabaseNames.UnitLogs` and `InfraLogs` (`tests/TestSupport/TestDatabaseNames.cs:31,37`). Both are under the same prefix and guard, created if missing and never dropped. |
| 3 | The grep control returns "only the documented Azurite skip" | CO-150, which is in scope, requires the create-and-drop tests to be *reported Skipped* without `GX_TEST_CREATE_DATABASES`. That gate is an `Assert.Ignore` (`tests/Application.UnitTests/Logging/LogDatabaseCreationAcceptanceTests.cs:66`, plus the comment at `:37`). A literal `grep -r` over `tests/` also reports 29 **binary** hits in `bin/`: built DLLs, including the SQLite package that `src/` still references. | Kept the gate. The source result is in Controls. The gate checks `GX_TEST_PG` first (`:102-104`), so an unset server fails these tests rather than skipping them (M3). |
| 4 | M1: "a `GX_TEST_PG` naming a database without the prefix → the guard refuses before connecting" | Any database named in `GX_TEST_PG` is refused by `PostgresTestDatabase.Create`'s server-only check (`tests/TestSupport/PostgresTestDatabase.cs:111-115`), so the name never reaches the prefix guard. The guard applies to each assembly's own name (`:118-123`). | Mutated both checks (M1a, M1b). M1's live scenario proves no connection is attempted. |
| 5 | Warnings are reported "against 18" | 19. The extra warning is NETSDK1206 on `tests/TestSupport/TestSupport.csproj`: "Affected libraries: SQLite", inherited through `src/Domain/Domain.csproj`. | Left for pass 48, which removes that reference. |
| 6 | "Never drop a database", and CO-150's opt-in tests "drop only names with their own prefix" | The two opt-in tests create a database (and a role) and drop them in `finally`. Running them here would drop databases. | Ported, with the drop confined to `gx_test_<project>_ldb_*` (`IsDroppable`, `:58`, which mutation E8 checks). **Not run locally**, so they report 2 skipped. CI sets `GX_TEST_CREATE_DATABASES=1`, but CI is disabled, so these two tests are **unverified** in this pass. |
| 7 | F8: `ShouldRequireValidKeyValueId` expects a `NotFoundException` | Awaited, the assertion would fail. `DeletePicklistSetCommandHandler` never throws for an unknown id: it deletes the rows it finds (`src/Application/Features/PicklistSets/Commands/Delete/DeletePicklistSetCommand.cs:40-60`). The test passed only because the assertion was never awaited. | Rewritten to assert what happens: `DeletingAnUnknownId_Succeeds_AndDeletesNothing` (`tests/Application.IntegrationTests/Picklist/Commands/DeletePicklistTests.cs:15`). |
| 8 | CO-151: apply test settings through `UseSetting` | Ported (`tests/Server.UI.IntegrationTests/GxWebApplicationFactory.cs:116`). But no template setting is read **while services are registered**: every read is inside an options lambda (`src/Infrastructure/DependencyInjection.cs:520,591,664`). A guard test through the Google options stayed green without `UseSetting` (E17), so it could not fail. | The vacuous test was removed. `UseSetting` stays. It becomes load-bearing when CO-10 (pass 49) makes external-login registration conditional; add its test then. |
| 9 | "The providers stay in `src` for this pass" | They do, but a project generated with `--Database mssql` or `sqlite` **no longer has a buildable test suite**: TestSupport needs `Migrators.PostgreSQL`, which generation excludes for those choices (`.template.config/template.json`, the `!UsePostgreSql` modifier). | Not worked around. The README says so (`README.md:219-222`). Pass 48 removes the option. The smoke's `-Database mssql` and `sqlite` variants were not run. |

---

## The changes, by carry-over item

### CO-143, 144: `TestDatabaseGuard` and `tests/TestSupport`
- **`tests/TestSupport/TestSupport.csproj` (new):**
  - `net10.0`, Nullable on, `IsTestProject=false`, and no test framework, so NUnit and xUnit both use it.
  - It references Infrastructure and `Migrators.PostgreSQL`.
  - It was added to `CleanArchitecture.Blazor.slnx:26` and to all four test projects.
- **`TestDatabaseGuard.cs:19`:** `RequiredPrefix = "gx_test_"`. `Refusal` (`:25-50`) is pure: it refuses no database, an unparseable string, and any name that does not start ordinally with the prefix.
- **`TestDatabaseNames.cs` (new, not in PT):**
  - `Project = "cleanarchitectureblazor"` (`:22`) is the template's source name, lower-cased and sanitised.
  - `ProjectPrefix`, and the seven names at `:25-46`.
- **`.template.config/template.json:119-146`:**
  - `TestDatabaseProjectLower` (the casing generator over `name`) feeds `TestDatabaseProject`.
  - That regex removes everything outside `[a-z0-9_]`, cuts the result to 40 characters, and `replaces` `cleanarchitectureblazor`.
  - So `SmokeApp` gives `gx_test_smokeapp_*`. The longest name, `_ui_logs`, stays within 63 bytes.

### CO-145: `PostgresTestDatabase` (`GX_TEST_PG`)
`tests/TestSupport/PostgresTestDatabase.cs`, ported from PT:
- `ServerVariable = "GX_TEST_PG"` (`:47`).
- An unset server fails naming the variable (`:94`). An unparseable one is refused. A server that names a database is refused (`:111-115`). Then the guard runs (`:118-123`). None of this connects.
- `EnsureReadyAsync` (`:132`) creates the database if missing (`EnsureExistsAsync`, `:186`, which also tolerates a concurrent `42P04` at `:204`, an addition to PT). It then refuses a stale history with `StaleHistoryMessage` (`:148`, `:268`), which prints host, port, user and the `psql DROP` command, and never a password. Finally it migrates with the application's own model (`ApplicationModel.NpgsqlOptions`, `ApplicationModel.cs:50`). `PendingModelChangesWarning` is not suppressed.
- It never drops a database.

### CO-146: the `DELETE`-batch reset, and Respawn removed
- **`BuildResetBatchAsync` (`:210-252`):**
  - reads the tables from `pg_catalog`, excluding `__EFMigrationsHistory` and `TBL_LK_` lookups;
  - reads the foreign keys;
  - orders the tables with `ResetOrder.ChildrenFirst` (`ResetOrder.cs:17`, which fails naming the tables on a true cycle);
  - uses `TRUNCATE` for a table whose own trigger refuses `DELETE` (`:251`);
  - appends `RestartSequences` (`:261`), which skips lookup sequences.
- **Lookup and trigger clauses:** no template table exercises them yet. So `MigrationModelTests.TheReset_KeepsLookupTables_AndTruncatesATableWhoseTriggerRefusesDelete` creates one probe table of each kind in `public` (tables, not a database), builds a fresh batch, asserts the outcome, and drops the probes.
- **Respawn:** removed from `tests/Application.IntegrationTests/Application.IntegrationTests.csproj`. `git grep -i respawn` outside `GXTemplate-passes/` returns nothing.

### CO-147: a database per assembly, serial execution
- **Application.UnitTests:** `UnitTestDatabase.cs:29` gives `gx_test_<project>_unit`. It is lazy, so a missing server fails only the database tests, and the assembly runs serially by NUnit's default.
- **Infrastructure.UnitTests:** `InfraTestDatabase.cs:22` gives `_infra` and `_infra_logs`. `[CollectionDefinition(Name, DisableParallelization = true)] PostgresCollection` (`:56`) joins the five database classes.
- **Application.IntegrationTests:** `Testing.cs:52` gives `_appint`, and `AssemblyInfo.cs` is unchanged (`NonParallelizable`).
- **Server.UI.IntegrationTests:** `UiTestDatabase.cs:11-12` carries `[assembly: NonParallelizable]` and `[assembly: LevelOfParallelism(1)]`. `UiTestDatabase.cs:32` names the databases (`_ui`, `_ui_logs`). `HarnessTests.TheWholeAssemblyRunsOneTestAtATime` (`HarnessTests.cs:28`) pins the serial rule (E14).

### CO-148: the SQLite classes moved onto the shared migrated database
- **Application.UnitTests (23 in-memory classes), converted mechanically:**
  - `new SqliteConnection(":memory:")` → `UnitTestDatabase.NewConnection()`;
  - `SqliteConnection` → `NpgsqlConnection`;
  - `UseSqlite` → `UseNpgsql`;
  - `EnsureCreatedAsync()` → `UnitTestDatabase.ResetAsync()`;
  - the `Microsoft.Data.Sqlite` using → `Npgsql`.

  The 23 classes are:
  - Interceptors: `InterceptorOrderingTests`, `SaveChangesInterceptorRegressionTests`, `ApplicationUserProjectionTests`, `PublisherAmbientContextTests`, `FileEndpointsAuthorizationTests`.
  - Tenant filters: `AuditTrailTenantFilterTests`, `DocumentTenantIsolationTests`, `GetFileStreamQueryHandlerTests`, `PicklistSetTenantFilterTests`.
  - Picklists and settings: `PicklistTenantUniquenessTests`, `SharedPicklistCreationTests`, `SharedPicklistWriteTests`, `InstallationPolicyWriteTests`.
  - Identity: `AdministratorProtectionTests`, `MustChangePasswordTests`, `PermissionAssignmentGuardTests`, `RoleDefinitionRightTests`, `UserContextAllowedTenantsTests`, `SwitchableTenantsTests`, `TenantSwitchAuthorizationTests`, `UserRoleChangeSecurityStampTests`, `UserTenantConsistencyTests`.
  - Provisioning: `ProvisioningTests`, which also lost an unused context (`:63-66`).

  Except for `SharedPicklistWriteTests` (CO-159), the result matches PT's own ported files line for line (checked with a normalised diff). The two Documents classes, which PT deleted, were converted the same way.
- **Application.UnitTests (2 file-database classes):** `TenantStampingTests` (`:48`) and `TransactionalAuditTests` (`:45`) are taken from PT. The template keeps Documents, so these two parts were restored:
  - the Document stamping test (`TenantStampingTests.cs:129`);
  - `typeof(Document)` in the schema list (`:215`).
- **Infrastructure.UnitTests:** `IdleTimeoutPolicyTests`, `PicklistDataSourceScopeTests`, `TenantVisibilityTests` and `UserVisibilityTests` moved to `_infra` in `PostgresCollection`. The model-only classes (`LogModelSeparationTests:30,34` and `GxTableNamingTests:91,191`) build on `UseNpgsql("Host=none")`. `SqliteFileCollection.cs` was deleted.
- **Server.UI.IntegrationTests:**
  - `RoleDefinitionComponentTests:117,126`, `UserDeactivationPermissionComponentTests:73` and `UserTenantScopeComponentTests:76` moved to `_ui`, reset at the start of each arrange, each context taking a connection string.
  - `ChangePasswordNavigationComponentTests:82` and `SuperiorBoundComponentTests:76` now use the ported in-memory Identity stores (`InMemoryUserStore.cs`, `InMemoryRoleStore.cs`, both new).

### CO-149: fail, never skip or fall back
- **Application.IntegrationTests:** `Testing.cs:68` calls `PostgresTestDatabase.FromEnvironment` in `[OneTimeSetUp]`. Both `Assert.Ignore`s, the provider switch and `EnsureDeleted` are gone.
- **Server.UI.IntegrationTests:** the factory has no SQLite fallback (`GxWebApplicationFactory.cs:79`).
- **Log acceptance tests:** `LogDatabaseCreationAcceptanceTests` and `SinkTimestampAcceptanceTests` lost their `CanConnect` fallbacks and their hard-coded 5433 server. The three LocalDB tests and the LocalDB sink test were deleted rather than converted (D1).

### CO-150: the `GX_TEST_CREATE_DATABASES` opt-in
In `LogDatabaseCreationAcceptanceTests.cs`, ported from PT:
- The variable is at `:46`. `OptedIn` is true only for `"1"`.
- `IsDroppable` (`:58`) allows only `gx_test_<project>_ldb_` names.
- The opt-in gate is at `:62-68`.
- The role name is not under the project prefix (`:140`), because 17 more bytes would pass 63.
- Adaptation: each opt-in test reads `GX_TEST_PG` **before** the gate (`:102-104`, and the same order in the second test), so a missing server fails rather than skips.

### CO-151: `GxWebApplicationFactory` (listed parts only)
- **Constructor (`:65-86`):** it takes `resetDatabase = true` (`:68`) and `configureServices`. It records `ConstructedAtUtc` before the reset (`:72`). It gets the databases from `UiTestDatabase` and resets before boot (`:79-82`).
- **`ConfigureWebHost`:** `ConfigureTestServices(_configureServices)` (`:111`), then `UseSetting` for every extra (`:116`; Disproven premise 8), then `DBProvider = Npgsql` (`:124`).
- **Removed:** the `DbProvider` property and the three environment-variable fallbacks.
- **Kept for later passes:** the guarded-environment defaults (pass 50) and the `IDatabaseLoginCheck` stub (pass 51) are not ported.
- **`ForcedPasswordChangeTests`:** the four nested hosts pass `resetDatabase: false` (`:176,258,292,314`). The new guard `ANestedHost_SharesTheFixturesInstallation_RatherThanProvisioningItsOwn` (`:237`) checks this (E16). The template's `/pages/documents` route case is kept.

### CO-152: `PostgreSqlCanaryTests`
`tests/Application.IntegrationTests/PostgreSqlCanaryTests.cs` (new) checks the provider name and that `SELECT version()` starts with "PostgreSQL". It reported `PostgreSQL 15.7`.

### CO-157: a real PostgreSQL log-table test
`tests/Infrastructure.UnitTests/Logging/LogTablePostgresTests.cs` (new, from PT) runs on `_infra_logs`:
- `TheDdlRunsTwice_AndCreatesTheShapeEfReadsAndTheSinkWrites` (`:59`) drops the log **table** and runs the DDL twice. It then checks the columns, `timestamptz`, the identity column and the three indexes.
- `TheSinkWritesTheAmbientTenant_IntoTenantId` (`:98`) uses the real `WriteTo.PostgreSQL` sink. It asserts `tenant_id = "tenant-round-trip"` inside a context and `NULL` outside one, replacing the SQLite-era "stays NULL" assertion.

Pointer comments were left in `LogTableDdlTests.cs:155` and `LogTenantStampingTests.cs:84`.

### CO-158: vacuous tests
- **`SinkColumnDriftTests`:**
  - `TheAcceptedSinkGaps_AreRealPropertiesAndReallyUnwritten` iterated an empty list for `mssql` and `postgresql`.
  - It now takes `ProvidersWithAcceptedGaps` (`:153`, SQLite only) and asserts `NotEmpty` first (`:217`); E11 checks it.
  - The two empty rows were removed.
- **`DeletePicklistTests.ShouldDeleteKeyValue`:**
  - It used to assert `FindAsync<Document>` is null, which is always true.
  - It now asserts `PicklistSet` exists before the delete and is gone after (`:39,45`).
  - E7 shows the handler mutation this catches.
- **`ServerHubTenantIsolationTests.AssertNothingSentTo`:** `mock?.Invocations.Should()` skipped its assertion when there was no proxy. It now asserts on `(mock?.Invocations.ToList() ?? [])` (`:569`). A group that was never addressed has no proxy and received nothing. This was not mutation-checked; see Found but not fixed.

### CO-159: no test depends on a fresh database
- **`CookieLoginTests:44`:** the bootstrap administrator's `CreatedAt` must be on or after the factory's `ConstructedAtUtc` (E15).
- **`LogDatabaseSeparationTests`:**
  - It is taken from PT: PostgreSQL catalogue only, and the sink period is the PostgreSQL default (`:49`).
  - New: `TheBusinessAndLogSettings_NameTwoDifferentDatabases` (`:161`), checked by E20.
- **`SharedPicklistWriteTests:62`:** generated ids are captured after seeding instead of explicit 1, 2 and 3, as in PT.
- **Quoted identifiers:** used in the raw SQL of `TenantStampingTests` and `TransactionalAuditTests`.
- **`pragma_*` checks:** replaced by EF-model assertions in `TenantColumnSchemaTests` (`TenantStampingTests.cs:204`) on `UseNpgsql("Host=none")`.

### CO-160: bUnit practice
- In-memory Identity stores where no database is needed (above).
- A connection string rather than a shared open connection:
  - `SaveChangesInterceptorRegressionTests:162` resets and uses `UnitTestDatabase.ConnectionString`, so each context disposes its own connection;
  - the three UI component classes do the same.
- `RoleDefinitionComponentTests.RenderPage` waits for the grid's first load (`:251-264`). Without the wait, PostgreSQL showed an empty grid and "a second operation was started on this context". That diagnosis is PT's (pass 4d); here the wait was ported, not reproduced.
- The dialog mock stays: PT keeps it too, and "stubs run `Action`" is CO-180 (pass 52).

### CO-161: `UseSerilog` with `ReadFrom.Services`
- **`src/Infrastructure/Extensions/SerilogExtensions.cs:36-41`:** the three-argument `UseSerilog` with `.ReadFrom.Services(services)`.
- **New guard:** `SerilogPipelineCaptureTests.ASinkRegisteredThroughTheFactory_SeesTheRealPipelinesEvents` (`tests/Server.UI.IntegrationTests/SerilogPipelineCaptureTests.cs:32`) registers a capturing `ILogEventSink` through `configureServices` and asserts that it receives enriched boot events. It covers both CO-161 (E18) and CO-151's hook (E19).

### CO-162: the real `IUserContextLoader`, `UseUser`, and no recursion
In `tests/Application.IntegrationTests/Testing.cs`:
- The ambient user comes from `IUserContextLoader.LoadAsync`, cache included (`:110-122`, `CurrentContext` at `:132-145`).
- `UseUser(string?)` is at `:155`.
- An `AsyncLocal` re-entry guard (`LoadingContext`, `:49`) returns "nobody" while a load is in progress. It is not load-bearing today, because the loader's query reads no filtered entity (see Found but not fixed).

### CO-164: clearing the ambient user after a reset
- `ResetIsolationTests` (`Harness/SharedDatabaseTests.cs`) resets as nobody. `TheAmbientUser_IsNobody_AfterTheReset` arranges a signed-in user whose context is cached, resets and writes.
- E5b shows that without `UseUser(null)` the write carries the deleted user's id and fails on `FK_AuditTrails_AspNetUsers_UserId`.

### CO-165: a settable `TestClock`
- `tests/Application.IntegrationTests/TestClock.cs` (new) is registered **scoped**, as the application registers `IDateTime`, over one instance (`Testing.cs:127-128`). `ResetState` resets it (`:240`).
- Guarded by `Harness/TestClockTests.cs` (E6).
- The UI-harness clock is not ported (Found but not fixed).

### CO-166: the core-schema test that broke on a project's first table
In `tests/Infrastructure.UnitTests/Persistence/GxTableNamingTests.cs`:
- `TemplateEntityTypes` (`:193-199`) lists the template's 15 entity types.
- `NoTemplateEntity_IsMappedIntoTheCoreSchema` (`:223`) no longer requires core to be empty; it requires none of those types to be in core (E10).
- New: `EveryTableOutsideCore_IsOneOfTheTemplates` (`:240`), so a project's table must be in core and a new template table must be listed (E9).

### CO-171: `IHostEnvironment` in test service collections
`Testing.cs:97` registers `IHostEnvironment` over the mocked `IWebHostEnvironment`. The other `AddInfrastructure` caller, `LogDatabaseCreationAcceptanceTests`, uses `HostBuilder`, which already provides one.

### CO-42: no passwords in committed test connection strings
Removed from:
- `LogDatabaseCreationAcceptanceTests` (5433, `postgres/postgres`);
- `SinkTimestampAcceptanceTests` (the same);
- `ModelMatchesMigrationsTests.cs:54` (a dummy password, `gx`);
- `LogDatabaseDdlTests.cs:131,152` (a dummy password, `secret`, twice);
- `.github/workflows/build.yml` (the `sa` password; see below).

### CO-192: `<Nullable>enable</Nullable>` in the test projects
- Added to `Application.UnitTests.csproj` and `Application.IntegrationTests.csproj`.
- One new warning, CS8602 at `AddEditPicklistCommandTests.cs:43`, was fixed by asserting non-null first.
- Harness types were annotated (`FindAsync<TEntity>` returns `TEntity?`).

### F8
See Disproven premise 7.

### `build.yml`
`.github/workflows/build.yml`:
- no service container and no Docker;
- starts the runner's preinstalled PostgreSQL;
- creates the login `gx_ci` (`CREATEDB CREATEROLE`) with a password generated per run by `openssl rand` and masked with `::add-mask::`;
- exports `GX_TEST_PG` and `GX_TEST_CREATE_DATABASES=1` through `$GITHUB_ENV`.

Actions is disabled for this repository, and `.github/**` is not generated, so **this was not run**.

### Supporting changes
- **`tooling/smoke-generate.ps1`:**
  - a `-TestServer` parameter, defaulting to `GX_TEST_PG` (`:75`);
  - "no test project uses SQLite" (code only, `:280-287`);
  - "the test databases are named after the project" (`:288-297`);
  - "without `GX_TEST_PG` the generated suites fail loud" (`:448`);
  - "the four suites pass against the server" (`:459`);
  - the description text updated.
- **`README.md`:**
  - "Running the tests" rewritten (`:1113-1146`);
  - the `--Database` bullet says that no test uses SQLite (`:219-222`);
  - the migration note says ModelMatchesMigrations no longer checks the SQLite chain;
  - the tree and harness lines corrected.
- **`tests/Application.IntegrationTests/appsettings.json:2-4`:** a comment naming `GX_TEST_PG`.

---

## Checks

Method (`scratchpad/mutate.sh`), for each mutation:
1. Back up the file and apply one `perl` substitution, printing the diff to prove it applied.
2. Build the test project with `-c Mutation`.
3. Run the named filter against `GX_TEST_PG`.
4. Restore the file and `cmp` it against the backup.

Afterwards, 24 `bin/Mutation` and `obj/Mutation` folders were deleted, and the solution was rebuilt with `--no-incremental` (0 errors) before the controls. Outputs are trimmed to the failing names and the first assertion.

### Load-bearing (pass 46 R4)

**M1 (live scenario): `GX_TEST_PG` names a database on an unroutable host**
- `GX_TEST_PG=Host=192.0.2.1;Port=5432;Username=postgres;Database=GXTemplateDatabase;Timeout=15`
- Result: `Failed! - Failed: 23, Passed: 19, Total: 42` in **1.7 s** of wall-clock, against a 15 s connect timeout.
- Message: `GX_TEST_PG names a database ('GXTemplateDatabase').`, with 0 timeout or connection messages.
- The 19 passes are the pure harness tests, outside the `[SetUpFixture]` namespace.

**M1a: the server-only check disabled**
```
<         if (!string.IsNullOrEmpty(server.Database))
>         if (false && !string.IsNullOrEmpty(server.Database))
    Failed AServerThatNamesADatabase_IsRefused
   Expected a <System.InvalidOperationException> to be thrown, but no exception was thrown.
    restored: dbea63f9fee1 (cmp identical)
```

**M1b: the prefix guard disabled**
```
<         if (!database.StartsWith(RequiredPrefix, StringComparison.Ordinal))
>         if (false)
    Failed ADatabaseNameTheGuardRefuses_IsRefused / the maintenance database / the prefix in the middle /
           the prefix in another case / TheApplicationsOwnDatabase_IsRefused
   Expected Refusal(connectionString) not to be <null>.
    restored: 1027647e8540 (cmp identical)
```

**M2: one table (`PicklistSets`) left out of the reset batch**
```
<  return string.Concat(ResetOrder.ChildrenFirst(tables, foreignKeys)
>  return string.Concat(ResetOrder.ChildrenFirst(tables.Where(t => t != "public.\"PicklistSets\"").ToList(), foreignKeys)
    Failed TheNextTest_StartsWithoutIt / AReset_RestartsGeneratedIds / TheAmbientUser_IsNobody_AfterTheReset
   Expected (CountAsync<PicklistSet>()) to be 0, but found 1 (difference of 1).
   UniqueConstraintException ... ConstraintName: PK_PicklistSets
    restored: dbea63f9fee1 (cmp identical)
```
`PicklistSets` references nothing, so it is the table the isolation test writes rather than a "child". A child left behind would fail the batch itself on a foreign key; E4 shows that failure mode.

**M3: `GX_TEST_PG` unset (no code change; normal verbosity)**

| Suite | Result | Failures naming `GX_TEST_PG` | Skipped |
|---|---|---|---|
| Application.UnitTests | Failed! 247 failed, 283 passed | 247 | 12 (Azurite only) |
| Infrastructure.UnitTests | Failed! 34 failed, 187 passed | 34 | 0 |
| Application.IntegrationTests | Failed! 23 failed, 19 passed | 23 (+1 `OneTimeSetUp` line) | 0 |
| Server.UI.IntegrationTests | Failed! 117 failed, 144 passed | 117 | 0 |

- There were no test-host aborts.
- The two opt-in tests **failed** here (`GX_TEST_PG is not set`); they did not skip.
- Every pass is a test that needs no database.

**M4: tampered `__EFMigrationsHistory`**
- A row `20990101000000_TamperedByPass47` was inserted into `gx_test_cleanarchitectureblazor_appint`.
- The suite ran with `GX_TEST_PG` carrying a **decoy** `Password=M4-decoy-not-a-real-password`, which the trust server ignores.

```
OneTimeSetUp: SetUp : System.InvalidOperationException : The test database 'gx_test_cleanarchitectureblazor_appint' has a stale migration history: it records 20990101000000_TamperedByPass47, which this assembly no longer defines (expected after regenerating InitialCreate). The harness never drops a database, so drop it by hand and run the suite again; the next run recreates it:
psql -h 127.0.0.1 -p 5432 -U postgres -d postgres -c "DROP DATABASE gx_test_cleanarchitectureblazor_appint WITH (FORCE)"
decoy password occurrences in output: 0
Password= occurrences: 0
```
The row was then deleted (`DELETE 1`), and the suite passed again.

### The new guards

| # | Mutation (diff applied) | Went red | First assertion |
|---|---|---|---|
| E1 | `+ RestartSequences;` → `+ "";` | `AReset_RestartsGeneratedIds` | "Expected first.Id to be 1 … but found 3." |
| E2 | lookup exclusion removed from the table query | `TheReset_KeepsLookupTables_AndTruncates…` | "Did not expect fresh.ResetBatch … to contain TBL_LK_HARNESS_PROBE" |
| E3 | `guarded.Contains(t) ?` → `false ?` | same test | "Expected fresh.ResetBatch … to contain TRUNCATE public.harness_guarded_probe;" |
| E4 | `ordered.Add(next)` → `ordered.Insert(0, next)` | `AResetOverAParentAndItsChild_…`, `ASelfReference_…`, `EveryTableComesBefore…` | "23503: update or delete on table "Tenants" violates foreign key constraint "FK_AspNetUsers_Tenants_TenantId"" |
| E5 | `UseUser(null);` removed from the ResetIsolation setup | **none: Passed!** | order-dependent; see E5b |
| E5b | same mutation, after the test arranges its own cached, deleted user | `TheAmbientUser_IsNobody_AfterTheReset` | "ReferenceConstraintException … FK_AuditTrails_AspNetUsers_UserId" |
| E6 | the `TestClock` registration removed (`Testing.cs:127-128`) | `ASetClock_IsTheTimeTheApplicationStamps` | "Expected row.CreatedAt to be <2026-01-02 03:04:05> … but found <2026-09-30 21:48:08…>" |
| E7 | `db.PicklistSets.RemoveRange(items);` removed (src) | `ShouldDeleteKeyValue` | "Expected item to be <null>, but found … PicklistSet". Before CO-158 this passed. |
| E8 | `IsDroppable` widened to the project prefix | `OnlyTheDatabasesTheseTestsCreate_MayBeDropped` ×3 (`_unit`, `_unit_logs`, `_appint`) | "Expected IsDroppable(name) to be False, but found True." |
| E9 | `typeof(SecurityPolicy)` removed from `TemplateEntityTypes` | `EveryTableOutsideCore_IsOneOfTheTemplates` | "Collection: ["SecurityPolicy"]" |
| E10 | `builder.ToTable("PicklistSets");` removed (src) | `ATemplateTable_KeepsItsName…(PicklistSet)`, `NoTemplateEntity_IsMappedIntoTheCoreSchema` | "Collection: ["PicklistSet"]" |
| E11 | `ProvidersWithAcceptedGaps` + `Npgsql` | `TheAcceptedSinkGaps_…(provider: "postgresql")` | "Assert.NotEmpty() Failure: Collection was empty" |
| E12 | `IF NOT EXISTS` removed from `ix_system_logs_level` (src `LogTableDdl.cs:294`) | both `LogTablePostgresTests` | "42P07: relation "ix_system_logs_level" already exists" |
| E13 | the `tenant_id` writer commented out (src `SerilogExtensions.cs:368`) | `TheSinkWritesTheAmbientTenant_IntoTenantId` | "Expected: "tenant-round-trip"" |
| E14 | `[assembly: LevelOfParallelism(1)]` removed | `TheWholeAssemblyRunsOneTestAtATime` | "Expected level not to be <null> because the assembly must cap NUnit at one worker." |
| E15 | `resetDatabase = true` → `false` (default) | `TheBootstrapProvisionsAnAdministrator_EvenInProduction` | "Expected _administrator.CreatedAt to be on or after <…21:49:05.72> … but found <…21:49:00.39>." |
| E16 | `, resetDatabase: false` removed from the nested hosts | `ANestedHost_SharesTheFixturesInstallation_…` | "Expected administrator.Id to be a match with the expectation …" |
| E17 | `builder.UseSetting(...)` removed | **none: Passed!** | Disproven premise 8; the test was removed |
| E18 | `.ReadFrom.Services(services)` removed (src) | `ASinkRegisteredThroughTheFactory_…` | "Expected sink.Events not to be empty …" |
| E19 | the `ConfigureTestServices(_configureServices)` line removed | same test | same |
| E20 | log settings pointed at the business database | `TheBusinessAndLogSettings_NameTwoDifferentDatabases`, `TheBusinessDatabase_HasNoSystemLogsTable`, `ThatSameMessage_IsNowhereInTheBusinessDatabase` | "Expected logs not to be "gx_test_cleanarchitectureblazor_ui" …" |

- **E20 side effect:** it created one **table**, `public.system_logs`, in `gx_test_cleanarchitectureblazor_ui`. I dropped that table with `DROP TABLE IF EXISTS public.system_logs`, not a database, and `LogDatabaseSeparationTests` passed again.
- **Restore checks:** every mutation line ended "restored … (cmp identical)"; the hashes are in `scratchpad/batch-out.txt`.

---

## Controls

**Build:** 0 errors and 19 warnings with `--no-incremental` (18 at pass 46).
- The 10 code warnings are unchanged (`DescriptionAttributeExtensions` ×4, `MapsterConfiguration` ×2, `TenantSelect`, `AuditTrails`, `Dashboard`, `MudDateTimeField`).
- NETSDK1206 went from 8 to 9 (+TestSupport; Disproven premise 5).

**Suites against `GX_TEST_PG` (PostgreSQL 15.7):** dotnet's summary line, with the wall-clock time of each `dotnet test --no-build`.

| Suite | HEAD on SQLite | Run 1 | Run 2 | Final (after all mutations) |
|---|---|---|---|---|
| Application.UnitTests | 523 + 12 skipped · 29.1 s | 528 + 14 skipped · 20.9 s | 528 + 14 · 20.1 s | 528 + 14 · 22.4 s |
| Infrastructure.UnitTests | 229 · 3.3 s | 221 · 3.9 s | 221 · 3.7 s | 221 · 4.5 s |
| Application.IntegrationTests | 0 + 12 skipped · 1.2 s | 40 · 8.7 s | 40 · 8.0 s | 42 · 9.6 s |
| Server.UI.IntegrationTests | 255 · 53.8 s | 261 · 49.9 s | 261 · 49.9 s | 261 · 81.8 s |

- There was no test-host abort in any run.
- Runs 1 and 2 predate `TestClockTests` (+2 in Application.IntegrationTests). The final Server.UI time was measured while the machine was short of memory (Summary 6); runs 1 and 2 are the representative figures.
- Per-suite count changes are listed under Found but not fixed §1.

**Grep control** (`grep -r "UseSqlite\|SqliteConnection\|Assert.Ignore\|CanConnect" tests/`, text files):
```
tests/Application.UnitTests/Logging/LogDatabaseCreationAcceptanceTests.cs:/// are reported as SKIPPED (<c>Assert.Ignore</c>, never a pass). ...
tests/Application.UnitTests/Logging/LogDatabaseCreationAcceptanceTests.cs:            Assert.Ignore($"Creates and drops a database or role on the GX_TEST_PG server; ...
tests/Application.UnitTests/Storage/AzureBlobFileStorageTests.cs:            Assert.Ignore($"Azurite is not listening on 127.0.0.1:{AzuriteBlobPort}; ...
```
- These are the Azurite skip and the CO-150 opt-in gate (Disproven premise 3).
- There were also 29 "Binary file … matches" lines under `bin/`.

**Respawn:** removed. `git grep -i respawn` outside `GXTemplate-passes/` is empty.

**`dotnet pack build/pack.csproj`:** succeeded. The package contains `content/tests/TestSupport/*` (6 files).

**`tooling/smoke-generate.ps1`: NOT COMPLETED.**
- Claude Code stopped it because the machine was critically low on memory, and its notice says not to restart it unasked. Eleven idle MSBuild and compiler nodes (about 1.3 GB) were shut down afterwards.
- **Passed before the stop:**
  - every `replaces` literal, including `TestDatabaseProject`;
  - pack and install;
  - generation with the template defaults;
  - `TestDatabaseNames.Project is 'smokeapp'` and no `cleanarchitectureblazor` token left;
  - the appsettings, IIS, UserSecretsId, Docker and attribution checks;
  - the generated solution built with **0 errors**;
  - **without `GX_TEST_PG`**, all four generated suites were `Failed!` and named the variable (247/283/12 skipped, 34/166, 23/19, 117/144);
  - **with the server**, `SmokeApp.Application.UnitTests` passed 528, with 14 skipped.
- **Not reached:** the other three suites against the server, and the gitignore step.
- **One FAIL:** the new "no test project uses SQLite" check matched a **comment** (`LogDatabaseDdlTests.cs:228`, "// Microsoft.Data.Sqlite creates the file on Open()"). The check now ignores comment lines (`smoke-generate.ps1:282-285`). The corrected pattern was run over the generated SmokeApp's 152 test files: 1 raw hit, 0 code hits.
- **To finish the control, run:** `powershell -ExecutionPolicy Bypass -File tooling\smoke-generate.ps1 -TestServer "Host=127.0.0.1;Port=5432;Username=postgres"`.

**Databases created on the test server (127.0.0.1:5432; none existed before, none dropped):**
```
gx_test_cleanarchitectureblazor_appint
gx_test_cleanarchitectureblazor_infra
gx_test_cleanarchitectureblazor_infra_logs
gx_test_cleanarchitectureblazor_ui
gx_test_cleanarchitectureblazor_ui_logs
gx_test_cleanarchitectureblazor_unit
gx_test_cleanarchitectureblazor_unit_logs
gx_test_smokeapp_infra          (by the interrupted smoke)
gx_test_smokeapp_unit           (by the interrupted smoke)
gx_test_smokeapp_unit_logs      (by the interrupted smoke)
```
No `gx_test%` role exists. The 80 databases on that server before this pass are all still present.

**Temporary folders:** my own folders were removed: `gxsmoke-baa792`, the HEAD export `gxh47`, `gxpk47`, and the 38 SQLite-era `gx-http-tests` folders my HEAD baseline created. The new harness leaves none.

---

## Found but not fixed

1. **Removed and rewritten tests.**
   - **Application.UnitTests** (−5 +12). Removed:
     - `OnSqlServer_TheLogDatabaseIsCreatedWhenAbsent_…`, `OnSqlServer_ALoginWithoutDbcreator_…`, `OnSqlServer_ALostRaceIsRecognisedAsAlreadyExisting`, `TheSqlServerSink_RecordsTimestampsInUtc`: LocalDB (D1).
     - `OnSqlite_AConfiguredPathInAMissingDirectory_NowWorks`: SQLite.

     Added: `OnlyTheValueOne_OptsIn` ×5 and `OnlyTheDatabasesTheseTestsCreate_MayBeDropped` ×7. The two tenant-schema tests were rewritten from `pragma_*` to the EF model and moved to `TenantColumnSchemaTests`, keeping their names.
   - **Infrastructure.UnitTests** (−12 +4). Removed, all SQLite:
     - `LogTableDdlTests.TheDdlNamesTheSameTableTheModelReads_OnSqlite`, `…OnSqlite_TheDdlRuns_IsIdempotent_…`, `…OnSqlite_TheExistenceQueryAnswersFalseThenTrue`;
     - `LogTenantStampingTests.OnSqlite_TheRowLands_…`, `…OnSqlite_AnEventWithNoAmbientContext_AlsoLands`;
     - `SinkTimestampTests.TheSinkRecordsTimestampsInUtc`, `…TheSinkWritesIntoTheTableTheApplicationCreated`;
     - `LogTableNamingTests.OnSqlite_TheModelReadsSystemLogs`;
     - `ModelMatchesMigrationsTests.TheSqliteMigrationsMatchTheModel`.

     Also removed: `PublishedNotificationLogTenantTests.ARowWrittenByAHandler…`, rewritten as `AnEventWrittenByAHandler…` on a capturing sink, as in PT; and `SinkColumnDriftTests.TheAcceptedSinkGaps_…("mssql"/"postgresql")`, vacuous (CO-158).

     Added: the 2 `LogTablePostgresTests`, `AnEventWrittenByAHandler…` and `EveryTableOutsideCore_IsOneOfTheTemplates`.
   - **Application.IntegrationTests** (12 → 42): `ShouldRequireValidKeyValueId` was rewritten (F8), and 30 were added (harness, canary, clock).
   - **Server.UI.IntegrationTests** (255 → 261): nothing removed. Added: 3 `HarnessTests`, `ANestedHost_…`, `TheBusinessAndLogSettings_…` and `ASinkRegisteredThroughTheFactory_…`.
2. **The smoke must be rerun to complete its control** (Controls).
3. **PostgreSQL 18 on 5434 was not exercised.** The same run there needs its credentials, for example as a pgpass line `localhost:5434:*:postgres:<password>` with `GX_TEST_PG=Host=localhost;Port=5434;Username=postgres`.
4. **The two opt-in create-and-drop tests and `build.yml` are unverified** (Disproven premise 6; Actions is disabled).
5. **`ModelMatchesMigrations` no longer checks the SQLite migration chain.** `Migrators.SqLite` still ships in `src/` and is unguarded until pass 48 deletes it. The SQL Server leg stays: it builds a model and never connects.
6. **Pure provider-branch tests remain** for SQLite and SQL Server code that is still in `src/`, such as the `LogDatabaseDdlTests` SQLite facts, the `SinkColumnDrift` SQLite rows and `DatabaseSettingsValidationTests`. They open nothing, and pass 48 removes them with the code.
7. **The `Testing.cs` re-entry guard (`LoadingContext`) is not load-bearing today.** `UserContextLoader` queries `Users → TenantUsers → Tenant`, none of which is filtered by the ambient user. It becomes load-bearing the day a loader query reads a filtered entity; PT's pass 6 E4 crashed the test host without it.
8. **The CO-158 `ServerHubTenantIsolationTests` rewrite was not mutation-checked.** No cheap production mutation reaches only that assertion.
9. **The CO-165 "UI harness clock that can cross midnight during a load" was not ported.** No template UI test needs it; it arrives with the first date-bound page.
10. **About 609 `%TEMP%\gx-http-tests` folders** from earlier passes' SQLite runs remain. They predate this pass and were left alone.
11. **Stale `CleanArchitecture.Blazor.Migrators.SqLite.dll` copies** stay in `tests/Infrastructure.UnitTests/bin/Debug`, because `dotnet build` never deletes them (CO-193). They are harmless and go when the output is cleaned.
