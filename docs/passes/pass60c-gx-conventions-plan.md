# Pass 60c: restore the GX conventions (plan)

**Status:** written 2026-11-12 at HEAD `368de373` (pass 60b report; code at `e9085bd4`, template 1.7.1, tag `gx-v1.7.1`). Amended the same day with the owner's decisions (§9) and amendment G1 (§3.10), then implemented in this pass. Text marked **(G1)** comes from the amendment. Text marked **(P60c)** was added during implementation, where the code required something the plan did not say; each (P60c) addition is listed in the pass report (`docs/passes/pass60c.md`).

**Database:** PostgreSQL only. Npgsql-only code sits under `#if (UsePostgreSql)`. The other two providers still build, but they are not targets.

**Authority:**
- `docs/gx-conventions.md` (MNEFleets, as practised);
- `docs/passes/conventions-audit.md`;
- owner decisions D1 to D7, given with the brief and quoted in §2.

Where the conventions document leaves a question open and D1 to D7 do not settle it, this plan recommends an answer and lists it in §9. It does not decide.

**Source of T9, T11 and T12:** the Spare Parts project (`C:\Yoab\Projects\SpareParts`), plan `docs/passes/01-foundation-org-catalogue-plan.md`:
- §12, "Collision sweep", rows S1 to S9;
- §14, "Template changes needed";
- commit `7c38ba4` (T11).

I read them there, read-only.

**Ships as:** template **1.8.0**. The release is breaking, because `ModuleDefinition.Schema` is removed and the approval contracts change their key type.

**Unchanged:**
- Pass 58's unit of work, idempotency, numbering and row locks.
- Pass 59's save rules, branch filter, `BranchAccessBehaviour` and `AcrossBranchesAsync`.
- Pass 60's approval policy, architecture rules, error handling and session ending.
- Pass 60b's extension points.
- The pinned suites must pass **unmodified**: `SaveChangesInterceptorRegressionTests`, `InterceptorOrderingTests` (exactly two `ISaveChangesInterceptor`s), `TransactionalAuditTests` and the publisher suites. I checked all three interceptor suites: none names a schema, a business table, a column or a Guid header.

---

## 0. GX conventions touched

This is the section D7 requires in every plan from now on. This pass exists to bring the template back to the conventions, so every row should read "conforms". The departures are the open questions in §9.

| Convention (`docs/gx-conventions.md`) | Effect of this pass | Conforms? |
|---|---|---|
| §1.1 business tables in `core` | Restored (D1) | Conforms |
| §1.2 `public` holds the framework's tables only | Template tables stay in `public` with their names (D2). Hand-named business tables may also land there (D1); see §8.1 | Conforms, apart from §8.1 |
| §2.1 `TBL_` + UPPER_SNAKE | Unchanged | Conforms |
| §2.2 plural table names | Made the rule, with a pluraliser (D3) | Conforms. MNEFleets' own 44 singular names are not followed. |
| §2.3 / §2.4 how class names relate to table names | The name is derived from the class, with an override (D3) | Departs from "written by hand"; decided by D3 |
| §3.1 `TBL_LK_` | Unchanged | Conforms |
| §3.2 lookup shape: Id, Name, Slug | Base classes plus a startup check (D4, Q-A2, Q-A3) | Conforms |
| §3.3 what a lookup is | A reference list users pick from, as MNEFleets practises it (Q-A1) | Conforms |
| §3.4 seeding by migration with explicit ids and a sequence reset | A migration helper is added | Conforms |
| §3.6 editing: one page per table, no generic editor | Nothing is shipped | Conforms |
| §4.1 snake_case columns | Business tables only (D5) | Conforms |
| §4.3 / §4.4 integer keys, int for masters, long for transactions | D6; a startup check | Conforms |
| §4.5 / §4.6 constraint names `pk_` / `fk_` / `ix_`, hand-named `ux_tbl_` / `ck_tbl_` | Defaults generated; hand-named `ux_tbl_` and `ck_tbl_` enforced on business tables (Q-E) | Conforms |
| §5.1 to §5.6 audit columns, soft delete, concurrency, tenancy | Unchanged; only the column casing changes | Conforms |
| §5.2 timestamps (`timestamp without time zone` in MNEFleets) | Unchanged: the template keeps `timestamptz` | **Departs**, by owner decision Q-F, named as a departure |
| §6.1 `[StringLength]` overridden by the 450 default | Unchanged; the README says a length must be set in the configuration; the attribute fix is queued for pass 61 (Q-G) | Conforms (MNEFleets has the same behaviour) |
| §6.3 enums stored as `integer` (template README: as a string) | Integer storage confirmed; every member of an enum used by an entity has an explicit value, checked at startup; the README is corrected (Q-H) | Conforms |
| §7 flat `Domain/Entities` | The template's `Modules/<M>/` layout stays (an addition, audit C16) | No conflict |
| §8 roles, permissions, routes, menu | Routes: lower-case kebab under the module area (conventions Q11, decided); class names singular in new code (Q6, decided) | Conforms |

---

## 1. What is wrong today (verified in code)

### 1.1 Schemas (D1, D2)

- **The schema comes from the module.**
  - `GxNamingConventions.ApplyGxTableNaming` sets `entityType.SetSchema(module.Schema)` (`src/Infrastructure/Persistence/Extensions/GxNamingConventions.cs:55-65`).
  - Joins take their schema the same way (`:104-127`).
  - `BusinessSchema = "core"` is gone. It was at `:33` at `d9d9c75f`.
- **A table named by hand with no schema goes to its module's schema** (`:55-65`; test `ATableNamedByHandWithoutASchema_StillGoesToItsModulesSchema`, `tests/Infrastructure.UnitTests/Persistence/GxTableNamingTests.cs:363`).
- **`ModuleDefinition.Schema`, `ReservedSchemas` and `SchemaProblem`** are at `src/Domain/Common/Modules/ModuleDefinition.cs:31-53`. `DataModelGuard` checks them (`src/Infrastructure/Persistence/Rules/DataModelGuard.cs:159-175`).
- The full list of what depends on module schemas is in the audit, §3.

### 1.2 Table names are singular (D3)

- The name is `TBL_` + `ToUpperSnake(clr.Name)` (`GxNamingConventions.cs:99-101`), so `StockMovement` gives `TBL_STOCK_MOVEMENT`.
- There is no pluralisation.
- No test lists the real model's table names, so a rename shows up only in a migration.

### 1.3 Lookups have no shape (D4)

- `ILookupEntity` is an empty marker (`src/Domain/Common/Entities/IBusinessEntity.cs:27-55`).
- There is no lookup base class and no check on lookup columns.
- There is no migration helper to reset an identity sequence after explicit-id inserts.
- Spare Parts already has three lookups built on `BaseVersionedEntity`: `Brand`, `VehicleMake` and `VehicleModel` (`SpareParts/src/Domain/Modules/Catalogue/Entities/CatalogueEntities.cs:14,45,52`).

### 1.4 Columns are PascalCase everywhere (D5)

- `UseSnakeCaseNamingConvention()` is applied only to `LogDbContext` (`src/Infrastructure/DependencyInjection.cs`, `UseDatabase`'s `snakeCaseNaming` flag). That is deliberate: README "Database naming", consequence 2.
- The plug-in works on the whole model. On `ApplicationDbContext` it would also rename:
  - the Identity columns;
  - the framework tables' columns;
  - `__EFMigrationsHistory` (`migration_id`, `product_version`).
- Test `OnPostgres_TheBusinessModelIsNotSnakeCased` (`GxTableNamingTests.cs:506`) pins its absence.

**Places that write SQL by hand or build it from names:**

| # | Where | What it builds | Names from |
|---|---|---|---|
| S1 | `src/Infrastructure/Persistence/Transactions/PostingSql.cs:50-60` (`Names<TEntity>`) | Quoted schema, table and column names | The model (`GetTableName`, `GetSchema`, `GetColumnName(StoreObjectIdentifier)`) |
| S2 | `Transactions/IdempotencyStore.cs:25-61` | `INSERT … ON CONFLICT`, `SELECT`, `UPDATE` on `ProcessedRequests` | S1 (a framework table) |
| S3 | `Transactions/DocumentNumberAllocator.cs:24-35` | An upsert on `DocumentSequences` | S1 (a framework table) |
| S4 | `Transactions/InsertIfAbsent.cs:58-64` | `INSERT … ON CONFLICT (key) DO NOTHING` on a **business** table | S1 |
| S5 | `Rules/AppendOnlyMigrationExtensions.cs:25-60` | `CREATE TRIGGER … ON <schema>.<table>` | Arguments the migration passes |
| S6 | `Rules/DataModelGuard.cs:377-400` (`FindTablesMissingTriggersAsync`) | A `pg_trigger` query by schema and table | The model |
| S7 | `Interceptors/GuardedTableCommandInterceptor.cs:133-150` (bulk-write guard) | A regex over `UPDATE` / `DELETE FROM` / `TRUNCATE <table>` | Table names from the model; case-insensitive |
| S8 | `Interceptors/GuardedTableCommandInterceptor.cs:111-127` (cache guard, rule 3) | A regex over `FROM` / `JOIN <table>` | The same |
| S9 | `Interceptors/ForUpdateCommandInterceptor.cs:50-77` | Appends `FOR UPDATE` | No names |
| S10 | `ApplicationDbContext.cs:203-207` | `HasFilter("\"IsDefault\"")` on `UserBranches` | A literal (framework table) |
| S11 | `Configurations/PicklistSetConfiguration.cs:59` | `HasFilter("\"TenantId\" IS NULL")` | A literal (framework table) |
| S12 | Tests: `DataFoundationsTests.cs:384` (`UPDATE tdf."TBL_TEST_LEDGER_ROW" SET "Amount" = 0`), `:404`; `SharedDatabaseTests.cs:151-185` (`"TBL_LK_HARNESS_PROBE" ("Id" …, "Name")`); `ModuleSchemaTests.cs` | SQL literals on business-shaped tables | Literals |
| S13 | `Persistence/Logging/LogTableDdl.cs:249-295` | The log table DDL | Literals. Already snake_case on PostgreSQL; a separate context |

### 1.5 Document headers are Guid (D6)

**Approval contracts and the code built on them:**

| Item | Where | Today |
|---|---|---|
| `IApprovable.Id` | `src/Domain/Common/Entities/IApprovable.cs:22-23` | `Guid Id { get; }`, documented "document headers are Guid v7 (pass 58)" |
| `IApprovalCommand.DocumentId` | `src/Application/Common/Approvals/ApprovalContracts.cs:13` | `Guid` |
| `ApprovalGrant` | `ApprovalContracts.cs:126-150` | `Node(Guid headerId…)`, `ApproverOf(Guid)`, `Begin(Guid, …)` |
| The behaviour's load by id | `src/Application/Pipeline/ApprovalBehaviour.cs:96, 111-113` | `Func<IApplicationDbContext, Guid, …>`, `LoadTyped<THeader>(…, Guid id, …)` |
| The guard message | `src/Infrastructure/Persistence/Rules/DataModelGuard.cs:50-53` | "derive from `BaseVersionedEntity<Guid>`" |

**Guidance and examples that name Guid headers:**

- `IApprovable.cs:40` (`PurchaseOrder : BaseVersionedEntity<Guid>`);
- `ModuleDefinition.cs:75` and README "Modules and schemas" checklist item 2 ("headers are `BaseVersionedEntity<Guid>`");
- README "Approvals".

**Tests that use Guid headers:**

- `tests/Application.IntegrationTests/DataFoundations/TestModel.cs:92, 107, 165` (`TestApprovableDocument`, `TestPlainApprovable` and `TestPostedDocument` derive from `BaseVersionedEntity<Guid>`);
- `ApprovalTests.cs` (17 Guid references);
- `tests/Application.UnitTests/DataFoundations/ApprovableGuardTests.cs` (5).

**The sample module:**

- `SampleBin : BaseEntity<Guid>` with `Id = Guid.CreateVersion7()` (`tooling/sample-module/src/Domain/Modules/Sample/Entities/SampleBin.cs:8-10`);
- `SampleBinCreated(Guid binId, …)` takes the id in its constructor (`…/Contracts/SampleContracts.cs:7-9`). This works only because a Guid id exists before the save. With an identity key it would capture 0.

**The pattern to follow already exists.**
- `BranchCreatedEvent` keeps the entity and reads `BranchId => _branch.Id` when asked (`src/Domain/Events/BranchEvents.cs:10-19`).
- This is safe because `TransactionBehaviour.DrainPostingEventsAsync` saves before it dispatches each round (`src/Application/Pipeline/TransactionBehaviour.cs:189-206`).

**Not affected, and staying as they are:**
- `Tenant` / `TenantUser` string ids (Guid v7 text);
- `ProcessedRequest.RequestId` (a Guid idempotency key, not an entity key of a business table);
- the Identity string keys.

### 1.6 Working rules (D7)

- The repository has no `CLAUDE.md`. `.claude/` is empty.
- Plans follow a format by habit only.
- The template package excludes `docs/**`, `tooling/**` and `GXTemplate-passes/**` (`.template.config/template.json:221-247`).
- A `CLAUDE.md` at the root would be shipped into every generated project unless it is excluded.

### 1.7 T9, T11, T12 (Spare Parts sweep)

**T9: template tests send branch commands that an application's branch rules may refuse.**

The integration harness runs every application validator and posting handler (`tests/Application.IntegrationTests/DataFoundations/ModelHarness.cs:111`). Template tests send `AddEditBranchCommand` with lower-case codes of 7 to 9 characters:

| File | Sends | Codes and names |
|---|---|---|
| `tests/Application.IntegrationTests/Extensions/BranchExtensionTests.cs` | 15 | `"E"` + 8 hex; repeated names "North", "South", "East", "West", "Retry" |
| `Extensions/DefaultBranchTests.cs:23` | 1 | — |
| `DataFoundations/PicklistVersionTests.cs:229` | 1 | `"R"` + 6 hex |
| `tests/Server.UI.IntegrationTests/Extensions/BranchScreenTests.cs` | 1 | — |
| The sample module's tests | 2 | `"S"` + 6 hex |

`ModelHarness.cs:200` inserts fixture branches directly, with `"B"` + 8 hex codes and the repeated name "Branch". So does any rule that adds a unique database index on `Branches.Name`, such as Spare Parts' planned one (sweep S2).

**T11: the probe entity `Warehouse` collides with any application entity of that name.**
- `tests/Application.UnitTests/DataFoundations/DataModelGuardTests.cs:80, 85, 131` declares `public class Warehouse : BaseEntity` inside `GuardContext`, which derives from the real `ApplicationDbContext`.
- An application entity `Warehouse` adds a second `TBL_WAREHOUSE`, and T25's `ContainSingle()` fails.
- Spare Parts fixed it with commit `7c38ba4`, which renames the type to `GuardProbeWarehouse` on those three lines and changes nothing else.
- Under D1 and D3 the clash would be the same table name in one schema, `core."TBL_WAREHOUSES"`. That is worse: EF would treat two entity types mapped to one table as table splitting, not as a guard finding.

**T12: a hard-coded role total.** `tests/Application.UnitTests/Identity/Roles/RoleDefinitionRightTests.cs:484` asserts `roleManager.Roles` has exactly 3 roles. It passes today only because its provider does not provision the role catalogue (sweep S3).

**Elsewhere in the sweep and the Spare Parts template-change list (§14 of that plan):**
- S2: recheck when T9 lands. It is covered by §3.7.
- T10: a README note on branch-defining commands nested in a non-defining request. Spare Parts marks it optional; it is small, so it is included (§3.7).
- T5 (permission discovery for `Module.Entity.Action` nesting), T7 (Excel import) and T8 (label printing) are not convention or test-collision items. They stay out of this pass: T7 and T8 belong to pass 61, and T5 is optional.
- S4 to S9 need no change.

---

## 2. Owner decisions (as given)

- **D1. Restore the `core` schema** (audit §4).
  - Every business table is in `core`, whether or not it declares a module.
  - A module is a code boundary: ownership, no cross-module navigation, composition and layout.
  - `ModuleDefinition.Schema` is removed outright.
  - A table named by hand with no schema stays where it was put, as before pass 60.
  - Joins go to `core`, as before.
- **D2. `public` holds Identity and the template's framework tables.** They stay where they are, with their current names.
- **D3. Table names are plural:** `TBL_` + the plural entity name in upper snake case. `StoreMovement` and `StoreMovements` both give `core."TBL_STORE_MOVEMENTS"`. Pluralisation is deterministic, with an explicit override, and a wrong plural fails loudly or is pinned by a test.
- **D4. Lookups are `TBL_LK_` tables with the fixed columns of MNEFleets' `BaseLkEntity`:** Id, Name, Slug. There is a base class, and an `ILookupEntity` without those columns fails at startup.
- **D5. Business-table columns are snake_case.** Identity, the framework tables and the migrations history are not touched.
- **D6. Keys are integers:** `int` for master data and lookups; `bigint` for document headers, lines and ledger rows; no Guid keys. A posting event never captures an id before the row is saved.
- **D7. Every future plan carries "GX conventions touched".** This is added to the working rules, with a check where one can enforce it.

---

## 3. Components

### 3.1 Schema restored (D1, D2)

**`GxNamingConventions`:**

- **Business tables.** Restore `public const string BusinessSchema = "core";`. The first pass goes back to the baseline loop (`d9d9c75f:GxNamingConventions.cs:45-97`): every `IBusinessEntity` with its own table, not owned and not named by hand, gets `SetSchema("core")` and its `TBL_` name.
  - Membership stays a type test, as before.
  - The module plays no part in naming.
- **Hand-named tables.** These are `ToTable(name)` or `[Table(name)]` with no schema. Both stay where they were put, which is the default schema (the baseline rule; D1). An explicit schema (`ToTable(name, "reporting")`) wins, as it does today.
- **Joins.** A join goes to `core` when either side lives in `core`. It is named `TBL_` + plural (§3.2).
- **Template entities.** No change. They name themselves with schema-less `ToTable` calls, and `TemplateEntities` lists them (`src/Infrastructure/Persistence/Rules/TemplateEntities.cs`).

**`ModuleDefinition`:**

- Remove `Schema`, `ReservedSchemas` and `SchemaProblem` (`ModuleDefinition.cs:31-53`).
- Keep `Name`, `Order` and `Of(Type)`. Rewrite the class and attribute remarks: "a module is a code boundary; its tables are in `core`".
- A 1.7.x module definition that overrides `Schema` stops compiling (CS0115). That is the intended loud failure. The release notes say to delete the line.

**`DataModelGuard`:**

- **Kept:**
  - the no-module check, which is the ownership check (`:111-116`);
  - template entities declare no module (`:105-110`);
  - TPH leaves and owned types stay in their root's module;
  - no navigation across modules (`:120-145`);
  - the module-name uniqueness check.
- **Removed:** the schema checks: invalid, reserved and shared schema (`:166-171`).
- **`CheckTableNames` (`:177-190`) is reworded and kept.** It refuses two entity types that are not in one hierarchy and that map to one table name, in any schema.
  - In one schema EF would otherwise treat them as table splitting (or fail obscurely).
  - The bulk-write and cache guards still match by name alone.
  - SQLite has no schemas.
  - This is the guard that would have caught T11 by name.
- **New, recommended (§8.1, Q-B):** fail when a **module** entity's table resolves to the default schema. That happens with a hand-named `ToTable("X")` and no schema. D1 keeps that rule for where the table goes; D2 says `public` holds framework tables only. The message would read: "{Entity}: a module entity's table is in public. Name it without ToTable, or give the schema explicitly."

**Configuration scan and architecture tests:** no change.

### 3.2 Plural table names (D3)

**Mechanism.** One function, `GxNamingConventions.TableNameFor(Type clr, bool lookup)`, returns `TBL_` or `TBL_LK_` + `ToUpperSnake(Pluralise(clr.Name))`. **Only the last word is pluralised.** A word is a camel segment, so `StockMovement` gives `Stock` + `Movements`.

**The rules for the last word, applied in this order. The first rule that matches wins.**

1. **Override.** `[PluralName("Criteria")]` on the class (Domain, `src/Domain/Common/Entities/PluralNameAttribute.cs`) supplies the plural of the **whole** class name, verbatim. For example, `[PluralName("StockHistory")]` keeps a singular name on purpose. The guard refuses a value that is not a PascalCase identifier.
2. **Irregular words.** A small, fixed dictionary in the template, for example `Person→People`, `Child→Children`, `Man→Men`, `Woman→Women`, `Criterion→Criteria`, `Datum→Data`, `Medium→Media`, `Index→Indices`, `Matrix→Matrices`, `Leaf→Leaves`, `Shelf→Shelves`, `Half→Halves`, `Knife→Knives`, `Life→Lives`, `Wife→Wives`. Anything not in it is an override (rule 1).
3. **Uncountable words, left unchanged:** `Equipment`, `Information`, `Data`, `Series`, `Species`, `News`, `Staff`, `Personnel`, `Feedback`, `Software`, `Metadata`, `Settings`. This list is fixed too.
4. **Already plural, left unchanged.** The word ends in `s`, but not in `ss`, `us`, `is`, `as` or `os`. So `Movements`, `Invoices`, `Categories` and `Days` are unchanged. This is what makes `StoreMovement` and `StoreMovements` give the same table.
5. **Ends in `sis`:** `sis` becomes `ses` (`Analysis → Analyses`, `Basis → Bases`).
6. **Ends in `s`, `ss`, `x`, `z`, `ch` or `sh`:** add `es` (`Status → Statuses`, `Address → Addresses`, `Box → Boxes`, `Batch → Batches`, `Bus → Buses`, `Gas → Gases`).
7. **Consonant followed by `y`:** `y` becomes `ies` (`Category → Categories`, `Company → Companies`, `History → Histories`). A vowel followed by `y` adds `s` (`Day → Days`, `Key → Keys`).
8. **Otherwise:** add `s` (`Warehouse → Warehouses`, `Bin → Bins`, `Uom → Uoms`).

`ToUpperSnake` stays exactly as it is, acronym handling included: `IMSSetting → IMS_SETTINGS`, `UomConversion → UOM_CONVERSIONS`.

**Lookups and joins:**

- A lookup is `TBL_LK_` + the same plural (conventions §3.1: `TBL_LK_GENDERS`, `TBL_LK_BANKS`).
- A join is `TBL_` + the upper-snake of EF's join name, with its **last word** pluralised: `SampleCourseSampleStudent` gives `TBL_SAMPLE_COURSE_SAMPLE_STUDENTS`.
  - The conventions document has no implicit joins: MNEFleets maps its link tables as entities, for example `TBL_REPAIR_STATUS_TECHNICIANS`.
  - The join rule is therefore a recommendation (Q-C).
- A hand-named join keeps its name.

**How a wrong or surprising plural is caught:**

1. **Unit tests of the pluraliser** (`PluralisationTests`): at least 60 cases, covering every rule and every dictionary entry, and the owner's own example: `StoreMovement` and `StoreMovements` both give `TBL_STORE_MOVEMENTS`.
2. **A table-list test that ships into every generated project:** `ModelTableListTests.TheModelsTables_AreTheApprovedList` (Application.UnitTests).
   - It builds the real model and prints one line per table: `schema.table  ← Entity`.
   - It compares that list with a checked-in file, `tests/Application.UnitTests/Model/tables.approved.txt`.
   - Any added, removed or renamed table fails the test. The message prints the difference and the one-line command that rewrites the file (`GX_APPROVE_TABLES=1 dotnet test …`).
   - The new name is therefore visible as a diff in review before it reaches a migration.
   - In the template the file lists the 20 template tables. A generated project's first module entity fails the test once, by design. The file is data owned by the application, **not a template test edit**; the README says so (Q-D).
3. **The startup guard** refuses two entities with one table name (§3.1), so a plural that collides (for example `Datum` and `Data`) cannot produce a silent table split.
4. **`ModelMatchesMigrationsTests`** still fails if the model and the migrations differ.

### 3.3 Lookups (D4)

**Base classes** (`src/Domain/Common/Entities/BaseLkEntity.cs`):

```csharp
public abstract class BaseLkEntity : BaseEntity<int>, ILookupEntity
{
    public string Name { get; set; } = "";
    public string? Slug { get; set; }
}

public abstract class BaseVersionedLkEntity : BaseVersionedEntity<int>, ILookupEntity
{
    public string Name { get; set; } = "";
    public string? Slug { get; set; }
}
```

- The second class exists because C# has single inheritance, and lookups that users edit need the pass 59 version check. Spare Parts' `Brand`, `VehicleMake` and `VehicleModel` are versioned and auditable today.
- The name `BaseLkEntity` follows MNEFleets, so code moves between the two unchanged (Q-A3).

**`ILookupEntity` declares the fixed members**, so a class without them does not compile:

```csharp
public interface ILookupEntity : IBusinessEntity
{
    int Id { get; }
    string Name { get; }
    string? Slug { get; }
}
```

**Startup check (`DataModelGuard`, new "lookup" rule).** For every `ILookupEntity` in the model, these all fail, naming the entity and the column:

- the key is not a single `int` property `Id`;
- `Name` or `Slug` is not mapped. This catches `Ignore(x => x.Name)` and owned or complex types;
- `Name` is nullable in the database;
- the table is not `TBL_LK_…` in `core`, which a hand-named lookup could cause.

**What else a lookup may carry.** The conventions document records practice, not a limit, so this is a recommendation (Q-A2).

- **May:** a `Code`, `IsActive`, a `SortOrder`, the audit columns, the version, a description, and a foreign key to another lookup. MNEFleets: `TirePositions.SortOrder`; the `ApprovalStatus` ordering columns.
- **Should not, and becomes `TBL_` instead:** money or quantity columns (`[Money]`, `[Rate]` …), or a foreign key to a non-lookup business table.
  - MNEFleets has exactly these under `TBL_LK_`: `TBL_LK_DISTANCE_RATES`, `TBL_LK_TANKS` and `TBL_LK_PAYMENT_PURPOSE`.
  - Whether the guard enforces this is Q-A2. I recommend a startup failure for decimal columns only, which is the clearest sign of master data with rules.

**What makes a table a lookup** is conventions question Q5. It is still open (Q-A1).
- **MNEFleets:** anything used as a reference list.
- **The template README:** "does any code branch on this row's value?". MNEFleets breaks this through its `MS.*Id` constants.

**Seeding** follows the conventions document §3.4. It is seeded by migration, with explicit ids, and the identity sequence is reset in the same migration. New migration helper (`src/Infrastructure/Persistence/Rules/LookupMigrationExtensions.cs`):

```csharp
migrationBuilder.SeedLookup("TBL_LK_WAREHOUSE_TYPES", ("1", "Main"), ("2", "In transit"));   // id, name; slug from the name
migrationBuilder.ResetIdentitySequence("TBL_LK_WAREHOUSE_TYPES");                           // schema "core" by default
```

- On PostgreSQL, `ResetIdentitySequence` runs `SELECT setval(pg_get_serial_sequence('core."T"', 'id'), coalesce(max(id), 0) + 1, false) FROM core."T"`. On the other providers it does nothing.
- This replaces MNEFleets' `core.advance_identity_sequences` function with a helper, so the template does not need a function in `core`.
- `PostgresTestDatabase` already keeps `TBL_LK_` rows when it resets between tests (`tests/TestSupport/PostgresTestDatabase.cs:53-57, 222, 269`). Nothing changes there.

**Editing:** the template ships no lookup editor. Conventions §3.6 says MNEFleets uses one page per table and no generic editor. The README says so; a generic editor would be a later pass (Q-A4).

### 3.4 snake_case on business tables only (D5)

**Mechanism: a model-finalizing convention, not the plug-in.**

- `GxSnakeCaseConvention : IModelFinalizingConvention` is registered in `ApplicationDbContext.ConfigureConventions` (`ApplicationDbContext.cs:391`). It is in-house: no new package, and `EFCore.NamingConventions` stays on the log context only.
- It runs after `OnModelCreating`, after `ApplyGxTableNaming` and after EF's own conventions have created the shadow foreign keys, indexes and keys.
- It acts only on **business tables**. A business table is any table mapped by an entity type that is not in `TemplateEntities`, is not an Identity type, and is not the history table, plus any join that has a business side. Identity, the framework tables, `__EFMigrationsHistory` and `LogDbContext` are not visited.

**What it sets, each only where the name was not configured by hand** (`ConfigurationSource` below Explicit and DataAnnotation, as `IsNamedByHand` does today):

| Object | Rule | Example |
|---|---|---|
| Column | `ToSnake(property.GetColumnName())` | `CreatedById → created_by_id`; owned `Address_Street → address_street`; `Discriminator → discriminator`; `xmin` unchanged |
| Primary key | `pk_` + lower(table) | `pk_tbl_warehouses` |
| Foreign key | `fk_` + lower(table) + `_` + lower(principal table) + `_` + the snake columns | `fk_tbl_warehouses_branches_branch_id` |
| Index | `ix_` + lower(table) + `_` + the snake columns | `ix_tbl_warehouses_branch_id` |
| Unique index | the same `ix_` name, like the plug-in | `ix_tbl_warehouses_branch_id_code` |
| Alternate key | `ak_` + lower(table) + `_` + the snake columns | — |

- **Long names.** Names longer than 63 characters are cut by EF's own uniquifier, exactly as in MNEFleets (conventions §4.5).
- **`ToSnake`** follows the `EFCore.NamingConventions` snake-case rewriter: a boundary between a lower-case letter or digit and an upper-case letter, then lower case.
  - Its unit tests pin MNEFleets' real names: `EmployeesId → employees_id`, `RaisedById → raised_by_id`, `TripKitsId → trip_kits_id`, `BHOrderId → bh_order_id`.
- **Names set by hand.**
  - A column named by hand (`HasColumnName`, `[Column]`) is left alone.
  - A constraint named by hand (`HasDatabaseName`, `HasConstraintName`) is left alone. The guard checks its prefix if Q-E is decided.

**Hand-written SQL fragments in business configurations** (`HasFilter`, `HasCheckConstraint`, `HasComputedColumnSql`, `HasDefaultValueSql`) must name the snake column.

- New helper: `GxSql.Column(nameof(Warehouse.IsMain))` returns `"is_main"` quoted. It uses the same `ToSnake`, so a filter reads `HasFilter($"{GxSql.Column(nameof(Warehouse.IsMain))}")` and survives a rename.
- A wrong literal still fails loudly at `EnsureCreated` in the integration harness and at `database update`.
- The README's "Hand-written SQL must quote its identifiers" changes. Table names still need quotes (`core."TBL_WAREHOUSES"`), because they are upper-case. Columns do not, but quoting stays recommended.

**Each place in §1.4, and what it needs:**

| # | Place | Needs |
|---|---|---|
| S1 | `PostingSql.Names` | Nothing. It reads `GetColumnName(StoreObjectIdentifier)` from the finalized model. A unit test pins `Names<TestBalance>` returning `core."TBL_TEST_BALANCES"` and `"item_code"`. |
| S2 | `IdempotencyStore` | Nothing. `ProcessedRequests` is a framework table and keeps `"RequestId"` and the rest. |
| S3 | `DocumentNumberAllocator` | Nothing. `DocumentSequences` is a framework table. |
| S4 | `InsertIfAbsent` | Nothing in the SQL: names come from S1. The `ON CONFLICT` key check (`:110-118`) matches indexes by property, not by name. Its integration tests (`StructureTests.cs:132-227`) run on snake-case `TBL_TEST_BALANCES` and prove it. |
| S5 | `CreateAppendOnlyTrigger(table, schema)` | Change the default schema from `null` to `"core"`. Callers pass the plural table name. The README example becomes `CreateAppendOnlyTrigger("TBL_STOCK_MOVEMENTS")`. Fix the comment at `:12`, which still says `"TBL_STOCK_MOVEMENT", "core"`. |
| S6 | `FindTablesMissingTriggersAsync` | Nothing. It reads schema and table from the model (`GetSchema() ?? "public"`). |
| S7 | Bulk-write guard | Nothing. It matches table names, which stay upper-case `TBL_`, case-insensitively, with an optional schema. Add a test row for `UPDATE core."TBL_TEST_LEDGER_ROWS" SET amount = 0`. |
| S8 | Cache guard | Nothing, for the same reason. Its tests (`CacheRuleTests.cs:54-67`) change only in the expected plural names. |
| S9 | `ForUpdate` | Nothing. It names nothing. |
| S10, S11 | Filters on framework tables | Nothing. Those tables stay PascalCase (D2, D5). |
| S12 | Test SQL literals | Rewrite to snake columns and plural names. `SharedDatabaseTests`' probe stays in `public`, as a stand-in lookup created by hand, but its columns become `id` and `name`, so it shows the convention. |
| S13 | Log DDL | Nothing. A separate context, already snake_case. |

**Also affected, by reading names rather than writing SQL:**

- **`DbExceptionHandler`** (`src/Application/Common/ExceptionHandlers/DbExceptionHandler.cs:53-90`). It strips the schema and shows the table name. With `EntityFrameworkCore.Exceptions`, the constraint properties come from the model, so nothing breaks. Recommend showing the entity's display name rather than `TBL_WAREHOUSES`. That is cosmetic and listed, not planned.
- **The audit trail.** It records C# property names, not columns, so nothing changes.
- **SQLite and SQL Server migrators.** They get the same names. SQLite ignores the schema, and the unique-name guard keeps it collision-free.

### 3.5 Integer keys (D6)

**Contracts:**

| Item | Change |
|---|---|
| `IApprovable.Id` | `long Id { get; }`. Approvable things are document headers, and headers are bigint. |
| `IApprovalCommand.DocumentId` | `long` |
| `ApprovalGrant` | `Node(long headerId, …)`, `ApproverOf(long)`, `Begin(long, …)` |
| `ApprovalBehaviour.HeaderLoader` | `Func<IApplicationDbContext, long, …>`; `LoadTyped<THeader>(…, long id, …)` with the same `ForUpdate()` query |
| Logging | Unchanged (`{DocumentId}` is formatted) |
| `DataModelGuard` approvable message | "derive from `BaseVersionedEntity<long>`" |

**A new key rule in `DataModelGuard`,** for non-template entities (Q-I):

- the key is a single `int` or `long`. A `Guid`, `string` or composite key fails, naming the entity;
- `IPostable`, `IPostableLine<>`, `IAppendOnly` and `IApprovable` keys must be `long`;
- `ILookupEntity` keys must be `int` (§3.3);
- other master data may be `int` or `long`, and `int` is recommended. Nothing more can be enforced, because "master data" has no marker.

**Posting events never capture an id before the save.**

- **The pattern.** An event that refers to a row created in the same unit of work keeps the entity and reads the id when asked, as `BranchCreatedEvent` does (`BranchEvents.cs:10-19`). Its handlers run after `SaveChangesAsync` in the same transaction (`TransactionBehaviour.cs:189-206`), when the identity value exists:

  ```csharp
  public sealed class WarehouseCreated(Warehouse warehouse) : PostingEvent
  {
      private readonly Warehouse _warehouse = warehouse;
      public int WarehouseId => _warehouse.Id;     // read at dispatch, after the save
      public int BranchId { get; } = warehouse.BranchId;
  }
  ```

  It goes into README "Posting transactions" and into the module checklist.
- **The loud failure (recommended, Q-J).** Before dispatching, `DrainPostingEventsAsync` checks each event's public `int` / `long` properties whose names end in `Id`. A value of 0 throws, naming the event and the property: "*{Event}.{Property} is 0: the event captured the id before the row was saved. Keep the entity and read its Id when asked (README 'Posting transactions').*"
  - This catches the defect on the first run of any test that raises the event.
  - An event with an optional id uses `int?`, which the check skips.
- `PostingEvent` stays in Domain. The check is in Application, beside the dispatcher.

**Guidance:**

- README "Modules and schemas" (renamed "Modules", §3.1) checklist item 2 becomes: "master data and lookups `int`; document headers, lines and ledger rows `long`; headers derive from `BaseVersionedEntity<long>`; never a Guid key".
- README "Approvals" and the remarks at `IApprovable.cs:40` and `ModuleDefinition.cs:75` are updated to match.
- The owner direction of 2026-11-11 (Guid v7 headers) is recorded as reversed by D6 in the release notes.

**Tests:**

- `TestModel.cs`: `TestApprovableDocument`, `TestPlainApprovable` and `TestPostedDocument` become `BaseVersionedEntity<long>`, and their constructors no longer set `Id`. `TestPostedLine` and `TestLedgerRow` are already `long`.
- `ApprovalTests.cs` (17 sites) and `ApprovableGuardTests.cs` (5) change from `Guid` to `long`. No test logic changes, only types and literals.
- New unit tests: the key rule rows, and the posting-event zero-id check, with one planted event that captures the id in its constructor.

### 3.6 Working rules (D7)

**Repository `CLAUDE.md` (new, at the root):**

- Add `"CLAUDE.md"` to the template's exclude list (`.template.config/template.json:221-247`), so generated projects keep their own rules.
- Contents, in short:
  - the pass workflow already practised (plan, decisions, implement, report, one local commit, pack after commit);
  - the pinned suites;
  - and the new rule: *"Every plan has a section 'GX conventions touched' that cites `docs/gx-conventions.md` and, for each convention the pass affects, says 'conforms' or 'departs'. A departure needs an owner decision that names it as a departure from a GX convention. A plan that changes a convention without saying so is not ready for the owner."*
- It also links `docs/gx-conventions.md` and this repository's memory rules: `gx-v` tags, never `--tags`.

**Check, in the template repository (not shipped):** `tests/Infrastructure.UnitTests/Repository/PassPlanRulesTests.cs` reads `docs/passes/*-plan.md`. For every plan from pass 60c on, it checks:

- the plan has a heading containing "GX conventions touched" and the text `docs/gx-conventions.md`;
- the section holds a table whose rows each end in "Conforms", "Departs" or "Not decided" (or "No conflict");
- every "Departs" row names an owner decision (`D<n>`) or a question in the plan's decisions section.

Plans before 60c are exempt by number. The test file is excluded from the package (`template.json`), because generated projects have no `docs/passes` from the template.

**Shipped to generated projects (Q-K, recommended):**

- `docs/gx-conventions.md` itself, by narrowing the `docs/**/*` exclusion to keep that one file, so an application's own plans can cite it.
- The same plan check, skipped when the project has no `docs/passes` folder. Spare Parts keeps its plans in `docs/passes/NN-<name>-plan.md`, which the check would read.

### 3.7 T9, T11, T12 and T10

**T9: template tests do not depend on an application's branch rules.**

- **Fixtures avoid the pipeline.** A test that needs a branch only as a fixture inserts it directly, through one helper: `TestBranches.InsertAsync(db, active: true)` in `tests/TestSupport`.
  - It replaces `ModelHarness.cs:200` and the fixture uses in `BranchExtensionTests`, `DefaultBranchTests` and `PicklistVersionTests`.
  - It raises no events, so an application's posting handlers do not run on fixtures.
- **Codes and names that any reasonable rule accepts.** Every branch a template test creates, directly or through the pipeline, gets:
  - `TestBranches.NewCode()`: a letter followed by 5 random upper-case letters or digits, 6 characters in all, for example `T7K2QX`;
  - `TestBranches.NewName(label)`: the label + a space + 8 random hex digits, so names are unique across runs on the kept test databases.
- **Tests of the branch command itself** still send `AddEditBranchCommand`: create, rename, deactivate, reactivate, the events and the grant (`BranchExtensionTests` T01 to T10). That is their subject.
  - README "Tightening the rules" states the floor an application's branch rules must accept: "a code of 1 to 6 upper-case letters and digits starting with a letter, and any name unique in the database".
  - An application whose rule is stricter than that floor owns the conflict, and the README says so (Q-L).
- **The sample module proves it.** It gains a strict branch validator (§3.8), so the smoke fails if any template test sends a code outside the floor.
- **Sweep S2** (Spare Parts' planned unique index on `Branch.Name`) is then safe, because every name is unique.

**T11.** In `tests/Application.UnitTests/DataFoundations/DataModelGuardTests.cs`, rename `Warehouse` to `GuardProbeWarehouse` on lines 80, 85 and 131. This is the identical change to Spare Parts `7c38ba4`; the navigation property, assertions and counts are unchanged.

- Under D3 its table becomes `TBL_GUARD_PROBE_WAREHOUSES`.
- A **test-entity naming rule** goes into `CLAUDE.md`: an entity a template test declares is prefixed with its test's subject (`GuardProbe…`, `Test…`, `Sample…`, `Alpha…`), never a plain business noun.
- A unit test enforces it over the test assemblies: it fails on any test-declared entity type whose name is a single plain word.

**T12.** `RoleDefinitionRightTests.ANonHolderCanStillREADTheRoleList` (`:484`) asserts that the three roles it created are visible: `Contain` on their names, plus a count equal to the count before + 3. No longer `HaveCount(3)`.

**T10 (README only).** README "Extending branches" gains this note: "a branch-defining command sent from inside another request keeps its grant only if the outer request is also `IBranchDefiningCommand`". The behaviour is not changed in this pass.

### 3.8 The sample module proves the conventions (smoke)

`tooling/sample-module` is reworked. The smoke copies it into a generated project, adds its migration and runs every suite (`tooling/smoke-generate.ps1:555-622`).

| Proof | Sample content |
|---|---|
| A business table in `core`, plural `TBL_` | `Warehouse : BaseVersionedEntity<int>, IMustHaveBranch, IAuditable` → `core."TBL_WAREHOUSES"`. The name is deliberately common (T11). |
| A lookup in `core`, `TBL_LK_` | `WarehouseType : BaseLkEntity` → `core."TBL_LK_WAREHOUSE_TYPES"`, seeded by migration with `SeedLookup` and `ResetIdentitySequence` |
| A `long` key | `WarehouseMovement : BaseEntity<long>, IAppendOnly` → `core."TBL_WAREHOUSE_MOVEMENTS"`, with `CreateAppendOnlyTrigger` |
| snake_case | Columns `branch_id`, `warehouse_type_id`, `created_by_id`, `xmin`; `pk_tbl_warehouses`, `fk_tbl_warehouses_branches_branch_id` |
| The posting-event id pattern | `WarehouseCreated(Warehouse)` reads `WarehouseId` at dispatch. Its handler writes the first movement row, which proves the id is real. |
| T9 | `SampleBranchRules : AbstractValidator<AddEditBranchCommand>` enforces the floor (`^[A-Z][A-Z0-9]{0,5}$`), and a unique index on `Branches.Name` is added through `BranchConfigurationExtension` (pass 60b) |
| Collisions | The template's whole suite runs with an application entity named `Warehouse` |

`SampleBin` and `SampleBinCreated` are replaced by `Warehouse` and `WarehouseCreated`. The rest of the sample module is kept and renamed to match: role, menu, `Contracts` interface, seeder, app-level page and permissions.

**`SampleModuleTests` S1 is rewritten** to check, in `information_schema`:

- `core.TBL_WAREHOUSES`, `core.TBL_LK_WAREHOUSE_TYPES` and `core.TBL_WAREHOUSE_MOVEMENTS` exist;
- their columns are lower-case;
- the primary key and foreign key names are as in the table above;
- the key types are `integer` and `bigint`.

**The smoke's migration check** (`smoke-generate.ps1:621-622`) changes from `EnsureSchema("smp")` to `EnsureSchema(name: "core")`, and asserts that no other `EnsureSchema` is present.

### 3.9 README and release

- **"Database naming"** is rewritten. It states the contract: `core`, plural `TBL_` / `TBL_LK_`, snake_case business columns, integer keys, and template tables unchanged in `public`. It also covers:
  - the pluraliser rules and `[PluralName]`;
  - the table list;
  - lookups (base classes, what they may carry, seeding helpers, no editor);
  - the constraint-name table;
  - `GxSql.Column`.
- **"Never apply `UseSnakeCaseNamingConvention()` to `ApplicationDbContext`"** stays, with its reason. The in-house convention is how business tables get snake case.
- **"Modules and schemas"** becomes "Modules". The schema content is removed. The checklist changes items 1 (no schema), 2 (keys, D6), 9 (`CreateAppendOnlyTrigger(table)`; lookups seeded with `SeedLookup` and `ResetIdentitySequence`) and adds "11. Update `tables.approved.txt`".
- **"Approvals"** and **"Posting transactions"** get the D6 key and the event-id pattern (§3.5).
- **"Upgrading from 1.7.x to 1.8.0":** §6.
- **Release notes:** 1.8.0, breaking. `AppConfigurationSettings.Version` becomes 1.8.0.
- **(Q-A1)** "Database naming" defines a lookup as *a reference list that users pick from* (MNEFleets' practice). The old "does any code branch on this row's value?" test is removed.
- **(Q-G)** "Database naming" says a string length reaches the database only when it is set in the entity configuration (`HasMaxLength`). `[StringLength]` alone is overridden by the 450 default. The attribute fix is queued for pass 61.
- **(Q-H)** "Database naming" says enums are stored as integers, and every member of an enum used by an entity has an explicit value. The old advice to store enums as strings is removed.
- **(Q6, Q11)** The module checklist says that class names are singular in new code, and that routes are lower-case kebab under the module area (`/inventory/stock-movements`).

### 3.10 (G1) The test reset restores lookups to their migrated state

**The problem.**
- `PostgresTestDatabase` keeps every `TBL_LK_` table's rows and sequence across resets (`tests/TestSupport/PostgresTestDatabase.cs:53-57, 222, 269`).
- Under Q-A1 users maintain some lookups (brands, units, vehicle makes), and tests create and edit them. Those rows would leak into later tests.

**The mechanism: a snapshot taken after migration, restored only where a test touched it.**

1. **Snapshot, once per run, after `MigrateAsync`, in `EnsureReadyAsync`.**
   - Create schema `gx_test_seed`.
   - For each `TBL_LK_` table `s.t`, run `DROP TABLE IF EXISTS gx_test_seed."s.t"; CREATE TABLE gx_test_seed."s.t" AS TABLE s.t;`.
   - Record its identity sequence's `last_value` and `is_called` in `gx_test_seed.sequences`.
   - Create `gx_test_seed.dirty (tbl regclass primary key)`.
   - The snapshot is retaken on every run, so a new seed migration is picked up.
2. **Dirty marking.**
   - Each lookup table gets one statement-level trigger, `gx_test_dirty`, `AFTER INSERT OR UPDATE OR DELETE OR TRUNCATE … FOR EACH STATEMENT`. It runs `INSERT INTO gx_test_seed.dirty VALUES (TG_RELID) ON CONFLICT DO NOTHING`.
   - It exists only in the throwaway test database. The migrations never create it, and nothing in the application sees it.
   - The append-only check looks only at its own trigger names, so it is unaffected.
3. **Restore, in the reset batch.** One `DO` block, in the same single round trip as today:
   - for each dirty table, children-first: `DELETE`;
   - then parents-first: `INSERT … OVERRIDING SYSTEM VALUE SELECT * FROM gx_test_seed."s.t"`, then `setval` the sequence to the recorded value;
   - then empty `dirty`.
   - Lookups are deleted after the business tables that reference them. They already come later in the children-first order, because business rows reference lookups.
   - The dirty trigger is disabled while the reset runs (`session_replication_role = replica` inside the batch), so the restore does not mark the tables again.
4. **Untouched lookups cost nothing.** The batch reads `dirty`, which is usually empty.

**Cost per test:**
- a test that touches no lookup pays one primary-key scan of an empty table (microseconds);
- a test that writes one lookup pays one `DELETE` and one `INSERT … SELECT` of that lookup's seeded rows. That is a few hundred rows at most, about 1 ms;
- the snapshot itself is one `CREATE TABLE AS` per lookup, once per run.

**How a project with many lookups and many tests stays fast:** the restore is proportional to the lookups a test **wrote**, not to the number of lookups. 200 lookups and 2,000 tests cost 200 small copies once, plus about 1 ms for each test that edits a lookup.

**Tests (`SharedDatabaseTests`, on a stand-in lookup built in the harness database like today's `TBL_LK_HARNESS_PROBE`):**
- **G1-a:** a row a test inserts into a lookup is gone after the reset;
- **G1-b:** a migration-seeded row that a test renamed or deleted is back after the reset, with its original id;
- **G1-c:** after the reset, the next insert without an id gets the id a freshly migrated database would give: the sequence is restored, not rewound and not left advanced;
- **G1-d:** a lookup no test touched is not rewritten. The batch's `dirty` set is empty, and the table's `xmin` values are unchanged.

### 3.11 (Q-H) Enum storage

- **Today.** EF stores enums as `integer` by default. No template entity converts them to strings.
- **New startup rule in `DataModelGuard`.** Every enum type mapped by a non-template entity (scalar properties and owned or complex members) must give every member an explicit numeric value in source.
  - Reflection cannot see whether a value was written explicitly. The check therefore reads a compile-time marker: `[ExplicitValues]` on the enum, plus a unit test (`EnumValuesAreExplicitTests`, shipped) that scans the source files under `src/Domain` for `enum` declarations used by entities and fails on any member without `=`.
  - **(P60c)** The guard's runtime part refuses an enum without `[ExplicitValues]`, naming the enum and the property.
  - The source scan proves the attribute is true.
- **README** "Database naming": enums are stored as integers; each member has an explicit value, because the stored number **is** the contract (MNEFleets, conventions §6.3).

---

## 4. What passes 58 to 60b keep, and what changes

| Pass | Stays exactly as it is | Changes |
|---|---|---|
| 58 | Unit of work, `TransactionBehaviour`, posting-event rounds, `ProcessedRequests`, `DocumentSequences`, `ForUpdate`, the lock order, `PostingRejectedException` | The posting-event zero-id check (Q-J); README guidance on Guid headers reversed (D6) |
| 59 | Decimal kinds, `GxRound`, `xmin` versioning, posted-record immutability, append-only triggers (mechanism), branches, branch filter, `AcrossBranchesAsync`, the branch UI | `CreateAppendOnlyTrigger`'s default schema becomes `core`; test model names become plural and snake_case; T9 test fixtures |
| 60 | `[Module<T>]`, the no-module startup failure, `TemplateEntities`, the cross-module navigation and FK rules, the architecture tests (T22–T26, T42), `IMenuContributor`, the approval **policy**, limits by role, `Result.ErrorCode`, session ending, the configuration scan by exclusion | **Removed:** `ModuleDefinition.Schema`, `ReservedSchemas`, `SchemaProblem`, the module-schema convention, the schema guard rows. **Changed:** the approval contracts' key type (`Guid` → `long`); `CheckTableNames` reworded; README "Modules and schemas" → "Modules" |
| 60b | `ModuleServices<T>`, `AppLevelServices`, `[NotAService]`, permissions decided by attribute, the role catalogue, branch events, `IBranchDefiningCommand`, `BranchScreenOptions`, exactly one default branch, the smoke running every suite on a module | The sample module's content (§3.8) and its smoke assertion |

**Tests that change** (none of them pinned):

- `GxTableNamingTests`: back to `core`; plural names; snake columns; `OnPostgres_TheBusinessModelIsNotSnakeCased` becomes `…_TheTemplateTablesAreNotSnakeCased_AndTheBusinessTablesAre`;
- `ModuleSchemaTests`: renamed `CoreSchemaTests`;
- `ModuleGuardTests`: the schema rows go;
- `DataModelGuardTests` (T11);
- `TestModel.cs` (no `Schema`, `long` headers);
- `ApprovalTests`, `ApprovableGuardTests`, `DataFoundationsTests`, `CacheRuleTests`, `StructureTests` (expected names only);
- `SharedDatabaseTests` (probe columns);
- `BranchExtensionTests`, `DefaultBranchTests`, `PicklistVersionTests`, `BranchScreenTests` (T9);
- `RoleDefinitionRightTests` (T12);
- `ModelHarness` (it drops `core` and `public`, plus any explicit schema in the model);
- `tests/ArchitectureSamples` (`Schema` removed);
- the sample module tests.

**Shipped migrations:** none change. The template ships no business table, and no shipped migration names a schema. Every template table keeps its name, its columns and its `public` schema (D2). `ModelMatchesMigrationsTests` proves that the three snapshots still match the model.

---

## 5. Order of work

1. **T11 and T12** (test-only, independent), and the test-entity naming test.
2. **Schema (§3.1):** the naming convention, `ModuleDefinition`, the guard, then the schema tests. The suites go green with singular names first.
3. **Plurals (§3.2):** the pluraliser and its tests, `[PluralName]`, the naming tests, and the table-list test with `tables.approved.txt`.
4. **Lookups (§3.3):** the base classes, the interface members, the guard rule, the migration helpers, and their tests.
   4a. **(G1) The lookup snapshot and restore in the test reset (§3.10)**, with tests G1-a to G1-d.
   4b. **(Q-H) The enum rule (§3.11).**
5. **snake_case (§3.4):** `ToSnake` and its tests, the finalizing convention, `GxSql.Column`, then every changed test literal (S12). Run all suites and confirm each S1 to S9 row against the database.
6. **Integer keys (§3.5):** the contracts, the behaviour, the guard key rule, the posting-event check, `TestModel`, `ApprovalTests`, `ApprovableGuardTests`.
7. **T9 (§3.7):** `TestBranches`, the fixture rewrites, the README floor.
8. **The sample module and the smoke (§3.8).**
9. **D7 (§3.6):** `CLAUDE.md`, the template exclusion, `PassPlanRulesTests`, the shipped conventions file if Q-K is yes.
10. **README, release notes, version 1.8.0.** Commit, then pack, then tag `gx-v1.8.0` (pushed by name only).
11. **Browser check:** generate from the pack, add the sample module, sign in, open the Warehouses page, create a warehouse, and confirm the first movement row in `core."TBL_WAREHOUSE_MOVEMENTS"`.

The memory notes for this machine apply:
- after any trial migration on the kept test databases, delete that `__EFMigrationsHistory` row and drop the schemas it created (`tsa`, `tsb`, `tdf`, `smp`, `org` if present). Never drop a database;
- run `dotnet build-server shutdown` between heavy steps.

---

## 6. Migration path for a project generated from 1.7.0 or 1.7.1

### 6.1 Spare Parts (not deployed): regenerate

Spare Parts (`C:\Yoab\Projects\SpareParts`, generated from 1.7.1) has:

- one committed module migration, `20261112181453_Organisation`, which creates `org."TBL_BRANCH_PROFILE"`, `org."TBL_COMPANY"`, `org."TBL_WAREHOUSE"` and `org."TBL_BIN_LOCATION"`;
- a second committed module migration, `20261112190011_Catalogue` (pass 1b, commit `b856ab1`), which creates 12 tables in `cat`: `TBL_CATEGORY`, `TBL_LK_BRAND`, `TBL_UNIT_OF_MEASURE`, `TBL_LK_VEHICLE_MAKE`, `TBL_LK_VEHICLE_MODEL`, `TBL_ITEM`, `TBL_ITEM_BARCODE`, `TBL_ITEM_CROSS_REFERENCE`, `TBL_ITEM_FITMENT`, `TBL_ITEM_UOM`, `TBL_PRICE_LIST` and `TBL_PRICE_LIST_ITEM`.

Nothing is deployed, so do not migrate. **Regenerate.** In Spare Parts' own next pass:

1. **Port 1.8.0.** The template files change as in §4. Spare Parts' T11 commit is already identical.
2. **Remove `Schema`** from `OrganisationModule` and `CatalogueModule` (it no longer compiles), and from Spare Parts' CLAUDE.md module table. The "Schema" column becomes a "Prefix" column only if the owner wants one (§8.3); otherwise it is dropped.
3. **Keys:** none of its entities uses a Guid key today. Its CLAUDE.md "Keys" rule changes to D6.
4. **Lookups:** `Brand`, `VehicleMake` and `VehicleModel` move to `BaseVersionedLkEntity`. They already have `Name`; they gain `Slug`.
5. **Hand-written SQL:**
   - `HasFilter("\"Type\" = 'HQ'")` becomes `GxSql.Column(nameof(BranchProfile.Type)) + " = 'HQ'"`;
   - `HasDatabaseName("IX_TBL_BRANCH_PROFILE_OneHq")` becomes `ux_tbl_branch_profiles_one_hq`, per Q-E. (Both are at `src/Infrastructure/Modules/Organisation/Configurations/OrganisationConfigurations.cs:42`.)
6. **Delete the `Organisation` and `Catalogue` migrations.** Regenerate one migration per provider, for example `Organisation`. It now creates `core."TBL_BRANCH_PROFILES"`, `core."TBL_COMPANIES"`, `core."TBL_WAREHOUSES"`, `core."TBL_BIN_LOCATIONS"` (and the Catalogue tables), with snake_case columns.
7. **Development and test databases:** for each one, delete the `Organisation` (and `Catalogue`) `__EFMigrationsHistory` rows, `DROP SCHEMA org CASCADE` (and `cat`), and run `database update`. Never drop the database.
8. **Rewrite their table list** (`tables.approved.txt`) and review the plurals.

### 6.2 A deployed project (general recipe for the README)

A deployed project cannot drop its data. One hand-checked migration per provider, generated with `dotnet ef migrations add GxConventions180` and then reviewed, must:

1. **`EnsureSchema("core")`.**
2. **Move and rename each table.** EF's differ matches entity types, so it emits a `RenameTable(name: "TBL_WAREHOUSE", schema: "org", newName: "TBL_WAREHOUSES", newSchema: "core")` per table.
   - **Check it emits renames, not drop and create.**
   - If an entity class was renamed at the same time, write the rename by hand.
3. **Rename columns** (`RenameColumn`), and rename primary keys, foreign keys and indexes. EF emits `DropForeignKey` / `AddForeignKey`, `DropPrimaryKey` / `AddPrimaryKey` and `RenameIndex`. These are safe on PostgreSQL, but they rebuild constraints, so run them in a maintenance window.
4. **Convert Guid keys to bigint, if any.** This needs a new identity column, re-pointing every foreign key and dropping the old key. It is written by hand per table. **It does not apply to Spare Parts.**
5. **Append-only triggers** follow the table on rename. The function `gx_refuse_append_only_change` is in `public` and is unaffected. `DataModelGuard` re-checks them at startup.
6. **Drop the empty module schemas** (`DROP SCHEMA org`, without `CASCADE`, so it fails if anything is left).
7. **Code changes** as in §6.1, steps 2 to 5.

---

## 7. Tests (Application.IntegrationTests on `GX_TEST_PG` unless noted)

| # | Test | Kind |
|---|---|---|
| 1 | A module entity and an entity with no module both map to `core."TBL_…S"`; a template entity stays in `public` with its name | unit (naming) |
| 2 | `ToTable("legacy")` with no schema stays in the default schema; `ToTable("legacy", "reporting")` keeps `reporting` | unit |
| 3 | An implicit join with a `core` side is `core."TBL_SAMPLE_COURSE_SAMPLE_STUDENTS"` | unit |
| 4 | A module definition overriding `Schema` does not compile: the member no longer exists. Documented, not tested. | — |
| 5 | Two entity types mapping to one table name fail startup, naming both | unit (guard) |
| 6 | (Q-B) A module entity hand-named into `public` fails startup | unit (guard) |
| 7 | The pluraliser, at least 60 cases, including `StoreMovement` and `StoreMovements` → `STORE_MOVEMENTS`; `[PluralName]` wins; an invalid `[PluralName]` fails startup | unit |
| 8 | `tables.approved.txt` matches the model; a planted entity fails with a diff and the approve command | unit |
| 9 | `ILookupEntity` with `Name` ignored, a nullable `Name`, a `long` key or (Q-A2) a `[Money]` column fails startup, naming it | unit (guard) |
| 10 | `SeedLookup` then `ResetIdentitySequence`: the next insert without an id gets max + 1 | integration |
| 11 | Business columns, primary key, foreign key and index names are snake_case per §3.4; Identity, framework tables and `__EFMigrationsHistory` are byte-for-byte unchanged (the three snapshots are unchanged) | unit and `ModelMatchesMigrationsTests` |
| 12 | `HasColumnName` and `HasDatabaseName` set by hand are kept | unit |
| 13 | `PostingSql.Names`, `InsertIfAbsent`, the append-only trigger, the bulk-write guard and the cache guard work on snake-case plural tables | integration (existing tests, renamed literals) |
| 14 | Approval of a `long` header end to end; a planted `Guid` header fails startup (key rule) | integration and unit |
| 15 | A posting event that captured its id in the constructor throws at dispatch, naming the event and property; `BranchCreatedEvent` and the sample `WarehouseCreated` pass | integration |
| 16 | No template test fixture branch fails a floor-strict validator plus a unique `Name` index: run by the smoke through the sample module | smoke |
| 17 | T11: no test-declared entity has a single plain-word name | unit |
| 18 | T12: the role-list test counts only its own roles | unit (existing, edited) |
| 19 | `PassPlanRulesTests`: 60c's own plan passes; a planted plan without the section fails | unit (repository) |
| 20 | Smoke: generate, add the sample module, migrate, run every suite; `EnsureSchema("core")` is the only schema; S1's `information_schema` checks | smoke |
| 21 | The pinned suites pass unmodified | all |

---

## 8. Conflicts with the brief or with the code, and what I recommend

### 8.1 D1 (hand-named tables stay put) against D2 (`public` is for framework tables)

- Under D1, a module entity with `ToTable("TBL_X")` and no schema lands in `public`. D2 forbids business tables there.
- **Recommend:** keep D1's naming rule, and add the startup failure in §3.1 (Q-B). The table still "stays where it was put"; the guard refuses where that is.

### 8.2 D3 plurals against MNEFleets' singular names

- MNEFleets has 44 singular tables, including `TBL_STOCK_HISTORY` and `TBL_LK_COLOR`. D3 makes plural the rule, so the template will not reproduce those.
- An application that must match an existing database uses `[PluralName]` or `ToTable(name, "core")`. Nothing to decide unless the owner wants exceptions built in.

### 8.3 D1 leaves modules without a database footprint

- Under D1, nothing in `pgAdmin` shows which module owns a table. The table list (§3.2) prints `schema.table ← Entity`.
- **Recommend:** add the module name to each line of `tables.approved.txt`: `core.TBL_WAREHOUSES ← Warehouse [Sample]`. Ownership is then reviewable without a schema.
- A table-name prefix per module (for example `TBL_INV_…`) is **not** recommended: it departs from §2.1 and MNEFleets has none.

### 8.4 D5 snake case against README consequence 2

- The README forbids the plug-in, and rightly so. D5 is met **without** the plug-in (§3.4), so the reason the README gives still stands, and the rule is kept.

### 8.5 D6 against pass 59's posted-record and approval tests

- None of those tests depends on the id being a Guid, apart from the declared types. The `ApprovalGrant` ambient keyed by id works the same with `long`.
- **One subtlety:** the grant is installed before the handler with an id that exists, because the header is loaded, never created, by an approval command. No conflict.

### 8.6 T9 cannot be made fully independent

- Tests of `AddEditBranchCommand` must send it, and an application can tighten it beyond any floor.
- **Recommend:** the stated floor plus fixtures that avoid the pipeline (§3.7). The alternative is to run the branch-command tests with application validators removed. That would hide real conflicts between a module's rules and template behaviour, so I recommend against it.

### 8.7 The table list is "a template test" that every application changes

- Spare Parts' CLAUDE.md forbids editing template tests.
- **Recommend:** the test ships unchanged. The **approved file** is the application's data, and the README and the test's failure message say so (Q-D).

---

## 9. Decisions for the owner

### 9.0 Decided by the owner, 2026-11-12

| # | Decision |
|---|---|
| Q-A1 | **Not as recommended.** A lookup is a reference list that users pick from (MNEFleets' practice), not the "no code branches on a row's value" test. The README states it. |
| Q-A2 | As recommended. A lookup may carry further columns; a decimal column fails startup. |
| Q-A3 | As recommended: `BaseLkEntity` and `BaseVersionedLkEntity`. |
| Q-A4 | As recommended: no lookup editor shipped. |
| Q-B | Yes: a module entity whose table resolves to `public` fails startup. |
| Q-C | As recommended: the last word of a join's name is pluralised. |
| Q-D | Yes. The table-list test and the approved file ship into generated projects, and each line carries the module name (§8.3). |
| Q-E | As recommended: `ux_tbl_` and `ck_tbl_` prefixes, enforced on business tables. |
| Q-F | As recommended: `timestamptz` stays, and the legacy switch stays forbidden. This is a named departure. |
| Q-G | As recommended: out of this pass. The README states that a string length must be set in the entity configuration to reach the database. The attribute fix is queued for the next pass. |
| Q-H | **Not as recommended.** Enums are stored as integers, as in MNEFleets. Every member of an enum used in an entity has an explicit numeric value, and the guard fails on one without. The README, which advises strings, is corrected. This conforms to convention 6.3 (§3.11). |
| Q-I | As recommended. |
| Q-J | Yes: the zero-id check in the dispatcher. |
| Q-K | Yes. |
| Q-L | As recommended. |
| Q-M | Drop the Schema column. |
| Q-N | Regenerate, do not migrate. The Organisation and Catalogue migrations are regenerated in the same step. |
| Q6 (conventions) | Class names are singular in new code. |
| Q11 (conventions) | Routes are lower-case kebab under the module area. |
| G1 | Amendment: the test reset restores each lookup to its migrated state (§3.10). |

### 9.1 The questions as put to the owner

Already decided (D1 to D7) and not repeated here. These are the questions the conventions document leaves open, or that implementing D1 to D7 raises. Each had a recommendation; the decisions are in §9.0.

1. **Q-A1. What makes a table a lookup?** (conventions Q5)
   - *Recommend:* the template's rule ("no code branches on a row's value"), stated in the README and not enforced. It cannot be checked mechanically.
   - *Alternative:* MNEFleets' practice ("anything used as a reference list").
2. **Q-A2. What else a lookup may carry.**
   - *Recommend:* allow `Code`, `IsActive`, `SortOrder`, audit and version columns, a description, and foreign keys to other lookups. Fail startup on a decimal column: a rate or an amount makes it master data (`TBL_`).
   - *Alternatives:* no limit beyond the fixed columns, or a stricter list (no foreign keys).
3. **Q-A3. The base class name.**
   - *Recommend:* `BaseLkEntity` and `BaseVersionedLkEntity`, as MNEFleets, so code ports unchanged.
   - *Alternative:* `BaseLookupEntity`.
4. **Q-A4. Lookup editing.**
   - *Recommend:* none shipped in 1.8.0 (conventions §3.6: one page per table). Consider a generic lookup editor in a later pass.
5. **Q-B. Refuse a module entity whose table resolves to `public`** (§8.1).
   - *Recommend:* yes, at startup.
6. **Q-C. Join table names.** The conventions document has no implicit joins.
   - *Recommend:* pluralise the join name's last word: `TBL_SAMPLE_COURSE_SAMPLE_STUDENTS`.
   - *Alternative:* leave joins singular, as EF names them.
7. **Q-D. The table-list file.**
   - *Recommend:* ship `ModelTableListTests` and `tables.approved.txt` into generated projects. The file is application data that it updates, and each line carries the module name (§8.3).
   - *Alternative:* a template-repository test only. Generated projects would then rely on migrations to reveal names.
8. **Q-E. Hand-named constraint prefixes** (conventions Q7, Q8).
   - *Recommend:* generated names as §3.4. A hand-named unique index must start `ux_tbl_`, a check constraint `ck_tbl_`, and the guard enforces both prefixes on business tables.
   - *Alternative:* no rule for hand-named constraints.
9. **Q-F. Timestamps** (conventions Q9).
   - *Recommend:* keep `timestamptz` and keep the legacy Npgsql switch forbidden. This is recorded as a deliberate departure from MNEFleets.
10. **Q-G. `[StringLength]` versus the 450 default** (conventions Q10).
    - *Recommend:* out of this pass. A later pass should make `[StringLength]` / `[MaxLength]` win over the default, and pin that with a test.
11. **Q-H. Enum storage.** MNEFleets uses `integer`; the template README advises a string.
    - *Recommend:* keep the README's advice (strings read in SQL and survive reordering). Record it as a departure.
12. **Q-I. The key rule's strictness** (§3.5).
    - *Recommend:* fail startup on Guid, string or composite keys on non-template entities. Require `long` on postable, line, append-only and approvable entities, and `int` on lookups.
13. **Q-J. The posting-event zero-id check** (§3.5).
    - *Recommend:* yes, in the dispatcher. It skips nullable ids.
    - *Alternative:* README guidance only.
14. **Q-K. Ship `docs/gx-conventions.md` and the plan check into generated projects** (§3.6).
    - *Recommend:* yes. The check is skipped when a project has no `docs/passes`.
15. **Q-L. The branch-rule floor for T9** (§3.7).
    - *Recommend:* "a code of 1 to 6 upper-case letters and digits starting with a letter, and any unique name". Spare Parts' planned rule fits it.
16. **Q-M. The `Schema` column in Spare Parts' CLAUDE.md** (§6.1 step 2).
    - *Recommend:* drop it. The module list stays.
17. **Q-N. The Spare Parts migration** (§6.1).
    - *Recommend:* regenerate, not migrate. Nothing is deployed.
    - The brief counted four tables in `org`. Spare Parts has since committed pass 1b (`b856ab1`), adding 12 tables in `cat`. Confirm that both migrations are regenerated in the same step.

---

## 10. Effort

| Item | Section | Effort (days) |
|---|---|---|
| T11, T12, test-entity naming test | §3.7 | 0.5 |
| Schema restored: convention, `ModuleDefinition`, guard, schema tests | §3.1 | 1.5 |
| Plurals: pluraliser, `[PluralName]`, naming tests, table-list test | §3.2 | 1.5 |
| Lookups: base classes, interface, guard rule, migration helpers, tests | §3.3 | 1.5 |
| snake_case: `ToSnake`, finalizing convention, `GxSql.Column`, test literals, S1–S13 verification | §3.4 | 2.5 |
| Integer keys: contracts, behaviour, guard key rule, posting-event check, tests | §3.5 | 1.5 |
| T9: `TestBranches`, fixture rewrites, README floor, T10 note | §3.7 | 1.0 |
| Sample module and smoke | §3.8 | 1.0 |
| Working rules: `CLAUDE.md`, template exclusion, `PassPlanRulesTests`, shipped conventions file | §3.6 | 0.5 |
| README, release notes, version, pack, tag, browser check | §3.9, §5 | 1.0 |
| **Total** | | **12.5** |

The Spare Parts port (§6.1) is Spare Parts work and not counted here. I estimate 1.5 days for Organisation and Catalogue together.
