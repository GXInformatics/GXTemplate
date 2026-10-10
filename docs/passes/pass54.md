# Pass 54 — Tenancy plumbing and system context

**Origin:** InventoryMS `docs/passes/ims-p0-template-audit.md` §1 (tenancy), §2.3 (request
authorization) and §5 (seeding). Fixed in the template so every GX product inherits the fixes.

**Name:** the pass plan in `GXTemplate-passes/pass46.md` R4 runs from 47 to 53. Passes 49, 50, 51 and
53 are planned and not yet run, and 52 ran narrowed. The next free number is therefore **54**. This
pass does not take over any planned pass's scope. It changes one thing pass 49 owns, F1, and only at
the filter layer (§3.2).

**Outcome:**

- All eight items are done.
- The build has 0 errors. Its warning set is identical to the baseline (§7.1).
- All four suites are green on local PostgreSQL 18 (port 5434).
- Mutations 2, 4 and 6 fail against unfixed code, and each mutated file was restored byte for byte
  (§5).
- `dotnet pack` and `tooling/smoke-generate.ps1` both pass (§7.3).

---

## 1. What each item delivered

| # | Item | Delivered |
|---|---|---|
| 1 | Tenant filter by interface | `ApplicationDbContext.ApplyTenantFilters` walks the model. It registers the named `QueryFilters.Tenant` filter on every hierarchy root that implements `IMustHaveTenant` (`TenantId == current`) or `IMayHaveTenant` (`TenantId == null \|\| TenantId == current`). The `PicklistSet` registration that used to be written out by hand is gone, because the walk now produces it. **`AuditTrail` keeps one explicit registration (see §3.1).** `IgnoreQueryFilters([QueryFilters.Tenant])` and its permission-gated users are unchanged. |
| 2 | Startup assertion | `TenantFilterGuard.AssertEveryTenantEntityIsFiltered` runs first in `ApplicationDbContextInitializer.InitialiseAsync`, before migrations. Startup fails, naming each offender, when an entity that is marked (or is `AuditTrail`) has no tenant filter on its hierarchy root. A marker on a TPH leaf gets its own message, which tells you to move the marker to the root. |
| 3 | Stamping by marker | `AuditableEntityInterceptor` now stamps tenants in a separate pass over **every** added entry. Before, it only stamped inside the `IAuditableEntity` loop. An explicit `TenantId` is never overwritten. New: an `IMustHaveTenant` row with no tenant and no ambient tenant is refused by the interceptor, which names the type (§3.4). |
| 4 | Control test | `TenantFilterTests`: a user in tenant A cannot read a tenant-B `Document` or `TenantProbe` (a plain `BaseEntity, IMustHaveTenant`) through the filtered `DbSet`, either by listing or by key. Tenant B's own rows are the positive control. |
| 5 | `ITenantSeeder` hook | `ITenantSeeder.SeedAsync(string tenantId, CancellationToken)` and `ITenantSeedRunner`. The runner is invoked by `CreateTenantCommand` for the new tenant and by `ProvisionAsync` for every tenant. It also runs for the Development sample tenant when that is created. It runs every registered seeder with the ambient principal hidden (`Push(null)`). One test seeder (`RecordingTenantSeeder`) proves both invocation points. |
| 6 | System context | `ISystemContext.RunAsync(tenantId, work)` runs the work as the provisioned `gx-system` account inside one tenant. Inside it, a request passes `AuthorizationBehaviour`; the same request outside it is denied. |
| 7 | Unique `TenantUsers (TenantId, UserId)` | Configured, plus a `TenantUserUniqueMembership` migration for all three provider chains. Each migration deletes existing duplicates first. |
| 8 | Split tenant command | `CreateTenantCommand` (`Tenants.Create`, refuses an existing id) and `UpdateTenantCommand` (`Tenants.Edit`, refuses an unknown id). The Create id defaults to `Guid.CreateVersion7()`, matching `Tenant` and `TenantDto`. The old command defaulted to `Guid.NewGuid()`. |

---

## 2. File-by-file changes

### Application

| File | Change |
|---|---|
| `Common/Interfaces/Identity/IUserContextAccessor.cs` | `Push(UserContext?)`. A pushed `null` means "no principal for this scope" and hides any outer context. |
| `Common/Interfaces/Identity/ISystemContext.cs` | **New.** `RunAsync(tenantId, work)` plus a generic overload. The docs explain why it takes a callback rather than returning a disposable scope (§3.5). |
| `Common/Interfaces/MultiTenant/ITenantSeeder.cs` | **New.** `ITenantSeeder` and `ITenantSeedRunner`, with the contract: idempotent per item, grant-only, no ambient principal, explicit `TenantId`, and reads that lift the filter and state the tenant. |
| `Common/Constants/Users.cs` | Adds `Users.System = "gx-system"`. The class summary now says "one account a person can sign in as". |
| `Features/Tenants/Commands/AddEdit/*` | **Deleted** (`AddEditTenantCommand` and its validator). |
| `Features/Tenants/Commands/TenantForm.cs` | **New.** `ITenantForm` (with `[Display]` names, which the generic dialog reads) and `TenantFormValidator<T>`. |
| `Features/Tenants/Commands/Create/CreateTenantCommand.cs` | **New.** Command, validator and handler. The handler refuses an existing id, inserts, then runs the tenant seeders. |
| `Features/Tenants/Commands/Update/UpdateTenantCommand.cs` | **New.** Command, validator and handler. The handler refuses an unknown id. |

### Infrastructure

| File | Change |
|---|---|
| `Persistence/ApplicationDbContext.cs` | `ApplyTenantFilters` (the marker walk, building the expression against `this`), `TenantIdOf`, and the explicit `AuditTrail` registration. The comment block is rewritten to match. |
| `Persistence/TenantFilterGuard.cs` | **New.** `RequiresTenantFilter`, `FindUnfiltered` and `AssertEveryTenantEntityIsFiltered`. |
| `Persistence/Interceptors/AuditableEntityInterceptor.cs` | `StampTenant` runs over all added entries. `SetCreationAuditInfo` now handles audit fields only. Adds the fail-closed refusal for an `IMustHaveTenant` row with no tenant. |
| `Persistence/ApplicationDbContextInitializer.cs` | Takes `ITenantSeedRunner`. `InitialiseAsync` runs the guard first. `ProvisionAsync` adds `EnsureSystemAccountAsync` and `SeedEveryTenantAsync`. `SeedSampleTenantAsync` seeds the tenant it creates. |
| `Persistence/Configurations/TenantUserConfiguration.cs` | Unique `(TenantId, UserId)` index. It replaces the foreign-key index on `TenantId`. |
| `Services/MultiTenant/TenantSeedRunner.cs` | **New.** Hides the principal, runs the seeders in order, and logs which seeder failed for which tenant before rethrowing. |
| `Services/Identity/SystemContext.cs` | **New.** Resolves the account and checks the tenant exists before running the work. Throws `InvalidOperationException` on an empty or unknown tenant, or a missing account. Then pushes the context and awaits the work. |
| `Services/Identity/UserContextAccessor.cs` | `Push(UserContext?)`. |
| `DependencyInjection.cs` | Registers `ITenantSeedRunner` (scoped) and `ISystemContext` (singleton). |

### Migrators: `TenantUserUniqueMembership`, three chains

| Provider | Migration | Duplicate removal before `CreateIndex` |
|---|---|---|
| PostgreSQL | `20261010123810` | `DELETE … USING` self-join, keeping the lowest `Id` |
| SQL Server | `20261010123836` | `ROW_NUMBER()` CTE. EF also adds its own `IS NOT NULL` filter to the index. |
| SQLite | `20261010123839` | `DELETE … WHERE EXISTS` a lower `Id` |

Each snapshot changes only the index. The query filters do not appear in snapshots, as expected.
`ModelMatchesMigrationsTests` passes for every chain it checks.

### Server.UI

| File | Change |
|---|---|
| `Endpoints/FileEndpoints.cs` | `IsPermittedAsync` takes `IUserContextAccessor` and **pushes the loaded user context around the document query**. This is required: see §3.3. |
| `Pages/Tenants/TenantFormDialog.razor` | Becomes generic: `@typeparam TCommand : ITenantForm, IRequest<Result<string>>`. |
| `Pages/Tenants/Tenants.razor` | Create sends `CreateTenantCommand`. Edit builds an `UpdateTenantCommand` from the DTO. One generic `ShowTenantDialog<TCommand>`. |

### README

The Tenancy section changes in these places:

- "What is stamped" now says entities are filtered automatically, not only stamped.
- The null-tenant paragraph notes the exception for `ISystemContext`.
- The Documents row of the table describes the new filter.
- A new subsection, **"Tenant-owned entities, tenant seeders and background work"**.
- The "scoped by default" paragraph and the Known-limitations bullet now include documents and
  marked project entities.
- The seeding rule (item 4) now points at `ITenantSeeder`.

### Tests

**New files:**

| File | Tests | What it covers |
|---|---|---|
| `Application.UnitTests/MultiTenant/TenantProbe.cs` | — | Probe entity, and a probe context derived from `ApplicationDbContext`. The table lives in its own `gx_probe` schema, created per fixture and dropped afterwards, so the migrated schema is never touched. |
| `Application.UnitTests/MultiTenant/TenantFilterTests.cs` | 9 | Item 4's control tests, both predicates, the exemption, and which entities are and are not registered |
| `Application.UnitTests/MultiTenant/TenantFilterGuardTests.cs` | 5 | Item 2: the shipped model and a project entity pass; an unfiltered marked entity is refused by name; a marked TPH leaf is refused naming its root; **`InitialiseAsync` itself throws** on a bad model |
| `Application.UnitTests/MultiTenant/TenantStampingByMarkerTests.cs` | 4 | Item 3 |
| `Application.UnitTests/MultiTenant/TenantMembershipUniquenessTests.cs` | 4 | Item 7, including the delete-and-re-add-in-one-save path that `UserFormDialog` uses |
| `Application.IntegrationTests/MultiTenant/RecordingTenantSeeder.cs` | — | The one test seeder. Inert unless a test enables it. |
| `Application.IntegrationTests/MultiTenant/TenantSeederTests.cs` | 3 | Item 5: on create (with the harness user signed in), on provision for every tenant, per-item idempotence, and catching up a tenant created earlier |
| `Application.IntegrationTests/MultiTenant/SystemContextTests.cs` | 5 | Item 6 |
| `Application.IntegrationTests/MultiTenant/TenantCommandTests.cs` | 7 | Item 8, including an Edit-only user who cannot create and a Create-only user who cannot update |

**Changed files, and why:**

| File | Change |
|---|---|
| `Application.IntegrationTests/Testing.cs` | The Moq accessor ignored `Push`. It is replaced by `HarnessUserContextAccessor`: an explicit push, including a pushed null, takes precedence, and with nothing pushed the harness user applies as before. The test seeder is registered, and is reset in `ResetState`. |
| `Application.UnitTests/Persistence/ProvisioningTests.cs` | Registers the runner and accessor. **`UsersAsync()` now excludes `gx-system`**: the existing assertions are about people (exactly one administrator, no Demo account), and provisioning now also creates an account nobody can sign in as. Adds 5 system-account tests: created and unusable, holds the registry, idempotent, lockout restored, and refuses to adopt an account of that name that has a password. |
| `Application.UnitTests/Endpoints/FileEndpointsAuthorizationTests.cs` | The factory builds contexts over the real accessor, as the application's factory does, and passes it to `IsPermittedAsync`. **One test is rewritten** (§3.2). |
| `Application.UnitTests/Features/Documents/DocumentTenantIsolationTests.cs`, `…/Queries/GetFileStreamQueryHandlerTests.cs` | Handlers now get contexts built over the **same accessor they are given**, which is how production wires them. Verification reads ("is the other tenant's document still there?") go through an explicit `IgnoreQueryFilters([QueryFilters.Tenant])` helper. No assertion changed. |
| `Application.UnitTests/Security/RequestAuthorizationRegistryTests.cs` | `ExpectedRequestTypeCount` goes from 24 to 25, with the reason recorded. |
| 14 test doubles (`AuditTrailTenantFilterTests`, `PicklistSetTenantFilterTests`, `PicklistTenantUniquenessTests`, `SharedPicklistCreationTests`, `SharedPicklistWriteTests`, `InstallationPolicyWriteTests`, `PermissionAssignmentGuardTests`, `RoleDefinitionRightTests`, `AuthorizationBehaviourTests`, `CacheInvalidationScopeTests`, `CacheScopeBehaviourTests`, `DataSourceScopeTests`, `PicklistDataSourceScopeTests`, `RoleDefinitionComponentTests`) | Signature only: `Push(UserContext? context)`. |

---

## 3. Decisions and deviations — read these

### 3.1 `AuditTrail` keeps one explicit registration (deviation from item 1's "replacing the hand-listed registrations")

`AuditTrail` implements neither marker. The interceptor constructs audit rows with the tenant the
change was made in. Its rule is **strict equality on a nullable column**: a null tenant is an
installation-level event (seeding, bootstrap, background work). Neither marker fits:

- Marking it `IMayHaveTenant` would give it null-or-equal, which **shows every tenant the
  installation's events**.
- `IMustHaveTenant`'s non-null `TenantId` would forbid recording those events at all.

So it stays as the one explicit line, documented in place. `TenantFilterGuard` checks it alongside
the marked entities. If you would rather have no exception at all, the clean alternative is a third
marker for "strict, nullable", but that is a design change I did not want to make on your behalf.

### 3.2 `Document` is now filtered, which closes F1 at the filter layer (deliberate behaviour change)

`Document` is the template's own `IMayHaveTenant` entity, so item 1 puts it under the filter. The
old comment said it was left out because `VisibleDocumentSpecification` scopes it, and two rules
could disagree. They cannot disagree in the permissive direction, because a global filter only
narrows:

- **A principal with a tenant:** the filter and the specification together are exactly the
  specification's rule. Nothing changes.
- **A principal without a tenant:** the filter reduces to `TenantId IS NULL`. Such a principal now
  sees only tenantless documents, **not every tenant's public documents**. That is pass 46's F1,
  which pass 49 planned to fix.

The specification's no-tenant branch is untouched; pass 49 still owns it. Its planned mutation 5 now
has a second line of defence underneath it.

`FileEndpointsAuthorizationTests.AGenuinelyTenantlessPrincipal_KeepsTheSpecificationsDocumentedBehaviour`
asserted the old behaviour. It is **renamed and rewritten** as
`AGenuinelyTenantlessPrincipal_IsConfinedToTenantlessDocuments`:

- a tenantless public document is served (positive control);
- tenant-1's public document and the user's own private one are refused;
- another tenant's document is refused.

This is the only test whose expectation changed.

### 3.3 `/files` now pushes the context it loads (a required companion fix)

The endpoint resolved the user's tenant through `IUserContextLoader`, but queried through a
`DbContext` whose filter reads the **ambient** accessor, which is null on HTTP. With `Document`
filtered, every tenant's documents would have been refused at `/files`. The endpoint now pushes the
loaded context around the query. §5.4 shows this is load-bearing in the real HTTP pipeline.

### 3.4 Stamping now refuses an `IMustHaveTenant` row it cannot stamp (small addition)

You did not ask for this. Item 3 implied it: without it, such a row reaches the database and fails
as a NOT NULL violation that names a column. The interceptor now throws
`InvalidOperationException` naming the entity and both remedies (an explicit `TenantId`, or
`ISystemContext`). It affects only `IMustHaveTenant`, which no template entity implements.

### 3.5 System context

- **A real account.** `gx-system` holds the administrator registry's permissions as **user claims**,
  reconciled grant-only on every start. It is deliberately **not in the Admin role**:
  `EnsureAdministratorAsync` only provisions an administrator when nobody holds that role, so the
  person who has to sign in would never be created.
- **Nobody can sign in as it.** It has no password, an unconfirmed `gx-system@localhost` email,
  `IsActive = false`, and a lockout until `DateTimeOffset.MaxValue` that every start re-asserts.
- **It refuses to adopt a real account.** If an account named `gx-system` already has a password or
  an external login, provisioning throws rather than granting it everything.
- **A callback, not an `IDisposable`.** The context lives in an `AsyncLocal`, and a value set inside
  an `async BeginAsync()` is discarded when that method returns. Handing back a scope would hand back
  one that had already ended.
- **Writes are attributed.** Writes inside the scope are stamped with the tenant and attributed to
  the account, and a test asserts both.
- **No job is wired yet.** The template has no hosted service or Hangfire job to convert. The README
  shows the call shape.

### 3.6 Tenant seeding hides the principal by pushing null (an interface change)

The runner needs "nobody" even inside an administrator's circuit, so `IUserContextAccessor.Push`
now accepts `null`. A pushed null hides an outer context; it does not fall through to it. This is
why 14 test doubles changed signature and the integration harness accessor was rewritten.

Seeders that read must lift the filter and state the tenant. With no principal, the filter shows
only installation rows, so an unlifted existence check always says "absent" and the seeder would
duplicate its rows on every start. The interface documentation says this, and the test seeder does
it.

### 3.7 Tenant commands

- `CreateTenantCommand` keeps an `Id` with a version-7 default, so the dialog can show it before
  saving, as it did before. It refuses an id that already exists. The old command silently fell
  through to an update in that case.
- `UpdateTenantCommand` has no default id and refuses an unknown one.
- The old command's two `[RequestAuthorize]` attributes are OR'd, so an **Edit-only operator could
  create tenants**. `AnEditOnlyOperator_CannotCreateATenant` pins the fix.

---

## 4. One correction made during the pass

The first edit, to the `IUserContextAccessor` / `UserContextAccessor` signature, was run through a
script needing Python, which this machine does not have. It silently did nothing; only the
test-double `sed` alongside it ran.

Everything still compiled and passed. Implementing an interface parameter with a nullable one is
legal, and passing `null` worked at runtime. The full warning comparison caught it as a new CS8625
on `Push(null)`, along with a CS0105 duplicate `using` I had added. Both were fixed, and every number
below is from after the fix.

---

## 5. Mutation checks

Protocol for each check:

1. Copy the file aside and record its SHA-256 prefix.
2. Apply the mutation (diff shown below) and build.
3. Run the named tests.
4. Copy the file back and compare hashes.

A git restore would have discarded the uncommitted pass, which is why the file is copied back
instead. After the last restore, `grep MUTATION` over `src` and `tests` returns 0.

### 5.1 Mutation 2: remove the filter for one marked entity → startup fails

```diff
             if (!mustHave && !typeof(IMayHaveTenant).IsAssignableFrom(clr)) continue;
+            if (clr == typeof(Document)) continue; // MUTATION 2
```

**Host startup (Server.UI.IntegrationTests, `CookieLoginTests`): 4 of 4 fail in SetUp:**

```
System.InvalidOperationException : Tenant isolation check failed: 1 tenant-scoped entity type(s) would be readable by every tenant.
  - Document: carries a tenant marker but has no 'Tenant' query filter.
The filter is registered by ApplicationDbContext.ApplyTenantFilters for every IMustHaveTenant/IMayHaveTenant hierarchy root; something has removed or bypassed it. The application refuses to start rather than serve these rows across tenants.
```

**Guard and filter tests (Application.UnitTests): 8 of 33 fail.**

- **Guard tests:** `TheApplicationModel_PassesTheGuard`, `AProjectEntity_PassesTheGuard…` and
  `AMarkedTphLeaf…`. The last fails because `Document` is also reported, beside the leaf.
- **Filter tests:** `AUserInTenantA_CannotReadATenantBDocument_ThroughTheDbSet`, `NorByKey`,
  `EveryMarkedEntity_CarriesTheNamedTenantFilter`, `TenantB_SeesItsOwnRows…` and
  `WithNoPrincipal_AMayHaveTenantEntity…`.

Restored: `c4607ef8c94cd063` → `c4607ef8c94cd063`.

### 5.2 Mutation 4: the control test against unfixed code

`ApplicationDbContext.cs` was replaced with its HEAD (pre-pass) version, which has the hand-listed
`AuditTrail` and `PicklistSet` filters and no marker walk:

```diff
-        ApplyTenantFilters(builder);
 …
+        builder.Entity<PicklistSet>().HasQueryFilter(
+            QueryFilters.Tenant,
+            (PicklistSet p) => p.TenantId == null || p.TenantId == CurrentTenantId);
```

`TenantFilterTests`: **7 of 9 fail.**

```
AUserInTenantA_CannotReadATenantBDocument_ThroughTheDbSet:
  Expected titles to be equal to {"doc-a", "doc-shared"} … but {"doc-a", "doc-b", "doc-shared"} contains 1 item(s) too many.
AUserInTenantA_CannotReadATenantBRow_OfAMustHaveTenantEntity:
  Expected notes to be equal to {"probe-a"}, but {"probe-a", "probe-b"} contains 1 item(s) too many.
```

The other failures are `NorByKey`, `EveryMarkedEntity_CarriesTheNamedTenantFilter`,
`TenantB_SeesItsOwnRows…` and both `WithNoPrincipal_…` tests. The two that pass,
`TheNamedExemption…` and `UnmarkedEntities_AreNotTenantFiltered`, are expected to hold on either
version.

Restored: `c4607ef8c94cd063` → `c4607ef8c94cd063`.

### 5.3 Mutation 6: the system scope does not push → the request is denied

The pre-pass code has no `ISystemContext`, so a literal "unfixed" run does not compile. The
load-bearing piece is the push, so the mutation removes it from both overloads:

```diff
         var context = await ResolveAsync(tenantId, cancellationToken);
-        using (_userContextAccessor.Push(context))
+        using ((IDisposable?)null) // MUTATION 6: the work runs with no pushed context
```

`SystemContextTests`: **2 of 5 fail.**

```
InsideTheScope_TheSameRequestPassesAuthorization_AndReadsOnlyThatTenant:
  ForbiddenAccessException : Access to 'GetAllPicklistSetsQuery' was denied because there is no authenticated user in context.
AWriteInsideTheScope_IsStampedWithTheTenant_AndAttributedToTheSystemAccount:
  Expected row.TenantId to be "t-fd55…", but found <null>.
```

The 3 that pass are `OutsideTheScope_ARequestIsDenied` (the control), `TheScopeEnds…` and
`AnUnknownOrMissingTenant…`, none of which depend on the push.

Restored: `ddc6e0cd96f5636d` → `ddc6e0cd96f5636d`.

### 5.4 Extra checks (not requested)

- **Item 3 against unfixed code.** With `AuditableEntityInterceptor.cs` at HEAD,
  `TenantStampingByMarkerTests` fails 2 of 4. A plain `IMustHaveTenant` row inserted under a
  principal fails with `DbUpdateException` (the NOT NULL column), and the fail-closed test gets that
  same database error instead of the interceptor's `InvalidOperationException`. That confirms the
  claim in the test file's remarks. Restored: `38cfb46e7fc56694` → `38cfb46e7fc56694`.
- **`/files` without the push (§3.3).** This makes 3 `FileEndpointsAuthorizationTests` and the real
  HTTP test `FileEndpointMatrixTests.AVisibleDocument_IsServedToItsOwner` fail: a tenant user is
  refused their own document. Restored: `b16e0078b20badae` → `b16e0078b20badae`.

---

## 6. Test counts

Same machine, same server (PostgreSQL 18.0, localhost:5434), `GX_TEST_PG` set for the run only.
`GX_TEST_CREATE_DATABASES` was not set, and Azurite was not running.

| Suite | Before (HEAD `45bee280`) | After | Δ |
|---|---|---|---|
| Application.UnitTests | 531 passed, 14 skipped, 0 failed (545) | **558 passed, 14 skipped, 0 failed (572)** | +27 (27 new; 1 renamed in place, net 0) |
| Infrastructure.UnitTests | 221 passed (221) | **221 passed (221)** | 0 |
| Application.IntegrationTests | 42 passed (42) | **57 passed (57)** | +15 |
| Server.UI.IntegrationTests | 283 passed (283) | **283 passed (283)** | 0 |
| **Total** | **1,077 passed, 14 skipped** | **1,119 passed, 14 skipped** | **+42** |

- **Removed:** none. One test was renamed and rewritten (§3.2).
- **Skips:** the same 14 tests before and after (compared by name): 12 Azurite tests and 2
  `GX_TEST_CREATE_DATABASES` tests.

Every "after" number comes from one full run of all four suites on the final tree, the tree that is
committed.

---

## 7. Standing controls

### 7.1 Build

`dotnet build --no-incremental`: 0 errors. **The warning set, by file, line and code, is identical
to the baseline: 50 unique before and 50 unique after, with nothing added or removed.** MSBuild's
summary counter read 50 on the baseline (incremental) build and 59 on the final non-incremental
one. The difference is NuGet advisory warnings repeated across restore and build passes, not new
warnings.

### 7.2 Migrations

All three chains were regenerated together. The duplicate-removal SQL in the **PostgreSQL**
migration runs against every test database as the suites migrate it, with no duplicates present, so
its syntax is exercised. Its semantics are reasoned from the statement rather than tested against a
database seeded with duplicates. The **SQL Server and SQLite** statements are not executed by any
test in this repository.

### 7.3 Pack and smoke

`powershell -ExecutionPolicy Bypass -File tooling\smoke-generate.ps1`, with `GX_TEST_PG` pointed at the
same server: **SMOKE PASSED (postgresql)**, 106 checks `ok`. It covers:

- `dotnet pack build/pack.csproj` → `GX.Blazor.Template.1.2.0.nupkg`, installed into a custom hive;
- generation with the template's defaults; every content assertion;
- the generated solution building;
- the generated suites failing loud without `GX_TEST_PG`.

The generated suites then passed against the server:

| Generated suite | Passed | Skipped | Failed |
|---|---|---|---|
| Application.UnitTests | 558 | 14 | 0 |
| Infrastructure.UnitTests | 200 | 0 | 0 |
| Application.IntegrationTests | 57 | 0 | 0 |
| Server.UI.IntegrationTests | 283 | 0 | 0 |

Infrastructure.UnitTests runs 200 rather than 221 because the SQL Server-only tests are stripped
for a PostgreSQL project, exactly as before this pass. So every new test also passes in a generated
project, where the MSSQL and SQLite conditionals have been removed.

---

## 8. Not done, and follow-ups

- **`gx-system` is visible and editable in the Users area** to a holder of `Users.ViewAllTenants`.
  `AdministratorProtectionService` does not protect it.
  - Deleting it is recoverable: the next start re-creates it, with a new id.
  - Unlocking it is reverted on the next start.
  - Giving it a password is only refused on the next start, which stops the application.

  Hiding or protecting it in the UI is a small follow-up I did not take, because it touches the
  Users grid and pass 49's scope.
- **No hosted service or Hangfire job uses `ISystemContext`**, because the template ships none. The
  first one a product adds is the real integration test.
- **Roles and permissions are still installation-wide**, as the IMS audit's §1.7 and §2.5 found.
  This pass gives background work a tenant; it does not make permissions tenant-scoped or
  branch-scoped.
- **The SQL Server and SQLite duplicate-removal SQL is untested** (§7.2).
## 9. Commit

One commit on `main`, `Pass54-TenancyPlumbing`, following the `passNN` / `PassNN-Name` convention of
the history. It contains everything above, including this report. Not pushed.
