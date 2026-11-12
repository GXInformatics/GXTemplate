# Conventions audit: GXTemplate against the GX conventions

**Written:** 2026-11-12, at HEAD `368de373` (pass 60b report; code `e9085bd4`, template 1.7.1). **Baseline:** `d9d9c75f` ("pass47b", 2026-11-03), the last commit before pass 58. **Reference:** `docs/gx-conventions.md` (MNEFleets, as practised).

Read-only: no source file was changed in either repository. Nothing below is implemented.

**Passes in scope:**

| Pass | Commit | Release |
|---|---|---|
| 58 | `18e300fe` | 1.5.0 |
| 59 | `428d5f0d` | 1.6.0 |
| 60 | `254175ba` | 1.7.0 |
| 60b | `e9085bd4` | 1.7.1 |

The reports are `docs/passes/pass58.md` to `pass60b.md`, and the plans are `*-plan.md` beside them.

---

## Summary

| # | Convention (MNEFleets) | At `d9d9c75f` | At HEAD | Changed by | Flagged as a GX departure? |
|---|---|---|---|---|---|
| C1 | Business tables in `core` | **Enforced** by convention and tests | **Removed**: each module's own schema (`inv`, `smp`, …) | Pass 60, `254175ba`, owner decision 8.2 | **No** |
| C2 | `public` holds framework tables only | Enforced (template tables listed and pinned) | Unchanged in mechanism; 5 more template tables in `public` (Q1) | Passes 58, 59, 60 (tables added) | No (each called "a template table") |
| C3 | `TBL_` + UPPER_SNAKE | Enforced | Unchanged | — | — |
| C4 | `TBL_LK_` for lookups | Enforced via `ILookupEntity` | Unchanged | — | — |
| C5 | Hand-named table, no schema → stays where it was put | Stayed in `public` | **Changed**: goes to the module schema | Pass 60 | No |
| C6 | Join tables beside their entities in `core` | Enforced | **Changed**: the module schema | Pass 60 | No |
| C7 | Plural table names | **Not followed** (name derived from the class) | Unchanged | — (it predates pass 58) | Q4 |
| C8 | Column casing | PascalCase, plug-in forbidden (MNEFleets: snake_case) | Unchanged | — (predates) | Q2 |
| C9 | Primary key `Id`, int/long identity | `Id`; any key type since pass 55 | Changed in guidance: headers are Guid v7 | Pass 58 owner direction; pass 60 README checklist | No |
| C10 | Constraint names EF default | EF default, PascalCase | Unchanged | — | Q7 |
| C11 | Audit columns `CreatedAt`/`CreatedById`/… | Same names, `BaseAuditableEntity` | Unchanged | — | — |
| C12 | Soft delete `DeletedAt`/`DeletedById` | Same | Unchanged | — | — |
| C13 | Concurrency rare (xmin once) | None by default | **Added**: `IVersioned` / xmin, required on edits | Pass 59 | Not a departure (an addition) |
| C14 | Tenancy dormant | Dormant | Unchanged | — | — |
| C15 | Lookup shape and seeding by migration | Marker only; seeded by migration (test harness spares `TBL_LK_`) | Unchanged | — | Q5 |
| C16 | Entities flat in `Domain/Entities`, one namespace | Template entities in `Domain/Entities` | **Added**: `Domain/Modules/<M>/…` for application entities, enforced by architecture tests | Pass 60 | No (not a MNEFleets rule either way; see below) |
| C17 | Permissions / roles / routes / menu | `Permissions.X.Y`; `/area/kebab` routes; `MenuService` | Additions: attribute-decided permissions, `IMenuContributor`, role catalogue | Passes 59, 60, 60b | No |
| C18 | Timestamps, strings, decimals, enums | `timestamptz`; 450 default; enum-as-string advice | Additions: decimal kinds required (pass 59) | Pass 59 | No |

Only **C1, C5 and C6** were changed by passes 58 to 60b, and all three in one place: the schema-per-module change in pass 60.

- **C2** grew within its own rule.
- **C9, C13, C16, C17 and C18** were additions, not reversals.
- **C7, C8, C10 and C15** were already different from MNEFleets before pass 58. They are questions for the owner, not regressions.

---

## 1. Per convention

### C1. Business tables in `core`

**a. At `d9d9c75f`: enforced in code and pinned by tests.**

- **The code:**
  - `src/Infrastructure/Persistence/Extensions/GxNamingConventions.cs:33` declares `public const string BusinessSchema = "core";`.
  - `ApplyGxTableNaming` (`:45-121`) sets `entityType.SetSchema(BusinessSchema)` and the `TBL_` name on every `IBusinessEntity` with a table of its own that is not named by hand (`:92-93`).
  - Membership is a type test: `BaseEntity<TKey>` implements `IBusinessEntity` (`src/Domain/Common/Entities/IBusinessEntity.cs:5-24`), so "there is no per-entity opt-in to forget".
  - It is called last in `ApplicationDbContext.OnModelCreating` (`ApplicationDbContext.cs:152-157`).
- **The tests** are in `tests/Infrastructure.UnitTests/Persistence/GxTableNamingTests.cs`:
  - `ABusinessEntity_BecomesCoreTblUpperSnake` (`:204`);
  - `ALookupEntity_BecomesCoreTblLkUpperSnake` (`:212`);
  - `AnImplicitManyToManyJoin_IsNamedIntoCore` (`:307`);
  - `NoTemplateEntity_IsMappedIntoTheCoreSchema` (`:400`);
  - `EveryTableOutsideCore_IsOneOfTheTemplates` (`:417`);
  - the TPT and TPC cases (`:286`, `:297`).
  - Pass 59's test model also put its entities in `core`. `tests/Application.IntegrationTests/DataFoundations/ModelHarness.cs:64` at `428d5f0d` runs `DROP SCHEMA IF EXISTS core CASCADE`, and `DataFoundationsTests.cs:384,404` wrote SQL against `core."TBL_TEST_LEDGER_ROW"`.
- **The README** at `d9d9c75f` (`:986-1116`), section "Database naming", says: "**Contract: your business models live in the `core` schema as `TBL_UPPER_SNAKE`** … `core."TBL_STOCK_MOVEMENT"`".

**b. At HEAD: removed.**

- `BusinessSchema` is deleted.
- The schema now comes from the module: `GxNamingConventions.cs:55-65` sets `entityType.SetSchema(module.Schema)` when `ModuleDefinition.Of(clr)` is not null.
- An `IBusinessEntity` with no module still gets its `TBL_` name but no schema (test `AnEntityWithNoModule_GetsItsTblNameAndNoSchema`, `GxTableNamingTests.cs:373`). `DataModelGuard` then refuses it at startup (`src/Infrastructure/Persistence/Rules/DataModelGuard.cs:111-116`).
- The tests now assert the opposite of the baseline:
  - `ModuleSchemaTests.T17_…` asserts `schemas.Should().NotContain("core")` (`tests/Application.IntegrationTests/DataFoundations/ModuleSchemaTests.cs:89`);
  - `ABusinessEntity_BecomesTblUpperSnake_InItsModulesSchema` (`GxTableNamingTests.cs:240`) asserts the sample module's schema `smp`.
- README "Database naming" (`README.md:1813-1817`) now reads: "Until Pass 60 every business table went to one schema, `core`; `core` is retired."

**c. What changed it, and what authorised it.**

- **The change:** pass 60, commit `254175ba`, plan Part B (`docs/passes/pass60-controls-structure-plan.md:42-66, 298-370`).
- **The trigger:** the adequacy review, written in pass 58 (`docs/gxtemplate-adequacy-review.md:21, 105-108, 143`). It proposed: "Extend `GxNamingConventions` to choose the schema from the module … **instead of always `core`**."
- **The plan:** its open question 12 (`pass60-…-plan.md:702-703`) reads: "With a schema per module, `core` holds nothing. **Recommend:** retire `BusinessSchema`."
- **The decision:** owner decision 8.2 (`:714`), dated 2026-11-11: "**The `core` schema: decided.** Retired. Template tables stay in `public`; every application table goes to its module's schema; nothing existing moves."
- **Was it flagged as a departure?** **No.**
  - Neither the review nor the plan nor the report (`pass60.md:37, 41, 70, 125`) calls `core` a GX or MNEFleets convention, or names MNEFleets at all.
  - The plan's justification was technical: "No table is in `core` today (§1), so no existing migration changes" (`:328`).
  - That is true of the template, but it is not a reason to depart from the house convention. The decision was taken without the convention in view.

### C2. `public` holds framework tables only

**a. At `d9d9c75f`.**

- Template entities stay in the default schema, by two routes:
  - they are not `IBusinessEntity` (Identity, `Tenant`, `TenantUser`, `AuditTrail`, `DataProtectionKey`);
  - or their configurations pin a schema-less `ToTable` (`Documents`, `PicklistSets`, `SecurityPolicies`).
- The rationale is in `IBusinessEntity.cs:12-21`: "a visible line in pgAdmin between the framework's tables and this business's tables".
- Pinned by `TemplateTablesStayOutOfCoreTests.ATemplateTable_KeepsItsNameAndTheDefaultSchema` (`GxTableNamingTests.cs:379-386`, 7 rows).

**b. At HEAD.** The mechanism is unchanged. Two things were added:

- Five more template tables, each with a schema-less `ToTable`:
  - `ProcessedRequests` and `DocumentSequences`: pass 58, `PostingTransactionConfigurations.cs:10,26`;
  - `Branches` and `UserBranches`: pass 59, `BranchConfiguration.cs:10,23`;
  - `ApprovalLimits`: pass 60, `BranchConfiguration.cs:45`.
- An explicit list, `src/Infrastructure/Persistence/Rules/TemplateEntities.cs:18-30`, with a startup check that a template entity declares no module (`DataModelGuard.cs:105-110`). The test now has 11 rows (`GxTableNamingTests.cs:430-441`).

**c. Authorisation.**

- Pass 58 plan §3.5 (`pass58-transactions-plan.md:102`): "in the default schema like the template's other infrastructure tables".
- Pass 59 plan (`pass59-data-foundations-plan.md:238`): "Both tables are template tables, so they live in the default schema".
- Pass 60 decision 8.2: "Template tables stay in `public`".
- None of these was flagged as a departure. This is consistent with MNEFleets' practice (framework tables in `public`). It is **not** consistent with the owner's statement that only Identity tables are in `public`, and `Branches` and `ApprovalLimits` are arguably business data (Q1).

### C3 and C4. `TBL_` / `TBL_LK_` + UPPER_SNAKE

- **a. At `d9d9c75f`:**
  - the code is `GxNamingConventions.cs:36-39` (the prefixes), `:91-93`, and `ToUpperSnake` (`:195-200`);
  - the lookup marker is `ILookupEntity` (`IBusinessEntity.cs:27-55`);
  - the tests are `ALookupEntity_…` and `ToUpperSnake_HandlesAcronymsAndCamelBoundaries` (`:332`);
  - the test harness keeps rows in `TBL_LK_` tables when it resets (`tests/TestSupport/PostgresTestDatabase.cs:53-57`).
- **b. At HEAD: unchanged** (`GxNamingConventions.cs:35-38, 99-101`).
- **c. —**

### C5. A table named by hand without a schema

- **a. At `d9d9c75f`:** `ToTable(name)` or `[Table]` won on both name and schema ("gates the schema too", `GxNamingConventions.cs:81-85`). So `ToTable("Legacy")` was `public."Legacy"`.
- **b. At HEAD: changed.**
  - A module entity named without a schema goes to its module's schema (`GxNamingConventions.cs:55-65`).
  - Test: `ATableNamedByHandWithoutASchema_StillGoesToItsModulesSchema` (`GxTableNamingTests.cs:363`), which maps `ToTable("legacy_plain")` to `smp."legacy_plain"`.
- **c. Authorisation:** pass 60 plan `:329` ("If it has none, the module's schema is applied"). Not flagged. It follows from C1.

### C6. Join tables

- **a. At `d9d9c75f`:** an implicit many-to-many join was put in `core` if either side was in `core` (`:99-117`). Test: `AnImplicitManyToManyJoin_IsNamedIntoCore`.
- **b. At HEAD: changed.** The join goes to the first linked side's module schema (`:104-127`). A join across two modules is refused by `DataModelGuard`. Test: `…_IsNamedIntoItsModulesSchema` (`:343`).
- **c. Authorisation:** pass 60 plan `:327`. Not flagged.

### C7. Plural table names

- **a. At `d9d9c75f`:** the name is derived from the class: `TBL_ + ToUpperSnake(clr.Name)`. A singular class gives a singular table (`TBL_STOCK_MOVEMENT` in the README and the code comments). MNEFleets writes the name by hand and mostly uses plurals.
- **b. At HEAD:** unchanged.
- **c.** This was not changed by any pass in scope; it already differed. Q4.

### C8. Column casing

- **a. At `d9d9c75f`:**
  - PascalCase quoted columns, with no snake-case plug-in on `ApplicationDbContext`.
  - Pinned by `OnPostgres_TheBusinessModelIsNotSnakeCased` (`GxTableNamingTests.cs:452`).
  - The README (`:1084-1093`) explains why: the plug-in also rewrites `__EFMigrationsHistory`.
  - MNEFleets is snake_case throughout.
- **b. At HEAD:** unchanged (test at `:506`).
- **c.** Already different before pass 58. Q2.

### C9. Primary keys

- **a. At `d9d9c75f`:** the key is always `Id`, an `int` for `BaseEntity`. Since pass 55 `BaseEntity<TKey>` takes `int`, `long`, `Guid` or `string`. MNEFleets uses int or long identity, with no Guid.
- **b. At HEAD:** the code is unchanged. The guidance changed:
  - the owner directed in pass 58 that document headers use `Guid.CreateVersion7()` set in the constructor, while high-volume line and ledger tables keep bigint identity;
  - pass 60's README checklist says "headers are `BaseVersionedEntity<Guid>`" (`README.md` "Modules and schemas", checklist item 2);
  - the sample module's `SampleBin` uses `BaseEntity<Guid>` with v7.
- **c. Authorisation:** owner direction 2026-11-11 (pass 58). Not described as a departure from MNEFleets' int/long practice. It is a deliberate new rule (Q13).

### C10. Constraint names

- **a. At `d9d9c75f`:** EF defaults (`PK_…`, `FK_…`, `IX_…`, PascalCase), plus Identity's `RoleNameIndex`, `EmailIndex` and `UserNameIndex`.
- **b. At HEAD:** unchanged (for example `IX_UserBranches_UserId_Default`).
- **c.** It differs from MNEFleets only because of the column-casing choice. Q7.

### C11 and C12. Audit columns and soft delete

- **a. At `d9d9c75f`:**
  - `BaseAuditableEntity<TKey>` carries `CreatedAt`, `CreatedById`, `LastModifiedAt` and `LastModifiedById` (`src/Domain/Common/Entities/BaseAuditableEntity.cs:18-31`).
  - `BaseAuditableSoftDeleteEntity<TKey>` carries `DeletedAt` and `DeletedById` (`BaseAuditableSoftDeleteEntity.cs:6-14`).
  - These are the same names as MNEFleets. MNEFleets has separate `…Long` classes where the template uses the generic key.
- **b. At HEAD:** unchanged. The files are identical.
- **c. —**

### C13. Concurrency

- **a. At `d9d9c75f`:** none by default.
- **b. At HEAD: added.** `IVersioned` / `BaseVersionedEntity<TKey>`, mapped to `xmin`, with the version check applied at save inside Mediator requests (pass 59, `src/Domain/Common/Entities/IVersioned.cs`).
- **c.** Pass 59 decisions. This is an addition. MNEFleets uses `xmin` once, the same way.

### C14. Tenancy

- **a. At `d9d9c75f`:** `IMustHaveTenant` / `IMayHaveTenant` with marker-built filters; no business entity uses them.
- **b. At HEAD:** unchanged. Tenancy stays dormant by owner direction.
- **c. —**

### C15. Lookup structure, seeding and editing

- **a. At `d9d9c75f`:**
  - `ILookupEntity` is a marker with no fixed columns.
  - The README rule is: "does any code branch on this row's value? No → lookup" (`README.md:1031-1043`).
  - Lookups are seeded by migration; `PostgresTestDatabase` keeps `TBL_LK_` rows between tests.
  - There is no generic editor.
- **b. At HEAD:** unchanged. The `PicklistSets` feature remains the template's runtime-editable list.
- **c.** Already different from MNEFleets: `BaseLkEntity` (Id, Name, Slug), master data under `TBL_LK_`, and explicit seeded ids with `advance_identity_sequences`. Q5.

### C16. Folder and namespace layout

- **a. At `d9d9c75f`:** entities in `src/Domain/Entities`, features in `Application/Features/<Plural>/{Commands,Queries,…}` and pages in `Server.UI/Pages`.
- **b. At HEAD: added.**
  - Application code goes under `{Domain,Application,Infrastructure,Server.UI}/Modules/<M>/…` (README "Modules and schemas", layout table).
  - `AppLevel` folders hold code that belongs to no module (pass 60b).
  - The architecture tests (`tests/Server.UI.IntegrationTests/Architecture/ArchitectureRulesTests.cs` T22-T26 and T42; `ModuleBoundaryTests.cs`; `tests/TestSupport/ModuleDeclarationRules.cs`) enforce that an entity under `Modules.<M>` declares `[Module<<M>Module>]`, that no navigation crosses modules, and that the context has no `DbSet` of a module entity.
  - The template's own entities stay in `Domain/Entities`.
- **c. Authorisation:** pass 60 decision 8.1 and pass 60b decisions.
  - MNEFleets' flat `Domain/Entities` is its practice, not a stated rule. A modular application has no MNEFleets precedent.
  - This is an addition, not a departure. It does **not** depend on schemas.

### C17. Permissions, roles, routes and menu

- **a. At `d9d9c75f`:**
  - `Permissions.<Area>.<Action>` string constants;
  - roles `Admin` and `Basic` (`src/Application/Common/Constants/Roles.cs:20-21`);
  - routes `/area/kebab` (`/system/audittrails`, `/identity/users`, `/pages/documents`);
  - a single `MenuService`.
- **b. At HEAD: additions.**
  - Permissions per module, decided by `[AdministratorGrant]` / `[AdministratorExcluded]` (pass 60b).
  - `AddApplicationRole` / `GrantToRole` (pass 60b).
  - `IMenuContributor` per module (pass 60).
  - New routes `/system/branches` and `/system/approval-limits`.
  - Nothing existing was renamed.
- **c.** Passes 59, 60 and 60b. MNEFleets authorises business pages by role and has mixed route styles (Q11). Neither is a template rule.

### C18. Column-level defaults

- **a. At `d9d9c75f`:**
  - `HaveMaxLength(450)` on strings (`ApplicationDbContext.cs:222`), the same as MNEFleets;
  - `timestamptz`, with Npgsql's legacy timestamp switch deliberately not set (MNEFleets sets it);
  - enums advised "stored as a string" (MNEFleets stores `integer`).
- **b. At HEAD:** decimal kinds are required (`[Money]` and similar, `src/Domain/Common/Precision/DecimalKindAttribute.cs`; startup fails on a bare `decimal`). MNEFleets has a blanket `HavePrecision(18,2)`. Everything else is unchanged.
- **c.** Pass 59. This is an addition.

---

## 2. Every table at HEAD

**Shipped model** (PostgreSQL snapshot `src/Migrators/Migrators.PostgreSQL/Migrations/ApplicationDbContextModelSnapshot.cs`; the SQL Server and SQLite snapshots carry the same model). Measured against C2: framework tables in `public`.

| Schema | Table | Added | Conforms? |
|---|---|---|---|
| public | `AspNetRoleClaims`, `AspNetRoles`, `AspNetUserClaims`, `AspNetUserLogins`, `AspNetUserPasskeys`, `AspNetUserRoles`, `AspNetUserTokens`, `AspNetUsers` | before 58 | Yes (Identity) |
| public | `AuditTrails`, `DataProtectionKeys`, `PicklistSets`, `SecurityPolicies`, `Tenants`, `TenantUsers` | before 58 | Yes by MNEFleets practice (framework). No by the owner's statement 1. Casing differs from MNEFleets (`audit_trails` …; Q2). |
| public | `Documents` | before 58 | Question: MNEFleets has `core."TBL_DOCUMENTS"` (Q1) |
| public | `ProcessedRequests` | pass 58 | Yes as a framework table (idempotency); no by statement 1 |
| public | `DocumentSequences` | pass 58 | Yes as a framework table; MNEFleets keeps its counters in `core` (Q1) |
| public | `Branches`, `UserBranches` | pass 59 | Question: business data in `public` (Q1) |
| public | `ApprovalLimits` | pass 60 | Question: business configuration in `public` (Q1) |
| public | `__EFMigrationsHistory` | — | Yes |

The template ships no business table, so there is no `TBL_` table and no `EnsureSchema` call in any of its shipped migrations: `PostingTransactions`, `DataFoundations`, `Approvals` and `OneDefaultBranch`.

**Tables created only by tests and the smoke run.** Measured against C1: business tables in `core`.

| Where | Schema | Tables | Conforms? |
|---|---|---|---|
| Sample module, `tooling/sample-module` (smoke, pass 60b) | `smp` | `TBL_SAMPLE_BIN` | **No** (C1) |
| Test model, `tests/Application.IntegrationTests/DataFoundations/TestModel.cs` | `tsa` | `TBL_ALPHA_THING` | **No** |
| Test model | `tsb` | `TBL_BETA_THING` | **No** |
| Test model | `tdf` | `TBL_TEST_BALANCE`, `TBL_TEST_TENANT_ROW`, `TBL_TEST_STAMPED_ROW`, `TBL_TEST_APPROVABLE_DOCUMENT`, `TBL_TEST_PLAIN_APPROVABLE`, `TBL_TEST_BRANCH_ROW`, `TBL_TEST_TRANSFER`, `TBL_TEST_LEDGER_ROW`, `TBL_TEST_POSTED_DOCUMENT`, `TBL_TEST_POSTED_LINE` | **No** (these were in `core` at pass 59) |
| `GxTableNamingTests` sample model (in memory, never created) | `smp` | `TBL_SAMPLE_WIDGET`, `TBL_LK_SAMPLE_WIDGET_KIND`, `TBL_SAMPLE_ANIMAL`, `TBL_SAMPLE_VEHICLE`, `TBL_SAMPLE_TRUCK`, `TBL_SAMPLE_CIRCLE`, `TBL_SAMPLE_SQUARE`, `TBL_SAMPLE_COURSE`, `TBL_SAMPLE_STUDENT`, `TBL_SAMPLE_MENTOR`, `TBL_SAMPLE_COURSE_SAMPLE_STUDENT`, `COURSE_MENTORS`, `legacy_plain` | **No** (these were `core` at baseline) |
| `GxTableNamingTests`, hand-named on purpose | `reporting` | `legacy_things`, `annotated_things` | Deliberate exceptions (tests that an explicit schema wins); unchanged |
| `GxTableNamingTests`, no module | (none) | `TBL_SAMPLE_ORPHAN` | Tests the no-module case; it would be `core` again under C1 |
| `ModuleGuardTests` (unit, model only) | `talpha`, `tbeta`, invalid `Inv-1` and `public` | guard fixtures | Fixtures for schema checks that would be retired |
| `tests/ArchitectureSamples` | `salpha`, `sbeta` (module definitions only) | — | Namespace fixtures; unaffected apart from the `Schema` value |

---

## 3. What passes 58 to 60b built on the changed behaviour

Only pass 60 (C1, C5 and C6) changed a convention. These depend on it.

**Code.**

1. `ModuleDefinition.Schema`, `ReservedSchemas` and `SchemaProblem` (`src/Domain/Common/Modules/ModuleDefinition.cs:31-53`).
2. `GxNamingConventions` setting the module schema (`:55-65, 117-126`).
3. `DataModelGuard`:
   - the schema checks in `CheckModuleDefinitions`: invalid, reserved or shared schema (`:166-171`);
   - `CheckTableNames`, which forbids one table name in two schemas (`:177-190`).
4. `AppendOnlyMigrationExtensions.cs:12`: the doc comment example `CreateAppendOnlyTrigger("TBL_STOCK_MOVEMENT", "core")`. It still says `core`, against the README's `inv` (a stale comment either way).

**Independent of schemas.** These stay as they are under any outcome:

- `[Module<T>]`;
- the no-module startup failure;
- `TemplateEntities`;
- the cross-module navigation rule;
- the TPH and owned-type module rules;
- `ModuleServices<T>`, `IMenuContributor`, attribute-decided permissions and the role catalogue;
- every architecture test.

**Migrations.**

- No shipped migration names a schema.
- A generated application's first module migration emits `EnsureSchema("<module schema>")` per module. The README "Modules and schemas" shows `EnsureSchema(name: "inv")`.
- `CreateAppendOnlyTrigger(table, schema)` takes the schema as an argument (pass 59); it needs no change.

**Tests.**

- `GxTableNamingTests`:
  - `SampleModule` with `SchemaName = "smp"` (`:19-24`);
  - every `…_InItsModulesSchema` test;
  - `ATableNamedByHandWithoutASchema_StillGoesToItsModulesSchema`;
  - `AnEntityWithNoModule_GetsItsTblNameAndNoSchema`;
  - `NoTemplateEntity_IsMappedIntoAModuleSchema`;
  - `EveryTableOutsideAModuleSchema_IsOneOfTheTemplates`.
- `ModuleSchemaTests`:
  - `T16_ModuleTablesLandInTheirSchemas_AndTemplateTablesStayInPublic` (`:38-48`);
  - `T16_AForeignKeyAcrossModuleSchemas_IsEnforced` (`:53`);
  - `T17_AFirstMigration_CreatesEachModuleSchema_…` (`:78-100`), which asserts `NotContain("core")`.
- `ModuleGuardTests`:
  - `T15_TwoModulesWithOneSchema_FailStartup` (`:128`);
  - `T15_AnInvalidOrReservedSchema_FailsStartup` (`:135`);
  - `T15_TwoTablesWithOneNameInDifferentSchemas_FailStartup` (`:150`);
  - `T16_EachModulesTablesLandInItsSchema_…` (`:157`).
- `TestModel.cs`: the three test modules' schemas `tdf`, `tsa` and `tsb` (`:20-37`).
- `ModelHarness.cs:63-69` drops every schema in the model, plus `core`, which it still lists for pass 59's databases.
- Pass 60's two `DataFoundationsTests` edits from `core."TBL_TEST_LEDGER_ROW"` to `tdf` (`pass60.md:70`).

**Smoke test and sample module.**

- `tooling/sample-module/src/Domain/Modules/Sample/SampleModule.cs` has `Schema => "smp"`.
- `SampleModuleTests.S1_TheBinTable_IsInTheModulesSchema` expects `smp.TBL_SAMPLE_BIN` (`tooling/sample-module/tests/Application.IntegrationTests/Modules/SampleModuleTests.cs:40-46`).
- `tooling/smoke-generate.ps1:621-622` checks that the PostgreSQL migration contains `EnsureSchema(name: "smp")`.

**README.**

- "Modules and schemas" (`README.md:1074-1168`): the section opener, the example, `→ inv."TBL_STOCK_ITEM"`, the startup-failure list, `REFERENCES cat."TBL_PART"`, the Migrations paragraph, "A module's schema is permanent", and checklist item 1 ("the name and the schema").
- "Database naming" (`:1813-1880`): the contract, the rules table, the join row, "ships dormant" and "stay out of the module schemas".
- "Posted records": the trigger example.
- The 1.7.0 release notes.

**Local test databases.** The kept `gx_test_cleanarchitectureblazor_*` databases hold `tsa`, `tsb` and `tdf` schemas (and `smp`, if a smoke run left it). They would need dropping by hand. Per the standing rule, drop the schemas, never the database.

---

## 4. Recommendation: restore `core` and keep what the passes added

### What a module means once every module's tables are in `core`

A module stays a **code boundary with a database footprint**. It is no longer a physical namespace in the database. Concretely, `[Module<T>]` still decides:

1. **Ownership.** Each entity belongs to exactly one module, checked at startup, so there is no accidental orphan.
2. **Boundaries.** There are no navigations across modules, only navigation-less foreign keys declared in configurations. There is no `DbSet` on the shared context, and other modules see only `Contracts` (architecture rules 1, 2, 8 and 10). PostgreSQL enforces foreign keys inside `core` just as across schemas.
3. **Composition.** Module services, seeders in `Order`, permissions, the role catalogue and the menu contributor.
4. **The folder and namespace layout,** checked by the architecture tests.

What it no longer decides is the schema. The table list per module is still available from the model, by grouping entity types on `ModuleDefinition.Of`. Use that list for documentation or a report, not pgAdmin folders.

### Changes, in order

1. **Schema.**
   - Restore `GxNamingConventions.BusinessSchema = "core"`.
   - For every `IBusinessEntity` with its own table, set `core` whether or not the entity declares a module. This is exactly the baseline loop.
   - Keep the module requirement in `DataModelGuard`. It is the guard that keeps ownership honest, not the schema.
2. **Hand-named tables (C5).** Choose one:
   - **Recommended:** restore the baseline rule. `ToTable(name)` with no schema stays where it was put, as before pass 60.
   - **Alternative:** keep pass 60's spirit but send such tables to `core` rather than to a module schema.

   Either way, an explicit schema (`ToTable(name, "reporting")`) wins, as at baseline and HEAD.
3. **Joins (C6).** Restore "in `core` when either side is in `core`".
4. **`ModuleDefinition`.**
   - Remove `Schema`, `ReservedSchemas` and `SchemaProblem`.
   - Keep `Name` and `Order`, and the name-uniqueness check.
   - This is a breaking change to 1.7.x's public API: a module definition that overrides `Schema` stops compiling. That is the right failure, because it is loud. The alternative is to keep `Schema` as `[Obsolete]` and ignore it; that is quieter and leaves a misleading member.
5. **`CheckTableNames`.**
   - Keep a uniqueness check, reworded: two entities mapping to one table name.
   - In one schema, EF would either fail or table-split silently. The bulk-write guard and SQLite still need unique names.
6. **Tests.**
   - Revert `GxTableNamingTests` to the `core` assertions. Keep `SampleModule` as the module the samples belong to, and assert that it does **not** affect the schema.
   - Keep `NoTemplateEntity_IsMappedIntoTheCoreSchema` and `EveryTableOutsideCore_IsOneOfTheTemplates` as at baseline, reading the list from `TemplateEntities`.
   - Rewrite `ModuleSchemaTests`:
     - T16 asserts that `TBL_ALPHA_THING`, `TBL_BETA_THING` and `TBL_TEST_LEDGER_ROW` are in `core`, and the template tables in `public`;
     - the cross-module foreign key test stays, now within `core`;
     - T17 asserts one `EnsureSchema("core")` and no other.
   - In `ModuleGuardTests`, drop the schema rows (T15 shared, invalid and reserved), keep the module rows, and reword the table-name row.
   - In `TestModel.cs`, remove the `Schema` overrides.
   - `ModelHarness` keeps dropping `core` and `public`.
7. **Sample module and smoke.**
   - `SampleModule` loses `Schema`.
   - `S1` expects `core.TBL_SAMPLE_BIN`.
   - `smoke-generate.ps1:621` checks for `EnsureSchema(name: "core")`.
8. **README.**
   - "Database naming" goes back to the baseline contract, adding one line: "every module's tables are in `core`; `[Module<T>]` decides ownership, not the schema".
   - "Modules and schemas" is renamed "Modules". Remove the schema content and keep the rest, including the foreign-key and layout material and the checklist without "and the schema".
   - Fix the `AppendOnlyMigrationExtensions` comment to say `core`, and add a release note.
9. **Migrations.** Nothing shipped changes. If any project was generated from 1.7.0 or 1.7.1 and has applied module migrations, it needs a hand-written migration that moves each table with `ALTER TABLE <s>."TBL_X" SET SCHEMA core` (EF: `migrationBuilder.EnsureSchema("core")` plus `RenameTable(name, schema: s, newName: name, newSchema: "core")`) and then drops the empty schemas (Q14).
10. **Version.** A minor bump (1.8.0), because `ModuleDefinition.Schema` disappears (Q15).

**What stays exactly as pass 60 and 60b left it:**

- `[Module<T>]` and the no-module startup failure;
- `TemplateEntities`;
- the cross-module navigation rule and the TPH and owned rules;
- foreign keys across modules without navigations;
- the architecture tests T22-T26 and T42 and the module boundary tests;
- the configuration scan by exclusion and its test;
- `ModuleServices<T>`, `AppLevelServices`, `[NotAService]`, attribute-decided permissions, the role catalogue, `IMenuContributor` and branch extension points;
- the sample module and the smoke run, apart from the schema assertion.

**Separately, not part of restoring `core`:** the questions below on what belongs in `public` (Q1), plurals (Q4), column casing (Q2) and lookups (Q5) concern conventions that differed **before** pass 58. They should be decided before the first application is generated, because each is costly to change afterwards. They should not be folded into the `core` fix without a decision.

---

## 5. Questions for the owner

Q1 to Q12 are listed in full in `docs/gx-conventions.md` §12. Their short forms:

- **Q1.** What may live in `public`? Identity only (your statement), or framework tables (MNEFleets' practice)? In particular, are `Branches`, `UserBranches`, `ApprovalLimits` (passes 59-60) and `Documents` framework tables or business tables? Should they be `core."TBL_…"`?
- **Q2.** Columns: PascalCase (GXTemplate, deliberate and tested) or snake_case (MNEFleets)?
- **Q3.** MNEFleets documents with stale counts and lists: should they be corrected?
- **Q4.** Table names: plural (MNEFleets, 149 of 193), derived from the class (GXTemplate), or written by hand?
- **Q5.** What is a lookup, and does it have fixed columns (MNEFleets `BaseLkEntity`: Id, Name, Slug)?
- **Q6.** Singular class names?
- **Q7.** Constraint names: EF default or a house scheme?
- **Q8.** Hand-named unique indexes: always `ux_tbl_…`?
- **Q9.** `timestamptz` for new applications?
- **Q10.** `[StringLength]` silently overridden by the 450 default: intended?
- **Q11.** Is there a route convention?
- **Q12.** `TBL_LK_PAYMENT_TYP`: dead?

Questions arising from this audit:

- **Q13. Keys.** Your pass 58 direction (Guid v7 document headers, bigint lines) departs from MNEFleets (int and long identity, no Guid). Confirm it is the new convention, not a departure to undo.
- **Q14. Generated applications.** Has any project been generated from 1.7.0 or 1.7.1 and migrated with module schemas (for example the Spare Parts system)? If so, restoring `core` needs a table-move migration there.
- **Q15. Restoring `core`.** Do you want it, as recommended in §4?
  - If so, at what version (1.8.0 is suggested, because `ModuleDefinition.Schema` is removed)?
  - Should `Schema` be removed outright or kept `[Obsolete]`?
  - For a table named by hand without a schema: the baseline rule (it stays where it was put) or `core`?
- **Q16. Process.** Pass 60 changed a house convention without saying so, because the review and plan did not check against the conventions. Should future plans include a "GX conventions touched" section, citing `docs/gx-conventions.md`?
