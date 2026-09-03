# Pass 26 — Stage 3: The Cache Layer, and the Permission Cleanup

**Nature:** editing pass — §A the isolation prerequisite, §B the ratified permission cleanup.
**No git actions.** **Date:** 2026-09-03.

> **§A makes scoping safe; it does not scope anything.** No query gained a filter in this pass. The
> proof is that every pre-existing test passed unmodified — §5.1.

---

## 1. Start state

| | |
|---|---|
| HEAD | `9630626656da7d18ab622767fc83fd6471a6c37a` — *"pass25"*, carrying Passes 23–25 |
| Working tree | **clean** |
| Build | **0 errors** |
| Warning locations | **10 distinct** — matches |
| Tests | **758 passed, 12 skipped, 0 failed** — matches |
| Spot-check `PrimaryTenantRule.cs` | present |
| Spot-check `AuditTrail.TenantId` | present (`Domain/Entities/AuditTrail.cs:40`) |

The precondition is met — Passes 24 and 25 were committed together as `pass25`. The two scratch
snapshots Pass 25 retained against that risk were deleted at the start of this pass.

---

## 2. §A.1 — The investigation

### 2.1 The class as it was

`DataSourceServiceBase<T>` took a **constant** `cacheKey` in its constructor and used it, unchanged,
for three things: the `GetOrSetAsync` read/write in `LoadAndCacheAsync`, the `Remove` in
`RefreshAsync`, and nothing else. `Items` — a `List<T>` field exposed as `DataSource` — held the
loaded list; `InitializeAsync` filled it **only when it was empty**; `SearchAsync` queried it in
memory.

**What invalidated an entry:** `RefreshAsync` only, called from `PicklistSetChangedEventHandler`,
`AddEditTenantCommand`, `Roles.razor:253` and `Users.razor` (×4).

### 2.2 The full list of subclasses and consumers

Four subclasses — Pass 23's list, confirmed complete by scanning for the base type:

| Service | Declared key | Backs |
|---|---|---|
| `UserDataSourceService` | `"ALL-ApplicationUserDto"` | `PickSuperiorAutocomplete`, `PickUserAutocomplete`, `Users.razor` |
| `TenantDataSourceService` | `TenantCacheKey.TenantsCacheKey` | `TenantSelect.razor`, `Users.razor` |
| `RoleDataSourceService` | `"ALL-ApplicationRoleDto"` | `Roles.razor`, `Users.razor` |
| `PicklistDataSourceService` | `PicklistSetCacheKey.PicklistCacheKey` | `PicklistAutocomplete` |

Six consumer files in total. Note `PickUserAutocomplete` remains dead (Pass 23 A2) — untouched here.

### 2.3 Key composition: reused, not reimplemented

**The mechanism already existed and is now used by three callers instead of two.**
`CacheScopeKey` is a static with `Compose(declaredKey, scope, user)` and
`RequiresUserContext(scope)`, and its own remarks state why it is a static:

> *"Kept as a static so the composition is testable on its own and so both caching behaviours compose
> identically — a scoped read and a scoped write that disagreed would be worse than no scoping."*

`DataSourceServiceBase` now calls it. Writing a second composition would have been the third instance
of the defect this template has already met twice (the Documents visibility rule, the sink column
sets): **a rule with two copies is a rule with one copy that is out of date.**

### 2.4 The instance field — the sharper half, as the brief suspected

**The field itself is safe. What it does with the key is not, and that was the real problem.**

These services are registered **`Scoped`** (`AddDataSourceServices`), which in Blazor Server means
one instance per circuit. So `Items` belongs to a single principal and is never shared between
users. The leak was entirely through the process-wide FusionCache entry.

**But `Items` outlives a change of key.** `InitializeAsync` loaded only `if (Items.Count == 0)`, so a
principal whose effective key moved *mid-circuit* would go on being served the list from before —
and a `PerTenant` key moves exactly when the user switches tenant. Nothing refreshes these services
on a switch: `TenantSwitchService` refreshes `IUserProfileState` and evicts the user context, and
never touches the datasources (verified — no datasource `RefreshAsync` call anywhere in it).

**So a cache key alone would have produced a scope that was decorative for the life of a circuit.**
The fix is a `_loadedKey` field recording the key `Items` was loaded under, and an
`InitializeAsync` that reloads when it moves. `WhenThePrincipalsTenantChanges_TheLoadedListFollowsIt`
is the test, and it is one of the four that go red without the fix.

---

## 3. §A.2 — The implementation

`Scope` is **abstract**, not virtual with a default: a scope is a claim about who may see the same
rows, and the wrong claim is a cross-principal leak that no single-principal test will report. Every
subclass has to answer.

| Service | Scope | Why |
|---|---|---|
| `UserDataSourceService` | `PerTenant` | the user list is tenant-visible data |
| `TenantDataSourceService` | `PerUser` | which tenants a principal may see differs by principal |
| `RoleDataSourceService` | `Global` | roles are installation-wide |
| `PicklistDataSourceService` | `Global` | shared reference data, today |

### 3.1 The two `Global` claims, quoted

**`RoleDataSourceService`:**

> *"This is a claim about the data, not a default. `ApplicationRole` carries no TenantId at all, and
> `ApplicationRoleConfiguration` puts a unique index on `NormalizedName` across the whole
> installation — so two tenants cannot even hold roles of the same name. There is exactly one role
> list and every principal sees it. … It is also the open product question of Pass 23 §2.5. If roles
> are ever made per-tenant, this line is one of the things that must change with them — and it will
> not fail to compile, which is why the reason is written down here."*

**`PicklistDataSourceService`:**

> *"A claim, and the one most likely to stop being true. … Pass 24 gave `PicklistSet` a `TenantId`
> and it is stamped on insert, but stamped is not scoped: no query reads it. This becomes
> `PerTenant` in the same change that scopes the query, and the two must move together — a scoped
> query behind a Global key would serve the first tenant's picklists to the rest."*

### 3.2 `PerTenant` versus `PerUserAndTenant`, and `PerUser` versus `PerTenant`

Both non-Global choices are deliberate and are argued at the site:

- **Users is `PerTenant`, not `PerUserAndTenant`** — the list is *who exists in a tenant*, the same
  answer for everyone in it. Partitioning per user as well would multiply entries by the user count
  for a list that backs an autocomplete and is read constantly.
- **Tenants is `PerUser`, not `PerTenant`** — and this is the distinction that matters. Two
  administrators sitting in the *same* tenant can legitimately have different answers, because
  membership is per-user (`TenantUsers`) and a `SwitchToAnyTenant` holder sees more than the
  colleague beside them. Keying by tenant would hand one of them the other's list.

### 3.3 The null-context posture: fail closed, matching the established one

A non-`Global` scope with no ambient principal **bypasses the cache entirely** — the list is loaded
so the component still renders, and nothing is read from or written to the shared cache. This is
`FusionCacheBehaviour`'s posture exactly, and for its stated reason: falling back to the unscoped key
would put one principal's rows under a key every principal reads, which is the precise leak scopes
exist to prevent.

`CacheScopeKey.Compose` makes the bypass mandatory rather than optional — it **throws** for a scoped
key with no principal, so a caller that forgets to check gets an exception rather than an unscoped
key. That guard is asserted by `ComposeRefusesAScopedKeyWithNoPrincipal`.

### 3.4 `RefreshAsync` now evicts one partition

Under the constant key, one administrator's refresh cleared the single shared entry and made every
other circuit in the installation reload. It now removes only the calling principal's entry.

---

## 4. §A.3 — Evidence

**Red before / green after**, captured by restoring the constant key and the old
`InitializeAsync` condition, then restoring the fix from a backup taken first
(`grep -rn "PRE-PASS-26" src/` → nothing):

**4 red / 6 green → 10 green.**

| # | Check | Test | Reverted-state failure |
|---|---|---|---|
| A.3.1 | different tenants get different **keys** | `PerTenant_TwoTenantsGetDifferentKeys` | *(green both — it asserts on `CacheScopeKey` directly)* |
| A.3.1 | …and different **values** | `PerTenant_TwoTenantsAreServedDifferentValues` | **RED** — *"Assert.NotEqual() Failure: Strings are equal"* |
| A.3.2 | same tenant **does** share | `PerTenant_TwoPrincipalsInTheSameTenantShareOneEntry` | green both — the control |
| A.3.3 | `Global` serves one entry to everyone | `Global_ServesOneEntryToEveryPrincipal` | green both — the control |
| A.3.4 | refresh evicts only this principal | `Refresh_EvictsOnlyThisPrincipalsEntry` | **RED** — *"Assert.Equal() Failure: Strings differ"* |
| A.3.5 | the loaded list follows the key | `WhenThePrincipalsTenantChanges_TheLoadedListFollowsIt` | **RED** — *"Assert.NotEqual() Failure: Strings are equal"* |
| A.3.5 | …but does not reload when nothing moved | `WhenNothingChanges_TheListIsNotReloaded` | green both — the control |
| A.3.3 | fail closed with no principal | `AScopedSourceWithNoPrincipal_LoadsButDoesNotCache` | **RED** |
| — | the composition guard | `ComposeRefusesAScopedKeyWithNoPrincipal` | green both |
| — | four services, each declaring a scope | `EveryDatasourceDeclaresAScope` | green both |

**The six that stayed green in both states are the evidence, not the tally.** Same-tenant sharing and
`Global` sharing are what distinguish a working partition from one that simply stopped caching —
a `DataSourceServiceBase` that reloaded on every call would satisfy all four red tests and be
useless. `WhenNothingChanges_TheListIsNotReloaded` is the specific guard against that, and these
lists back autocompletes initialised on every render.

Two assertions go beyond behaviour to the keys themselves: the composed keys are compared directly,
and `PerTenant_TwoTenantsAreServedDifferentValues` asserts that **nothing was written under the bare
declared key** — the key every circuit used to read and write.

### 4.1 §A.3.6 — nothing user-visible changed

**Every pre-existing test passed unmodified after §A**, before any test was added: 758 → 758, with
no file in `tests/` touched at that point. That is the proof this is a capability change and not a
behaviour change. No query gained a filter; a scope changes which key an entry is stored under,
never which rows are loaded.

---

## 5. §B — The permission cleanup, all eleven

### 5.1 Wire (2)

- **`AuditTrails.Search`** — the page had the keyword box and audit-type filter and had already
  loaded `AuditTrailsAccessRights` without ever reading it. One `@if`.
- **`Logs.Search`** — identical shape: `LogsAccessRights` was loaded and only `Purge` was read.

**The list-view selector beside each is deliberately not gated.** Choosing "My change histories" or a
date window is part of *viewing* the trail, which is what `AuditTrails.View` grants; only searching
is behind `Search`. That distinction is why the component test keys on the audit-type placeholder
rather than counting selects — the page renders two selects and only one is gated.

### 5.2 Delete (3)

| Constant | What went with it |
|---|---|
| `Documents.Export` | the constant, `DocumentsAccessRights.Export`, the registry grant, and the 3-byte `ExportDocumentsQuery.cs` |
| `Documents.Import` | the same, plus the 3-byte `GetAllDocumentsQuery.cs` |
| `NavigationMenu.View` | the constant, its containing `NavigationMenu` class, and the registry grant |

Both now-empty directories (`Queries/Export/`, `Queries/GetAll/`) were removed too.

**The `AccessRights` half is not optional.** `PermissionService` builds the claim string from the
**property name** — `"Permissions.Documents." + prop.Name` — so a property left behind would go on
manufacturing a claim string that no constant declares and no role can be granted. That is the exact
pairing `LogsAccessRights` already documents for its own missing `Export`, and this follows the
precedent Pass 11B/11C set.

Each deletion left a comment where the constant was, saying what was removed and why, so the next
reader does not rediscover the absence as a gap.

### 5.3 Knowingly exclude (6)

Moved from `Granted` to `Excluded` with two new reason constants, in the `EmailTemplates.*` shape:

**`ExcludedRoleSurface`** — `Roles.ManageUsersInRole`, `ViewUsersInRole`, `ManageClaimsInRole`,
`ViewClaimsInRole`, `ViewPermissions`:

> *"There is no users-in-role or claims-in-role administration in this template, and no read-only
> permission viewer: the Roles page's one permission feature is the dialog `Roles.ManagePermissions`
> already gates, and viewing happens inside it. Granting these would advertise a role-administration
> surface the application does not have."*

**`ExcludedDashboard`** — `Dashboards.View`, with the non-obvious part stated as the brief required:

> *"The dashboard is routed at `@page "/"` — it is the landing page every authenticated user arrives
> on. Gating it would strand a principal without this right on a 403 at sign-in, which is a worse
> failure than the right doing nothing. The name is reserved for when the dashboard moves off the
> root route; at that point it becomes a one-line page attribute and this entry moves back to
> Granted."*

### 5.4 The registry's divergence assertion still holds

It is what makes this safe: a constant in neither list fails startup and the test run, a constant in
both fails, and a listed name that is no longer a declared constant fails too. All three directions
still pass — which is how the three deletions and six exclusions were proved consistent rather than
merely intended.

**One existing test moved, and it had to.**
`AdministratorPermissionRegistryTests.TheExcludedEmailTemplatePermissions_AreTheOnesWhosePageDoesNotExist`
asserted the exclusion list was *exactly* the four email-template rights — an expectation that
encoded the state §B changes. It is renamed `TheExcludedPermissions_AreTheOnesWhoseSurfaceDoesNotExist`
and now names all ten exhaustively, grouped with a comment per group. **This is a re-statement, not
a relaxation:** the assertion is still exhaustive equality, so an exclusion still cannot be added
without a reviewer seeing it. A new test, `EveryExclusionCarriesAReason`, additionally requires each
entry's reason to be non-trivial — an exclusion without a reason is indistinguishable from a
permission somebody forgot to grant.

### 5.5 The recount, by the two-spelling method

Recomputed exactly as Pass 25 §6.1 established — a constant counts as enforced if it is named
directly **or** if the section's `*AccessRights` property of that name is read in a Razor `@if`:

| | Before | After |
|---|---:|---:|
| Declared constants | 64 | **61** |
| Inert | 15 | **10** |
| Inert **and** knowingly excluded | 4 | **10** |
| Inert and **not** accounted for | 11 | **0** |

**The target is met.** The ten that remain inert are exactly the ten now in `Excluded`:

```
Dashboards.View
EmailTemplates.View / Create / Edit / Delete
Roles.ManageUsersInRole / ViewUsersInRole / ManageClaimsInRole / ViewClaimsInRole / ViewPermissions
```

**Every declared permission now either enforces something or says in the registry why it does not.**

*Why that is stated as a measurement rather than encoded as a test:* deciding "inert" requires
scanning `.razor` files for `_accessRights.X` reads, which an in-process unit test cannot do against
compiled Razor. The registry's own divergence assertion covers the half that is testable — every
constant is granted or excluded — and the sweep above covers the other half. The gap is named here
rather than papered over.

### 5.6 The circuit-level check

`SearchPermissionComponentTests` renders the real `AuditTrails` page through bUnit. **1 red / 2 green
before, 3 green after:** `WithoutTheSearchPermission_TheSearchControlsAreAbsent` failed with the gate
removed. The two controls are `TheRestOfThePageIsUnaffectedEitherWay` — the grid and the refresh
button survive in both states — and the positive case.

An HTTP test cannot see this: the app renders at `InteractiveServerRenderMode(prerender: false)`, so
a response carries the shell and none of the toolbar. Same lesson as Pass 16A's empty Security tab.

---

## 6. §C — Verification

### 6.1 Counts

| Suite | Start | After | Delta |
|---|---:|---:|---:|
| `Infrastructure.UnitTests` | 192 | **202** | +10 |
| `Application.IntegrationTests` | 9 | 9 | 0 |
| `Application.UnitTests` | 406 (+12 skipped) | **407** (+12 skipped) | +1 |
| `Server.UI.IntegrationTests` | 151 | **154** | +3 |
| **Total passed** | **758** | **772** | **+14** |
| Skipped / Failed | 12 / 0 | 12 / 0 | 0 |

**+14 is exactly the new tests:** `DataSourceScopeTests` 10, `SearchPermissionComponentTests` 3,
`EveryExclusionCarriesAReason` 1. **No test was deleted; one was renamed and broadened (§5.4).**

### 6.2 Warnings

**10 distinct locations — the same ten, with one line number moved:**

```
AuditTrails.razor(107,72) CS8602   ← was (100,72)
Dashboard.razor(202,60)   CS8604
DescriptionAttributeExtensions.cs(12,45) CS8600   (23,46) CS8603
DescriptionAttributeExtensions.cs(20,32) CS8600   (33,20) CS8603
MapsterConfiguration.cs(26,32) CS8601   (28,29) CS8601
MudDateTimeField.razor(1,1) MUD0002
TenantSelect.razor(13,44) CS8603
```

**The one change explained:** `AuditTrails.razor`'s pre-existing CS8602 moved from line 100 to line
107 because §B.1 inserted seven lines above it — the four-line comment, the `@if` and its brace. Same
file, same column, same warning, same cause; nothing was introduced and nothing removed. No file this
pass touched compiles with a new warning.

### 6.3 Pass 24 and Pass 25 suites, unmodified

§A touches the cache the user context is loaded through, so these are the boundary proof.
**60 tests, 0 failures**, none edited: `TenantStampingTests`, `DocumentTenantIsolationTests`,
`TenantSwitchAuthorizationTests`, `PrimaryTenantRuleTests`, `UserContextAllowedTenantsTests`,
`UserTenantConsistencyTests`, `UserDeactivationPermissionComponentTests`.

### 6.4 Generation probe

```
dotnet pack build/pack.csproj -o .        → GX.Blazor.Template.1.0.0.nupkg
dotnet new install ./GX.Blazor.Template.1.0.0.nupkg
dotnet new gxblazor -n P26 -o P26         → created
dotnet build P26.slnx                     → 0 errors
dotnet test P26.slnx                      → 772 passed, 12 skipped, 0 failed
dotnet new uninstall GX.Blazor.Template   → uninstalled
```

Generated project deleted, template uninstalled.

---

## 7. File map and diffstat

**Modified — §A (5)**

| File | Lines | Why |
|---|---:|---|
| `src/Infrastructure/Services/DataSourceServiceBase.cs` | 107 | the scope, the composed key, `_loadedKey`, the bypass |
| `…/Services/Identity/UserDataSourceService.cs` | 21 | `PerTenant` + justification |
| `…/Services/PicklistDataSourceService.cs` | 21 | `Global` + justification |
| `…/Services/Identity/RoleDataSourceService.cs` | 20 | `Global` + justification |
| `…/Services/MultiTenant/TenantDataSourceService.cs` | 20 | `PerUser` + justification |

**Modified — §B (5)**

| File | Lines | Why |
|---|---:|---|
| `…/Common/Security/AdministratorPermissionRegistry.cs` | 43 | 3 grants removed, 6 exclusions added, 2 reason constants |
| `…/Features/Documents/Security/DocumentsPermissions.cs` | 23 | Export/Import removed from constants **and** AccessRights |
| `src/Application/Common/Security/Permissions.cs` | 13 | `NavigationMenu` removed |
| `src/Server.UI/Pages/SystemManagement/SystemLogs.razor` | 14 | `Logs.Search` gate |
| `src/Server.UI/Pages/SystemManagement/AuditTrails.razor` | 7 | `AuditTrails.Search` gate |

**Deleted (2 files + 2 directories)**

`Features/Documents/Queries/Export/ExportDocumentsQuery.cs`,
`Features/Documents/Queries/GetAll/GetAllDocumentsQuery.cs` — 3 bytes each; their directories went
with them.

**Modified — tests (1)** · `AdministratorPermissionRegistryTests.cs` (34 lines) — §5.4.

**New — tests (2)**

| File | Lines | Tests |
|---|---:|---:|
| `tests/Infrastructure.UnitTests/Services/DataSourceScopeTests.cs` | 300 | 10 |
| `tests/Server.UI.IntegrationTests/SearchPermissionComponentTests.cs` | 143 | 3 |

**Diffstat:** `13 files changed, 279 insertions(+), 46 deletions(-)` plus 2 new test files (443
lines). No migration was touched — this pass changes no schema.

### Edit fidelity

- **Line endings unchanged** — LF throughout, verified against untouched files. The git
  `LF will be replaced by CRLF` notices are the repository's standing `core.autocrlf=true` against
  `* text=auto`.
- **No BOM added or removed.**
- **No scaffolding left behind** — `grep -rn "PRE-PASS-26" src/` returns nothing.
- **The deleted constants appear nowhere** in `src/` or `tests/` except the comments that record
  their removal.

---

## 8. Scratch probe disclosure

| Probe | Purpose | Disposed |
|---|---|---|
| `scratchpad/p26/` | backups of `DataSourceServiceBase.cs` and `AuditTrails.razor` for the red-capture restores | deleted |
| `C:\src\P26` | the generated project | deleted, template uninstalled |
| Pass 24/25 snapshots | retained by Pass 25 against the uncommitted-tree risk | **deleted** — that work is now committed |

No database was created or dropped. No application was booted. `GX.Blazor.Template.1.0.0.nupkg` at
the repository root was rebuilt by §6.4 and is gitignored.

---

## 9. Anomalies

**A1 — `Items` staleness is now bounded by the key, not eliminated.** `InitializeAsync` reloads when
the *effective key* moves, which covers a tenant switch. It does **not** cover another circuit
invalidating the shared entry: circuit B's `RefreshAsync` evicts the cache, but circuit A's `Items`
keeps the list it already has until A refreshes for its own reasons. That is pre-existing and
unchanged by this pass — worth knowing because Stage 4 will make these lists tenant-dependent, and a
stale list will then be a *wrong* list rather than merely an old one. Fixing it needs a cross-circuit
signal (FusionCache backplane, or the existing `OnChange` raised from an eviction), which is a design
choice rather than a repair.

**A2 — `PicklistDataSourceService.SearchAsync` bypasses the cache and the scope entirely.** It is
the one override of `SearchAsync`, and it queries the database directly rather than filtering
`Items`. So a scope declared on that class governs `DataSource`/`InitializeAsync` but not
`SearchAsync`. Harmless while the scope is `Global`; it must be looked at in the same change that
makes picklists `PerTenant`, or the autocomplete will return unscoped rows while the list beside it
is scoped.

**A3 — the inert sweep cannot be expressed as a test** (§5.5). The registry covers "granted or
excluded"; nothing in-process can see whether a `.razor` file reads an `*AccessRights` property. The
recount therefore has to be re-run by hand whenever permissions change, and this report records the
method so it can be.

**A4 — `PickUserAutocomplete` is still dead** (Pass 23 A2) and now carries a `PerTenant` cache scope
through `UserDataSourceService`. Nothing changes for it because nothing renders it; noted so the
scope is not mistaken for evidence that the component is live.

---

## 10. What was deliberately not done

**No filtering was begun.** Every datasource still loads exactly the rows it loaded before; only the
key changed. Stage 4 scopes the Users surfaces — the grid, the export, `TenantSelect` and
`PickSuperiorAutocomplete` — and is now safe to write, because the entry each of those reads is
partitioned by the principal it belongs to.

**`RoleDataSourceService` and `PicklistDataSourceService` stay `Global`**, pending the two product
questions Pass 23 left open (§2.5 roles, §2.6 picklists). Both now carry the reasoning at the site,
so whoever answers those questions finds the cache line next to the argument for changing it.
