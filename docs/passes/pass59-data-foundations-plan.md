# Pass 59: data foundations (plan)

**Status:** written 2026-11-11 at HEAD `18e300fe` (pass 58, template 1.5.0). Amended the same day with the owner's decisions (§8) and amendments 1 to 5, then implemented in this pass. Text marked **(B1)** to **(B5)** comes from the amendments. Text marked **(P59)** was added during implementation, where the code required something the amendments did not say. Each (P59) addition is listed in the pass report (`docs/passes/pass59.md`).
**Database:** PostgreSQL only. The concurrency token, the immutability triggers and the branch-filter parameter tests are PostgreSQL-specific. Npgsql code sits under `#if (UsePostgreSql)`, and the PostgreSQL-only services throw `NotSupportedException` on other providers, as in pass 58.
**Source:** `docs/gxtemplate-adequacy-review.md`, change list items 1 (money precision), 4 (the concurrency token only; pass 58 delivered the rest of item 4), 6 (immutability) and 7 (branch model).
**Unchanged:** pass 58's unit of work, idempotency, numbering and row locks (`docs/passes/pass58.md`, README "Posting transactions"). The pinned suites (`SaveChangesInterceptorRegressionTests`, `InterceptorOrderingTests`, `TransactionalAuditTests`, the publisher suites) must pass **unmodified**.
**Ships as:** template 1.6.0.

The first application is a Spare Parts Management System: one company, an HQ and up to 100 branches, 20 concurrent users. Its needs shaped the mechanisms below. Nothing specific to spare parts goes into the template.

## 1. What is wrong today (verified in code)

### Part A: precision
- **No decimal convention.** `ApplicationDbContext.ConfigureConventions` (`ApplicationDbContext.cs:242-246`) sets only `Properties<string>().HaveMaxLength(450)`. Without a convention, a `decimal` property gets the provider default. On PostgreSQL that is `numeric` with no precision or scale.
- **No decimal, float or double property exists in `src/Domain` today**, so nothing is wrong yet in the data. Nothing would stop the first module adding one, though.
- **No rounding helper exists.** There is no shared `Math.Round` policy anywhere in `src`.

### Part B: concurrency
- **No entity carries a concurrency token.** `BaseEntity`, `BaseAuditableEntity` and `BaseAuditableSoftDeleteEntity` (`Domain/Common/Entities/`) have no version property. `src` has no `IsRowVersion`, `[Timestamp]` or `IsConcurrencyToken`.
- **Edits lose the loaded state.** `AddEditPicklistSetCommandHandler` (`AddEditPicklistSetCommand.cs`) re-reads the row with `FindAsync` in a fresh context and maps the command onto it. Nothing the user loaded travels with the command. The last writer silently wins.
- **The failure message already exists.** Pass 58 added `ResultExceptionBehavior.ConcurrencyConflictMessage`, "This record was changed by someone else. Reload and try again." (`ResultExceptionBehavior.cs:46-50, 76`). It is logged at Error. Today it can never fire, because no entity has a token.
- **The form shows it as an ordinary error.** For example, `CreatePicklistDialog.razor:94` shows `result.ErrorMessage` in a Snackbar. There is no reload path.
- **`IApplicationDbContext` does not expose `Entry()`**, only DbSets and `Database` (`IApplicationDbContext.cs`). A handler therefore cannot set an original value without casting to the concrete context.

### Part C: immutability
- **No save path rejects a change for business reasons.** The only save interceptors are `AuditableEntityInterceptor` and `DispatchDomainEventsInterceptor` (`Infrastructure/DependencyInjection.cs:122-130`). Neither refuses Modified or Deleted entries.
- **Soft delete changes the entry state.** `AuditableEntityInterceptor.SetDeletionAuditInfo` (`AuditableEntityInterceptor.cs:409-416`) turns a Deleted `ISoftDelete` entry into Modified. A posted document marked `ISoftDelete` would therefore be "deleted" by an update, which an immutability rule written against Deleted would miss.
- **Bulk operations bypass the save path.** `ExecuteUpdate`, `ExecuteDelete` and raw SQL do not run save interceptors. The template already relies on this for logs (`DependencyInjection.cs:199`, `LogDbContext.cs:60`). Nothing guards business tables against them. `ForUpdateCommandInterceptor` shows that a `DbCommandInterceptor` can inspect and refuse commands.
- **A third save interceptor would break a pinned suite.** `InterceptorOrderingTests.cs:63` asserts that exactly **two** `ISaveChangesInterceptor` services are registered. This constrains where the new save-time rules can live (§3.0).
- **Users can be hard-deleted.** Every place a user can be deleted today:
  1. `Server.UI/Pages/Identity/Users/Users.razor:589`, `ProcessUserDeletionAsync` (row menu, "Delete"), calling `UserManager.DeleteAsync`;
  2. `Users.razor:642`, `DeleteSelectedUsersAsync` (toolbar, "Delete" on the checked users), with the same call in a loop. It is not transactional: "the rows before a failure stay deleted".

  Both are gated by `Permissions.Users.Delete` (`Permissions/Users.cs:22`), which the administrator is granted (`AdministratorPermissionRegistry.cs:165`). No Mediator command, endpoint or self-service "delete personal data" page deletes users. `grep` for `DeleteAsync(`, `DeletePersonalData` and `ExecuteDelete` over `src` finds no other path.

  These deletions cascade to claims, roles, logins, tokens and `TenantUsers` (`IdentityUserConfiguration.cs`, `TenantUserConfiguration.cs:16-18`). They null the audit trail's user (`AuditTrailConfiguration.cs:14-17`, `OnDelete(SetNull)`). They fail with a foreign-key error for any user who uploaded a `Document` (`DocumentConfiguration.cs:26-33`, `Restrict`); `ResultExceptionBehavior` is not involved there, because the page calls `UserManager` directly.
- **Deactivation already exists.** `Users.razor:781-840` sets `IsActive = false` and `LockoutEnd = MaxValue`. It is guarded by `EnsureNotRemovingLastAdministratorAsync` and `Permissions.Users.Deactivation`. Sign-in refuses inactive users (`Login.razor:168`, `IdentityComponentsEndpointRouteBuilderExtensions.cs:262`).

### Part D: branches
- **No branch concept exists.** There is no `Branch` entity, no `UserBranch` table, no `IMustHaveBranch` and no `QueryFilters.Branch` (`QueryFilters.cs` has `Tenant` and `SoftDelete` only).
- **`DocumentSequences.BranchId` is an `int` with nothing behind it** (pass 58, `DocumentSequence.cs`).
- **`UserContext` has no branch members** (`UserContext.cs:19-30`). `SystemContext` builds one with only a tenant (`SystemContext.cs:281-289`).
- **The user context is a snapshot per connection, not per call.** `UserContextHubFilter.OnConnectedAsync` (`UserContextHubFilter.cs:87-101`) loads it once into `HubCallerContext.Items`, and every hub invocation pushes that same object (`:42-56`). `UserContextLoader` caches it for **1 hour** (`UserContextLoader.cs:43`). `ClearUserContextCache` has three callers: `TenantSwitchService.cs:118` and `UserFormDialog.razor:293, 320`. None of them updates a live connection's snapshot. The tenant switcher copes with a full page load (`TenantSelector.razor:185-193`, `forceLoad: true`).
- **Only authorization is fresh on every call.** `IdentityService.AuthorizeAsync` rebuilds the principal from the database each time (`IdentityService.cs:405-417`). A branch read set held in `UserContext` would not be (see §7.1).
- **The tenant filter is the pattern to copy:**
  - `ApplyTenantFilters` builds the filter expression against a context **member**, so the cached model is re-bound per context (`ApplicationDbContext.cs:203-226`);
  - `TenantFilterGuard` fails startup for a marked entity with no filter (`TenantFilterGuard.cs`);
  - the guard runs first in `ApplicationDbContextInitializer.InitialiseAsync` (`:59`).
- **The organisation-switcher work** is in `stash@{0}`. It changes one file, `TenantSelector.razor`: the menu re-reads its list when it opens, and reloads when the profile arrives. Its handoff note is in the stash's untracked part.

## 2. Target behaviour

1. **Every `decimal` in the model declares what it is.** An undeclared one stops startup. Each kind maps to one precision. A value with more decimals than its kind allows is refused at save; the database does not round it silently.
2. **An edit to versioned master data or a document header saves only if the row is unchanged since the user loaded it.** Otherwise the user sees "This record was changed by someone else. Reload and try again." and the form offers Reload. An edit that does not supply the version fails loudly: it never silently skips the check.
3. **Ledger rows are insert-only. A posted document and its lines can no longer change**, except properties marked as allowed after posting. A reversal posts a mirror entry. Bulk SQL against these tables is refused. Optionally, the database refuses it too (§8.6).
4. **Users are deactivated, never deleted.** Any code path that deletes a user fails loudly. The audit trail's user reference cannot become null.
5. **Each user reads only their branch read set and writes only to their write set.** View-all widens reading only. Every branch-scoped write names its branch in the command, and the write is refused outside the write set. A branch-scoped write that does not go through such a command fails loudly. A two-branch document is visible to both branches and to no third.

## 3. Components

### 3.0 Where the save-time rules run

The rules in B, C and D check entries before anything is written. They run in **one place: an override of `ApplicationDbContext.SaveChanges(bool)` and `SaveChangesAsync(bool, CancellationToken)`.** The override calls `ChangeTracker.DetectChanges()`, runs `DataRules.Check(...)`, then calls `base`. I recommend this over a third save interceptor for three reasons:

- `InterceptorOrderingTests` pins the count of `ISaveChangesInterceptor` registrations at 2. The override needs no registration.
- It runs **before** `AuditableEntityInterceptor`. The rules therefore see the entry states the handler produced: no soft-delete conversion has happened yet, and no audit transaction has been opened.
- Every save goes through it, including Identity's `UserStore`, which calls the context's `SaveChangesAsync`, and the 17 directly constructed contexts. Those contexts hold no guarded entities, so the pinned suites see no difference.

The bulk-operation guard is a `DbCommandInterceptor`. It is added with `m.AddInterceptors(...)` beside `ForUpdateCommandInterceptor.Instance` (`DependencyInjection.cs:141`), not as an `ISaveChangesInterceptor`.

The startup checks go into a new `DataModelGuard` (Infrastructure, beside `TenantFilterGuard`). It is called on the next line of `InitialiseAsync`, before migrations. It collects every problem and throws once, naming each offender, as `TenantFilterGuard` does.

### 3.1 Part A: money and quantity precision

**How a property declares its kind: attributes. I recommend this.**

| Attribute (Domain) | Column | Use |
|---|---|---|
| `[Money]` | `numeric(18,2)` | amounts, totals, prices in currency |
| `[Quantity]` | `numeric(18,4)` | quantities |
| `[UnitCost]` | `numeric(18,4)` | cost or price per unit |
| `[Rate]` | `numeric(18,6)` | tax rate, markup percent, exchange rate |

All four derive from `DecimalKindAttribute(precision, scale)`, so the guard and the convention read one property.

- **Why not value types** (`Money`, `Quantity` structs):
  - every property would need a value converter;
  - LINQ aggregates (`Sum`) over converted types translate poorly;
  - Mapster, FluentValidation and MudBlazor's `MudNumericField` all work on `decimal`;
  - the template would have to ship arithmetic operators and currency semantics it does not need.

  Attributes keep `decimal` everywhere and add one line per property.
- **The convention:** a loop in `OnModelCreating`, after `ApplyConfigurationsFromAssembly` and before `ApplyGxTableNaming`. It sets `HasPrecision(p, s)` on every `decimal`/`decimal?` property carrying a kind. It covers owned and complex types.
- **The guard** (`DataModelGuard`) fails startup when any of these holds:
  - a `decimal`/`decimal?` property on **any entity type in `ApplicationDbContext`'s model** has no kind. The brief says "business entity"; the whole model is stricter and costs nothing, since none exists today (§7.9);
  - a property declares two kinds;
  - a configuration overrode the precision so it no longer matches the kind;
  - a kind attribute sits on a non-decimal property.
- **The save check:** in `DataRules`, refuse a value whose scale exceeds its kind, naming the entity and property. PostgreSQL would otherwise round it on insert (half away from zero) and the stored value would differ from the computed one without anyone knowing. The check forces rounding to be explicit, through the helper.
- **The rounding helper:** `Application/Common/Numerics/GxRound`, with `Money(x)`, `Quantity(x)`, `UnitCost(x)` and `Rate(x)`. **Proposed rule** (the owner decides, §8.1):
  - `MidpointRounding.AwayFromZero` at the kind's scale, so 2.345 becomes 2.35 and −2.345 becomes −2.35. This matches commercial invoices and what PostgreSQL itself does.
  - Round **once**, when a value is computed for storage. For example, a line amount is `GxRound.Money(qty * unitCost)`.
  - A total is the sum of the already-rounded stored amounts, never a recomputation.
  - Intermediate arithmetic is never rounded.
- **`float`/`double` on entities:** **I recommend they fail the guard**, with one escape: `[Approximate]`, for genuinely approximate data such as coordinates. A financial application has no other legitimate use for them, and `ExcelService`'s decimal-to-double cast (review J) shows how such values creep in (§8.2).

### 3.2 Part B: concurrency token

**Mapping, checked against the installed provider's current documentation.** The provider is `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.3 (`Domain.csproj:25`), and the documentation is npgsql.org/efcore/modeling/concurrency, read 2026-11-11.

- The supported mapping is a **`uint` property with `[Timestamp]` or `.IsRowVersion()`**, which maps to the `xmin` system column.
- `UseXminAsConcurrencyToken()` has been obsolete since 7.0 (7.0 release notes, "Obsoleted UseXminAsConcurrencyToken"). It is not used.
- The documentation does not say whether a migration adds a column. Step 2 of §4 verifies that the generated PostgreSQL migration contains **no** `AddColumn` for `xmin`, and test 9 pins it.
  - **(P59) Verified differently.** The generated C# migration does list `AddColumn("xmin", rowVersion: true)`, and `xmin` appears in the `Branches` `CreateTable`, because EF's differ sees a property. Npgsql's SQL generator skips system columns, so the SQL the database receives creates no column. Test 9 therefore asserts on the generated SQL, not on the C# operations.
- `GxNamingConventions` renames tables only, never columns (`GxNamingConventions.cs:92-93, 115-116`), so the column keeps the name `xmin`.

**Types (Domain):**
- `IVersioned { uint Version { get; } }`, an opt-in marker.
- `BaseVersionedEntity<TKey> : BaseAuditableEntity<TKey>, IVersioned` and `BaseVersionedEntity : BaseVersionedEntity<int>`, convenience bases for master data. A Guid-keyed document header uses `BaseVersionedEntity<Guid>`.
- `Version` has a **private setter**. Only EF writes it, so a mapping from a DTO cannot set it by accident.

**Configuration:**
- **PostgreSQL:** a model loop applies `IsRowVersion()` to `Version` on every `IVersioned` root, under `#if (UsePostgreSql)` with a provider check.
- **Other providers:** `Version` is ignored, and the helpers below throw `NotSupportedException`. SQL Server's `rowversion` is 8 bytes and does not fit `uint`, and pass 58 decided against building MSSQL equivalents.
- **Guard:** `IVersioned` on a TPH leaf whose root is not versioned fails startup, in the same form as the tenant leaf rule.

**The round trip.** Each handler opens a fresh context, so the version has to travel with the form:

1. **Query:** the DTO of a versioned entity carries `uint Version`, mapped by Mapster from the entity. This is a read, so Mapster convention mapping is fine.
2. **Form:** the edit dialog holds the DTO's `Version` and puts it on the command unchanged.
3. **Command:** an edit command for a versioned entity implements `IVersionedEdit { uint? Version { get; } }`. It is nullable so that "not supplied" is distinguishable from a value. `xmin` is never 0 on a user row, but nullable says what is meant.
4. **Handler:** loads the row, maps the command onto it, then calls `db.ApplyLoadedVersion(entity, request.Version)`. This sets `Entry(entity).Property(e => e.Version).OriginalValue`. EF then issues `UPDATE … WHERE "Id" = @id AND xmin = @loaded`, and zero rows affected raises `DbUpdateConcurrencyException`. A delete works the same way.
5. **Helper:** `IApplicationDbContext` gains `EntityEntry<TEntity> Entry<TEntity>(TEntity entity)`, which `DbContext` already implements. `VersionExtensions.ApplyLoadedVersion` and `UseCurrentVersion` (below) live in `Application/Common/Concurrency`.
6. **Mapster:** the command-to-entity mapping must not touch `Version`. The private setter makes this automatic, and test 8 proves it.
7. **(B4) Saves return the new version.** A command that edits a versioned entity, or the lines of a versioned header, returns the entity's new `Version` in its result. The form replaces the version it holds with it. Without this, a form that saves more than once without reloading (a draft saved as each line is added) conflicts with its own first save.
   - **Result shape:** `Result<Saved<TId>>`, where `Saved<TId>` carries `Id` and `Version`. The handler creates it with `db.SavedVersionOf(entity, id)` and returns it.
   - **Where the version comes from.** EF reads the new `xmin` back in the same statement as the save (`UPDATE … RETURNING xmin`, and `INSERT … RETURNING` for a new row). Outside a unit of work the handler saves before it returns, so the value is already current.
   - **Inside a unit of work it is not enough, and the plan says so rather than working around it.** The handler's own save reads back the version, but `TransactionBehaviour` saves again after the handler returns, and pass 58's posting-event rounds may save the same header again. A `Version` copied into the result by the handler would therefore be stale by the time the transaction commits.
   - **How the behaviour supplies it.** `Saved<TId>` is a reference type. `SavedVersionOf` registers it against the context. After **every** successful save on that context, the `SaveChanges` override (§3.0) refreshes each registered `Saved<TId>` from its entity's current `Version`. The last save before the commit therefore leaves the result holding the committed version, and an idempotent response is recorded after it. Registrations end when the context is disposed. Test 34 covers both paths.

**How an omission fails loudly:**
- **Validation:** a shared `VersionedEditValidator` refuses `Version == null` on an `IVersionedEdit` with Id > 0.
- **Save** (`DataRules`, the backstop that catches a handler that forgets): every **Modified or Deleted** `IVersioned` entry must have been passed through either `ApplyLoadedVersion` or `UseCurrentVersion` in this context. The context records this in a `ConditionalWeakTable<object, …>`. Otherwise the save throws `InvalidOperationException`: "*{Entity} {Id} was modified without the version the user loaded. Call ApplyLoadedVersion(entity, request.Version), or UseCurrentVersion(entity) for a system update made under a row lock.*"
- **`UseCurrentVersion(entity)`:** for server-side updates where no user version exists, such as a Post handler changing a header's status. It keeps the freshly read `xmin` as the original. That is still a check: the update fails if the row changed between the read and the save. Under `ForUpdate()` it cannot fail, because the row is locked.
- **There is no static check that every edit command of a versioned entity implements `IVersionedEdit`.** Which entity a handler edits is only known at run time. The save check is the loud failure, on the first exercise of the path, which any test of that path hits.
- **(P59, owner decision during implementation) The save check applies inside Mediator requests only.** As planned, it broke five pinned tests: `SaveChangesInterceptorRegressionTests` and `TransactionalAuditTests` edit and delete a `PicklistSet` through a plain context, and `PicklistSet` is versioned (decision 5). The owner chose to scope it. `ValidationBehavior`, which runs for every request, marks the call chain (`VersionChecks.EnterRequest`).
  - Any handler that saves a versioned entity without applying a version still fails loudly, whatever its command type.
  - Direct context use outside Mediator (seeding, infrastructure services, the pinned suites) is not checked. EF still compares against the version it read.
- **(P59)** `IVersionedEdit` also carries `bool IsEdit`, because the validator must tell an edit from a create, and the interface cannot know the key's name.
- **(P59)** A delete by id (`DeletePicklistSetCommand`) carries no user-loaded version, so it uses `UseCurrentVersion`.

**Message and recovery:**
- The pass 58 message is what the user sees: `ResultExceptionBehavior` catches `DbUpdateConcurrencyException` before `DbUpdateException` and returns `ConcurrencyConflictMessage`. Test 7 asserts the exact text through a real stale edit.
- `Result` has no code field (pass 58, A2 limitation), so a form recognises the conflict with `ConcurrencyConflict.Is(result)`, which compares against the constant.
- **Recovery:** the template's edit dialog (`CreatePicklistDialog`, as the working example) shows the message with a **Reload** action. Reload re-queries the record into the form, with the new values and the new `Version`. The user's unsaved edits are discarded; this pass does not merge them (§8.4). The dialog stays open.

**Interaction with pass 58:**
- **`ForUpdate()`:** `SELECT … FOR UPDATE` records the locker in `xmax` and does not change `xmin`. Locking does not invalidate a loaded version, and the versioned `UPDATE` is unaffected. While A holds the lock, B's versioned `UPDATE` waits. When A commits, READ COMMITTED re-evaluates B's `WHERE xmin = @loaded` against A's new row version, finds 0 rows, and B gets the conflict message. Test 10 covers this.
- **Unit-of-work retry:** `DbUpdateConcurrencyException` is **not** retried. `UnitOfWork.IsRetryable` matches only `PostgresException` 40001 and 40P01 (`UnitOfWork.cs:229-238`), and a conflict is not transient. A retried attempt (after a deadlock) builds a fresh context and re-reads the row. The handler calls `ApplyLoadedVersion` again with the same `request.Version`, so the check is identical on every attempt. The A6 rule (no mutation of the request) is what makes this true.
- **Several saves in one unit of work:** EF reads the new `xmin` back after each save (`RETURNING xmin`), so the posting-event loop's second save on the same header checks against the version the first save produced.
- **A risk for later versions:** the Npgsql documentation says that from **11.0**, `40001`/`40P01` during `SaveChanges` are reported as `DbUpdateConcurrencyException`. That would make pass 58's retry check still match (it walks inner exceptions), but `ResultExceptionBehavior` would show "changed by someone else" for a deadlock that exhausted its retries. Test 11 pins today's behaviour so an upgrade to 11 fails loudly (§7.7).

**Template entities that become versioned:**
- `PicklistSet` is the working example, with its DTO, command, handler and dialog.
- `Branch` (§3.4), if shipped.
- `SecurityPolicy` stays unversioned: it is a single-row settings record edited by administrators.

### 3.3 Part C: immutability

**Types (Domain):**

| Type | Meaning |
|---|---|
| `IAppendOnly` | Ledger row. Added is the only allowed state change; any Modified or Deleted entry fails the save. |
| `IPostable { DateTime? PostedAt { get; } }` | Document header. It is **posted** when the **original** (database) value of `PostedAt` is not null. Status enums stay with the modules; `PostedAt` is the one neutral fact the template can rely on. |
| `IPostableLine<THeader> where THeader : IPostable` | A line of a postable document, with a required navigation to its header. |
| `[AllowedAfterPosting]` | On a property of an `IPostable` type: may change after posting. Examples are `Status` moving to Reversed, `ReversedById`, `ReversedAt` and `ReversalOfId`. **(B2)** Also allowed on a property of an `IPostableLine<>` type, for counters a posted line legitimately carries, such as a returned quantity. The audit fields (`LastModifiedAt`, `LastModifiedById`) are always allowed; `Version` is store-generated and never compared. |

**Save rules (`DataRules`):**
1. **`IAppendOnly`:** Modified or Deleted fails, naming the type and key, with the hint "post a reversing entry".
2. **`IPostable`, posted:**
   - Deleted fails.
   - Modified fails if any property other than an allowed one has `IsModified` with a changed value. `PostedAt` itself is never allowed, so a posting cannot be un-posted. Owned types count as part of the owner.
   - **(Exemption)** A header that became posted **in the current unit of work** stays editable until commit, because pass 58's posting-event handlers may save the header again (for example, to set a link to the journal entry). The unit-of-work session records the headers whose `PostedAt` went from null to a value. Outside a unit of work there is no exemption (§7.4).
3. **`IPostableLine<THeader>`:** Added, Modified or Deleted fails if the header is posted.
   - **(B2)** Except that a **Modified** line of a posted header passes if only its `[AllowedAfterPosting]` properties (and the audit fields) changed. Added and Deleted lines still fail. The header touch and its version check below still apply.
   - **(B3)** The unit-of-work exemption in rule 2 covers the lines of an exempt header too. Until that unit of work commits, they may be **Modified** (for example, a posting handler records the unit cost on a sales line), but not Added or Deleted. Without this, rule 3 would judge the lines by the header's original `PostedAt`, which is no longer null after the first save.
   - The header is found through the line's navigation (EF metadata).
   - If the header is tracked, its original `PostedAt` is used. Otherwise one query reads `PostedAt` for each distinct header key in the save.
   - **A line change also touches the header.** A line can be added while another user posts the same document: the poster locks the header and reads the lines, the line commits afterwards, and a posted document ends up holding an unposted line. To close this gap, the rule **requires** the header to be `IVersioned` (enforced by the guard), and marks the tracked header Modified (`LastModifiedAt`). The line save then carries the header's version check, which conflicts with the post (§7.3). An untracked header fails: "*load the header with its version to edit its lines*".

**Startup guard (`DataModelGuard`) fails when:**
- an `IAppendOnly`, `IPostable` or `IPostableLine<>` type also implements `ISoftDelete`;
- an `IPostable` is not `IVersioned`;
- `[AllowedAfterPosting]` is on a type that is neither `IPostable` nor **(B2)** `IPostableLine<>`, on `PostedAt`, or on a key;
- a type is both `IAppendOnly` and `IPostable`;
- an `IPostableLine<THeader>` has no navigation to `THeader`.

**Bulk operations:**
- **The interceptor.** `GuardedTableCommandInterceptor` is a `DbCommandInterceptor` for `NonQueryExecuting(Async)` and `ReaderExecuting(Async)`. It reads `CommandEventData.CommandSource`; EF 10.0.11 defines `ExecuteUpdate`, `ExecuteDelete`, `BulkUpdate` and `ExecuteSqlRaw`, checked in the package's XML docs.
  - For those sources, if the SQL contains `UPDATE`, `DELETE FROM` or `TRUNCATE` against a table mapped to an `IAppendOnly`, `IPostable`, `IPostableLine<>` **or branch-scoped** type (§3.4), it throws `InvalidOperationException` naming the table. Table names come from `eventData.Context.Model`, cached per model.
  - `SaveChanges` and `Migrations` commands are not touched.
- **For posted documents, the refusal covers the whole table.** A bulk statement cannot be checked row by row against `PostedAt`, so all bulk writes to these types are refused. Neither the brief nor any template need calls for them.
- **Limits.**
  - The `ExecuteSqlRaw` check matches text, so it is a safety net, not a proof.
  - SQL sent on the raw connection (`Database.GetDbConnection()`) or from outside the application is invisible to it.
  - Only the database can stop those, which is what the triggers are for.
- **Triggers (defence in depth), evaluated:**
  - **Append-only tables:** a `BEFORE UPDATE OR DELETE` row trigger and a `BEFORE TRUNCATE` statement trigger that `RAISE EXCEPTION`. This is simple, needs no knowledge of the columns, and stops every path, including psql and a raw connection. Cost: one migration helper (`migrationBuilder.CreateAppendOnlyTrigger(schema, table)`) the application calls for each ledger table. The guard queries `pg_trigger` at startup and fails if an `IAppendOnly` table has no trigger, so a forgotten trigger fails loudly. A deliberate correction then needs a superuser to disable the trigger, which is the point.
  - **Posted documents:** a `BEFORE UPDATE OR DELETE` trigger `WHEN (OLD."PostedAt" IS NOT NULL)` that compares every column not marked `[AllowedAfterPosting]`. The column list comes from the model, so the trigger must be **regenerated whenever the entity changes**. A line trigger would need a lookup of the header. This is more machinery and more ways to drift.
  - **Recommendation:** triggers for append-only tables in this pass. Posted documents are protected by the save rules and the bulk interceptor, and triggers for them are revisited once a real module exists. The owner decides (§8.6).

**Users: deactivate, never delete.**
- **UI:**
  - Remove both delete paths (`Users.razor:589, 642`), the toolbar button (`:72-77`) and the menu item (`:140-143`).
  - The toolbar gains "Deactivate selected". It reuses `DeactivateUserAsync` and the last-administrator guard, and requires `Permissions.Users.Deactivation`.
- **Permission:**
  - Retire `Permissions.Users.Delete`: remove it from `Permissions/Users.cs`, the access-rights class, `AdministratorPermissionRegistry.Granted` and the role seed.
  - A one-off data step in the migration deletes existing `Permissions.Users.Delete` role and user claims, so no dead permission stays assigned.
- **Save rule (the loud failure):** `DataRules` refuses any **Deleted** `ApplicationUser` entry: "*Users are deactivated, never deleted (pass 59).*" Identity's `UserStore` saves through the context, so a future `UserManager.DeleteAsync` anywhere fails at once.
- **Database:**
  - `AuditTrail.Owner` changes from `SetNull` to `Restrict`, so no path, raw SQL included, can null the reference.
  - The other user foreign keys keep their current behaviour; there are no more user deletes for the cascades to act on.
  - This needs a migration for all three providers (the template builds all three).
- **Not in scope, recorded:** whether deactivation ends a user's live circuits (security stamp). Pass 59 does not change deactivation itself.

### 3.4 Part D: branch model

**Model (Domain):**
- `IMustHaveBranch { int BranchId { get; } }`.
- `IBranchVisibility<TSelf> where TSelf : IBranchVisibility<TSelf>`, for entities that involve several branches:
  - `static abstract Expression<Func<TSelf, int[], bool>> VisibleTo { get; }`, for example `(t, ids) => ids.Contains(t.FromBranchId) || ids.Contains(t.ToBranchId)`;
  - `IEnumerable<int> InvolvedBranchIds { get; }`, used by the write check.
- **The template ships Branch and UserBranch (I recommend this, §8.7):**
  - `Branch : BaseVersionedEntity, IAuditable`, with `Code` (unique), `Name` and `IsActive`. It is not sealed, so applications can add fields.
  - `UserBranch`, with `UserId`, `BranchId` and `IsDefault`. Its key is (`UserId`, `BranchId`), with a filtered unique index for one default per user. Both foreign keys use `Restrict`: a user is never deleted, and a branch is deactivated, not deleted.
    - **(P59)** There is no filtered unique index. A boolean filter has no spelling all three providers accept: SQL Server has no `TRUE` literal, and PostgreSQL will not compare a boolean with `1`. The template still builds all three migrations. `SetUserBranchesCommand`, the only writer, keeps "one default".
  - **(P59)** Both tables are template tables, so they live in the default schema (`Branches`, `UserBranches`), like `ProcessedRequests`. `GxTableNamingTests` lists them.
  - The keys are `int`. The brief specifies `int BranchId`, and it matches `DocumentSequences.BranchId`. A branch is master data, not a document header, so the Guid v7 decision does not apply.

**Filter (`ApplicationDbContext`):**
- **Two new context members,** built like `CurrentTenantId`: `BranchReadsAll` (bool) and `BranchReadIds` (`int[]`). Both come from `UserContext.Branches`. When that is null, they are `false` and an empty array: **no principal, no rows**, as with `IMustHaveTenant`.
- **`ApplyBranchFilters`, in the same loop shape as `ApplyTenantFilters`, registers `QueryFilters.Branch` on every hierarchy root:**
  - **`IMustHaveBranch`:** `BranchReadsAll || BranchReadIds.Contains(e.BranchId)`.
  - **`IBranchVisibility<T>`:** `BranchReadsAll || T.VisibleTo(e, BranchReadIds)`. The parameter is replaced with the context member through `ReplacingExpressionVisitor`, as `ModelBuilderExtensions` already does.
- **Named,** so the filter composes with `QueryFilters.Tenant` and `QueryFilters.SoftDelete` on the same entity.
- **(B5) Reading across branches: one sanctioned helper.** `IBranchQueryScope.AcrossBranchesAsync(query, permission, ct)` removes the branch filter from one query, and only after checking, fresh, that the caller holds the **permission it names**. It throws `ForbiddenAccessException` otherwise. `Permissions.Branches.ViewAll` is one such permission. A module may define a narrower one for a single query, for example letting counter staff see a part's stock in other branches without reading those branches' documents.
  - It removes **only** `QueryFilters.Branch`. The tenant and soft-delete filters still apply.
  - It is the **only** sanctioned use of `IgnoreQueryFilters` for the branch filter, so uses can be found by searching for `AcrossBranchesAsync`, and an architecture test can cover them later.
  - Writes are unaffected: the branch grant (below) still decides every save.
- **Guard:**
  - `TenantFilterGuard` gains the branch check, or `DataModelGuard` carries it; the wording follows the tenant guard. Any `IMustHaveBranch` or `IBranchVisibility<>` entity without a declared `QueryFilters.Branch` filter on its root fails startup.
  - Startup also fails for a type implementing both, for a marker on a TPH leaf, and for `IMustHaveBranch` on `Branch` or `UserBranch`.

**User context:**
- **`BranchAccess`.** `UserContext` gains `BranchAccess? Branches`, a record holding:
  - `bool ReadsAll` (from `Permissions.Branches.ViewAll`);
  - `IReadOnlyList<int> ReadIds` (the assigned active and inactive branches, so history stays visible);
  - `IReadOnlyList<int> WriteIds` (the assigned **active** branches only);
  - `bool WritesAll` (system context, and **(B1)** holders of `Branches.WriteAll`);
  - `int? DefaultBranchId`.

  `null` means "not computed", as with `AllowedTenantIds`, and is treated as no access.
- **View-all never grants write.** `ReadsAll` affects only the filter. `WriteIds` is always the assignment list.
- **I recommend a flag for "reads all", not a list of every branch id** (§7.2). A list would leave out any branch created after the user signed in.
- **`UserContextLoader`** fills `Branches` with one extra query (`UserBranches` joined to `Branch`) and one permission check for `ViewAll`.
  - **(P59)** The two permissions are read from the user's and their roles' claims directly, which is exactly what the policies of the same names require. This avoids `IIdentityService`, which the loader's test containers do not register. The query uses the scope's own `ApplicationDbContext`, the one `UserManager` uses, never the unit of work's context.
  - **(P59)** Cached contexts carry the FusionCache tag `user-context`. `IUserContextInvalidator` (implemented by the loader) drops one user's entry or all of them. `SetUserBranchesCommand`, `AddEditBranchCommand` (on activation changes) and `PermissionAssignmentService` (user and role permission changes) call it.
  - **(P59)** If the hub filter's reload fails, it uses the connection's snapshot and logs a warning. A user who no longer exists gets no context.
- **`SystemContext`:** `Branches = BranchAccess.System` (ReadsAll and WritesAll). Background work reads and writes every branch, and its writes are still audited as `gx-system`.

**Write check:**
- **Request marker.** Application gains `IBranchCommand { int BranchId { get; } }`. Every command that creates or changes an `IMustHaveBranch` or `IBranchVisibility<>` entity implements it.
- **`BranchAccessBehaviour<TRequest,TResponse> where TRequest : IBranchCommand`** sits immediately after `AuthorizationBehaviour`. It is therefore outside `ValidationBehavior` and outside the unit-of-work retry, so it runs once per send. I recommend it over folding the check into `AuthorizationBehaviour`: that behaviour answers "may this user perform this request?", and the branch check answers "in this branch?". A refused branch also logs differently.
  - Mediator's generator places a constrained behaviour only on matching requests. T20's probe, `ImportPicklistSetsCommand`, is not a branch command, so its asserted order is unchanged.
- **What the behaviour does:**
  1. It refuses `BranchId <= 0`.
  2. It reads the write set **fresh from the database**: is (`UserId`, `BranchId`) in `UserBranches` joined to an active `Branch`? This is one indexed query. A removed assignment therefore takes effect for writes at once, even though the user's `UserContext` snapshot is stale (§7.1). System context skips the query.
     - **(B1) Write-all permission.** If there is no assignment, the behaviour checks, also fresh on each send, whether the user holds `Permissions.Branches.WriteAll`. If they do, the write is allowed for an **active** branch only. Head-office roles that approve, reverse and pay for records of any branch need this. Assigning them to every branch by hand breaks whenever a branch is added.
     - `ViewAll` never implies `WriteAll`. `WriteAll` is **not** in `AdministratorPermissionRegistry.Granted`: it is excluded, so the bootstrap administrator never holds it.
     - `BranchAccess.WritesAll` is true for such a user, so the UI can offer every active branch in the switcher.
     - The refusal message and logging are unchanged for everyone else.
  3. If allowed, it installs a **branch grant** for the rest of the call chain: an `AsyncLocal` holder that is emptied at the end, the same pattern as pass 58's A6.
  4. It throws `ForbiddenAccessException`: "*You are not assigned to branch {Code}.*" This is the same exception type `AuthorizationBehaviour` uses, so the UI treats both alike.
- **Save rule (`DataRules`, the loud failure for an omission):**
  - Every Added, Modified or Deleted `IMustHaveBranch` entry needs an active grant whose branches include `entity.BranchId`.
  - For an `IBranchVisibility<>` entry, the grant must include one of its `InvolvedBranchIds`.
  - **No grant fails:** "*{Entity} is branch-scoped and was saved outside a branch command. Implement IBranchCommand on {RequestType}.*"
  - **A different branch fails,** naming both branches.
  - **Changing `BranchId` on a Modified entry always fails.** A row is not moved between branches; a transfer is a document.
  - System context grants all.
- **Writes derived from a posting.** A transfer posted by the sending branch writes stock rows for the receiving branch, in pass 58's posting-event handlers in the same unit of work. The rule: when an `IBranchVisibility<>` entity whose involved branches include a granted branch is saved, the grant is **extended** with its other involved branches for the rest of that unit of work. A handler can therefore write rows for a branch only through a document that names it (§8.9).
  - **(P59) The extension is per branch, not per row.** Once the transfer is saved, any row of the receiving branch may be written for the rest of that command. A row cannot say which document it derives from. Test 24 asserts the boundary that does hold: before the transfer is saved the receiving branch is refused, and a third branch is refused throughout.
- **Nested requests (pass 58, A5):** an inner `IBranchCommand` runs its own check and **pushes** its grant, which is popped when the inner request ends.

**Active branch (UI):**
- **Held** in a circuit-scoped `ActiveBranchState`, like `UserProfileState`. It is a **default for forms, not a security boundary**: a form puts `ActiveBranchState.BranchId` on the command, and the server validates it. Lists are filtered by the read set. Whether a screen also narrows to the active branch by default is the module's choice (§8.8).
- **Reconnect:** a brief drop keeps the circuit, so the state survives. A lost circuit (restart or retention expiry) starts a new one, and the state is restored from `ProtectedSessionStorage`, which is tab-scoped and encrypted. It is used only if the branch is still in the fresh write set.
- **(P59)** The stored entry is keyed by user, so another user in the same tab never inherits it. Both sign-out paths remove it: the sign-out form (`onsubmit`) and the idle sign-out (`gxIdleTimeout.js`).
- **New sign-in:** session storage is cleared at sign-out. The state starts at `DefaultBranchId`, else the first assigned active branch, else none. With none, branch-scoped forms show "You are not assigned to a branch" and branch commands cannot be sent.
- **Switcher:** a `BranchSelector` in the top bar, starting from the stashed `TenantSelector` change (it re-reads its list when the menu opens, and reloads when the profile arrives).
  - It lists the fresh write set. Switching changes the circuit state only.
  - No full page load is needed, because unlike the tenant there is no SignalR presence group keyed on the branch (`TenantSelector.razor:185-193`).
  - The tenant selector stays hidden, since tenancy is dormant.
- **An assignment is removed while the user is signed in:**
  - **Writes:** refused at once by the fresh check in the behaviour.
  - **Active branch:** the next branch command is refused. The selector re-reads its list when opened, and if the active branch is gone it moves to the default branch and says so.
  - **Reads:** see the refresh rule below.

**Refreshing the read set when assignments change:**
- **The problem.** `IdentityService.AuthorizeAsync` is fresh, but `UserContext` is not. It is a snapshot taken when the connection opens and cached for an hour (§1, §7.1).
- **The change:**
  1. Every write to `UserBranches`, and every role-permission change affecting `Branches.ViewAll`, clears the user context. For one user that is `ClearUserContextCache(userId)`; for a role, every member's entry. Context cache entries are tagged `user-context`, and FusionCache 2.x's tag removal is used for role changes.
  2. `UserContextHubFilter.InvokeMethodAsync` re-reads `IUserContextLoader.LoadAsync` on **each invocation** rather than pushing the `Items` snapshot. The result is cached in memory, so the cost is a cache hit, and `Items` is updated with it. `OnConnectedAsync` is unchanged.
- **Result:** a removed assignment disappears from reads at the user's next UI interaction. Hub-isolation behaviour (`ServerHubTenantIsolationTests`) is unaffected, because group membership is still taken in `OnConnectedAsync`.

**Composition with pass 58:**
- **`ForUpdate()`:** the branch filter adds a `WHERE` predicate. `ids.Contains(x)` translates to `= ANY(@p)` or an expanded `IN (@p0, …)`, and neither is a `JOIN`, so `ForUpdateCommandInterceptor`'s join refusal is not triggered. A custom `VisibleTo` is limited by the guard to the entity's own columns. The guard inspects the expression and refuses navigations, because a navigation would bring a `JOIN` and break `ForUpdate`. Test 25 covers this.
- **Unit of work:** the grant is installed outside `TransactionBehaviour`, so it spans every retry attempt and every posting-event round. The filter reads `UserContext`, which the unit of work does not change. The shared context therefore sees the same read set throughout.
- **Idempotency:** a replayed `RequestId` returns its stored response after the branch check has passed again. A user who lost the branch cannot replay into it.
- **Numbering:** `IDocumentNumberAllocator.NextAsync(type, branchId, …)` takes the command's `BranchId`, which the behaviour has validated.

**Branch management UI (if shipped):**
- A Branches page (list, add, edit and deactivate, with `Permissions.Branches.View/Create/Edit`) and a "Branches" section on `UserFormDialog`: checkboxes for the assigned branches, and a default. **(P59)** The section saves on its own ("Save branch assignments") and appears only for an existing user: a new user has no id to assign until the dialog has saved it.
- Saving goes through a `SetUserBranchesCommand`, which is transactional and clears the user context cache.
- The administrator is granted `Branches.*` including `ViewAll`, but is **not auto-assigned** to any branch, so view-all-without-write is what an administrator has by default (§8.10). **(B1)** The administrator is never granted `Branches.WriteAll`.

## 4. Order of work

0. **Start.**
   - Branch from `main` at `18e300fe`.
   - Bring `stash@{0}` back only in step 7, as the starting point for `BranchSelector`. The stash stays in the stash until then.
1. **Scaffolding.**
   - The `SaveChanges` override with an empty `DataRules`, and an empty `DataModelGuard` wired into `InitialiseAsync`.
   - The empty `GuardedTableCommandInterceptor`, registered.
   - Prove the pinned suites unchanged.
2. **Part A:**
   - attributes, convention, guard, the scale check at save, `GxRound`;
   - the float/double rule as decided (§8.2).
3. **Part B:**
   - `IVersioned`, the base class, the PostgreSQL mapping, `Entry` on `IApplicationDbContext`, the helpers and the save check, `VersionedEditValidator`;
   - `PicklistSet` as the example, with migrations; check the PostgreSQL migration has no `xmin` column;
   - the dialog's Reload.
4. **Part C:**
   - the save rules, the unit-of-work exemption, the line rule with the header touch;
   - the guard;
   - the bulk interceptor;
   - triggers if chosen (§8.6);
   - users: UI, permission retirement with its claim clean-up, the save rule, the `AuditTrail` foreign key, migrations.
5. **Part D, model and reads:**
   - `IMustHaveBranch`, `IBranchVisibility<>`, `Branch`, `UserBranch` (§8.7), `BranchAccess`;
   - the loader, the system context, the filters, the guard;
   - the refresh rule (hub filter and cache tags).
6. **Part D, writes:** `IBranchCommand`, `BranchAccessBehaviour`, the grant holder, the save rule, grant extension (§8.9), nested pushes.
7. **Part D, UI:** `ActiveBranchState`, `BranchSelector` (from the stash), the Branches page, the user-dialog section.
8. **Documentation:** README sections "Money and quantities", "Concurrency", "Posted records" and "Branches", with the rules in §3. Update the review's change list (items 1, 4, 6 and 7 done).
9. **Release 1.6.0.** Release notes; set `AppConfigurationSettings.Version` to 1.6.0. Commit, **then** pack (pass 58's stamp rule), install, run `tooling/smoke-generate.ps1 -UseInstalled`, and tag `gx-v1.6.0`.

**Effort:**

| Part | Work | Effort |
|---|---|---|
| Scaffolding (step 1) | override, guard and interceptor shells | 0.5 day |
| A: precision | attributes, convention, guard, scale check, helper | S, 1 day |
| B: concurrency | token, helpers, save check, example, dialog Reload | M, 1.5 days |
| C: immutability | rules, line touch, guard, bulk interceptor, users, migrations | M, 2 days, **+1 day** with append-only triggers |
| D: branches | model, filter, guard, context, refresh, behaviour, grant, UI | L, 4 days (UI about 1.5 of it) |
| Docs and release | README, notes, pack | 1 day |
| **Total** | | **About 10 days (11 with triggers).** Steps 1 to 4 (about 5 days) are the part to protect if time runs short; Part D's UI is the easiest to split into a follow-up. |

## 5. Tests (Application.IntegrationTests on `GX_TEST_PG` unless noted)

- **Test entities.** The template ships no ledger, no posted document and no transfer, so the tests use test-only entities: `TestLedgerRow`, `TestPostedDocument`/`TestPostedLine`, `TestBranchRow` and `TestTransfer`.
- **(P59) Where they run.** `TestModelDbContext : ApplicationDbContext` adds them before the base model is built, so the conventions apply. `ModelHarness` gives it its own database (`…_appint_model`, schema rebuilt with `EnsureCreated` and the trigger SQL) and its own service provider, with the factory swapped. The rows that use `PicklistSet` run on the ordinary harness.
- **(P59) The test reset.** `PostgresTestDatabase` used to empty a trigger-guarded table with `TRUNCATE`; the append-only `TRUNCATE` trigger refuses that too, so the reset now disables the table's user triggers around a `DELETE`. `SharedDatabaseTests` pins the new form.
- **Test context.** They live in a `TestModelDbContext : ApplicationDbContext`, in its own database. The integration harness registers the factory to build it (as `Testing.cs` registers the test posting handlers).
- **Real entities.** `PicklistSet` and `Branch` are tested as themselves.

| # | Test |
|---|---|
| 1 | A `decimal` property with no declared kind fails startup, naming the entity and property (unit, `DataModelGuard` on a built model). |
| 2 | Each kind maps to its precision and scale in the model. A configuration that overrides one fails the guard (unit). |
| 3 | A `double` property fails the guard unless it is `[Approximate]`, per decision §8.2 (unit). |
| 4 | Saving `[Money] 1.005m` is refused, naming the property. `GxRound.Money(1.005m)` (1.01) saves (integration). |
| 5 | `GxRound` follows the decided midpoint rule for positive and negative values, at each scale (unit). |
| 6 | A fresh edit with the loaded `Version` succeeds, and `Version` changes. |
| 7 | **A stale edit is refused:** user A loads, user B saves, then A saves with the old version. A receives exactly `ConcurrencyConflictMessage`, the row keeps B's values, and an Error is logged. |
| 8 | An edit that omits the version fails loudly: the validator refuses `null`, and a handler that never calls `ApplyLoadedVersion` throws at save, naming the entity. Mapster mapping from the command leaves `Version` untouched. |
| 9 | The PostgreSQL model maps `Version` to `xmin`, and the PostgreSQL migration snapshot has no `xmin` column (unit). |
| 10 | `ForUpdate()` does not change `xmin`. B's versioned edit waits on A's lock, then gets the conflict message once A commits. |
| 11 | Inside a unit of work, a concurrency conflict is not retried (one attempt, the conflict message). A deadlock is still retried (the T11 path from pass 58). This pins the Npgsql 10 behaviour (§7.7). |
| 12 | Modifying or deleting an `IAppendOnly` row fails. Adding one succeeds. |
| 13 | Modifying a posted document fails. Changing only an `[AllowedAfterPosting]` property (Status to Reversed, plus the reversal link) succeeds, and audit fields may change alongside it. Deleting a posted document fails. Un-posting (`PostedAt` to null) fails. |
| 14 | **Adding a line to a posted document fails**, and so does editing or deleting one. Adding a line to a draft succeeds and changes the header's `Version`. |
| 15 | **A line is added while another user posts the same document.** Exactly one wins: the line save gets the conflict message, or the post sees the line. A posted document never holds a line it did not post. |
| 16 | A header posted in a unit of work can be saved again by a posting-event handler in that unit of work. After commit it cannot. |
| 17 | `ExecuteUpdate`, `ExecuteDelete` and `ExecuteSqlRaw("UPDATE …")` against a guarded table throw, naming the table. The same calls against an unguarded table work. |
| 18 | Startup fails for an append-only or postable type that also implements `ISoftDelete`, and for an `IPostable` that is not `IVersioned` (unit). |
| 19 | (If triggers are chosen.) A raw-connection `UPDATE`, `DELETE` or `TRUNCATE` on an append-only table is refused by PostgreSQL. Startup fails if a trigger is missing. |
| 20 | **Deleting a user deactivates instead:** the Users page offers no delete, and "Deactivate selected" deactivates. `UserManager.DeleteAsync` throws the pass 59 message. The user's `AuditTrail` rows keep `UserId`. A raw `DELETE` of that user fails on the foreign key (Server.UI and integration). |
| 21 | A user reads only their read set (branches 1 and 2 of 3) across `IMustHaveBranch` queries, `Find`, counts and projections. With no principal, they read nothing. |
| 22 | **A view-all user reads all three branches, but a command for an unassigned branch is refused** with `ForbiddenAccessException`, and nothing is written. |
| 23 | A branch-scoped entity saved by a command without `IBranchCommand` fails, naming the request. A command whose entity's `BranchId` differs from the command's fails. Changing `BranchId` on an existing row fails. |
| 24 | **A two-branch entity (From 1, To 2) is visible to users of branch 1 and of branch 2, and not to a user of branch 3.** A posting by branch 1 may write branch-2 rows derived from it (the grant extension). **(P59)** Before the transfer is saved, a branch-2 row is refused; a branch-3 row is refused throughout (see §3.4: the extension is per branch). |
| 25 | **An entity marked `IMustHaveBranch` with no branch filter fails startup** (filter removed in a test model). A TPH leaf marker and a `VisibleTo` with a navigation also fail (unit). |
| 26 | `ForUpdate()` on a branch-filtered entity emits no `JOIN` and locks. A transactional branch command retried after a deadlock re-checks nothing it should not, keeps its grant, and succeeds. |
| 27 | Removing an assignment refuses that user's next write at once. Their next hub invocation no longer reads the branch (`UserContextHubFilter` refresh). Changing a role's `ViewAll` takes effect on members' next invocation. |
| 28 | Background work in `ISystemContext` reads and writes every branch. |
| 29 | The active branch: it starts at the default branch, survives a reconnect, is restored on a new circuit only if it is still writable, and falls back to the default when its assignment is removed (Server.UI, bUnit). |
| 30 | Unchanged: the interceptor regression, ordering, transactional audit and publisher suites, and pass 58's transaction tests (T20's behaviour order included). |
| 31 | **(B1)** A user with `WriteAll` and no assignment writes to any active branch and to no inactive one. A user with `ViewAll` only is still refused (test 22 unchanged). Removing `WriteAll` from the user's role refuses their next write. |
| 32 | **(B2)** On a posted document, changing an `[AllowedAfterPosting]` line property succeeds, and changing any other line property fails. |
| 33 | **(B3)** Extends test 16: a posting-event handler modifies a line of the header posted in that unit of work, and it saves. After commit the same change fails. |
| 34 | **(B4)** Two consecutive saves from one form instance, each using the version returned by the previous save, both succeed. A third save using the original version is refused. The same holds inside a unit of work, where the returned version is the one committed after the behaviour's final save. |
| 35 | **(B5)** A user holding the named permission gets rows from all branches through `AcrossBranchesAsync`. A user without it is refused. The same user's ordinary queries on the same entity still return only the read set. |

## 6. Effort summary

About **10 days**, or 11 with append-only triggers (table in §4). The size comes mostly from Part D. A, B and C are about 5 days and are the foundation every module needs first.

## 7. Conflicts with the brief or with pass 58, and what I recommend

1. **"IdentityService rebuilds the principal on each call" does not cover the read set.**
   - Only authorization is rebuilt (`IdentityService.cs:405-417`).
   - `UserContext`, where the brief puts the read set, is loaded once per connection into `HubCallerContext.Items` (`UserContextHubFilter.cs:87-101`) and cached for 1 h (`UserContextLoader.cs:43`).
   - A removed assignment would stay readable until the circuit reconnected.
   - **Recommend:** check the write set fresh in the behaviour, refresh the hub filter's context on each invocation, and clear the cache on assignment and role changes (§3.4).
2. **"All of them for a holder of view-all" as a list of ids would go stale.** A branch created after sign-in would be missing.
   - **Recommend:** a `ReadsAll` flag next to `ReadIds`. The filter is `ReadsAll || ReadIds.Contains(...)`.
3. **The brief's posted-line rule leaves a race.** Checking "header is posted" when a line is saved does not stop a line committing just after a concurrent post read the lines.
   - **Recommend:** postable headers must be `IVersioned`, and a line change touches the header, so the post and the line edit conflict on the header's version (§3.3, test 15).
4. **"After posting, modification fails" conflicts with pass 58's event loop.** A posting-event handler may save the header again in the same transaction (§2 step 5 of pass 58).
   - **Recommend:** exempt headers posted in the current unit of work until commit (§3.3, test 16).
5. **A third save interceptor would break a pinned suite.** `InterceptorOrderingTests` asserts exactly two (`:63`).
   - **Recommend:** the `SaveChanges` override (§3.0), and the command interceptor added with `AddInterceptors` like `ForUpdateCommandInterceptor`.
6. **"An edit command that omits the version fails loudly" cannot be checked at startup.** Which entity a handler edits is known only at run time.
   - **Recommend:** a validator for `null`, plus a save-time check that every Modified or Deleted versioned entry went through `ApplyLoadedVersion` or `UseCurrentVersion` (§3.2). It fails on the first exercise of the path, so in its first test.
7. **Npgsql 11 changes the concurrency exception.** From 11.0, `40001`/`40P01` in `SaveChanges` become `DbUpdateConcurrencyException`, per the provider documentation. Pass 58's retry would still work, but an exhausted deadlock retry would tell the user "changed by someone else".
   - **Recommend:** stay on 10.x for 1.6.0. Test 11 makes the upgrade fail loudly.
8. **The brief's posting-to-two-branches case needs a write rule the brief does not state.** "A check before the handler refuses a BranchId outside the write set" alone would refuse a sender's posting handler writing the receiving branch's rows.
   - **Recommend:** grant extension through a document's `InvolvedBranchIds` (§3.4, §8.9).
9. **"A decimal property on a business entity."** `IBusinessEntity` excludes `Tenant`, `AuditTrail`, `ProcessedRequest` and `DocumentSequence`.
   - **Recommend:** check the whole `ApplicationDbContext` model, which is stricter and costs nothing today.
10. **Deactivation is the replacement, but deleting a user today also fails on `Document.CreatedBy`** (`Restrict`) **with an unhandled foreign-key error.** This is a further reason to remove deletion rather than repair it. No conflict with the brief; recorded so the change is not mistaken for a regression.

## 8. Decisions for the owner

All decided by the owner on 2026-11-11.

1. **Rounding: decided.** `MidpointRounding.AwayFromZero` at the kind's scale. Round once, when a stored value is computed. Totals are sums of stored values. A value with excess scale is **refused** at save, not rounded.
2. **`float`/`double` on entities: decided.** They fail the guard, with the `[Approximate]` opt-out.
3. **Kinds: decided.** Attributes on `decimal`: `[Money]`, `[Quantity]`, `[UnitCost]` and `[Rate]`. `[UnitCost]` stays separate from `[Quantity]`.
4. **Concurrency recovery: decided.** Reload and discard.
5. **Versioned template entities: decided.** `PicklistSet` and `Branch`. `SecurityPolicy` is not versioned.
6. **Triggers: decided.** Yes for append-only tables (row `UPDATE` and `DELETE`, and `TRUNCATE`), with the migration helper and the startup check. Not for posted documents.
7. **Branch UI: decided.** Ship `Branch`, `UserBranch`, the Branches page, the user-dialog section and the top-bar switcher.
8. **Lists and the active branch: decided.** The query filter is the read set. The active branch is a form default only. Whether a list narrows to the active branch is each module's choice.
9. **Derived writes: decided.** Grant extension through a two-branch document, as recommended, plus the write-all permission in amendment **(B1)**.
10. **The administrator: decided.** Gets `Branches.*` including `ViewAll`, and no automatic assignment. The administrator is never granted `Branches.WriteAll` **(B1)**.
11. **Inactive branches: decided.** An inactive branch stays in the read set and leaves the write set.
12. **`Permissions.Users.Delete`: decided.** Retired, and its existing claims are deleted in the migration.
13. **Version: decided.** Ships as 1.6.0. If time runs short, 1.6.0 may ship parts A to C with part D's model, reads and writes, and the branch UI (step 7) may follow as 1.6.1. The pass report says which was done.
