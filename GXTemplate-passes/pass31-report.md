# Pass 31 — Picklists: Shared Plus Per-Tenant

**Nature:** editing pass. The product decision was already made; this builds it. **No git actions.**
**Date:** 2026-09-04.

**Result in one line:** `PicklistSet` joined the Pass 29 global-filter list with the predicate
`TenantId == null || TenantId == current`; **four** cache scopes moved with it, not the one the brief
named; the import's duplicate check became per-tenant for free; and `SearchAsync` turned out to be
bounded by the filter rather than by anything Pass 26 A2 had to worry about. **859 → 877 tests**,
warnings unchanged.

---

## 1. Start state

| | |
|---|---|
| HEAD | `0b0e64e0` — *"Pass30"* |
| Working tree | **clean** |
| Spot-check `HubUserContext.cs` | present |
| Spot-check `Clients.All` in `ServerHub.cs` | **absent from code** (three occurrences, all in comments that forbid it) |
| Build | **0 errors, 19 warnings across 10 distinct source locations** |
| Tests | **217 + 12 + 429 + 201 = 859 passed, 12 skipped, 0 failed** |

The eleventh raw warning line is `NETSDK1206`, emitted once per project from the SDK targets with no
source location. It is not one of the ten and has not been across this programme.

---

## 2. §A.1 — The predicate shape: nothing was baked in, and that is lucky

**The Pass 29 mechanism has no shared expression to accommodate anything.** The wording of the brief
allowed for one — "whether `AuditTrail`'s stricter equality was baked into the shared expression" —
and the answer is that there is no shared expression at all. `ApplyGlobalFilters<ISoftDelete>` is a
marker-driven helper, but the tenant filter is not registered through it. Pass 29 wrote the entity
out by hand:

```csharp
builder.Entity<AuditTrail>().HasQueryFilter(
    QueryFilters.Tenant,
    (AuditTrail a) => a.TenantId == CurrentTenantId);
```

`HasQueryFilter` takes a lambda **per entity**, so the "explicit entity list" the Pass 29 comment
describes is literally a sequence of such calls, each stating its own rule. Adding `PicklistSet` is a
second call with a different predicate and no refactoring:

```csharp
builder.Entity<PicklistSet>().HasQueryFilter(
    QueryFilters.Tenant,
    (PicklistSet p) => p.TenantId == null || p.TenantId == CurrentTenantId);
```

**So the two entities do need genuinely different predicates, and the list expresses that at no
cost.** The brief is right about why: a null `TenantId` means the *opposite* thing for each.

| | A null `TenantId` means | Predicate |
|---|---|---|
| `AuditTrail` | an installation-level **event** belonging to **nobody** — seeding, bootstrap, background work | `TenantId == CurrentTenantId` |
| `PicklistSet` | shared **reference data** belonging to **everyone** | `TenantId == null \|\| TenantId == current` |

**Had Pass 29 chosen a marker interface or a shared expression, this pass would have had to undo
it.** That is worth recording as a property of the mechanism rather than a coincidence: the reason
the list is written out is that adding to it should be a deliberate act, and the payoff arrived one
pass later in a form nobody predicted — not "a new entity was added by accident" but "a new entity
needed a different rule under the same name".

**The name stays shared, deliberately.** `QueryFilters.Tenant` is what an exemption names, and an
exemption means the same thing for either entity: "read across tenants, having checked a right".
Splitting it into two names would have made `IgnoreQueryFilters([...])` call sites depend on which
entity they happened to be querying. `TheTwoFilteredEntitiesTreatANullTenantOppositely` reads both
predicates off the built model and asserts they differ, because one shared predicate is the natural
implementation and it could only ever have served one of them.

---

## 3. §A.2 — The consumer inventory, classified before the filter landed

Every reader and writer of `PicklistSet`, and what the filter does to it.

### Scoped — bounded by the filter, no code change needed

| Consumer | | Effect |
|---|---|---|
| `GetAllPicklistSetsQueryHandler` | `db.PicklistSets.OrderBy(...)` | shared + own |
| `PicklistSetsQueryByNameHandler` | `Where(x => x.Name == request.Name)` | shared + own, per picklist name |
| `PicklistSetsQueryHandler` (pagination) | via `PicklistSetAdvancedSpecification` | shared + own; the specification never mentioned a tenant and still does not |
| `ExportPicklistSetsQueryHandler` | `Where(keyword)` | **the export is bounded** — the surface Pass 27 had to fix by hand for users comes free here |
| `PicklistDataSourceService.LoadAsync` | the selectors' backing list | shared + own |
| `PicklistDataSourceService.SearchAsync` | see §6 | shared + own |
| `ImportPicklistSetsCommandHandler`'s duplicate check | `AnyAsync(name && value)` | **behaviour change** — see §7 |
| `AddEditPicklistSetCommandHandler` edit path | `FindAsync(request.Id)` | cannot reach another tenant's row |
| `DeletePicklistSetCommandHandler` | `Where(Id.Contains)` | cannot reach another tenant's row |
| `SeedPicklistsAsync`'s guard | `AnyAsync()` | **sees shared rows only, which is the right guard** — see §5 |

### Exempt — no exemption needed, because none reads the table

`PicklistSetChangedEventHandler` calls `RefreshAsync()` and reads nothing. The three domain events,
the DTO, the validators, the specification, the permissions registry and `PicklistAutocomplete` carry
no query. `AuditTrails.razor` mentions `Picklist` only as a `PickListView` enum — a name collision,
not a consumer.

### Unclear at the point of classification — and each one resolved

1. **`AddEditPicklistSetCommandHandler`'s `FindAsync`.** EF's `Find` returns a tracked entity without
   querying when one is already tracked, and it was not obvious that the database path applies a
   global filter. It does — `ARowCannotBeReachedByIdFromAnotherTenant` asserts it rather than
   assuming it, because the edit and delete commands are the two places a filter bypass would be an
   escalation rather than a disclosure.
2. **`SearchAsync`.** §6.
3. **The seed guard.** §5.

**Writes are the half a filter does not bound, and the semantics come from elsewhere.** A global
query filter constrains reads; what makes a new picklist private is `AuditableEntityInterceptor`
stamping `IMayHaveTenant` from the ambient principal, which Pass 24 already built. The consequence is
stated in §9 because it is not obvious and it is not entirely comfortable.

---

## 4. §A.3 — Seeding, confirmed live rather than reasoned

Pass 29 §Q2 established that infrastructure paths satisfy the filter naturally. **That holds here for
both the guard and the write, and the two need separate arguments.**

`ApplicationDbContextInitializer` builds its context from `IDbContextFactory<ApplicationDbContext>`,
so EF resolves `IUserContextAccessor` from the container. Seeding runs from `HostExtensions` at
startup with nothing ambient, so `CurrentTenantId` is null and EF's null-semantics rewriting reduces
`TenantId == null || TenantId == null` to `TenantId IS NULL`.

- **The guard.** `if (await _context.PicklistSets.AnyAsync()) return;` now asks *"have the **shared**
  picklists been seeded?"* rather than *"does any tenant have a picklist?"*. That is strictly better:
  under the unfiltered version, one tenant's private addition on a database that had somehow lost its
  reference data would have suppressed re-seeding forever.
- **The write.** The rows carry no tenant, because the interceptor stamps from the same absent
  principal. So a fresh seed produces rows **every tenant can see** rather than rows nobody can.

**The failure mode this avoids is silent.** Pass 29 A4 established that seeded *audit* rows are
invisible to tenant principals **by design**. Picklists had to come out the opposite way, under the
same filter name — and a seed that produced reference data nobody could see would show up as every
dropdown in the application being empty, with no error anywhere. `PicklistSeedVisibilityTests` boots
the real application and reads what it actually wrote; §8.3 has the evidence.

---

## 5. §A.4 — The admin page: what it does now, and what I recommend

**What it does after the filter.** `PicklistSets.razor` is a `MudDataGrid` with
`EditMode="DataGridEditMode.Cell"` and `ReadOnly="false"`, whose rows come from
`PicklistSetsWithPaginationQuery`. So a tenant-scoped administrator now sees **their own rows plus
the installation's shared ones**, and every one of them is editable in place; the per-row delete
button is gated on `_accessRights.Delete` and nothing else. Editing a shared row changes it **for
every tenant**.

**Can the page express "visible but not editable"? Yes, and it takes three changes, none of them
hard:**

1. `PicklistSetDto` carries no `TenantId`. **The page cannot currently tell a shared row from a
   private one** — that is the binding constraint, and it is a DTO field.
2. MudBlazor's `Editable` is per *column*, not per row. Per-row read-only means giving each
   `PropertyColumn` an `EditTemplate` that renders text instead of an editor when the row is shared.
   `CommittedItemChanges` is already the commit hook and can reject one. The delete button already
   takes a per-row `Disabled` expression.
3. **The real guard belongs in the command handlers, not the page.** `AddEditPicklistSetCommand` and
   `DeletePicklistSetCommand` address rows by id through a filter that admits shared rows, and they
   are reachable through Mediator regardless of what the grid renders.

**My recommendation, with the consequence stated because it is the part that needs your decision.**

I recommend shared rows be **visible and not editable by a tenant-scoped principal**, as you expected
— *but* the bootstrap administrator **is** tenant-scoped: `EnsureAdministratorAsync` assigns them
`Tenants.First()`. Combined with §C's decision that picklists have no cross-tenant escape, the honest
consequence is that **the seeded picklist values would become uneditable by anyone through the UI** —
seeded once, frozen for the life of the installation. That may well be the right answer for reference
data. It is not obviously the right answer, and it is a product decision rather than a defect, so I
have **reported and recommended it rather than built it**, which is what §A.4 asked for.

If you want it, the shape is: add `TenantId` to `PicklistSetDto`, guard both command handlers on
"shared rows require no tenant on the acting principal", and mark shared rows in the grid. If you
want shared rows editable by a named right instead, that right is the cross-tenant escape §C declined,
and the two decisions have to be taken together.

**This is the one item left open.** It is written up as a README limitation in the meantime, so a
consumer relying on multi-tenant picklists is told rather than left to find out.

---

## 6. §D — `SearchAsync`: checked, not assumed, and the finding inverts the concern

Pass 26 A2 recorded that `PicklistDataSourceService.SearchAsync` bypasses both the cache and the
scope — harmless at `Global`, *"a leak the moment the scope means something"*.

**What it does.** The base class's `SearchAsync` filters the in-memory `Items`, so it inherits
whatever `Scope` partitioned. This override queries `db.PicklistSets` directly and **never reads or
writes the cache at all**.

**The finding: it is bounded by the global filter, and I confirmed it rather than assuming.** The
chain is `IApplicationDbContextFactory` → EF's `IDbContextFactory<ApplicationDbContext>` → container
→ `IUserContextAccessor` injected → `CurrentTenantId` is the caller's. So the filter reaches this
query exactly as it reaches `LoadAsync`.

**And the concern inverts.** Bypassing the cache makes this path *safer* than the cached one, not
riskier: there is no entry for it to serve to the wrong tenant. Pass 26 A2's phrasing anticipated the
opposite, and it was reasonable to — a method that skips the mechanism you just built is exactly
where you look first. Two tests hold the result rather than the reasoning, because "it bypasses the
cache, therefore it is safe" stops being true the day someone routes it through `Items`:

- `SearchAsyncIsBoundedByTheGlobalFilter` — asks for another tenant's row **by name**, the shape a
  hostile caller would use, and still finds its own row and the shared one.
- `SearchAsyncDoesNotServeAWarmedEntryFromAnotherTenant` — warms tenant A's entry first, then
  searches as tenant B.

---

## 7. §B — The cache scope, and the three the brief did not name

### 7.1 The partition answer: `PerTenant` is correct, and the reasoning is the point

**Is a picklist list identical for any two principals in the same tenant? Yes.** The predicate is
`TenantId == null || TenantId == current`, and `CurrentTenantId` reads `UserContext.TenantId` and
**nothing else**. No permission enters it, no `AllowedTenantIds` union, and — per §C — no cross-tenant
escape exists to make one principal in a tenant differ from another. `t:{TenantId}` is exactly the key
the filter's own input composes to.

**This is the opposite outcome to Pass 28's, and the contrast is why the question had to be asked.**
`UserDataSourceService`'s declared `PerTenant` became false the moment its query read
`AllowedTenantIds` and `Users.ViewAllTenants`: two principals in one tenant genuinely differ if one
also belongs to a second tenant or holds the cross-tenant right. **A partition is a claim about who
may share an entry, and changing what a query returns can invalidate it without touching the line
that declares it.** So it was re-derived here rather than carried forward, and
`TwoPrincipalsInTheSameTenantShareOneEntry` asserts the sharing half — the assertion that would fail
if the reasoning ever stopped holding.

The comment beside the declaration says all of that, and adds the conditional: **if a cross-tenant
escape is ever added, this must become `PerUser` in the same change.**

### 7.2 Four scopes moved, not one

The brief named `PicklistDataSourceService`. Three more carried the same defect, in the same feature,
for the same reason.

| Declaration | Before | After | Why |
|---|---|---|---|
| `PicklistDataSourceService.Scope` | `Global` | **`PerTenant`** | the one the brief named |
| `GetAllPicklistSetsQuery.Scope` | `Global` | **`PerTenant`** | a `ICacheableRequest` whose handler never mentions a tenant and whose result now varies by one |
| `PicklistSetsQueryByName.Scope` | `Global` | **`PerTenant`** | same |
| `PicklistSetsWithPaginationQuery.Scope` | `PerUser` | **`PerUserAndTenant`** | see below |

**The pagination query is the interesting one, and `PerUser` is not merely under-strict — it is
wrong in a way a circuit reload does not fix.** It was `PerUser` because the specification narrows the
date window by the caller's local time offset. The rows are now narrowed by tenant too, and the user
id does not capture that: **one principal can occupy two tenants over time**, which is precisely what
the tenant switcher does. Under a `u:{userId}` key they would be served, after switching, the list
they cached before it. The FusionCache entry is process-wide and outlives the circuit, so Pass 30's
forced page load does not clear it.

**Invalidation was checked and needs no change.** All four picklist commands carry
`PicklistSetCacheKey.Tags`, and `CacheInvalidationBehaviour` flushes by tag — which removes matching
entries *whatever key they were written under*, so it already reaches every scoped variant.
`ICacheInvalidatorRequest`'s own remarks say why invalidation must not be scoped. The unscoped
`CacheKey` removal beside it is now a no-op for these requests and is left alone: it is harmless, and
the interface documents `CacheKey` as optional.

### 7.3 `_loadedKey` and the tenant switch — moot for the switch, kept for everything else

`DataSourceServiceBase.InitializeAsync` reloads when `_loadedKey != EffectiveKey()`, which is what
makes a scope real rather than declarative mid-circuit.

**For the tenant switch it is moot, and Pass 30 is why.** `TenantSelector.razor` navigates with
`forceLoad: true`, which destroys the circuit; these services are `Scoped`, so the instance and its
`Items` are disposed and rebuilt. The reload path is never reached for that case. Pass 30 pinned the
`forceLoad` with a comment and a test, so this is a documented dependency rather than an assumption —
**but it is a second thing now resting on that one line**, and it is recorded here for that reason.

`_loadedKey` is not redundant: it still covers any future path that changes a principal's effective
key without tearing down the circuit. It was not touched.

---

## 8. §E — Verification

### 8.1 Shared and private, in one test (§E.1) and narrowed not emptied (§E.2)

`PicklistSetTenantFilterTests` seeds five rows — two shared, two tenant-A, one tenant-B — and asserts
**full equalities over the visible set**, not `NotContain`:

```
tenant A sees [shared-status, shared-brand, a-status, a-brand]
tenant B sees [shared-status, shared-brand, b-status]
no principal  sees [shared-status, shared-brand]
```

The equality form is deliberate. "Tenant B cannot see tenant A's row" passes against a filter that
returns nothing; "shared rows are visible" passes against a filter that returns everything. **The
shared/private split makes an over-broad filter look plausible**, which is exactly the brief's
warning, so `NarrowedNotEmptied_TenantASeesEverySharedRowAndEveryTenantARow` states two shared rows,
two private rows, a count, and the exclusion — it fails against a dropped shared half, a dropped
private half, and a narrowing to one.

`AQueryThatNeverHeardOfTenancyIsStillScoped` holds the property that distinguishes a global filter
from a per-surface predicate: a count, a `Where` on picklist name, and an `AnyAsync` on a value,
none of which mentions a tenant.

### 8.2 The cache partition (§E.4)

`PicklistDataSourceScopeTests` runs **two real `PicklistDataSourceService` instances over one
`FusionCache`** — as two circuits share the process-wide cache — against a real SQLite database and
the real filter. Not a probe: `DataSourceScopeTests` next door already pins the base class mechanism,
and what needed asserting here is that *this service's* declared scope matches what *its* query
depends on.

- different tenants are not served each other's picklists, **in either arrival order** (the Global-key
  defect is order-dependent, which is what makes it intermittent);
- two principals in the same tenant **do** share one entry, and the composed keys are asserted equal;
- a tenant still gets the shared rows and its own.

### 8.3 The live seed (§E.3)

`PicklistSeedVisibilityTests` boots the real application through `GxWebApplicationFactory`, serves a
request so the host actually initialises and seeds, then pushes an ambient `UserContext` onto the real
singleton `IUserContextAccessor` and reads `PicklistSets` through a real `IDbContextFactory`.

- The probe tenant is `"a-tenant-that-does-not-exist"` — **deliberately not a seeded tenant**, so
  nothing about the seed can have arranged for it and anything visible is shared data and nothing
  else. It sees the shipped values, `"initialization"` among them.
- Two unrelated tenants and the infrastructure path all see the **same** set.
- `TheSeededRowsCarryNoTenant` compares the filtered count to an `IgnoreQueryFilters()` count, so a
  failure reads *"the seeder stamped a tenant"* rather than *"a dropdown was empty"*.

### 8.4 The import duplicate check (§E.5) — proved, and the gap closed

Two tests, because one would have proved less than it appeared to.

- `TheImportDuplicateCheckIsNowPerTenant` runs the handler's exact predicate against the real filter:
  tenant B importing tenant A's `Status/a-status` is **not** a duplicate (the behaviour change), and
  tenant B importing the shared `Status/shipped-status` **is** (nobody may shadow a shipped value —
  a shadowing row would render twice in the same dropdown).
- `TheImportHandlerAsksTheQuestionTheTestAboveAnswers` reads the handler's source and asserts the
  predicate is still the name/value pair, and that no `IgnoreQueryFilters` has appeared. **Without
  it, the pair proves only that a predicate I typed behaves correctly.** The predicate is a lambda in
  a method body, so there is nothing to reflect on; the source pin follows Pass 30's shape and
  `GetDateRangeKindTests.SourcePath`'s repository-relative anchoring, so a generated project runs it
  against its own copy.

### 8.5 Red before, green after — **separately**, per §E.7

Demonstrated by reverting each half in place and restoring byte-identically (verified by `diff`
against copies taken beforehand, not re-edited from memory).

**A — filter removed, scopes left at `PerTenant`:**

```
Application.UnitTests   (filter)  Failed: 7,  Passed: 1
Infrastructure.UnitTests (scope)  Failed: 5,  Passed: 2
Server.UI (live seed)             Failed: 0,  Passed: 3
```

**B — filter restored, `PicklistDataSourceService.Scope` reverted to `Global`:**

```
Application.UnitTests   (filter)  Failed: 0,  Passed: 8      <-- all green
Infrastructure.UnitTests (scope)  Failed: 3,  Passed: 4
Server.UI (live seed)             Failed: 0,  Passed: 3
```

**B is the result the brief asked for, and it is worth reading carefully.** With the query correctly
filtered and only the cache key wrong, **every query-level test passes**. All eight filter tests, all
three live-seed tests. The leak is invisible to them because the query that would expose it is never
executed — the entry is served from cache. Only three tests fail:
`TheDeclaredScopeIsPerTenant`, `TwoTenantsAreNotServedEachOthersPicklists`,
`TheOrderTheTenantsArriveInDoesNotMatter`.

That is the concrete demonstration of *"the two halves are not separable"*. Had this pass shipped the
filter and forgotten the scope, the test suite would have been entirely green over a live
cross-tenant leak.

Restored: `Failed: 0, Passed: 18`.

### 8.6 Boundary suites (§E.8)

**No existing test file was modified.** Every Pass 26–30 scope, isolation and presence suite was
confirmed byte-unmodified with `git diff --quiet` per file — including `DataSourceScopeTests`, which
is Pass 26's own cache-partition suite and the one most likely to have needed touching:

```
HarnessPrincipalTests            AuditTrailTenantFilterTests       SwitchableTenantsTests
TenantSwitchAuthorizationTests   TenantVisibilityTests             UserVisibilityTests
DataSourceScopeTests             OnlineUsersTrackerComponentTests  ServerHubTenantIsolationTests
SuperiorAutocompleteScopeComponentTests   SuperiorBoundComponentTests   TenantSelectorComponentTests
UserDeactivationPermissionComponentTests  UserTenantScopeComponentTests
```

Run as a filtered set: **120 passed, 0 failed** (32 + 34 + 3 + 51).

`PicklistServiceTests` — a pre-existing integration suite that adds picklists and counts them — also
passes untouched. That is a useful negative: its harness supplies a `UserContext` with **no** tenant,
so its rows are written shared and read back shared, and the filter is transparent to it.

### 8.7 Counts (§E.9)

| | Before | After | Delta |
|---|---|---|---|
| `Infrastructure.UnitTests` | 217 | **224** | **+7** |
| `Application.IntegrationTests` | 12 | 12 | — |
| `Application.UnitTests` | 429 (+12 skipped) | **437** (+12 skipped) | **+8** |
| `Server.UI.IntegrationTests` | 201 | **204** | **+3** |
| **Total** | **859 passed, 12 skipped** | **877 passed, 12 skipped** | **+18, 0 failed** |

The +18 is exactly the three new files. No pre-existing test changed count or outcome.

**Warnings: unchanged.** `dotnet build --no-incremental` gives **19 warnings across the same 10
distinct source locations** as the start state — `DescriptionAttributeExtensions.cs` ×4,
`MapsterConfiguration.cs` ×2, `MudDateTimeField.razor`, `TenantSelect.razor`, `Dashboard.razor`,
`AuditTrails.razor` — plus `NETSDK1206`. **No new warning location. 0 errors.**

(Two new `CS8602` warnings appeared briefly from a `GetDeclaredQueryFilters()` chain in the first
draft of the filter test and were removed by extracting a helper that throws with a useful message
instead of null-propagating. Recorded because "warnings unchanged" is only meaningful if the
intermediate states are reported too.)

### 8.8 Generation probe (§E.10)

```
dotnet pack (nuspec) → dotnet new install → dotnet new gxblazor -n P31
  → build: 0 Error(s), 19 Warning(s)
  → dotnet test: 224 + 12 + 437 + 204 = 877 passed, 12 skipped, 0 failed
  → dotnet new uninstall; probe directory removed
```

Identical to source, suite for suite. The generated project carries the `PicklistSet` filter
registration and the `PerTenant` declaration, and its three new test files find their source anchors
under its own `src/`.

**No migration was needed or generated.** A global query filter is part of the EF model but not part
of the schema, so `InitialCreate` and the model snapshots are untouched — confirmed by the generated
project building and migrating cleanly with no pending-model-changes complaint.

---

## 9. §C — The escape: there is none, and none was left behind

**Picklists have no cross-tenant escape.** No permission, no `IgnoreQueryFilters` call site, no
extension point "for later".

The reasoning the brief expected holds on inspection: unlike users and audit trails, there is no
administrative task that requires one. An installation operator managing shared reference data works
with the **null-tenant rows, which everyone already sees**. A tenant's private additions are that
tenant's own business, and there is no support question they answer that asking would not.

Two things follow, and both are written into the code rather than only here:

- `QueryFilters.Tenant`'s remarks now say explicitly that picklists have no exemption **by decision
  rather than omission**, so a future reader who finds `AuditTrailTenantScope` and looks for its
  picklist equivalent is told there isn't one and why.
- `PicklistDataSourceService.Scope`'s remarks carry the conditional: **an escape would be a
  per-principal fact, so adding one requires moving the scope to `PerUser` in the same change.** That
  is the Pass 28 lesson written down at the point where it would next be needed, rather than left to
  be rediscovered.

The README states it too, in the paragraph that now covers presence and picklists together.

---

## 10. Consequences of the decision, stated rather than discovered later

Three behaviour changes this decision implies, none of which is a defect and none of which is
obvious from the diff:

1. **A picklist created through the UI is private to its creator's tenant.** The interceptor stamps
   `IMayHaveTenant` from the ambient principal, so shared rows come only from seeding. **There is no
   way to create a shared picklist through the application.** For a template whose shipped picklists
   are seeded that is coherent; for an installation that wants to add installation-wide reference
   data later, it is a gap. Recorded in `PicklistSet`'s own remarks, in `SeedPicklistsAsync`'s, and
   in the README.
2. **Editing a shared row is now the only way to change what every tenant sees, and any tenant's
   administrator can do it.** §5.
3. **A change to a shared row no longer evicts every tenant's cached list.** `RefreshAsync` removes
   *this principal's* entry, which under `Global` was the single shared one. Other tenants now serve
   a stale list until the entry expires. That is a freshness regression, not a leak, and it is
   inherent to partitioning — `DataSourceServiceBase.RefreshAsync` already documents the trade-off in
   general terms. It is a second argument for §5's recommendation: if shared rows are not editable,
   they do not go stale.

---

## 11. README and package metadata

**The Tenancy table has a new row and the Picklists row moved out of the unscoped block:**

- Old: *"Picklists | No — stamped, not filtered; shared reference data by design"*. **Deleted.**
- New, placed above the unscoped block: *"Picklists | **Yes, on a different shape** — SHARED plus
  per-tenant additions…"*, naming the predicate and the absence of an escape.
- The warning *"treat everything below the **Online presence** row"* now says **below the Picklists
  row**.
- The contract line went from *"Four surfaces are filtered by it"* to **five**, and the intro
  paragraph and the limitations bullet with it.
- New Tenancy paragraph on **the null-tenant asymmetry** — why the same value means "everyone's" here
  and "nobody's" on `AuditTrail`, and the three consequences of §10.
- The escape paragraph now covers **presence and picklists together**, with the different reasons for
  each, and names the `PerUser` conditional.
- *"The audit trail is the first surface scoped by default"* became **"Audit trails and picklists are
  scoped by default rather than by remembering; the rest are not."**
- **New paragraph: "A filtered query behind a shared cache key is a leak no query test can see, so
  the two always move together."** Aimed at a consumer who filters a query in a generated project,
  with the two scope moves this pass made as the worked example. This is the transferable lesson and
  it was not in the README before.
- New limitations bullet: **"A shared picklist value is editable by any tenant's administrator"**, so
  §5's open item is disclosed rather than pending.

**`GX.Blazor.Template.nuspec`'s `<description>`** updated to match — it ships in the package and on
the NuGet listing, and it named picklists as installation-wide.

---

## 12. File map, diffstat and edit fidelity

### 12.1 File map

**New (3), all tests:**

| File | |
|---|---|
| `tests/Application.UnitTests/Features/PicklistSets/PicklistSetTenantFilterTests.cs` | 301 lines, **8 tests** — the filter, the null-tenant asymmetry, the import check, reach-by-id |
| `tests/Infrastructure.UnitTests/Services/PicklistDataSourceScopeTests.cs` | 240 lines, **7 tests** — the cache partition and `SearchAsync`, over a real service, cache and database |
| `tests/Server.UI.IntegrationTests/PicklistSeedVisibilityTests.cs` | 131 lines, **3 tests** — the live seed |

**Modified (9 + 2 docs):**

| File | |
|---|---|
| `src/Infrastructure/Persistence/ApplicationDbContext.cs` | the `PicklistSet` filter, and the comment on one name / two predicates |
| `src/Application/Common/Constants/QueryFilters.cs` | `Tenant`'s remarks: both entities, the asymmetry, no picklist exemption |
| `src/Domain/Entities/PicklistSet.cs` | remarks rewritten — the open product question is closed |
| `src/Infrastructure/Services/PicklistDataSourceService.cs` | `Scope` → `PerTenant` with the re-derivation; `SearchAsync` remarks |
| `src/Application/Features/PicklistSets/Queries/GetAll/GetAllPicklistSetsQuery.cs` | `Scope` → `PerTenant` |
| `src/Application/Features/PicklistSets/Queries/ByName/PicklistSetsQueryByName.cs` | `Scope` → `PerTenant` |
| `src/Application/Features/PicklistSets/Queries/PaginationQuery/PicklistSetsWithPaginationQuery.cs` | `Scope` → `PerUserAndTenant` |
| `src/Application/Features/PicklistSets/Commands/Import/ImportPicklistSetsCommand.cs` | comment on the now-per-tenant duplicate check |
| `src/Infrastructure/Persistence/ApplicationDbContextInitializer.cs` | `SeedPicklistsAsync` remarks: why the guard and the write are both correct |
| `README.md`, `GX.Blazor.Template.nuspec` | §11 |

**Deleted:** none.

### 12.2 Diffstat (tracked files; the three new test files are untracked and listed above)

```
 GX.Blazor.Template.nuspec                                        |   9 +-
 README.md                                                        | 100 ++++++++++++-----
 src/Application/Common/Constants/QueryFilters.cs                 |  18 +++-
 .../PicklistSets/Commands/Import/ImportPicklistSetsCommand.cs    |  11 +++
 .../PicklistSets/Queries/ByName/PicklistSetsQueryByName.cs       |  11 ++-
 .../PicklistSets/Queries/GetAll/GetAllPicklistSetsQuery.cs       |  13 ++-
 .../Queries/PaginationQuery/PicklistSetsWithPaginationQuery.cs   |  19 +++-
 src/Domain/Entities/PicklistSet.cs                               |  25 +++--
 src/Infrastructure/Persistence/ApplicationDbContext.cs           |  24 ++++
 src/Infrastructure/Persistence/ApplicationDbContextInitializer.cs|  21 ++++
 src/Infrastructure/Services/PicklistDataSourceService.cs         |  54 +++++++---
 11 files changed, 246 insertions(+), 59 deletions(-)
```

**The functional change is four lines**: three added to `OnModelCreating` and four `Scope`
expressions. Everything else is the reasoning that makes them reviewable.

### 12.3 Edit fidelity

- **No git actions.** Nothing staged, committed, stashed or reset. `git show`, `git diff`,
  `git log` and `git status` were used read-only, to identify Passes 26–30's test files and to
  confirm they are unmodified.
- **Both red-before demonstrations were reverted byte-identically**, verified by `diff` against
  copies taken beforehand.
- **No existing test file was touched.** The +18 is entirely in three new files.
- No migration was added; none was needed (§8.8).

---

## 13. What remains unscoped, and which are product questions

| Surface | Status | Product question or defect? |
|---|---|---|
| **Picklists** | **scoped — shared plus per-tenant, no escape** | **closed by this pass** |
| Whether shared picklist rows are **editable** by a tenant principal | **open** — reported and recommended at §5, not built | **product question.** The recommendation ("visible, not editable") makes the seeded values uneditable by anyone, because the bootstrap administrator is tenant-scoped and there is no escape. That trade-off is yours |
| System logs | unscoped, and **unreachable** by this mechanism — `SystemLog` is on `LogDbContext`, not `ApplicationDbContext`, so it is outside the model the filter is registered on | **defect, but not a deferrable one.** Scoping them is a separate design — a second filter on a second context, or a predicate at each of the log queries — not a switch waiting to be flipped |
| Roles | unscoped — `ApplicationRole` has no `TenantId` at all, and role names are unique across the installation | **product question.** Per-tenant roles would need a schema change and a decision about whether two tenants may define a role with the same name. Nothing is half-built |
| Security settings (idle policy) | unscoped — one row per installation | **product question, and the answer is plausibly "leave it".** An idle-timeout policy is an installation posture; per-tenant variation is a feature request, not a leak |

**One status changed, and it is this pass's own.** Nothing else moved. The remaining three are
unchanged in both status and reason.

**Of the four originally-unscoped surfaces Pass 23 named, three are now closed** (audit trails in
Pass 29, presence in Pass 30, picklists here) and the fourth — system logs — is the one that needs a
different mechanism rather than another application of this one.

---

## 14. Scratch probe disclosure

Two, both removed:

1. **Green-file backups** for the two red-before demonstrations — copies of `ApplicationDbContext.cs`
   and `PicklistDataSourceService.cs` under the session scratchpad, restored from and then deleted.
2. **The generation probe** — a packed nupkg, an installed template, and a generated `P31` solution
   at a short path. Template uninstalled, directory removed. The nupkg in the repository root was
   rebuilt by `dotnet pack`; it is a gitignored build artifact and does not appear in `git status`.

No database was created or written outside the tests' own throwaway SQLite files. The working tree
contains only the intended changes.

---

## 15. Anomalies

**A1 — the brief named one cache scope; there were four.** `GetAllPicklistSetsQuery` and
`PicklistSetsQueryByName` both declared `CacheScope.Global` with the comment *"reference data,
identical for every caller"* — a claim that was true when written and that this pass falsified,
in two files nobody had to open to make the change. `PicklistSetsWithPaginationQuery`'s `PerUser` was
subtler still: not a leftover but a *correct* declaration for a different reason (the local time
offset), which stopped being sufficient once a second dimension entered the query.
**The general form: a `CacheScope` is a claim about a query's inputs, and scoping a query adds an
input without touching the declaration.** There is no compiler or test that connects them. A
`[RequestAuthorize]`-style startup assertion cannot help either, because the correct scope is not
derivable from the request type.

**A2 — Pass 26 A2's concern about `SearchAsync` inverted on inspection.** It was recorded as *"a leak
the moment the scope means something"*; the scope now means something and the method is safer than
its cached sibling, because bypassing the cache means there is no entry to serve to the wrong tenant.
The original note was the right thing to write — a method that skips the mechanism you just built is
where you look first — and the finding is that the inherited hazard was in the opposite direction to
the one anticipated. Recorded because a flagged concern that turns out to be inverted is easy to
close quietly, and the two tests holding the result exist so the reasoning does not have to be
re-derived.

**A3 — Pass 29's refusal to use a marker interface paid off one pass later, in an unforeseen form.**
The stated reason was that `IMayHaveTenant` would have missed `AuditTrail` and wrongly caught
`Document`. The reason it mattered here is different: `PicklistSet` **does** implement
`IMayHaveTenant`, so a marker-driven filter would have picked it up automatically — with
`AuditTrail`'s predicate, which is the wrong one. **An automatic mechanism would have scoped picklists
correctly-looking and silently backwards**, hiding every shipped value from every tenant. Recorded
because "the explicit list is more work" is the objection this design keeps having to answer.

**A4 — the seed guard got better by accident, and it is worth noticing which way.** `AnyAsync()` on a
filtered `DbSet` is the kind of line that usually breaks when a filter lands under it. Here it
improved: it went from "does any picklist exist anywhere" to "have the shared picklists been seeded",
which is the question it was always trying to ask. The general point is that an idempotence guard's
correctness depends on the visibility of the thing it guards, and a filter changes that silently —
this one happened to change it the right way, and the next one may not.

**A5 — `PicklistSetDto` has no `TenantId`, and that is what blocks §5 rather than any UI limitation.**
The admin page can express per-row read-only through `EditTemplate`; what it cannot do is *know*
which rows are shared. Recorded because the constraint sits one layer away from where the question
gets asked, and a reader who goes looking at `PicklistSets.razor` for the obstacle will not find it
there.

**A6 — two `CS8602` warnings were introduced and removed inside this pass.** The first draft of
`TheTwoFilteredEntitiesTreatANullTenantOppositely` chained
`FindEntityType(...)!.GetDeclaredQueryFilters().Single(...).Expression.ToString()`. Replaced with a
helper that throws a message naming the entity and the missing filter. Recorded because a claim that
warnings are unchanged is only worth anything if the states in between are reported too — and because
a missing query filter is precisely the defect that fixture exists to catch, so it should fail
loudly rather than through a null-reference somewhere downstream.
