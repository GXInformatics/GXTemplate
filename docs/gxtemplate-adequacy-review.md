# GXTemplate adequacy review: Spare Parts Management System

Reviewed 2026-11-11 at commit `d9d9c75f` (pass47b). `docs/architecture.md` was not found, so this review uses the requirement summary in the brief. No source files were changed.

## 1. Verdict

**Adequate with changes.** The template is a solid base. It has deny-by-default request authorisation, a transactional audit trail, tenant filters that are checked at startup, a factory-based DbContext suited to Blazor Server, a careful reconnect UI and about 1,080 tests. The biggest reason it is not ready is that **domain events cannot take part in the posting transaction**. `DispatchDomainEventsInterceptor` publishes after `SaveChanges`. The registered `ChannelBasedNoWaitPublisher` then queues each handler to a background task. That task runs it later on its own DbContext, and if the handler fails the error is only logged. A `GoodsReceiptPosted` handler that writes stock movements and a journal entry would therefore run outside the transaction, after the document had already committed. Requirement 2 depends on this, so it has to be redesigned before any module is built. Concurrency control (row versions, locks, gap-free numbering) and idempotent posting are missing altogether.

## 2. Summary table

| Area | Rating | Finding | Effort |
|---|---|---|---|
| A. Tenancy and scoping | Needs change | Tenant filter is opt-in by marker interface; a BranchId filter can be added the same way, but UserContext has no branches | M |
| B. DbContext and transactions | Risk | Factory per handler; events dispatched after save via a fire-and-forget channel, outside any transaction | L |
| C. Mediator pipeline | Needs change | Auth → Validation → ResultException → Performance → Cache → CacheInvalidation; no transaction or idempotency behaviour | M |
| D. Concurrency | Gap | No row version, no concurrency token, no locking, no raw SQL; concurrency failures become a generic message | M |
| E. Base entities, audit, soft delete | Needs change | Good transactional audit (opt-in `IAuditable`); soft delete is opt-in and unused; no immutability guard | S |
| F. Identity and authorisation | Needs change | Permission per request via attributes and policies; no value limits, no maker-checker, no rule that blocks admin approval | M |
| G. Caching | Needs change | FusionCache, 30-minute duration with fail-safe up to 3 h; tag invalidation; unsafe for stock or balances if used | S |
| H. UI layer | Needs change | MudBlazor 9.8, MudDataGrid; reconnect is safe for brief drops, but nothing saves drafts | M |
| I. Module fit | Needs change | Features are folders in one Application project; one `core` schema by convention; no in-repo generator | M |
| J. Import, export, print, jobs | Needs change | ClosedXML (decimal → double), QuestPDF A4 only, Hangfire in-memory with no jobs | M |
| K. Testing and operations | Ready | Four test projects on PostgreSQL; Serilog to a log DB; CI defined but disabled; Docker removed | S |
| L. Risks | Risk | Event-handler errors swallowed; DbUpdateException not logged; Excel loses decimal precision; Identity cascades | M |

## 3. Stack and structure

- **Solution:** `CleanArchitecture.Blazor.slnx` contains Domain, Application, Infrastructure, Server.UI, three migrator projects (MSSQL, PostgreSQL, SQLite) and five test projects. All target **net10.0**. Packages are versioned in each project file; there is no central package management.
- **Mediator:** `Mediator.SourceGenerator`/`Mediator.Abstractions` **3.0.2** (martinothamar), **not MediatR**.
- **EF Core** 10.0.11; Npgsql 10.0.3; EntityFrameworkCore.Exceptions 10.0.1; EFCore.NamingConventions 10.0.1 (log DB only).
- **Identity:** ASP.NET Core Identity EF 10.0.11 with Google and Microsoft external sign-in (`Infrastructure/DependencyInjection.cs`).
- **UI:** MudBlazor **9.8.0**, Blazor-ApexCharts 7.0.0, Interactive Server without prerendering (`App.razor:66`).
- **Other packages:** FluentValidation 12.1.1, Mapster 10.0.12, Ardalis.Specification 9.3.1, FusionCache 2.7.1, Hangfire.AspNetCore 1.8.24 with **Hangfire.InMemory**, Serilog.AspNetCore 10.0.0 (with MSSQL, PostgreSQL and SQLite sinks), ClosedXML 0.105.1, QuestPDF 2026.7.3, Scriban 7.2.6, Azure.Storage.Blobs 12.26.0.
- **Database:** provider is chosen by `DatabaseSettings:DBProvider` (`postgresql` by default, also `mssql` and `sqlite`). The template's `#if (UseSqlServer)`/`UsePostgreSql` symbols remove the providers you do not choose. Logs go to a separate database through `LogDbContext`.
- **Migrations:** one project per provider, each with `InitialCreate` and `TenantUserUniqueMembership`. They are applied by `ApplicationDbContextInitializer` at startup (`app.InitializeDatabaseAsync()` in `Program.cs`).
- **Packaging:** `dotnet new` template (`.template.config/template.json`, versions 1.0.0 to 1.4.0 nupkgs at the repository root).

## 4. Drift from upstream

The `template` remote is neozhu/CleanArchitectureWithBlazorServer. The fork point is `0b7f78f6` (2026-08-23), and upstream has 23 commits since then. GX work since the fork is 64 commits ("pass1" to "pass56"). Across `src` and `tests`, 707 files changed (+49,111 / −20,048).

- **Removed:** the Contacts and Products sample features and entities; `Pipeline/PreProcessors/LoggingPreProcessor.cs`; the Dockerfile, docker-compose files, `.dockerignore` and upstream's five GitHub workflows (replaced by one `build.yml`).
- **Added:** `AuthorizationBehaviour` (deny-by-default, checked at startup by `RequestAuthorizationRegistry`); `AdministratorPermissionRegistry`; marker-based tenant filters and `TenantFilterGuard`; audit rows written in the same transaction (`AuditWriteScope`); `IApplicationDbContext.Database` (Pass 55); `GxNamingConventions`; `SecurityPolicy` and idle timeout; Mailgun mail; `IFileStorage`; a separate log database; `GxSubmitButton`; a rewritten `gxReconnect.js`; ICU with default `Africa/Lagos`.
- **Changed:** `AuditableEntityInterceptor` (+367 lines), `DispatchDomainEventsInterceptor` (no longer opens a transaction), `ChannelBasedNoWaitPublisher` (now restores the caller's ExecutionContext).
- **Inherited unchanged:** the factory-only DbContext and the `ChannelBasedNoWaitPublisher` registration.
- **Could not determine:** whether any of the 23 newer upstream commits have been brought in. No merge commit from `template/main` was checked.

## 5. Findings by area

### A. Tenancy and data scoping
- **Evidence:** `ApplicationDbContext.ApplyTenantFilters` adds a named filter (`QueryFilters.Tenant`) to every root that implements `IMustHaveTenant`/`IMayHaveTenant`. The filter compares against `CurrentTenantId`, a context member that reads `IUserContextAccessor.Current.TenantId`. `AuditableEntityInterceptor.StampTenant` sets the tenant on new rows. `TenantFilterGuard` fails startup if a marked entity has no filter. `UserContext` carries `TenantId` and `AllowedTenantIds`.
- **Change:** keep tenancy dormant. Do not put tenant markers on module entities, and run with a single seeded tenant. Add `IMustHaveBranch` together with an `AllowedBranchIds` and `IsAllBranches` member on `UserContext`, populated by `UserContextLoader` from a user-branch table. Register a second named filter (`QueryFilters.Branch`) in the same loop: `IsAllBranches || AllowedBranchIds.Contains(e.BranchId)`. Extend `TenantFilterGuard` to check it. Do **not** stamp BranchId from the ambient context: a user can work in several branches, so the command must carry the branch, and the handler must check it against `AllowedBranchIds`.

### B. DbContext and transactions
- **Evidence:** only `AddDbContextFactory<ApplicationDbContext>(…, ServiceLifetime.Scoped)` is registered. Each handler calls `IApplicationDbContextFactory.CreateAsync` and disposes the context (`AddEditPicklistSetCommand`). No context is injected directly. The factory and its interceptors live as long as the circuit's scope. `IApplicationDbContext.Database` lets a handler call `BeginTransactionAsync`, and `AuditableEntityInterceptor` joins an existing transaction.
- **Event timing:** `DispatchDomainEventsInterceptor` publishes events for deleted entities in `SavingChanges` and all others in `SavedChanges`. That is **after** the SQL runs, but **before** the commit if the handler owns a transaction. `AddMediator` sets `NotificationPublisherType = ChannelBasedNoWaitPublisher` (`Application/DependencyInjection.cs:22`). That publisher writes handlers to a channel and returns at once. A background loop runs them with `CancellationToken.None`, and catches and logs any exception (`ProcessNotifications`). Event handlers get their own contexts from the factory, so they cannot share the command's connection or transaction.
- **Change:** add a separate path for in-transaction domain events.
  - Add an `IUnitOfWork` or ambient-context service that creates **one** context per command and exposes it to the handler and its in-transaction event handlers.
  - Add a `TransactionBehaviour` for requests marked `ITransactionalCommand`. It opens the transaction (serializable or with row locks, see D), runs the handler, then raises and handles that context's events **synchronously and in order** (inline, not through the channel publisher), calls `SaveChanges` again and commits.
  - Keep `ChannelBasedNoWaitPublisher` only for after-commit side effects (notifications, cache refresh).
  - Use a separate marker (`IPostingEvent`) so the two kinds of event cannot be confused.

### C. Mediator pipeline
- **Evidence:** order (outermost first): `AuthorizationBehaviour`, `ValidationBehavior`, `ResultExceptionBehavior`, `PerformanceBehaviour`, `FusionCacheBehaviour` (only `ICacheableRequest`), `CacheInvalidationBehaviour` (only `ICacheInvalidatorRequest`). There is no logging pre-processor any more.
- **Change:** insert `IdempotencyBehaviour` and then `TransactionBehaviour` after `ResultExceptionBehavior`, so exceptions in either are still turned into results. Run cache invalidation only after commit (it currently runs as soon as the handler returns).

### D. Concurrency
- **Evidence:** no matches for `IsRowVersion`, `IsConcurrencyToken`, `[Timestamp]`, `[ConcurrencyCheck]`, `DbUpdateConcurrencyException`, `FromSql`, `ExecuteSql`, `UPDLOCK` or `FOR UPDATE` in `src`. `DbUpdateConcurrencyException` would be caught as a `DbUpdateException` in `ResultExceptionBehavior` and become "A database error occurred…" (`DbExceptionHandler.GetGenericDatabaseErrors`).
- **Change:**
  - Add a `Version` concurrency token to the base class for master data and documents (rowversion on SQL Server, `xmin` on PostgreSQL).
  - Add an Infrastructure `IStockLockService` that locks balance rows in a fixed key order (`SELECT … FOR UPDATE` / `WITH (UPDLOCK, ROWLOCK)`).
  - Add a `DocumentSequence` table keyed by (type, branch, year), incremented under a row lock inside the posting transaction.
  - Map `DbUpdateConcurrencyException` to a specific "changed by someone else, reload" result.

### E. Base entities, auditing and soft delete
- **Evidence:** `BaseEntity<TKey>` (domain events), `BaseAuditableEntity` (Created/LastModified by and at), `BaseAuditableSoftDeleteEntity`. Audit rows are written only for entities marked `IAuditable` (`GenerateAuditTrails`). Each row records user, tenant, UTC time, table, primary key, `AffectedColumns` and per-field `Old`/`New` as JSON (`AuditTrailConfiguration`). They are written in the same transaction, and a failed audit write rolls the change back. Soft delete is opt-in through `ISoftDelete`, and nothing implements it today (`ModelBuilderExtensions` remarks). `AuditTrail.UserId` is `SetNull` on user delete.
- **Need:** requirement 6 is met if every master-data and chart-of-accounts entity is marked `IAuditable`.
- **Change:**
  - Add an `IImmutableWhenPosted`/`ILedgerEntry` rule in a save interceptor that rejects Modified or Deleted entries for posted documents and ledger rows.
  - Never apply `ISoftDelete` to ledgers.
  - Make Identity users non-deletable (deactivate instead) so the audit "who" cannot become null.

### F. Identity and authorisation
- **Evidence:** permissions are string constants in nested static classes (`Permissions.Users.*`) and are turned into policies in `AddAuthorizationCore`. Requests carry `[RequestAuthorize(Policy = …)]`; any one matching attribute is enough. `AuthorizationBehaviour` checks them through `IdentityService.AuthorizeAsync`, which rebuilds the principal from the database on each call. The UI uses `[Authorize(Policy)]` on pages plus `PermissionService.GetAccessRightsAsync<TAccessRights>()`, which must match the constant names. The only roles are `Admin` and `Basic` (`Constants/Roles.cs`). The `Users.Administrator` user is granted every permission listed in `AdministratorPermissionRegistry.Granted`.
- **Need:** create, approve, post and reverse permissions fit the existing pattern; value limits, maker-checker and the Super Admin rule do not exist.
- **Change:**
  - Add an `ApprovalLimit` table (role or user, document type, currency, maximum amount).
  - Add an `IApprovalPolicy` service that handlers call. It checks the limit, `RequestedById != currentUserId`, and that the user is not in the SuperAdmin role.
  - Put "Approve" permissions in `AdministratorPermissionRegistry.Excluded` so the bootstrap administrator never receives them.

### G. Caching
- **Evidence:** `FusionCacheBehaviour` caches any `ICacheableRequest` with keys scoped Global, PerUser, PerTenant or PerUserAndTenant (`CacheScope`). Defaults are 30-minute duration, fail-safe up to 3 h, and a 2 s soft factory timeout (`Infrastructure/DependencyInjection.cs:672`). The pipeline sets `AllowStaleOnFailure: false`. Invalidation is by key or tags in `CacheInvalidationBehaviour`. There is no backplane; the cache is in-process memory.
- **Risk:** a cached stock or balance query would show stale values until a write declares the same tag. There is no branch scope.
- **Change:** never mark stock, balance or ledger queries `ICacheableRequest` (enforce this with an architecture test). Add a PerBranch scope if master-data lookups are cached by branch.

### H. UI layer
- **Evidence:** MudBlazor 9.8.0. Seven pages use `MudDataGrid`, including server-side `ServerData` (`Documents.razor`). Pages use access-rights objects and MudDialog for add and edit. Localisation uses resx with en-US, de-DE and zh-CN (`LocalizationConstants`). `ReconnectModal.razor` and `gxReconnect.js` act only on terminal circuit states, so a brief drop keeps the circuit and the form. `GxSubmitButton` prevents a second click while busy.
- **Gap:** the code does not set `CircuitOptions` (default retention applies). There is no use of `PersistentComponentState` or browser storage for drafts. A circuit that is lost (server restart, retention expired) loses the half-entered document. No idempotency key exists for a retried post.
- **Change:**
  - Save draft documents to the server (a `Draft` status row) as lines are added. Save on change, not only on submit.
  - Have each document form create a client `RequestId` (GUID), store it on the command, and keep it across reconnects.
  - Raise `DisconnectedCircuitRetentionPeriod` to suit the network.

### I. Module fit
- **Evidence:** features live under `Application/Features/<Feature>/{Commands,Queries,DTOs,EventHandlers,Caching,Security,Specifications}` with a matching folder in `Server.UI/Pages`. There is one `ApplicationDbContext`. `GxNamingConventions.ApplyGxTableNaming` puts every `IBusinessEntity` in the single schema `core` as `TBL_UPPER_SNAKE` unless the table is named by hand with `ToTable`. No code generator is in the repository. Upstream's generator is an external Visual Studio extension that was not reviewed.
- **Change:**
  - Add `Features/<Module>/<Feature>` folders, keeping one context.
  - Extend `GxNamingConventions` to choose the schema from the module (a namespace segment or an `[Module("inventory")]` attribute) instead of always `core`.
  - Keep cross-module references to IDs only, with no navigation properties across modules, and enforce this with an architecture test (none exist today).

### J. Import, export, printing, files and background jobs
- **Evidence:** `ExcelService` uses ClosedXML. Export casts `decimal` to `double` (line 51). Import reads numbers as `(decimal)cell.GetDouble()` (line 71). `PDFService` uses QuestPDF for **A4 tables only**. `IFileStorage` supports local disk and Azure Blob. Hangfire uses **`UseInMemoryStorage()`**, the dashboard is at `/jobs`, and no recurring jobs are defined. `ISystemContext` exists to run background work with no user.
- **Change:**
  - Write decimals to Excel as decimals.
  - Add QuestPDF A4 invoice and 80 mm receipt documents (or ESC/POS output).
  - Switch Hangfire to persistent storage (Hangfire.PostgreSql or SqlServer) before adding the reorder and reconciliation jobs.
  - Handle barcode scanning as keyboard input in a MudTextField with Enter submit; nothing in the template prevents this.

### K. Testing and operations
- **Evidence:** test counts by attribute: Application.UnitTests 557, Infrastructure.UnitTests 178, Application.IntegrationTests 54, Server.UI.IntegrationTests 294 (NUnit, bUnit, Jint for the reconnect JavaScript, Moq). Integration suites need a PostgreSQL server in `GX_TEST_PG`. `.github/workflows/build.yml` is described in its header as inert because Actions is disabled. Serilog writes to the log database. `AddHealthChecks()` registers no checks (no database check) and `/health` allows anonymous access. Secrets are empty in `appsettings.json` and supplied through environment variables or `UserSecretsId`. Docker: **not found**.
- **Change:** add a database health check. Re-enable CI. Add tests for posting atomicity, concurrent sale of the last unit, and numbering with no gaps.

### L. Risks for financial data
1. **Event handler errors are silently swallowed and run outside the transaction:** `ChannelBasedNoWaitPublisher.ProcessNotifications`.
2. **DbUpdateException is turned into a generic message with no log entry:** `ResultExceptionBehavior` (the `DbUpdateException` branch) and `DbExceptionHandler` (no logger).
3. **`ResultExceptionBehavior` returns `exception.Message` to the UI** for unexpected exceptions.
4. **Decimal to double conversion** in `ExcelService` import and export.
4a. **No money precision convention.** No `HasPrecision` or `decimal` property exists in the model, and `ConfigureConventions` sets only a string length of 450.
5. **Cascade deletes** apply to Identity and `TenantUser` only (`IdentityUserConfiguration`, `TenantUserConfiguration`); none apply to business data yet.
6. **Indexes on foreign keys:** EF creates them by convention and the migrations include them (`CreateIndex` in `InitialCreate`). The risk is only in new modules that turn conventions off.
7. **In-memory Hangfire and ticket store** (`MemoryCacheTicketStore`): jobs and sign-ins are lost on restart.

## 6. Changes to make before any module is built (in order)

1. Add a model convention for money: `decimal(18,2)` for amounts and `decimal(18,4)` for quantity and unit cost, using `ConfigureConventions` plus attributes or value types.
2. Add the unit of work and `TransactionBehaviour`, with synchronous in-transaction dispatch of posting events. Keep the channel publisher for events after commit only (B).
3. Log `DbUpdateException`, map `DbUpdateConcurrencyException` to its own result, and stop sending raw exception messages to users (L2, L3).
4. Add the concurrency token base, the `IStockLockService`, and the `DocumentSequence` allocator (D).
5. Add `IdempotencyBehaviour` with a `ProcessedRequest(RequestId, ResultJson)` table, written in the same transaction (C, H).
6. Add the immutability interceptor and the posted-status and reversal base types; never soft-delete ledgers (E).
7. Add the branch model: `Branch`, `UserBranch`, `UserContext.AllowedBranchIds`, the `QueryFilters.Branch` filter and the guard (A).
8. Add `IApprovalPolicy`, the approval-limit table, the maker-checker rule and exclusion of the Super Admin (F).
9. Add schema selection per module in `GxNamingConventions`, plus architecture tests for module isolation and for "no cached stock queries" (I, G).
10. Fix decimal handling in Excel, add receipt and invoice PDFs, and switch Hangfire to persistent storage (J).
11. Add a database health check and re-enable CI (K).

## 7. Things to remove or switch off

- **Tenant features:** the Tenants pages and feature, `TenantSelector`, and the `SwitchTenants` and `SwitchToAnyTenant` permissions. Hide them or seed a single tenant. Keep the filter machinery as the pattern for branches.
- **Unused sign-in:** Google and Microsoft external sign-in, and self-registration (`AllowSelfRegistrationSetting`), unless they are wanted.
- **Unused languages:** de-DE and zh-CN.
- **Caching:** `ICacheableRequest` on anything that reads stock, balances or the journal.
- **Unused storage and providers:** Azure Blob if storage is on premises; unused database providers (template option).

## 8. Open questions for the owner

1. Which database (PostgreSQL by default, or SQL Server)? Locking SQL and row-version mapping depend on it.
2. Is `docs/architecture.md` meant to exist? It was not found.
3. Should the 23 upstream commits since `0b7f78f6` be brought in before work starts?
4. Is the organisation switcher work in progress (the uncommitted `TenantSelector.razor` change and `docs/HANDOFF-tenant-switcher.md`) still wanted, given that branches are not tenants?
5. Receipt printers: browser printing to an 80 mm driver, or raw ESC/POS?
6. How long must a dropped circuit be kept before a draft is considered lost?
7. Can a user hold more than one branch at once in a single session, or do they switch an "active branch"?
8. Why is GitHub Actions disabled, and where should CI run?
9. Is the external Visual Studio code generator used? It is not in the repository and was not reviewed.
