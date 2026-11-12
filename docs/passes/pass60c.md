# Pass 60c: the GX conventions restored, and 1.8.0

**Date:** 2026-11-12.
**Plan:** `docs/passes/pass60c-gx-conventions-plan.md`. It was amended with the owner's decisions (§9.0) and amendment G1 (§3.10), then implemented in its order of work.
**Commit:** `28c8051f` "Pass60c-GxConventions and 1.8.0", tag `gx-v1.8.0` (on `28c8051f`). This report follows in a separate commit, because it quotes that hash and the smoke result.
**Authority:** `docs/gx-conventions.md` and `docs/passes/conventions-audit.md`.
**Database:** PostgreSQL 15.7 at 127.0.0.1:5432 via `GX_TEST_PG`.

## Suites

Each project was run alone, with 0 failed. **Build:** 0 errors.

| Suite | Before (1.7.1, `368de373`) | After |
|---|---|---|
| Application.UnitTests | 623 passed, 14 skipped | **638 passed, 14 skipped** |
| Infrastructure.UnitTests | 239 | **361** |
| Application.IntegrationTests | 155 | **160** |
| Server.UI.IntegrationTests | 359 | **359** |

The pinned suites pass **unmodified**: `SaveChangesInterceptorRegressionTests`, `InterceptorOrderingTests`, `TransactionalAuditTests` and the publisher suites. This pass's diff touches none of them.

Some tests were removed or renamed:

- Removed: the two module-schema rows of `ModuleGuardTests` (shared schema; invalid or reserved schema), because `ModuleDefinition.Schema` is gone.
- Renamed: `ModuleSchemaTests` is now `CoreSchemaTests`.

**Shipped migrations:** none changed. `git diff 368de373 -- src/Migrators` is empty, because the template has no business table and its own tables keep their names, columns and schema. `ModelMatchesMigrationsTests` passes for all three providers.

## 1. What was built

### D1 and D2: the `core` schema

- `GxNamingConventions.BusinessSchema = "core"` is back.
  - Every `IBusinessEntity` with a table of its own goes to `core`, module or not.
  - Joins go to `core` when either side is in it.
  - A table named by hand with no schema stays where it was put, as before pass 60.
- `ModuleDefinition.Schema`, `ReservedSchemas` and `SchemaProblem` are removed.
- `DataModelGuard`:
  - the schema checks are gone;
  - a new rule refuses a module entity whose table resolves to `public` (Q-B);
  - `CheckTableNames` is kept, because it still catches a table named by hand into another schema.
- Template tables are unchanged in `public`.

### D3: plural table names

- `GxNamingConventions.Pluralise` and `TableNameFor` apply fixed rules to the last word. They use two fixed lists: 22 irregular words, and 34 words left unchanged.
- `[PluralName("…")]` overrides the rules. One that is not a PascalCase identifier fails, and so does a name ending in a digit.
- Joins pluralise their last word (Q-C).
- **The table list (Q-D):**
  - `ModelTableListTests` compares the real model with `tests/Application.UnitTests/Model/tables.approved.txt`, one line per table: `schema.table <- Entity [Module]`;
  - a mismatch prints `+` and `-` lines and the approve command (`GX_APPROVE_TABLES=1`);
  - a planted module entity is tested to appear as one new line.

### D4: lookups

- `BaseLkEntity` and `BaseVersionedLkEntity` carry `Name` and `Slug`. `ILookupEntity` declares `int Id`, `Name` and `Slug`, so a class without them does not compile.
- `DataModelGuard`'s lookup rule refuses:
  - a key other than one `int` `Id`;
  - `Name` or `Slug` unmapped;
  - a nullable `Name`;
  - a decimal column (Q-A2);
  - a table outside `core."TBL_LK_…"`.
- `LookupMigrationExtensions.SeedLookup` (explicit ids into `id`, `name`, `slug`), `ResetIdentitySequence` and `Slug`.
- The README states the Q-A1 definition: "a reference list that users pick from".

### G1: the test reset restores lookups

`PostgresTestDatabase` works in three steps:

1. **Once per run, before migrating,** it runs a full reset that also restores every snapshotted lookup. This undoes what the previous run's last test left.
2. **After migrating,** it snapshots every `TBL_LK_` table into `gx_test_seed."<schema>.<table>"`. It records each identity sequence's `last_value` and `is_called`, and puts a statement-level dirty trigger on each lookup.
3. **Each reset** appends one `DO` block to the existing single round trip. It restores only lookups that are dirty, or that reference a dirty lookup:
   - delete children-first;
   - insert parents-first with `OVERRIDING SYSTEM VALUE`;
   - `setval` each sequence back;
   - clear the dirty set.

**Cost:**
- a test that writes no lookup: one read of an empty table;
- a test that writes one: one `DELETE` and one `INSERT … SELECT` of that lookup's seeded rows;
- the snapshot: one `CREATE TABLE AS` per lookup per run.

Tests G1-a to G1-d are in `ResetIsolationTests` (`tests/Application.IntegrationTests/Harness/SharedDatabaseTests.cs`).

### D5: snake_case on business tables only

- `GxSnakeCaseConvention` is an `IModelFinalizingConvention` registered in `ApplicationDbContext.ConfigureConventions`. It visits business entity types only:
  - `IBusinessEntity` types not in `TemplateEntities`;
  - the types they own;
  - joins with a business side.
- **Names.** Columns use the plug-in's rewrite, character for character. Keys, foreign keys and indexes take EF's default name in lower case. Anything set by hand is kept.
- **Hand-named constraints (Q-E).** A unique index named by hand must start `ux_tbl_`, and a check constraint `ck_tbl_`; otherwise the model build fails, naming each one.
- `GxSql.Column(nameof(X.Prop))` names a column in SQL written by hand.
- Identity, the template's tables and `__EFMigrationsHistory` are not touched. A test pins all three, the history's `"MigrationId"` / `"ProductVersion"` included.

### D6: integer keys

- **The approval contracts are `long`:** `IApprovable.Id`, `IApprovalCommand.DocumentId`, `ApprovalGrant` and `ApprovalBehaviour`'s loader.
- **`DataModelGuard`'s key rule (Q-I):** on application entities, a Guid, string or composite key fails. Postable, line, append-only and approvable entities must be `long`; lookups must be `int`.
- **`PostingEventIdCheck` (Q-J)** runs before each posting event is dispatched. A public `int` / `long` `…Id` that is 0 fails, naming the event and the property.
- The pattern ("keep the entity, read the id when asked") is in README "Posting transactions" and the module checklist.

### Q-H: enums

- `[ExplicitValues]` marks an enum whose members all have values.
- `DataModelGuard` refuses a stored enum without it.
- The shipped `ConventionSourceTests` reads each such enum's source and fails on a member without `=`. A self-test covers its parser, including comments and attributes.

### D7: working rules

- `CLAUDE.md` holds the repository's working rules. It is excluded from the package in `template.json` and in the nuspec, and `pack.csproj` asserts it is absent.
- `PassPlanRulesTests` is shipped. It checks every plan in scope for the section, the citation, a verdict per row, and a named decision on each departure.
  - Scope in this repository: `docs/passes/.gx-plan-rules` names `pass60c-gx-conventions-plan.md`.
  - Scope in a generated project: plans dated after 2026-11-12.
- `docs/gx-conventions.md` ships in the package (Q-K), and `pack.csproj` asserts it. It now opens with its 1.8.0 status.

### T9, T10, T11 and T12

- **T9:** `TestBranches` (tests/TestSupport) gives floor-safe codes (`T` + 5 capitals or digits) and tagged unique names.
  - `ModelHarness.CreateBranchAsync`, `BranchExtensionTests`, `DefaultBranchTests` and `PicklistVersionTests` use it.
  - `BranchExtensionTests` keeps one tag per test, so a renamed branch's name stays stable within the test.
  - README "Extending branches" states the floor.
- **T10:** the README note on a branch-defining command nested in a non-defining request.
- **T11:**
  - `Warehouse` is renamed `GuardProbeWarehouse` on `DataModelGuardTests.cs` lines 80, 85 and 131, identical to Spare Parts `7c38ba4`.
  - The shipped `ConventionSourceTests.NoTestDeclaresAnEntityNamedWithAPlainWord` enforces the rule.
- **T12:** `ANonHolderCanStillREADTheRoleList` now asserts that the roles it created are present. It no longer asserts a total of 3.

### Sample module (smoke)

The sample module now holds:

- `Warehouse` (int, versioned, `IMustHaveBranch`, `WarehouseStatus` enum);
- the `WarehouseType` lookup (seeded 1 Main, 2 In transit);
- the `WarehouseMovement` ledger (bigint, `IAppendOnly`, with its trigger);
- `WarehouseCreated`, which reads the id after the save, and a handler that writes the opening movement;
- `SampleBranchRules` (the T9 floor), and a unique `Branches.Name` index;
- the page `/sample/warehouses` and its menu entry;
- its own `tables.approved.txt`;
- tests S1 (rewritten), S10, S11 and S12.

`SampleBin` is now `int`-keyed, and `SampleBinCreated` keeps the bin.

The smoke patches the generated migration by hand, as the README tells a developer to: `SeedLookup`, `ResetIdentitySequence` and `CreateAppendOnlyTrigger`. It then asserts:

- one `EnsureSchema("core")` and no other schema;
- plural `TBL_` / `TBL_LK_` names;
- snake_case columns.

### README and release

- "Database naming" is rewritten: the contract, plurals, the table list, lookups, columns and constraints, enums, lengths (Q-G) and times (Q-F). It now ends with "Upgrading to 1.8.0 from 1.7.x".
- "Modules and schemas" is now "Modules". It also covers names (Q6, Q11) and the 11-step checklist.
- Also updated: "Posting transactions" (keys and the event pattern), "Posted records" (the trigger example) and "Approvals" (`long`).
- The nuspec's 1.8.0 release notes are written. The version is 1.8.0 in the nuspec and in `AppConfigurationSettings`.

## 2. Additions not in the plan (P60c)

1. **`ModelHarness` drops every user schema of its own throwaway database.** Pass 60's leftover `tdf` / `tsa` / `tsb` schemas still held tables, so `EnsureCreated` skipped the whole model and `core` was never created.
2. **The convention reads a column name from the table it maps to** (`GetColumnName(StoreObjectIdentifier)`). An owned type's key shares its owner's key column. Without this the model failed: "keys … mapped to … pk_tbl_sample_widgets, but with different columns".
3. **G1 reads each sequence's own `last_value` and `is_called`.** `pg_sequences` shows NULL for a sequence set with `is_called = false`, which is exactly what `ResetIdentitySequence` leaves, so the first version restored the start value and G1-c caught it.
4. **G1 runs a full restore once, before migrating.** Each run's snapshot is otherwise taken from whatever the previous run's last test left.
5. **The T11 rule needed four more renames.** Applied to all test entities, it flagged `Floats`, `Kinds`, `Moduleless` and `Undeclared` (now `Guard…`), and `Criterion` (now `TestCriterion`).
6. **The plan check's scope:** the `.gx-plan-rules` marker here, and the date rule elsewhere. Spare Parts' pass-1 plan (2026-11-12) predates D7 and is not failed after the fact.
7. **The sample module keeps `SampleBin`** (int key, `SampleBinCreated(bin)`) and adds the warehouse set beside it, instead of replacing it as plan §3.8 said. This means less churn in the 60b tests (S2 to S9), and every convention is still proved.
8. **The smoke patches the generated migration's `Up()`.** `dotnet ef migrations add` cannot write a seed or a trigger.
9. **T12 asserts `Contain`** on the three created roles, not "count before + 3". It is equivalent, and does not depend on reading the count first.
10. **`TenantProbe` and `KeyedProbe`'s hand-written DDL is now snake_case.** They are business entities in their own schemas, so the convention renames their columns.
11. **The nuspec ships `docs/gx-conventions.md` explicitly,** because its `**\docs\**` exclude cannot carve one file out. `template.json` gets a second source for it.
12. **The model harness puts the append-only trigger on EVERY `IAppendOnly` table in the model,** not only `TestLedgerRow`. Found by the first smoke run: in a generated project the test model also holds the application's ledgers (`TBL_WAREHOUSE_MOVEMENTS`), and T19's "no ledger lacks its trigger" failed. This is the same class of defect as T11: a template test that assumes the application's model lacks something.
13. **The smoke clears its own databases of an earlier sample migration before the suites.** The sample migration keeps one fixed id (`20990101000000_SampleModule`), and the smoke's `gx_test_smokeapp_*` databases are kept between runs. They already recorded 1.7.1's version, so EF skipped the new one and its tables never existed. That broke 13, 93 and 8 tests in the first run; the startup guard named the missing trigger. The step uses psql when it can find it. It deletes only that history row, the `core`, `smp` and `gx_test_seed` schemas, and `public."IX_Branches_Name"`, which is the one object the sample migration puts outside `core` (its `Down()` drops it too). It fails if the history row survives. It never drops a database.
14. **The smoke script itself had five defects, which turned up while finishing the release (§6).** All five are fixed in `28c8051f`, and none touches the template or the generated code:
    - It forbade any `docs` folder in the output, but this pass ships `docs/gx-conventions.md`. It now requires exactly that one file under `docs`.
    - The cleanup loop variable `$database` was the script's own validated `-Database` parameter, because PowerShell names are case-insensitive. It is now `$testDatabase`.
    - Windows PowerShell 5.1 turned psql's NOTICE on stderr into a terminating error. The SQL now sets `client_min_messages` to warning, and the step judges psql by its exit code.
    - Windows PowerShell 5.1 strips the double quotes from a native argument, so `"__EFMigrationsHistory"` reached psql unquoted and matched nothing. The history row survived while `core` was dropped. The SQL now goes through a file (`-f`).
    - The expected count of sample application tests was still pass 60b's 7. It is now 10.

## 3. Each SQL site (S1 to S13), confirmed against the database

| # | Site | How it was confirmed |
|---|---|---|
| S1 | `PostingSql.Names` | Used by S3 and S4. `StructureTests` drives `InsertIfAbsent` against `core."TBL_TEST_BALANCES"`, whose columns in the database are `id, item_code, branch_id, on_hand` (psql). Passes. |
| S2 | `IdempotencyStore` | The Transactions tests pass against `public."ProcessedRequests"`, whose columns are unchanged: `RequestId, RequestType, UserId, CreatedAt, Response` (psql). |
| S3 | `DocumentNumberAllocator` | The numbering tests pass against `public."DocumentSequences"` (`DocumentType, BranchId, Year, NextValue`, psql). |
| S4 | `InsertIfAbsent` | `StructureTests` passes: rows created by natural key, the branch and precision refusals, the race. All on snake_case `core."TBL_TEST_BALANCES"`. |
| S5 | `CreateAppendOnlyTrigger` (default schema `core`) | The model harness applies it to `core."TBL_TEST_LEDGER_ROWS"`. psql shows `gx_append_only_row` and `gx_append_only_truncate` on it. T19 sees `UPDATE … SET amount = 1 WHERE id = …`, `DELETE` and `TRUNCATE` refused with `restrict_violation`. |
| S6 | `FindTablesMissingTriggersAsync` | T19: empty with the triggers on. With one disabled it names `core.TBL_TEST_LEDGER_ROWS`. |
| S7 | Bulk-write guard | T17: `ExecuteUpdate`/`ExecuteDelete`, and raw `UPDATE core."TBL_TEST_LEDGER_ROWS" SET amount = 0`, are refused naming the table. A bulk update of `Branches` works. |
| S8 | Cache guard | `CacheRuleTests` T21: refused reads of `TBL_TEST_LEDGER_ROWS` and `TBL_TEST_BRANCH_ROWS`, and the uncached query works. |
| S9 | `ForUpdate` | `DataFoundationsTests` T10, T15 and T26 lock rows of `core."TBL_TEST_POSTED_DOCUMENTS"` and `TBL_TEST_BRANCH_ROWS`. |
| S10 | `UserBranches` filter `"IsDefault"` | Unchanged. psql shows `IX_UserBranches_UserId_Default`. The default-branch tests pass. |
| S11 | `PicklistSets` filter `"TenantId" IS NULL` | Unchanged (`IX_PicklistSets_Name_Value`). The picklist tests pass. |
| S12 | Test SQL literals | Rewritten (`DataFoundationsTests`, `SharedDatabaseTests`, `TenantProbe`, `GenericKeyEntityTests`, `TenantStampingByMarkerTests`). All pass. |
| S13 | Log DDL | Untouched; the log tests pass. |

**In the database** (psql, test model database `gx_test_cleanarchitectureblazor_appint_model`):

- 12 tables in `core`, all `TBL_…S`;
- constraints `pk_tbl_*`, plus `fk_tbl_beta_things_tbl_alpha_things_alpha_thing_id` and `fk_tbl_test_posted_lines_tbl_test_posted_documents_header_id`;
- the 20 template tables in `public` with PascalCase columns;
- `__EFMigrationsHistory` keeps `MigrationId, ProductVersion`.

## 4. The table list

**Before (1.7.1)** there was no list. The template's model held the 20 template tables below. Business tables, which the template did not ship, would have been `<module schema>."TBL_<SINGULAR>"`.

**After: the template's `tables.approved.txt`:**

```
public.ApprovalLimits <- ApprovalLimit [template]
public.AspNetRoleClaims <- ApplicationRoleClaim [template]
public.AspNetRoles <- ApplicationRole [template]
public.AspNetUserClaims <- ApplicationUserClaim [template]
public.AspNetUserLogins <- ApplicationUserLogin [template]
public.AspNetUserPasskeys <- IdentityUserPasskey`1 [template]
public.AspNetUserRoles <- ApplicationUserRole [template]
public.AspNetUserTokens <- ApplicationUserToken [template]
public.AspNetUsers <- ApplicationUser [template]
public.AuditTrails <- AuditTrail [template]
public.Branches <- Branch [template]
public.DataProtectionKeys <- DataProtectionKey [template]
public.DocumentSequences <- DocumentSequence [template]
public.Documents <- Document [template]
public.PicklistSets <- PicklistSet [template]
public.ProcessedRequests <- ProcessedRequest [template]
public.SecurityPolicies <- SecurityPolicy [template]
public.TenantUsers <- TenantUser [template]
public.Tenants <- Tenant [template]
public.UserBranches <- UserBranch [template]

```

**The sample module's version (smoke)** has these four lines first, then the same 20:

```
core.TBL_LK_WAREHOUSE_TYPES <- WarehouseType [Sample]
core.TBL_SAMPLE_BINS <- SampleBin [Sample]
core.TBL_WAREHOUSES <- Warehouse [Sample]
core.TBL_WAREHOUSE_MOVEMENTS <- WarehouseMovement [Sample]
```

## 5. The pluraliser's tests, in full

**`PluralisationTests.Pluralise_GivesTheFixedPlural`**, 88 cases:

StoreMovement → StoreMovements; StoreMovements → StoreMovements; StockMovement → StockMovements; Warehouse → Warehouses; Bin → Bins; Item → Items; Uom → Uoms; UomConversion → UomConversions; PriceList → PriceLists; JournalVoucher → JournalVouchers; Employee → Employees; Route → Routes; TripKit → TripKits; Company → Companies; Category → Categories; Inventory → Inventories; History → Histories; Day → Days; Key → Keys; Survey → Surveys; Journey → Journeys; Status → Statuses; Address → Addresses; Process → Processes; Bus → Buses; Gas → Gases; Box → Boxes; Tax → Taxes; Batch → Batches; Branch → Branches; Dispatch → Dispatches; Wash → Washes; Waltz → Waltzes; Analysis → Analyses; Basis → Bases; CostAnalysis → CostAnalyses; Invoices → Invoices; Categories → Categories; Days → Days; Settings → Settings; TripRequests → TripRequests; VAT → VATS; GoodsVAT → GoodsVATS; IMS → IMS; IMSSetting → IMSSettings; Person → People; SalesPerson → SalesPeople; Child → Children; Man → Men; Woman → Women; Criterion → Criteria; Datum → Data; Medium → Media; Index → Indices; PriceIndex → PriceIndices; Matrix → Matrices; Vertex → Vertices; Leaf → Leaves; Shelf → Shelves; Half → Halves; Knife → Knives; Life → Lives; Wife → Wives; Foot → Feet; Tooth → Teeth; Goose → Geese; Mouse → Mice; Ox → Oxen; Quiz → Quizzes; People → People; Children → Children; Equipment → Equipment; OfficeEquipment → OfficeEquipment; Information → Information; Data → Data; Series → Series; Species → Species; News → News; Staff → Staff; Personnel → Personnel; Feedback → Feedback; Software → Software; Metadata → Metadata; Sheep → Sheep; Deer → Deer; CategoryItem → CategoryItems; StatusHistory → StatusHistories; BoxLabel → BoxLabels


**`TheTableName_IsTblPlusThePluralInUpperSnake`:**

- StoreMovement → `TBL_STORE_MOVEMENTS`
- StoreMovements → `TBL_STORE_MOVEMENTS`
- UomConversion → `TBL_UOM_CONVERSIONS`
- IMSSetting → `TBL_IMS_SETTINGS`
- VAT → `TBL_VATS`
- SampleCourseSampleStudent → `TBL_SAMPLE_COURSE_SAMPLE_STUDENTS`

**The other tests:**

- `ALookup_IsTblLkPlusThePlural`: `VehicleMake` → `TBL_LK_VEHICLE_MAKES`.
- `PluralName_OverridesTheRules_ForTheWholeClassName`:
  - `TestCriterion` [PluralName("TestCriteria")] → `TBL_TEST_CRITERIA`;
  - `StockHistory` [PluralName("StockHistory")] → `TBL_STOCK_HISTORY`;
  - `GoodsVatRate` → `TBL_GOODS_VAT_RATES`.
- `APluralNameThatIsNotAPascalIdentifier_FailsLoudly`.
- `ANameEndingInADigit_FailsLoudly_AskingForPluralName` (`PurchaseOrderLine2`).
- `TheFixedLists_AreAllPinnedAbove`: 22 irregular words, 34 unchanged.

## 6. The smoke test

**Result: SMOKE PASSED (postgresql), 125 checks ok and 0 failed.** The smoke ran twice back to back, in the foreground with nothing else running (`C:\gxp60l`, then `C:\gxp60m`), on the tree committed as `28c8051f`. The second run started from the databases the first left behind: the sample migration recorded and `core` populated. That is the case additions 13 and 14 are about. The figures below are from the second run.

```
powershell -NoProfile -ExecutionPolicy Bypass -File tooling\smoke-generate.ps1 -WorkRoot C:\gxp60m -KeepOutput
```

It packs the source tree into a throwaway hive, generates `SmokeApp` with the template's defaults, copies `tooling/sample-module`, adds the `20990101000000_SampleModule` migration to `Migrators.PostgreSQL` and `Migrators.SqLite`, and patches it with `SeedLookup`, `ResetIdentitySequence` and `CreateAppendOnlyTrigger`. Each of these sample checks passed:

- 22 sample files copied with the namespace `SmokeApp`;
- the solution builds with the module, and again with its migrations;
- the migration gets the lookup seed and the ledger trigger;
- the PostgreSQL migration creates `core` and no other schema;
- the sample tables are plural `TBL_` / `TBL_LK_` with snake_case columns;
- the smoke's own test databases forget any earlier sample migration (8 databases).

**The four suites with the module in, against `GX_TEST_PG`:**

| Suite | Generated project |
|---|---|
| Application.UnitTests | 637 passed, 15 skipped (652) |
| Infrastructure.UnitTests | 340 |
| Application.IntegrationTests | 170 |
| Server.UI.IntegrationTests | 361 |

All four have 0 failed. Image uploads: 28. The suites also fail loudly without `GX_TEST_PG`, naming it, as they should.

The generated counts differ from the template's for two reasons:

- **Application.UnitTests** has one more skip because `PassPlanRulesTests` ignores itself in a project with no `docs/passes`.
- **Infrastructure.UnitTests** keeps 21 fewer tests: the provider-conditional cases of the providers not chosen. The gap is the same as in 1.7.1 (239 in the template, 218 generated).
- The integration suites add the module's own tests: 10 application tests and 2 UI tests.

**The sample's own tests, run alone and listed by name.** All passed:

| Row | Test | Suite |
|---|---|---|
| S1 | `S1_TheModulesTables_AreInCore_PluralWithSnakeCaseColumns_AndIntegerKeys` | Application.IntegrationTests |
| S2 | `S2_ABinIsCreatedInItsBranch_Announced_AndAUserOfAnotherBranchCannotReadIt` | Application.IntegrationTests |
| S3 | `S3_TheContractsInterface_ResolvesToTheInfrastructureImplementation` | Application.IntegrationTests |
| S4, S5 | `S4_S5_ProvisioningRunsTheModulesSeeder_AndCreatesItsCatalogueRole` | Application.IntegrationTests |
| S6 | `S6_CreatingABranch_OpensItsDefaultBin_InTheSameTransaction` | Application.IntegrationTests |
| S7 | `S7_TheSampleSection_IsBetweenApplicationAndManagement_OnTheRealHost` | Server.UI.IntegrationTests |
| S8 | `S8_TheModulesBranchValidator_RefusesAReservedCode` | Application.IntegrationTests |
| S9 | `S9_TheApplicationLevelPermission_IsDecidedByAttribute_AndNotGrantedToTheAdministrator` | Application.IntegrationTests |
| S9 | `S9_TheApplicationLevelPage_IsRouted_Gated_AndKeepsTheBoundary` | Server.UI.IntegrationTests |
| S10 | `S10_AWarehouseIsCreated_AndItsOpeningMovementPointsAtItsSavedId` | Application.IntegrationTests |
| S11 | `S11_TheLookupIsSeededWithItsIds_AndItsSequenceContinuesAfterThem` | Application.IntegrationTests |
| S12 | `S12_TheModulesBranchRule_RefusesACodeBelowTheFloor_AndTheTemplatesTestsPassUnderIt` | Application.IntegrationTests |
| (T05) | `T05_EveryModuleService_ResolvesUnderScopeValidation` | Server.UI.IntegrationTests |

That is 10 application tests and 3 UI tests, the counts the smoke asserts.

**The table list as the generated project sees it.** This is `tests/Application.UnitTests/Model/tables.approved.txt` in `SmokeApp`, which the sample module ships and `ModelTableListTests` passed against:

```
core.TBL_LK_WAREHOUSE_TYPES <- WarehouseType [Sample]
core.TBL_SAMPLE_BINS <- SampleBin [Sample]
core.TBL_WAREHOUSES <- Warehouse [Sample]
core.TBL_WAREHOUSE_MOVEMENTS <- WarehouseMovement [Sample]
public.ApprovalLimits <- ApprovalLimit [template]
  ... the 20 template tables of §4, unchanged
public.UserBranches <- UserBranch [template]
```

The same tables in `gx_test_smokeapp_appint` after the run, from `pg_attribute`:

| Table | Columns |
|---|---|
| `core."TBL_LK_WAREHOUSE_TYPES"` | `id` integer, `name` varchar(100), `slug` varchar(100). Rows: 1 Main `main`, 2 In transit `in-transit` |
| `core."TBL_SAMPLE_BINS"` | `id` integer, `branch_id` integer, `name` varchar(100), `announced` boolean |
| `core."TBL_WAREHOUSES"` | `id` integer, `branch_id` integer, `code` varchar(10), `name` varchar(100), `warehouse_type_id` integer, `status` integer, `created_at` / `last_modified_at` timestamptz, `created_by_id` / `last_modified_by_id` varchar(450) |
| `core."TBL_WAREHOUSE_MOVEMENTS"` | `id` bigint, `warehouse_id` integer, `quantity` numeric(18,4), `note` varchar(200). Triggers: `gx_append_only_row`, `gx_append_only_truncate` |

**Schemas:** `core`, `gx_test_seed` (G1's snapshot, with `gx_test_dirty` on the lookup) and `public`. No `smp` remains.

**How it got here.** These are the runs before the passing pair, in order, with each failure and its cause:

| Run | Outcome | Cause | Fix |
|---|---|---|---|
| `C:\gxp60c` | 5 failed | Stale sample history in the smoke databases. T19 found a ledger without a trigger. | P60c 12, 13 |
| `C:\gxp60e` | Killed by the system for low memory during the build | — | Rerun with nothing else running |
| `C:\gxp60f` | 2 failed | "docs was generated". The `$Database` name clash stopped the cleanup. | P60c 14 |
| `C:\gxp60g` | 1 failed | A psql NOTICE became a terminating error. | P60c 14 |
| `C:\gxp60h` | 1 failed | The sample count was still 7; 10 of 10 passed. | P60c 14 |
| `C:\gxp60i` | 4 failed | The cleanup dropped `core` but kept the history row (quote stripping), so the migration was skipped. | P60c 14 |
| `C:\gxp60j`, `C:\gxp60k` | 6 failed each | The re-applied migration hit the sample's `IX_Branches_Name`, which the cleanup had left on `public."Branches"`. | P60c 13 |
| `C:\gxp60l`, `C:\gxp60m` | **Passed, 125 / 125 each** | — | — |

None of the failures was in the template or in the code it generates, apart from P60c 12. Every other fix is in the smoke script.

`C:\gxp60h` is the only run before the passing pair that got every suite green: the earlier runs had stopped before migrating, so its databases held no sample history. That is why `C:\gxp60i` exposed the quote stripping, and why the proof is a second consecutive run.

A smoke of the installed package (`-UseInstalled`) was not run. The package was packed from `28c8051f`, whose tree is the one smoked above, and `build/pack.csproj` asserted its contents.

## 7. The browser check

**I could not drive a browser.** This session has no browser-automation tool, so the interactive part of the check was not done: signing in, opening the page, creating a warehouse in the form and watching the list update. It is listed below for the owner.

**What was done instead,** on the generated `SmokeApp` from the second passing smoke (`C:\gxp60m`, with the sample module and its migration in):

1. **Startup.** I ran the app with `dotnet run` in Development on `http://127.0.0.1:5199`, against a new database of its own on the test server (`gx_browsercheck_smokeapp` and `_logs`; created, not dropped).
   - The app started: `DataModelGuard` passed at real startup with the module in, the migrations ran, and provisioning seeded the roles and the administrator.
   - The one `ERR` in the log is EF probing the database before it created it.
2. **HTTP probes.**
   - An anonymous `GET /sample/warehouses` returned 302 to `/account/login?ReturnUrl=%2Fsample%2Fwarehouses`, so the page is routed and gated.
   - `GET /account/login` returned 200.
3. **The database the real startup built:**
   - `__EFMigrationsHistory` ends at `20990101000000_SampleModule`.
   - `core` holds `TBL_LK_WAREHOUSE_TYPES`, `TBL_SAMPLE_BINS`, `TBL_WAREHOUSES` and `TBL_WAREHOUSE_MOVEMENTS`.
   - The lookup holds 1 Main `main` and 2 In transit `in-transit`.
   - Its sequence stands at `last_value 3, is_called false`, so the next id is 3.
   - `TBL_WAREHOUSE_MOVEMENTS` carries `gx_append_only_row` and `gx_append_only_truncate`.
4. I stopped the app afterwards.

The behaviour behind the form is covered by S10: create a warehouse through the command, then confirm the opening movement in `core."TBL_WAREHOUSE_MOVEMENTS"` carries the saved id. S9 (UI) covers the page's routing and gating on the real host, and S7 its menu position. None of these is a browser.

**For the owner to check by hand, in a browser,** on a project generated from the installed 1.8.0 with `tooling/sample-module` added:

1. Sign in as `administrator` with the password printed once at first startup. Change it when asked (`MustChangePassword`).
2. Confirm the Sample section of the menu sits between Application and Management, and that it lists Warehouses.
3. Open `/sample/warehouses`. The page should load, with no circuit error, and show the Branch id, Code and Name fields. The form has no type picker.
4. Create a warehouse with the id of an existing branch, code `W01` and name `Main store`. The page should say "Created warehouse N." and list it without a reload.
5. Try a second warehouse with the same code in the same branch. It should be refused by `ux_tbl_warehouses_branch_code`, with a readable message.
6. Try a lower-case code. It should be refused, by the validator or by `ck_tbl_warehouses_code_upper`.
7. In psql:

   ```sql
   SELECT * FROM core."TBL_WAREHOUSE_MOVEMENTS" ORDER BY id DESC LIMIT 1;
   ```

   The row's `warehouse_id` is the new warehouse's `id` and is not 0.

   Then try:

   ```sql
   UPDATE core."TBL_WAREHOUSE_MOVEMENTS" SET note = 'x';
   ```

   It should fail on the append-only trigger.

## 8. Not done

- **Q-G:** making `[StringLength]` reach the database is queued for pass 61, as decided. The README says to set lengths in the configuration.
- **The Spare Parts port (plan §6.1)** is Spare Parts' own next pass. It regenerates the Organisation and Catalogue migrations (Q-N) and drops the CLAUDE.md Schema column (Q-M). Nothing was changed there.
- **Showing an entity's display name in `DbExceptionHandler`** instead of `TBL_…` was listed in the plan as cosmetic, and is not done.
- **T5, T7 and T8** (Spare Parts' list) are out of scope: pass 61, or optional.
- **`PostingSql` is internal,** so S1 is confirmed through its callers rather than by a direct test.
- **The development and test databases of 1.7.x** may still hold the empty `tdf` / `tsa` / `tsb` / `smp` schemas. The model harness now drops them in its own database; the other test databases never had them.

## 9. Release

- **Commit `28c8051f`** "Pass60c-GxConventions and 1.8.0", then the pack: `GX.Blazor.Template.1.8.0.nupkg`, built from the commit.
  - The nuspec records `commit="28c8051fc0e215193afc1f10920301328c6b91d0"`.
  - `build/pack.csproj` asserted the contents: `content/docs/gx-conventions.md` is present, and there is no `CLAUDE.md` and no `docs/passes`.
- **Installed:** 1.7.1 uninstalled and 1.8.0 installed. `dotnet new uninstall` lists exactly one `GX.Blazor.Template`, version 1.8.0.
- **Tag `gx-v1.8.0`** is on `28c8051f`, a lightweight tag like `gx-v1.7.1`.
- **Pushed on the owner's instruction:** `main` (with this report) and the tag, by name (`git push origin gx-v1.8.0`), never `--tags`.
