# Pass 29 — Stage 5: The Global Query Filter

**Nature:** investigation with a design gate at §A, ratified mid-pass, then implemented. **No git
actions by me** - see the precondition note below. **Date:** 2026-09-03.

> §A was written as a recommendation and is left as it was put (§2-§5), so the reasoning that was
> ratified can be read as it stood. §10 onward records the ratification and what was built:
> **§C first** as a dependency, then §B, then §D's evidence.

---

## 1. Start state

| | |
|---|---|
| HEAD | `e8b937d808e185069c160b70aeeb0266e69fcbab` — *"Pass29"* |
| Working tree | **clean** |
| Build | 0 errors |
| Warning locations | **10 distinct** — unchanged |
| Tests | **824 passed, 12 skipped, 0 failed** |
| Spot-check `GetSwitchableTenantsAsync` | present (`ITenantSwitchService.cs:41`) |
| Spot-check `UserTenantVisibility` | present |

**Precondition note.** The tree was **dirty** when this pass was sent — Pass 28's §A work was
uncommitted, because "No git actions" has been in force every pass. I stopped, and you committed it.
That commit is **messaged `Pass29` but contains Pass 28's §A work**; Pass 29's own commit will need a
different name so the two do not collide in the history.

---

## 2. §A.1 — The stamped-entity set, established rather than assumed

The brief says "`AuditTrail`, `PicklistSet`, and any other stamped entity — establish the full set".
The full set is **not** what the marker interfaces say.

| Entity | `TenantId` | `IMayHaveTenant` | Stamped by | Reachable by a filter on `ApplicationDbContext`? |
|---|:--:|:--:|---|:--:|
| `Document` | yes | yes | interceptor, line 328 | yes |
| `PicklistSet` | yes | yes | interceptor, line 328 | yes |
| `AuditTrail` | yes | **no** | interceptor, line 383 — **constructed, not stamped** | yes |
| `SystemLog` | yes | no | Serilog sink | **no — different context** |
| `ApplicationUser` | yes | no | not the interceptor; it is the *primary tenant* | yes |
| `TenantUser` | yes | no | it *is* the membership join | yes |

**Two findings here, both load-bearing for §A.2.**

**`AuditTrail` does not implement `IMayHaveTenant`.** It has a `TenantId`, but the interceptor sets
it by *constructing* the row (`AuditableEntityInterceptor.cs:383`), not by the marker-driven stamp at
line 328. So `ApplyGlobalFilters<IMayHaveTenant>` — the obvious implementation — **would silently
miss the entity this pass most wants to filter**, and would silently pick up `Document`, which is
already scoped by specification and would then be scoped twice.

**`SystemLog` is mechanically out of reach.** It is not a `DbSet` on `ApplicationDbContext` — the
`ApplyConfigurationsFromAssembly` predicate at line 51 exists specifically to keep it out, and its
comment says so. It lives only on `LogDbContext`, which has no interceptors and no tenant story. So
"system logs stay unscoped" is not a product decision deferred; **it is not addressable by this
mechanism at all.** Any tenant scoping of logs is a separate design.

### The query inventory

**`AuditTrail` — 2 read sites. Both scoped, no exemptions.**

| Site | Classification |
|---|---|
| `AuditTrailsWithPaginationQuery.cs:46` | **scoped** — the grid |
| `ExportAuditTrailsQuery.cs:41` | **scoped** — the export |
| interceptor `GenerateAuditTrails` / `ResolveAuditTrails` | **not a query** — builds rows in memory from the `ChangeTracker`; reads nothing |

The audit interceptor **never reads** `AuditTrails`. Pass 5's transactional guarantee and Pass 24's
stamping are therefore untouched by a read filter — §B.4's boundary condition is satisfied by
construction, not by care.

**`PicklistSet` — 9 read sites.**

| Site | Classification |
|---|---|
| `GetAllPicklistSetsQuery.cs:38` | scoped |
| `PicklistSetsWithPaginationQuery.cs:44` | scoped |
| `PicklistSetsQueryByName.cs:43` | scoped |
| `ExportPicklistSetsQuery.cs:42` | scoped |
| `DeletePicklistSetCommand.cs:36` | scoped — and *should* be: it stops a cross-tenant delete |
| `AddEditPicklistSetCommand.cs:39` (`FindAsync`) | scoped — **and `FindAsync` does honour filters**, proven in §3 Q5 |
| `ImportPicklistSetsCommand.cs:79` (duplicate check) | **unclear → product question** — a filtered check lets tenant B create a value tenant A already has, which is *correct* if picklists are per-tenant and *a bug* if they are shared |
| `PicklistDataSourceService.cs:50, 70` | scoped — **but see the cache, below** |
| `ApplicationDbContextInitializer.cs:366` (`AnyAsync` seed guard) | **no ambient principal** — the critical path |

**The cache is part of the query.** Pass 26 left an explicit instruction on
`PicklistDataSourceService.Scope`, re-confirmed at the point it would change:

> *"**This becomes `CacheScope.PerTenant` in the same change that scopes the query**, and the two must
> move together — a scoped query behind a Global key would serve the first tenant's picklists to the
> rest."*

It is `CacheScope.Global` today. **A filter without this line is a cross-tenant leak through the
cache**, and the leak would not show in any query-level test.

### The no-principal paths

| Path | Ambient principal? | What a filter does |
|---|:--:|---|
| `ApplicationDbContextInitializer` (seeding, bootstrap) | **none** | sees exactly the null-tenant rows — see §3 Q2 |
| `AuditableEntityInterceptor` | reads `currentUser?.TenantId`, may be null | **writes only**; no read to filter |
| `LogDatabaseStartupCheck`, the migrators | different context / no model | unaffected |
| `Application.IntegrationTests` harness | a `UserContext` with **`TenantId` left null** (`Testing.cs:95`) | every test sees null-tenant rows only |
| Hangfire / `IHostedService` | **none exist** in this template | n/a |

---

## 3. §A.2 — The mechanism, decided by experiment

The brief names a trap to check "explicitly": a filter's expression is compiled into the model, so a
captured value would be wrong for every subsequent request. **I built a probe rather than reasoning
about it**, against the real EF (10.0.11), with two named filters, a cached model and a SQLite
database. Six questions, all answered:

```
Q1 per-instance re-evaluation (same cached model, different ambient tenant):
   tenant A -> [1]      Q4 SQL, ambient tenant null:
   tenant B -> [2]         WHERE "r"."DeletedAt" IS NULL AND "r"."TenantId" IS NULL

Q2 no ambient principal, as during seeding:
   tenant null -> [3]   (row 3 is the null-tenant row — NOT zero rows)

Q3 named filters compose, and drop selectively:
   A, ignore Tenant only -> [1,2,3]   (soft-delete still hides 4)
   A, ignore both        -> [1,2,3,4]

Q5 tenant A, Find(2) where row 2 belongs to tenant B -> null (filter APPLIED)

Q6 shared-plus-per-tenant predicate (TenantId == null || TenantId == current):
   tenant A     -> [1,3]
   tenant B     -> [2,3]
   no principal -> [3]
```

**Q1 — the trap is avoidable, and the escape is an instance member.** A filter referencing a field on
the context instance is re-evaluated per instance even though the model is cached once. A filter
capturing a *local* would indeed be baked in permanently. The difference is one line of style and the
whole correctness of the feature.

**And this codebase is unusually well-placed for it.** Two registrations that are usually the
problem are already right:

- `UserContextAccessor` is a **singleton over a `static AsyncLocal`** (`DependencyInjection.cs:610`),
  so the ambient tenant is readable from anywhere with no scope capture.
- `AddDbContextFactory<ApplicationDbContext>(…, ServiceLifetime.Scoped)` — **not pooled**. Pooling
  would have made constructor injection unsafe; it is absent.

**Q2 — the "seeding sees nothing" fear does not materialise, and I expected it to.** I predicted
`WHERE TenantId = @p` with a null parameter returning zero rows. EF's null-semantics rewriting
generates `IS NULL` instead, so a no-principal context sees **exactly the installation-level rows** —
which is precisely what seeding needs. The seed guard `PicklistSets.AnyAsync()` keeps working,
because the rows it seeded are themselves null-tenant. §B.2's choice is therefore available in its
better form: infrastructure paths **run in a context that satisfies the filter**, rather than needing
a bypass.

**Q3 — EF 10's named filters are the difference that makes this safe.** Confirmed present in the
installed assembly: `HasQueryFilter(string, LambdaExpression)` and
`IgnoreQueryFilters(IReadOnlyCollection<string>)`. This matters because of a latent bug found on the
way:

> **`ApplyGlobalFilters<ISoftDelete>(s => s.DeletedAt == null)` currently matches *zero* entity
> types.** Nothing in the template derives from `BaseAuditableSoftDeleteEntity`. It is a no-op today
> — and a collision tomorrow: the single-argument `HasQueryFilter` **replaces** any prior filter on
> an entity, so the moment someone adds a soft-deletable entity that also has a tenant, one of the
> two filters would vanish silently. Naming both filters removes the hazard permanently.

**Q5 — `FindAsync` honours query filters.** This is the one that would have been guessed wrong in
either direction. `AddEditPicklistSetCommand` uses it, and it is scoped automatically.

**The cross-tenant escape cannot live inside the filter.** `UserContext` carries `TenantId`,
`AllowedTenantIds` and `Roles` — **no permissions**. A filter expression cannot perform the
permission query, so "unless the principal holds the cross-tenant right" cannot be a term in the
predicate. It has to be an explicit, named exemption at a call site that has already checked — which
is exactly the shape §B.3 asks for.

### Recommendation on mechanism

**A hybrid, and specifically: named global filters on `AuditTrail` only, keyed on an explicit entity
list rather than on a marker interface.**

1. **Not the `IMayHaveTenant` marker.** It misses `AuditTrail` (the target) and catches `Document`
   (already scoped by specification — Pass 23 §4.4). Scoping `Document` twice is not additive safety;
   it is two rules that can disagree.
2. **Named filters throughout**, and rename the existing soft-delete filter in the same change, so
   the replacement hazard cannot bite.
3. **`Document` keeps its specification.** It works, it is proven, and `VisibleDocumentSpecification`
   expresses an owner-or-tenant rule a global filter cannot state.

---

## 4. §A.3 — Scope: `AuditTrail` yes, `PicklistSet` **no**

**`AuditTrail`: filter it.** Two read sites, both UI, both permission-gated; no interceptor read; no
cache; the exemption list is short and knowable. It is the clean case.

**`PicklistSet`: do not filter it in this pass — the evidence contradicts the expectation.** The
brief anticipated this pass taking both stamped entities. It should not, and the reason is Q1:

> The picklists are seeded **with a null tenant**, because seeding has no ambient principal. Under a
> strict filter, tenant A sees `[1]` and *not* `[3]` — so **every shipped picklist becomes invisible
> to every real user**, and Status, Unit and Brand dropdowns come up empty across the application.

That is not a bug in the filter; it is the filter correctly implementing a product decision nobody
has made. There are exactly two coherent answers, and they are different products:

| | Predicate | Consequence |
|---|---|---|
| **Shared reference data with per-tenant additions** | `TenantId == null \|\| TenantId == current` (Q6) | Shipped values stay visible; a tenant may add its own. The import duplicate-check at `:79` becomes per-tenant, which is then correct. **Backwards-compatible.** |
| **Strictly per-tenant** | `TenantId == current` | Requires seeding a copy of every picklist per tenant, and a new-tenant provisioning path that does the same. **Nothing in the template does this today.** |

**My recommendation: the first — but as its own decision, not as a rider on this one.** A filter
makes the answer permanent in a way the column does not: a stamped column is inert and reversible,
whereas once queries depend on a filter, changing the rule changes what every existing row means.
Pass 24 stamped without deciding, which was right; deciding by implication here would be wrong.

**Also required if picklists are ever filtered:** `PicklistDataSourceService.Scope` moves
`Global → PerTenant` in the *same* change. It is not optional and not separable.

**Everything else stays unscoped, and one of them for a new reason:** roles (`ApplicationRole` has no
tenant), security settings (one row per installation, by design), presence and chat (broadcast), and
**system logs — now known to be mechanically out of reach, not merely deferred** (§2).

---

## 5. What I recommend you ratify

1. **Mechanism:** named global query filters on `ApplicationDbContext`, driven by an explicit entity
   list, reading an instance member fed by the ambient `IUserContextAccessor`. Name the existing
   soft-delete filter in the same change.
2. **Scope now:** **`AuditTrail` only.**
3. **Exemptions:** the cross-tenant escape as a named permission checked at the call site, then an
   explicit `IgnoreQueryFilters(["Tenant"])` carrying a stated reason — following Pass 27's
   `Users.ViewAllTenants` pattern. Infrastructure paths need **no** exemption: they satisfy the
   filter naturally (Q2).
4. **Picklists:** deferred to a product decision, with "shared + per-tenant" recommended and the
   cache-scope coupling recorded.
5. **Also fix, as a by-product:** the `ApplyGlobalFilters<ISoftDelete>` no-op/collision (§3).

**Open question for you:** should an audit trail be filtered at all, or is an audit trail's value
precisely that it is installation-wide? Filtering it means a tenant-A administrator can no longer see
that a cross-tenant administrator acted on their data. I lean to filtering it with the cross-tenant
escape, because Pass 23 §7.2 framed the audit trail as customer-visible — but if it is an
*operator's* record rather than a *customer's*, the right answer is to leave it alone and this pass
has no §B.

---

## 6. §C — the harness fix is a dependency, not an errand

`Testing.RunAsAdministratorAsync` resolves `RoleManager<IdentityRole>` while the application
registers `ApplicationRole`; `GetService` returns null and the next line throws
`NullReferenceException`. Confirmed present at `Testing.cs:213–224`, and confirmed *unreached* —
`RunAsDefaultUserAsync` passes an empty roles array, so nothing dereferences the null. **Catalogue
defect #15, the first of that catalogue's harness findings confirmed present.**

It blocks §D directly: §D.5 wants a live run that "bootstraps an administrator", and §D.2 needs
role-bearing principals. I will fix it first when §B is ratified.

**A second harness fact found while reading it, which §B must account for:** the mocked
`IUserContextAccessor` builds its `UserContext` **without a `TenantId`** (`Testing.cs:95`), so every
`Application.IntegrationTests` case runs with a null ambient tenant. Under a filter they would see
installation-level rows only. That is survivable — the rows those tests create are themselves
null-tenant — but it means the harness cannot demonstrate isolation without being given a tenant, and
§D.2 will need that.

---

## 7. Scratch probe disclosure — at the gate

> Superseded by §14, which lists every probe across the whole pass. Kept because it is what the
> gate was reviewed against.

| Probe | Purpose | Disposed |
|---|---|---|
| `tests/Application.UnitTests/ScratchQueryFilterProbe.cs` | the six mechanism questions in §3, against real EF 10.0.11 + SQLite | **deleted from the repo** |

Written inside the test tree because it needed the real EF version and provider rather than a
reasoned argument. No source file was modified; `git status` is clean and the full build succeeds.

---

## 8. What remains unscoped — as assessed at the gate

> Superseded by §15. Audit trails moved out of this list when §A was ratified.

**Roles, security settings, presence and chat** — unchanged, and unchanged in status.

**System logs** — unchanged, but **their status has changed**: previously "no — stamped, not
filtered", now known to be *unreachable* by a filter on `ApplicationDbContext` (§2). The README's
Tenancy table should say so whenever §B lands.

**Picklists** — still stamped, still unfiltered, and now with the product question stated precisely
enough to answer (§4).

No README change yet: nothing has become scoped.

---

## 9. Anomalies found during the investigation

> A4-A7, found during implementation, are in §16.

**A1 — `ApplyGlobalFilters<ISoftDelete>` matches no entity types.** Nothing derives from
`BaseAuditableSoftDeleteEntity`. The call is a no-op today and a silent-replacement hazard the moment
a second `ApplyGlobalFilters` call is added for tenants. Recommended for repair inside §B, where the
naming fixes it as a side effect rather than as a separate change.

**A2 — `AuditTrail` is stamped without the marker that means "stamped".** Line 383 constructs the row
with a `TenantId`; line 328 stamps marker-bearing entities. Both are correct, but the two mechanisms
mean the marker is not a reliable index of what carries a tenant — which is exactly how a
marker-driven filter would have missed the entity this pass targets. Recorded because the marker
*looks* authoritative.

**A3 — I predicted Q2 wrongly.** I expected a null tenant parameter to produce `TenantId = @p` and
return zero rows during seeding, and said so as the likely blocking hazard. EF's null-semantics
rewriting produces `IS NULL` and the seed path is fine. Recorded because the erroneous prediction is
the standard folklore about this pattern, and the report should not leave it standing.

---

## 10. Ratified — and what was built

§A was ratified as recommended: named filters driven by an explicit entity list reading an instance
member; **`AuditTrail` only**; the escape as `AuditTrails.ViewAllTenants` checked at the call site;
the soft-delete filter named in the same change; **§C first**.

### 10.1 §C — the harness fix (catalogue defect #15)

One word, as advertised, plus one that was not asked for and is the point:

```diff
- var roleManager = scope.ServiceProvider.GetService<RoleManager<IdentityRole>>();
+ var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
```

`ApplicationRole` fixes the defect. **`GetRequiredService` is why it cannot recur in this shape**:
`GetService` returning null and the *next* line throwing is what made a missing registration look
like a mystery `NullReferenceException` rather than "this service is not registered". The comment at
the site says so.

**Three tests now call the helper**, in `HarnessPrincipalTests`:

| Test | What it holds down |
|---|---|
| `RunAsAdministratorAsync_Succeeds` | it returns at all — RED: `NullReferenceException` |
| `RunAsAdministratorAsync_ActuallyCreatesTheRoleAndAssignsIt` | the role exists **and** the user is in it — "did not throw" is not the requirement |
| `AUserAskedForNoRoles_GetsNone` | the path that *did* work, so a future edit to the shared body cannot fix one by breaking the other |

**Red before:** all three failed, the first two with the exact `NullReferenceException`.

`Testing.CreateScope()` was added so those tests can reach `UserManager`/`RoleManager`. Pass 28
reached the private scope factory by reflection rather than modify a shared harness file for a
scratch probe; a permanent test earns a real seam.

### 10.2 §B — the mechanism

**The filter, in `ApplicationDbContext.OnModelCreating`:**

```csharp
builder.ApplyGlobalFilters<ISoftDelete>(QueryFilters.SoftDelete, s => s.DeletedAt == null);

builder.Entity<AuditTrail>().HasQueryFilter(
    QueryFilters.Tenant,
    (AuditTrail a) => a.TenantId == CurrentTenantId);
```

- **`CurrentTenantId` is a member on the context**, not a captured local — the whole correctness of
  the feature, and asserted by `TheFilterIsRecomputedPerContext_NotBakedIntoTheCachedModel`.
- **The accessor is an optional constructor parameter.** Seventeen sites construct this context with
  options alone, including interceptor suites §D.7 requires byte-unmodified. There is deliberately
  **no** special case making an absent accessor unfiltered: absent means "no ambient principal", the
  same as production, because a test path and a production path that disagree about a security
  boundary is how the boundary stops being one.
- **`QueryFilters` lives in Application**, not Infrastructure. The filters are registered in
  Infrastructure but the exemption is written in an Application handler, and Application must not
  reference Infrastructure — see A5.

**The exemption, in `AuditTrailTenantScope.VisibleAsync`** — one definition, both consumers:

```csharp
if (!mayCrossTenants) return source;
return source.IgnoreQueryFilters([QueryFilters.Tenant]);
```

The stated reason at the site, quoted in full because §B.1 asks for it:

> *EXEMPTION, and the reason it is allowed: this principal holds
> `Permissions.AuditTrails.ViewAllTenants`, an administrator right whose entire purpose is reading
> audit history across tenants — the auditor and support-engineer case.*
>
> *By NAME, and the name matters twice over. It drops only the tenant filter, so soft-delete (and
> anything else added later) keeps applying — the bare `IgnoreQueryFilters()` would drop every filter
> on the entity and quietly widen far more than intended. And it is a constant rather than a literal,
> so it cannot drift from the name the filter was registered under; a drifted name does not throw, it
> silently drops nothing and leaves the holder wondering why their right does nothing.*

**The exemption inventory, as built:**

| Path | Classification | Exemption |
|---|---|---|
| `AuditTrailsWithPaginationQuery` (grid) | scoped, exemptible | `AuditTrailTenantScope`, reason above |
| `ExportAuditTrailsQuery` (export) | scoped, exemptible | same rule, same reason |
| `AuditableEntityInterceptor` | writes only | **none needed** — it never reads |
| Seeding, provisioning, bootstrap | no ambient principal | **none needed** — satisfies the filter naturally (§3 Q2) |
| Migrators, `LogDatabaseStartupCheck` | different context | n/a |
| `Application.IntegrationTests` harness | tenant-null principal | **none needed** — its rows are null-tenant |

**Two exemption sites in the whole application, and no infrastructure exemptions at all.** That is
§B.2 answered in its better form: those paths run in a context that satisfies the filter rather than
bypassing it.

**A1 fixed as a by-product.** `ApplyGlobalFilters` now takes a name. The soft-delete call still
matches zero entity types — nothing derives from `BaseAuditableSoftDeleteEntity` — and is kept and
named rather than deleted, so it composes correctly the day a generated project adds one. Before this
change, adding a second filter would have silently discarded one of the two.

---

## 11. §D — Verification

### 11.1 Red before, green after

**§B: 10 of 11 red → 11 green.** Replacing the filter with a comment turned red every case except
`TheCrossTenantRightLiftsTheFilter` — which is honest: with no filter everything is visible anyway, so
that test *cannot* distinguish the two states. It is the control that says the suite is not
tautological.

**The exemption, separately: 1 red → green.** Removing the permission check turned exactly
`WithoutTheRightTheScopeChangesNothing` red — the guard against an ungated exemption — while
`AMissingUserIdFailsClosed` stayed green, correctly, because it returns before the check is reached.

**§C: 3 red → 3 green** (§10.1).

**Narrowed-not-emptied.** `NarrowedNotEmptied_BothOfTenantAsRowsSurvive`, with **two** tenant-A rows,
so it catches a filter that narrowed to one and not merely one that emptied.

### 11.2 The isolation and no-principal cases

| Case | Assertion |
|---|---|
| Isolation | tenant A sees `[1,2]`, tenant B sees `[3]` |
| Narrowed, not emptied | tenant A sees **both** its rows, and not B's |
| **Scoped by default** | `CountAsync()` and an unrelated `Where` — queries that never heard of tenancy — are still bounded |
| `FindAsync` | scoped too; the one people assume goes around filters |
| Model-cache trap | A → B → A through one cached model, interleaved |
| No ambient principal | installation rows only: not everything, not nothing |
| Cross-tenant right | every tenant's rows **and** the installation's |
| Without the right | passing through the scope changes nothing |
| Missing user id | fails closed, though the mock would grant if consulted |

`AQueryThatNeverHeardOfTenancyIsStillScoped` is the one that distinguishes this pass from 27 and 28.
Every other isolation assertion would pass under an opt-in scheme; that one would not.

### 11.3 The live run — a fresh database

**PostgreSQL, fresh database `GXP29Probe`, application booted end to end:**

```
[INF] Process-wide state: database provider postgresql; timestamptz in force: yes
[INF] The log database GXP29Probe_Logs does not exist on this server; creating it now.
[INF] Provisioned the Admin role.
[INF] Granted 53 permission(s) to the Admin role: ... Permissions.AuditTrails.ViewAllTenants ...
[INF] Provisioned the Basic role.
[INF] Provisioning the default organisation...
[WRN] ================ ADMINISTRATOR ACCOUNT CREATED ================
[INF] Seeding a second organisation...
[INF] Seeding picklist values...
```

Then `curl http://localhost:41977/` → **HTTP 302** (redirect to login). **Migrated, provisioned,
bootstrapped an administrator, seeded, and served** — with the filter in place. This is the check
§D.5 exists for: a filter that breaks seeding is found on the first fresh database, and it was looked
for here instead.

**And the filter's behaviour on the real provider**, which SQLite cannot answer because the SQL
differs:

```
TENANTS: .../Default, .../Europe
AUDIT ROWS BY TENANT (unfiltered): <null>=13
NO PRINCIPAL sees: 13            SQL: ... WHERE a."TenantId" IS NULL
TENANT <Default> sees: 0
TENANT <Europe>  sees: 0
UNKNOWN TENANT   sees: 0
```

Npgsql produces `IS NULL`, as SQLite did — the null-semantics rewriting §3 Q2 depends on is not
provider-specific.

**A consequence worth stating plainly, because it will look like a bug.** On a freshly seeded
installation **a tenant-scoped principal sees an empty audit trail**, as the `sees: 0` lines show.
That is correct: everything written during seeding and provisioning belongs to the installation, not
to a tenant, because those paths have no ambient principal. Rows appear once real users act inside a
tenant. The shipped administrator is unaffected — the Admin role is granted
`AuditTrails.ViewAllTenants` and sees all 13.

**SQL Server** is covered by `Application.IntegrationTests`, which runs against a real SQL Server
instance: 12 green, including the three new harness tests.

### 11.4 Boundary suites — green and byte-unmodified

`git diff --quiet HEAD` clean for **all eleven**:

`TransactionalAuditTests` · `InterceptorOrderingTests` · `TenantStampingTests` (Pass 5 / Pass 24) ·
`DataSourceScopeTests` (26) · `TenantVisibilityTests`, `UserTenantScopeComponentTests`,
`SuperiorAutocompleteScopeComponentTests` (27) · `UserVisibilityTests`, `SuperiorBoundComponentTests`,
`SwitchableTenantsTests`, `TenantSelectorComponentTests` (28)

**`InterceptorOrderingTests:114` is the one that mattered.** It is the only boundary assertion that
reads `AuditTrails` through an **EF query** rather than raw SQL — `(await context.AuditTrails
.CountAsync()).Should().BeGreaterThan(0)` — so it was the one at risk from a new filter. It passes
unmodified, because the context it builds has no accessor, its rows are stamped null-tenant, and a
no-principal context sees exactly those. The other two interceptor suites read via
`SELECT ... FROM AuditTrails` on a separate connection, which bypasses query filters entirely.

**No test was modified to accommodate the filter.** The only pre-existing test file changed is
`Testing.cs`, and that is §C's subject, not an accommodation.

### 11.5 Counts

| Suite | Start | After | Delta |
|---|---:|---:|---:|
| `Infrastructure.UnitTests` | 217 | 217 | 0 |
| `Application.IntegrationTests` | 9 | **12** | +3 |
| `Application.UnitTests` | 418 (+12 skipped) | **429** (+12 skipped) | +11 |
| `Server.UI.IntegrationTests` | 180 | 180 | 0 |
| **Total passed** | **824** | **838** | **+14** |
| Skipped / Failed | 12 / 0 | 12 / 0 | 0 |

**+14 is exactly the new tests:** `HarnessPrincipalTests` 3 (§C), `AuditTrailTenantFilterTests` 11
(§B). No test deleted, renamed or weakened.

### 11.6 Warnings

**10 distinct locations — the same 10.** An intermediate build had **13**: three `CS8632` from using
`?` annotations inside `ApplicationDbContext`'s `#nullable disable` region. Rather than drop the
annotations — the nullability *is* the design here, a null accessor and a null tenant being distinct
meaningful states — the tenancy members opt back in with a local `#nullable enable`/`restore` pair.

### 11.7 Generation probe

```
dotnet pack → install → dotnet new gxblazor -n P29 → build: 0 Error(s)
             → dotnet test: 838 passed, 12 skipped, 0 failed → uninstall
```

Identical to source, suite for suite.

---

## 12. README

Two rows changed status and two prose claims were corrected:

- **Audit trails**: now *"**Yes — and by default**"*, naming the mechanism and the single right that
  lifts it.
- **System logs**: still no, but for a **different reason** — not a deferred switch but unreachable,
  `SystemLog` not being on `ApplicationDbContext` at all.
- The "treat everything below the **Users** row as installation-wide" warning now says **Audit
  trails** row, and no longer claims an audit trail is readable in full by any holder of the view
  permission — which this pass made false.
- A new paragraph states the property that distinguishes this pass: *"the first surface scoped by
  default rather than by remembering"*, and why `QueryFilters.Tenant` is a constant.
- The feature-list bullet now reads "Documents, the Users area and audit trails".

---

## 13. File map and diffstat

**Modified — source (6)**

| File | Why |
|---|---|
| `Infrastructure/Persistence/ApplicationDbContext.cs` | the accessor, `CurrentTenantId`, both named filters |
| `Infrastructure/Persistence/Extensions/ModelBuilderExtensions.cs` | `ApplyGlobalFilters` takes a name (fixes A1) |
| `Application/Features/AuditTrails/Security/AuditTrailsPermissions.cs` | `ViewAllTenants` + the access-rights property |
| `Application/Common/Security/AdministratorPermissionRegistry.cs` | the grant, with its reason |
| `Application/Features/AuditTrails/Queries/PaginationQuery/…` and `…/Export/…` | the two exemption sites |
| `README.md` | §12 |

**Modified — tests (1)** · `Testing.cs` — §C's fix plus `CreateScope()`.

**New (4)**

| File | Lines | Tests |
|---|---:|---:|
| `Application/Common/Constants/QueryFilters.cs` | 38 | — |
| `Application/Features/AuditTrails/AuditTrailTenantScope.cs` | 78 | — |
| `tests/Application.IntegrationTests/HarnessPrincipalTests.cs` | 83 | 3 |
| `tests/Application.UnitTests/Features/AuditTrails/AuditTrailTenantFilterTests.cs` | 240 | 11 |

**Diffstat:** `8 files changed, 196 insertions(+), 15 deletions(-)` plus 4 new files. **No migration
was touched** — this pass changes no schema; `AuditTrail.TenantId` already existed from Pass 24.

### Edit fidelity

- **Line endings uniform LF**, matching the repo, on every touched file — verified by byte count.
  Six files briefly carried stray CRs from my inserts and were normalised before any build was taken
  as evidence. **My Pass 28 report's claim that touched files were "wholly CRLF" was wrong**: it
  rested on `grep -c` for a carriage return, which is unreliable in this Git Bash. The byte-level
  check (`tr -cd '\r' | wc -c`) is the one used here and from now on.
- **BOMs unchanged** — each file matches its state at HEAD, verified with `od`.
- **No scaffolding left** — a search for `PRE-PASS-29` across `src/` and `tests/` returns nothing.

---

## 14. Scratch probe disclosure

| Probe | Purpose | Disposed |
|---|---|---|
| `tests/Application.UnitTests/ScratchQueryFilterProbe.cs` | §3's six mechanism questions | deleted |
| `scratchpad/p29/` | backups of the three changed sources for the red captures | deleted |
| `tests/Application.UnitTests/ScratchLivePostgresFilterProbe.cs` | §11.3's live filter behaviour on Npgsql | deleted |
| `tests/Application.UnitTests/ScratchDropProbeDbs.cs` | dropped the two probe databases | deleted |
| `C:\gxp29\P29` | the generated project | deleted, template uninstalled |

**Databases: two were created, and both were dropped.** `GXP29Probe` and `GXP29Probe_Logs` on the
local PostgreSQL instance, created by the application's own migration on first boot — §D.5 requires a
fresh database and there is no way to satisfy it without one. This is a departure from previous
passes, which recorded that no database was created or dropped, so it is stated plainly. They were
uniquely named so nothing existing could be touched, and the drop script refuses any name not
starting `GXP29Probe`. Both confirmed dropped; the probe web application was stopped. The root
`.nupkg` was rebuilt and is gitignored.

---

## 15. What remains unscoped

| Surface | Status | Changed this pass? |
|---|---|---|
| **Audit trails** | **now scoped, by default** | **yes** |
| System logs | unscoped — and **unreachable** by this mechanism | **status clarified**, behaviour unchanged |
| Picklists | stamped, unfiltered; product question stated in §4 | no |
| Roles | unscoped — `ApplicationRole` has no tenant | no |
| Security settings | unscoped — one row per installation, by design | no |
| Presence, chat, login notifications | unscoped — broadcast | no |

**Picklists remain the open decision**, with "shared reference data plus per-tenant additions"
recommended (§4) and the `PicklistDataSourceService.Scope` coupling recorded — `Global → PerTenant`
in the *same* change, never separately.

---

## 16. Anomalies

**A4 — a freshly seeded installation shows a tenant-scoped principal an empty audit trail.** Correct,
and it will still be reported as a bug. Everything written during seeding and provisioning is
installation-level because those paths have no ambient principal, so no tenant matches it. Recorded
in §11.3 with the live evidence rather than left to be discovered.

**A5 — the layering caught a design error before the compiler did.** The exemption constant was first
written beside the filter in Infrastructure, where the Application-layer handler that needs it cannot
see it. The rule that Application must not reference Infrastructure is what forced `QueryFilters`
into Application — which is also where it belongs on the merits. Recorded because the first instinct
was wrong and the architecture, not a test, is what said so.

**A6 — `Application.IntegrationTests` cannot yet demonstrate isolation.** Its mocked
`IUserContextAccessor` builds a `UserContext` with **no `TenantId`** (`Testing.cs:95`), so every case
runs tenant-null and sees installation rows. That is survivable — those tests create null-tenant rows,
and none of them broke. But the harness cannot express "tenant A cannot see tenant B" until the mock
is given a tenant, which is why §D.2's isolation evidence lives in `Application.UnitTests` against the
real `ApplicationDbContext` instead. Worth fixing whenever a pass needs tenant-bearing integration
cases.

**A7 — the still-listening probe application briefly looked like a passing test.** The first live run
kept serving after its command reported complete; a second launch then failed to bind, and the
`HTTP 302` I had just recorded had come from the *first* instance. Harmless — the 302 is still genuine
evidence that the application serves — but the process was stopped explicitly rather than left, and
the sequence is recorded because "the port answered" is not by itself evidence that *this* run
answered.
