# GX conventions, as practised in MNEFleets

**Written:** 2026-11-12. **Source:** the MNEFleets repository at `C:\Yoab\Projects\MNEFleets\FrontEnd`, read at commit `31be39eb` ("retakesteps"). Nothing in that repository was changed.

**Purpose.** MNEFleets is the reference GX application. This document records the conventions it actually follows, with real examples, so that GXTemplate can be checked against them (see `docs/passes/conventions-audit.md`).

**Status in GXTemplate 1.8.0 (pass 60c, 2026-11-12).** The template follows these conventions, with the
owner's answers to §12 and one named departure:

- **Decided:**
  - business tables in `core` (§1);
  - plural `TBL_` names, derived from the class with a `[PluralName]` override (Q4);
  - snake_case business columns and EF's lower-case constraint names, with `ux_tbl_` / `ck_tbl_` for
    hand-named ones (Q2, Q7, Q8);
  - a lookup is a reference list users pick from, with `BaseLkEntity`'s Id, Name, Slug (Q5);
  - integer keys, `int` for master data and `long` for transactions;
  - enums stored as integers with explicit values;
  - singular class names in new code (Q6);
  - lower-case kebab routes under the module area (Q11).
- **Framework tables:** `public` keeps the template's own tables (Q1).
- **The departure:** times are `timestamptz` (Q9), by owner decision.
- **Queued:** `[StringLength]` reaching the database is queued for the next pass (Q10).
- **For MNEFleets to answer:** Q3 and Q12.
- **§10 is historical:** it describes the template before 1.8.0.

Every pass plan cites this document in its section "GX conventions touched" (owner decision D7).

**How sure each rule is.** Every rule is marked with one of these:

- **Stated**: a document says so, and the code agrees.
- **Everywhere**: no document says so, but the code follows it without exception.
- **Mostly**: followed, with the exceptions listed.
- **Question**: MNEFleets contradicts a document or itself. The question is listed in §12 and not decided here.

**Abbreviations.** Paths are relative to the MNEFleets root.

- **S** means `src/Migrators/Migrators.PostgreSQL/Migrations/ApplicationDbContextModelSnapshot.cs`, the model snapshot of `ApplicationDbContext`.
- **E** means `src/Domain/Entities/`.

---

## 1. Schemas

| # | Convention | Sureness |
|---|---|---|
| 1.1 | **Every business table is in the `core` schema.** All 193 `TBL_` tables in S are `ToTable("TBL_…", "core")`. | Everywhere (193 of 193) |
| 1.2 | **`public` holds the framework's tables only.** That is the 8 ASP.NET Identity tables plus `SecurityPolicies`, `audit_trails`, `data_protection_keys`, `picklist_sets`, `tenants` and `tenant_users`: 14 in S. The log context's `system_logs` is also in `public`. | Stated (`docs/etl/table-inventory.md:75`), with one stale entry: see Q3 |
| 1.3 | **A separate bounded context may own a schema of its own.** `TrackingDbContext` uses `HasDefaultSchema("tracking")` (`src/Infrastructure/Tracking/TrackingDbContext.cs:23,39`) and keeps its migration history there (`TrackingPersistence.cs:16`). | Everywhere (one case) |
| 1.4 | **`core` is reached by an attribute on each entity**, not by a default schema or a sweep. It is written `[Table("TBL_X", Schema = "core")]` on the class (193 hits in E). `ApplicationDbContext` has no `HasDefaultSchema`. An entity without the attribute falls to `public`. | Stated (`docs/passes/2026-09-01-investor-wht-account-investigation.md:233-238`: "opt-in per entity via attribute, not an automatic sweep over `IBusinessEntity` as it is in the GX template") |
| 1.5 | **`core` also holds tables that are not in the EF model.** For example, `core."TBL_PROFORMA_COUNTERS"` was created by raw SQL (`…/Migrations/20260718093522_pfcounters.cs:14`), later renamed `TBL_CLIENT_INVOICE_COUNTERS`, and is upserted in `src/Infrastructure/Repository/ClientInvoiceNumberGenerator.cs:21-24`. The SQL function `core.advance_identity_sequences` (`docs/Seed-Manifest.md:73`) is also in `core`. | Everywhere |

**Examples.**

- `E/InventoryTables.cs:284`: `[Table("TBL_STOCK_MOVEMENTS", Schema = "core")] public class StockMovements : BaseAuditableEntityLong`
- `E/BillingTables.cs:21`: `TBL_BILLING_RATES` in `core`
- `E/LkTables.cs:18`: `TBL_LK_ACCOUNT_TYPES` in `core`
- `src/Infrastructure/Persistence/Configurations/SecurityPolicyConfiguration.cs:12`: `ToTable("SecurityPolicies")`, so it stays in `public`

**The owner's statements, checked:**

1. **"Only the Identity tables are in the public schema."** *Not exactly.*
   - Six non-Identity framework tables are in `public` as well: `SecurityPolicies`, `audit_trails`, `data_protection_keys`, `picklist_sets`, `tenants` and `tenant_users`.
   - So is the log table `system_logs`.
   - What MNEFleets keeps out of `public` is **every business table**. See Q1.
2. **"Every other table is in the core schema."** *True for business tables*, with two qualifications:
   - the framework tables above stay in `public`;
   - the tracking context uses `tracking`.
3. **"StoreMovements is `core."TBL_STORE_MOVEMENTS"`."**
   - *Historically close, but that table no longer exists.* `E/MainTables.cs:1147` reads: "StoreMovements (TBL_STORE_MOVEMENT) — dropped 2026-10-07".
   - Its table name was **singular** (`TBL_STORE_MOVEMENT`) even though the class was plural.
   - The live equivalent is `StockMovements` → `core."TBL_STOCK_MOVEMENTS"` (`E/InventoryTables.cs:284`). The pattern the owner describes holds there. See Q4.
4. **"Lookup tables are named `TBL_LK_…`."** *True.* There are 73 `TBL_LK_` tables, all in `core`. What makes a table a lookup is less clear-cut: see §3 and Q5.

---

## 2. Table naming

| # | Convention | Sureness |
|---|---|---|
| 2.1 | **`TBL_` prefix, then UPPER_SNAKE_CASE.** | Everywhere (193 of 193 in `core`) |
| 2.2 | **Plural.** 149 of 193 names end in S. The 44 that do not are listed below. | Mostly (Q6) |
| 2.3 | **The table name is written by hand.** It is not derived from the class name, so the two can differ, and often do (table below). | Everywhere |
| 2.4 | **The class name is usually the PascalCase form of the table name**, plural included: `StockMovements` → `TBL_STOCK_MOVEMENTS`. Newer code uses singular class names (`WorkflowDefinition`, `ReversalRequest`, `PendingJournalVoucher`, `NotificationPreference`). | Mostly (Q6) |
| 2.5 | **Framework tables keep the name the framework gives them.** `AspNetUsers` comes from Identity. `audit_trails`, `tenants` and the other snake_case names come from the snake-case naming plug-in (§4). `SecurityPolicies` is pinned by hand with `ToTable`. | Everywhere |

**Singular exceptions (2.2):**

- lookups: `TBL_LK_COLOR`, `TBL_LK_CONDITION`, `TBL_LK_POSITION`, `TBL_LK_VEHICLE_MAKE`, `TBL_LK_FUEL_TYPE`, `TBL_LK_BODY_TYPE`, `TBL_LK_STORE_LOCATION`, `TBL_LK_DOCUMENT_TYPE`, `TBL_LK_PAYMENT_TYPE` (next to the plural `TBL_LK_STORE_REQUEST_TYPES`);
- business tables: `TBL_DASHBOARD`, `TBL_PAYMENT_PROOF`, `TBL_CUMULATIVE_CHART`, `TBL_PACKING_LIST`, `TBL_VEHICLE_LOADING`, `TBL_LOADING_LINKAGE`, `TBL_VENDORS_LEDGER`, `TBL_GOODS_RECEIVED`, `TBL_INVESTOR_PAYOUT_SCHEDULE`, `TBL_OTHER_INCOME`, `TBL_NON_TRIP_DIESEL`;
- the `*_HISTORY` tables.

**Where the class name and the table name differ (2.3):**

| Table | Class | Where |
|---|---|---|
| `TBL_DOCUMENTS` | `Docs` | `E/MainTables.cs:1014` |
| `TBL_REPAIRS` | `RepairHistory` | `E/MainTables.cs:1070` |
| `TBL_CHART_OF_ACCOUNTS` | `Account` | `E/MainTables.cs:2764` |
| `TBL_JOURNAL_LINE_ENTRIES` | `JournalLine` | `E/MainTables.cs:2874` |
| `TBL_CLIENT_INVOICES` | `ClientInvoice` | `E/MainTables.cs:1632` |
| `TBL_LK_GENDERS` | `Gender` | `E/LkTables.cs:121` |
| `TBL_LK_PREFERENCES` | `DashboardPreferences` | `E/LkTables.cs:267` |

**Oddities:**

- `TBL_LK_PAYMENT_TYPE` (`E/LkTables.cs:466`) and `TBL_LK_PAYMENT_TYP` (`:472`) are two separate live tables.
- Several names are abbreviated: `TBL_LK_NOK_REL`, `TBL_LK_LGA`, `TBL_LK_PL_TEMPLATE`, `TBL_LK_CURRENT_PR_MONTH`.
- The `TBL_` names came from the legacy database FleetMSDbx, which already had `core."TBL_LK_…"` (`docs/etl/README.md:830-853`).

---

## 3. Lookup tables (`TBL_LK_`)

| # | Convention | Sureness |
|---|---|---|
| 3.1 | **Prefix `TBL_LK_`, in `core`.** There are 73 such tables. | Everywhere |
| 3.2 | **Shape: `BaseLkEntity`.** It has only `int Id`, `string Name` and `string? Slug` (`E/LkTables.cs:10-16`). There is no Code, IsActive or SortOrder column, and no audit columns. 65 of the 73 lookups use it, and no non-lookup table does. | Mostly (8 exceptions below) |
| 3.3 | **What makes a table a lookup: the code reads it as a dropdown or reference list.** No document gives a test. In practice the prefix covers both pure value lists and master or configuration data (list below). Some newer reference tables did not get the prefix. | Question (Q5) |
| 3.4 | **Seeded by migration, with explicit ids.** 55 migrations insert rows with raw `INSERT INTO core."TBL_LK_…"`; the largest is `20260527183530_lkdata.cs`, 1,740 lines. 7 more use `InsertData`. There is no `HasData` outside migrations. The ids are a contract: the `MS.*Id` constants in `src/Application/Common/Functions/MagicStrings.cs` are compiled against them. A migration that writes explicit ids must reset the identity sequence with `core.advance_identity_sequences(...)` in the same migration. | Stated (`docs/Seed-Manifest.md:47-73`) |
| 3.5 | **A few lookups are seeded at startup** instead, for example `SeedEmployeeDocumentCategoriesAsync` (`src/Infrastructure/Persistence/ApplicationDbContextInitializer.cs:431`). | Mostly |
| 3.6 | **Edited one page per table; there is no generic lookup editor.** Each table has its own page under `src/Server.UI/Pages/ReferenceTables/<Name>/` (21 folders), each with a `BaseCrudService` subclass in `src/Infrastructure/Services/Grids/GridServices/`. The index page is `ReferenceTables.razor` (`@page "/ReferenceTables/ReferenceTables"`, Admin only). It mixes lookups with master data (Clients, Vendors, Chart of Accounts). Most simple lookups (Gender, Color, Genotype) have no editor. | Mostly |

**Lookups that do not use `BaseLkEntity` (3.2):**

- `States` and `Lga`, with primary keys `StatesId` and `LgaId` (`E/LkTables.cs:81,94`);
- `Banks`;
- `ApprovalStatus`, which has ordering columns per role (`OrderId`, `BHOrderId`, `AuditorOrderId`, `AccountantOrderId`; `:230`);
- `Tariff`, `CurrentPRMonth`, `Months`;
- `TireSizes`.

`TirePositions` adds a `SortOrder` of its own. `E/TireTables.cs:39-46` says it has "no free-standing precedent elsewhere".

**`TBL_LK_` tables that carry rules or relations, which a strict "pure lookup" test would not call lookups (3.3):**

- `TBL_LK_COMPANIES`;
- `TBL_LK_GROUPS` (Supervisor, MonthlyTripTarget, CompaniesId);
- `TBL_LK_DEPOTS`;
- `TBL_LK_TANKS` (Capacity, DepotsId);
- `TBL_LK_MNE_BANK_ACCOUNTS`;
- `TBL_LK_DISTANCE_RATES` (Min/MaxDistance);
- `TBL_LK_TRIP_EXPENSES` (Rate);
- `TBL_LK_FUEL_SOURCE` (VendorsId);
- `TBL_LK_OWNERS` (UserId);
- `TBL_LK_PAYMENT_PURPOSE` (AccountId to the chart of accounts);
- `TBL_LK_CURRENT_PR_MONTH`, a single-row setting;
- `TBL_LK_REPORT_TYPES` (`string[] Role`).

These are in `E/LkTables.cs:350-576`. The code also branches on lookup ids through the `MS.*Id` constants.

**Reference tables without the prefix:** `TBL_STORAGE_LOCATIONS`, `TBL_ROUTES`, `TBL_DRIVER_PAY_RATES`, `TBL_COMPLIANCE_REMINDER_STAGES` and `TBL_BILLING_RATES`.

---

## 4. Columns, keys and constraint names

| # | Convention | Sureness |
|---|---|---|
| 4.1 | **Columns are lower snake_case**, produced by `UseSnakeCaseNamingConvention()` on the application context (`src/Infrastructure/DependencyInjection.cs:193`), the log context (`:144`) and the tracking context. None of the 1,954 column names in S has an upper-case letter. None is written by hand. Examples: `employees_id`, `raised_by_id`, `created_at`. | Everywhere |
| 4.2 | **The C# property is PascalCase; a foreign key is `<NavigationClass>Id`**, usually plural, giving columns such as `employees_id`, `trip_kits_id` and `vehicles_id`. | Mostly |
| 4.3 | **Primary key: a column named `Id`, an `int` or `long` identity (BY DEFAULT).** 109 int and 84 long; no Guid key in `core`. `UseIdentityByDefaultColumns` is set at S:23. Exceptions: `StatesId`, `LgaId`, `AccountId`, `EntryId` (JournalEntry) and `LineId` (JournalLine) (`E/MainTables.cs:2768,2802,2878`). | Mostly |
| 4.4 | **Keys follow volume.** `int` goes with lookups and low-volume masters (`BaseAuditableEntity`); `long` goes with transactional and ledger rows (`BaseAuditableEntityLong`, 64 tables). | Mostly (inferred from the base classes; not stated) |
| 4.5 | **Constraint and index names are EF's defaults, lower-cased by the plug-in:** `pk_tbl_<table>` (193), `fk_<table>_<principal>_<column>` (319) and `ix_…` (339). Example: S:91 `pk_tbl_accessory_charges`. Long names are cut at PostgreSQL's 63 characters: S:12467 `fk_tbl_accessory_charges_tbl_lk_approval_status_approval_statu`. | Everywhere (except Identity's own `RoleNameIndex`, `EmailIndex` and `UserNameIndex`) |
| 4.6 | **Hand-named constraints:** unique indexes are `ux_…` and check constraints are `ck_tbl_…`. Example: `ck_tbl_truck_swaps_shape`. | Mostly: 11 `ux_tbl_…` names but 5 `ux_…` without `tbl_`, e.g. `ux_client_credit_notes_one_pending_per_invoice` (S:1030) (Q8) |
| 4.7 | **Deleting a parent never cascades.** Every relationship is `Restrict` unless it is listed as an exception (`src/Infrastructure/Persistence/DeleteRules.cs:5-12`: "DELETING A PARENT NEVER SILENTLY DELETES OR BLANKS RECORDS (decision of record…)"). | Stated |

---

## 5. Audit, soft delete, concurrency and tenancy

| # | Convention | Sureness |
|---|---|---|
| 5.1 | **Audit columns** `CreatedAt`, `CreatedById`, `LastModifiedAt` and `LastModifiedById` (in the database: `created_at`, `created_by_id` …) come from `BaseAuditableEntity` / `BaseAuditableEntityLong`. The interceptor stamps them. | Everywhere on audited tables |
| 5.2 | **Timestamps are `timestamp without time zone`, holding UTC.** There are 338 such columns. `Npgsql.EnableLegacyTimestampBehavior` is switched on in `Program.cs:24`. The log context alone was converted to `timestamptz` (`src/Infrastructure/Migrations/20260829133351_SystemLogTimeStampToTimestamptz.cs`). | Everywhere (Q9) |
| 5.3 | **Soft delete uses `DeletedAt` and `DeletedById`; there is no `IsDeleted`.** `ISoftDelete` is applied through a global filter, `DeletedAt == null` (`src/Infrastructure/Persistence/Extensions/ModelBuilderExtensions.cs:22`). 18 entities use it. | Everywhere where used |
| 5.4 | **A row-level audit trail is opt-in by marker.** `IAuditTrial` (sic) is on 39 entities and drives `audit_trails` (`src/Infrastructure/Persistence/Interceptors/AuditableEntityInterceptor.cs:212`). | Everywhere |
| 5.5 | **Concurrency tokens are rare.** There is one: `InvestorPayoutSchedule.RowVersion`, mapped to `xmin` (`InvestorConfigurations.cs:90-91`). | Everywhere (one case) |
| 5.6 | **Tenancy is dormant.** `IMustHaveTenant` and `IMayHaveTenant` exist and are stamped, but no business entity implements them. `TenantId` exists only on `ApplicationUser` and `TenantUser`. | Everywhere |

**Base classes in use across the 193 `core` tables:**

| Base class | Count |
|---|---|
| `BaseLkEntity` | 65 |
| `BaseAuditableEntityLong` | 64 |
| `BaseAuditableEntity` | 31 |
| `BaseAuditableSoftDeleteEntityLong` | 14 |
| `BaseAuditableSoftDeleteEntity` | 4 |
| `BaseEntityLong` | 2 |
| none | 13 |

Of the 13 with no base class, 8 are the hand-written lookups. `Account` and `JournalEntry` implement `IAuditableEntity` directly. `JournalEntry` alone also carries `IRequireAttribution`, which refuses a save with no creator.

---

## 6. Other column-level defaults

| # | Convention | Sureness |
|---|---|---|
| 6.1 | **Strings default to `varchar(450)`** through `HaveMaxLength(450)` in `ConfigureConventions`; 725 columns. `[StringLength]` on a property does **not** reach the database unless fluent configuration repeats it. For example, `InventoryItems.SKU` has `[StringLength(50)]` (`E/InventoryTables.cs:20`) but is `varchar(450)`. | Everywhere (Q10) |
| 6.2 | **Decimals default to `numeric(18,2)`** through `HavePrecision(18,2)`; 194 columns. A handful differ: (6,2), (15,2) and (5,2), plus 4 `double precision`. | Mostly |
| 6.3 | **Enums are stored as `integer`** (S:1258 `ContractType`). There are no string conversions. | Everywhere |
| 6.4 | **JSON is stored as `jsonb`** (`audit_trails`) or through `HasJsonConversion` (`src/Infrastructure/Persistence/Conversions/ValueConversionExtensions.cs`). | Mostly |

---

## 7. Entities: folders and namespaces

| # | Convention | Sureness |
|---|---|---|
| 7.1 | **Entities live in one flat folder and one namespace,** `src/Domain/Entities/` and `MNEFE.Blazor.Domain.Entities`. They are grouped into multi-class files by area: `LkTables.cs`, `MainTables.cs` (3,117 lines), `InventoryTables.cs`, `TireTables.cs`, `BillingTables.cs` and so on, 26 files in all. | Everywhere |
| 7.2 | **Base classes live in `src/Domain/Common/Entities`.** `BaseLkEntity` is the exception: it is in `LkTables.cs`. | Mostly |
| 7.3 | **EF configuration lives in `src/Infrastructure/Persistence/Configurations/`.** There are 26 files holding 72 partial configurations; more is written inline in `OnModelCreating`. `src/Infrastructure/Configurations/` is a different thing: options classes. | Everywhere |
| 7.4 | **Business logic is in services, not the template's feature folders.** It lives in `src/Application/BusinessServices/{Interfaces,Implementation}` (40 interfaces, 50 implementations) and in grid CRUD services (`BaseCrudService`, `BaseCrudServiceLong`). The template's `Features/<Plural>/Commands/AddEdit…` layout survives only for `PicklistSets` and `Tenants`. | Everywhere |

---

## 8. Roles, permissions, routes and menus

| # | Convention | Sureness |
|---|---|---|
| 8.1 | **Roles are PascalCase constants named after themselves,** for example `AdministrationPHC`, `LogisticsManager`, `InventoryOfficer` and `MNEOGAccountant` (`src/Application/Common/Functions/MagicStrings.cs:380-411`). The display name is derived from the constant: "Administration PHC", and "Admin" is shown as "Super Admin" (`:413-424`). | Everywhere |
| 8.2 | **Business pages are authorised by role:** 75 `[Authorize(Roles=…)]` attributes, plus `PageRoles.EveryAssignedRole` and the `GroupAssigned` policy (`src/Server.UI/Services/Authorization/PageRoles.cs:13,20`). | Everywhere |
| 8.3 | **`Permissions.<Area>.<Action>` constants exist only for the template's own areas,** for example `"Permissions.Roles.View"` (`src/Application/Common/Security/Permissions/Roles.cs:13`). | Everywhere |
| 8.4 | **Routes have no single style.** Three coexist across 93 routes: lower-case kebab (`/finance/credit-notes`, `/system/compliance-reminders`), lower-case run-together (`/humanresources/payrollruns`) and PascalCase (`/ReferenceTables/ReferenceTables`, `/Employees/Manage`, `/RaiseTrip/{TripRequestsId:long}`). There is no `/pages/` prefix. | Question (Q11) |
| 8.5 | **The menu has one section per role, with upper-case titles:** `MANAGEMENT`, `OPERATIONS`, `SYSTEM` and `HUMAN RESOURCES` (`src/Server.UI/Services/Navigation/MenuService.cs:26,271,585`). | Everywhere |

---

## 9. Other deliberate, repeated patterns

- **Status history tables beside the main row:** `TBL_TRIP_STATUS_HISTORY`, `TBL_REPAIR_STATUS_HISTORY`, `TBL_VEHICLE_ASSIGNMENT_HISTORY` and `TBL_STOCK_HISTORY`.
- **Append-only tables enforced in code:** `TruckSwaps`, through `ApplicationDbContext.RefuseAppendOnlyChanges`.
- **Document numbers come from counter tables upserted in raw SQL** (`ClientInvoiceNumberGenerator.cs:21-24`), not from database sequences.
- **Check-constraint SQL is kept in named static classes,** for example `ClientInvoiceContractTypeConstraint` (`ClientOpeningBalanceConfigurations.cs:41`).
- **The log database is separate:** `LogDbContext`, with `system_logs` in `public` and its own history table, `__EFMigrationsHistory_Audit` (`DependencyInjection.cs:142`).
- **Migration names:** early migrations used throwaway lower-case names (48 of 217: `lkdata`, `tbles`, `adjxx`); later ones are descriptive PascalCase.

---

## 10. What MNEFleets shows about the GX template's own rules

MNEFleets is template-derived (it keeps `AGENT.md`, `PicklistSets`, `Tenants` and the interceptors). It applies the `core`/`TBL_` standard differently from GXTemplate's `GxNamingConventions`:

| | MNEFleets | GXTemplate's standard (as of `d9d9c75f`) |
|---|---|---|
| How a table reaches `core` | `[Table("TBL_X", Schema = "core")]` written on each entity | An automatic sweep over `IBusinessEntity` |
| Table name | Written by hand, usually plural | Derived from the class name, so singular for a singular class (`TBL_STOCK_MOVEMENT`) |
| Column names | snake_case (naming plug-in) | PascalCase, quoted; the plug-in is forbidden on the business context |
| Constraint names | `pk_tbl_…`, `fk_…`, `ix_…` (lower case) | `PK_…`, `FK_…`, `IX_…` (EF defaults) |
| Framework tables in `public` | `audit_trails`, `tenants`, … (snake_case) | `AuditTrails`, `Tenants`, … (PascalCase) |
| Documents | `core."TBL_DOCUMENTS"` | `public."Documents"` |
| Timestamps | `timestamp without time zone` (legacy switch on) | `timestamptz` (legacy switch forbidden) |
| What a lookup is | Anything used as a reference list | "Does any code branch on this row's value? No → lookup" |
| Lookup shape | `BaseLkEntity` (Id, Name, Slug) | Marker interface `ILookupEntity`; no fixed columns |
| Enums | `integer` | The template's README says "a C# enum stored as a string" |

GXTemplate made these choices on purpose before pass 58. MNEFleets reflects its legacy migration (FleetMSDbx). Which one is the GX convention is the owner's call: see Q2, Q4, Q7 and Q9.

---

## 11. Documents read

- **`AGENT.md`**: generic Clean Architecture guidance only. Its commands name `CleanArchitecture.Blazor.slnx` and `Migrators.MSSQL`, which do not exist here.
- **`README.md`**: no conventions.
- **`docs/Handover GX Informatics, MNEFleets and related work.md`**: business decisions only, such as "one role per user; approved records locked; paid records corrected by journal" (line 33). Nothing on naming.
- **`docs/Seed-Manifest.md`**: explicit lookup ids and resetting identity sequences (§3.4).
- **`docs/etl/README.md:830-853`**: the legacy naming.
- **`docs/etl/table-inventory.md:68-75`**: table counts and the `public` list.
- **`docs/passes/2026-09-01-investor-wht-account-investigation.md:230-238`**: the attribute convention, compared with GXTemplate.
- **`docs/passes/2026-09-01-investor-piece1-domain-ef.md:46-49`**: "these are the project's own business models, so they belong in `core`".
- **`docs/dev/*`** and **`docs/superpowers/*`**: no conventions.

GXTemplate has no handover or convention document of its own apart from the README's "Database naming" and "Modules and schemas" sections and the code comments covered in the audit.

---

## 12. Questions for the owner

- **Q1. What may live in `public`?**
  - You said only the Identity tables. MNEFleets also keeps six framework tables there (`SecurityPolicies`, `audit_trails`, `data_protection_keys`, `picklist_sets`, `tenants`, `tenant_users`), plus `system_logs`.
  - GXTemplate keeps more: `Documents`, and since passes 58-60 `ProcessedRequests`, `DocumentSequences`, `Branches`, `UserBranches` and `ApprovalLimits`.
  - Is the rule "framework tables in `public`, business tables in `core`"? If so, are branches and approval limits framework or business?
- **Q2. Is the GX naming rule "`TBL_` in `core`", or also "PascalCase columns and no snake-case plug-in"?**
  - MNEFleets is snake_case throughout.
  - GXTemplate forbids that on the business context, has a test for it, and its README explains why: the plug-in also rewrites `__EFMigrationsHistory`.
- **Q3. `docs/etl/table-inventory.md:75` is stale.**
  - It lists `notification_preference` in `public`, but it is now `core."TBL_NOTIFICATION_PREFERENCES"`.
  - Its counts (176 tables in `core`; the runbook says 183) differ from today's 193. `Seed-Manifest.md` says 75 `TBL_LK_` tables; there are 73.
  - Should these be corrected?
- **Q4. Plural or singular, and where does the name come from?**
  - MNEFleets writes each name by hand and mostly uses plurals (149 of 193), but 44 are singular and the dropped `StoreMovements` table was `TBL_STORE_MOVEMENT`.
  - GXTemplate derives the name from the class, so it is plural only if the class is.
  - Should the convention say "plural table names", "the class name, upper-snaked", or "written by hand"?
- **Q5. What is a lookup?**
  - MNEFleets puts master and configuration data under `TBL_LK_` (companies, depots, tanks, bank accounts, distance rates, the current payroll month). The code also branches on lookup ids through `MS.*Id`.
  - GXTemplate's rule ("does any code branch on this row's value?") would rename about a dozen of them `TBL_`.
  - Which rule is the convention, and should a lookup have fixed columns (MNEFleets: Id, Name, Slug; no active flag and no order)?
- **Q6. Should class names be singular?** Newer MNEFleets code uses singular classes; older code uses plurals that mirror the table name.
- **Q7. Constraint names.** Should they be EF's defaults (cased by whichever naming rule applies to columns), or a house scheme? MNEFleets has `ux_tbl_…` and `ux_…` side by side (Q8).
- **Q8. Hand-named unique indexes.** Should they all be `ux_tbl_<table>_…`? Five do not follow that form.
- **Q9. Timestamps.** MNEFleets keeps `timestamp without time zone` with Npgsql's legacy switch; GXTemplate uses `timestamptz` and forbids the switch. Is `timestamptz` the convention for new applications?
- **Q10. `[StringLength]` versus the 450 default.** The default silently overrides `[StringLength]`, so hundreds of declared lengths are not enforced. Is that intended?
- **Q11. Routes.** Is there a convention? GXTemplate uses lower-case kebab under an area (`/system/branches`, `/identity/users`); MNEFleets mixes three styles.
- **Q12. `TBL_LK_PAYMENT_TYPE` and `TBL_LK_PAYMENT_TYP`** both exist. Is the second one dead?
