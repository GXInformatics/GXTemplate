# Pass 27 — Stage 4: Scope the Users Surfaces — **IMPLEMENTATION**

**Nature:** editing pass, following the §A gate ratified in `pass27-report.md`.
**No git actions.** **Date:** 2026-09-03.

> **This is the first pass that filters anything.** Four surfaces, one predicate, one escape — and
> the export asserted separately from the grid, because it is the surface a partial fix misses.

---

## 1. Start state

| | |
|---|---|
| HEAD | `e53616d34075d7b4b64dc7671f618dbde497b4c2` — *"Pass26"* |
| Working tree | **clean** — Pass 26 was committed between the gate report and this one |
| Build | 0 errors |
| Warning locations | **10 distinct** |
| Tests | **772 passed, 12 skipped, 0 failed** |
| Spot-check `DataSourceServiceBase.Scope` | abstract, present |
| Spot-check `Permissions.NavigationMenu` | absent |

The gate report was written against an uncommitted Pass 26; it is committed now, and this pass ran
against a clean tree.

---

## 2. §A — the ratified decision, implemented

**`Permissions.Users.ViewAllTenants`**, with a matching `UsersAccessRights.ViewAllTenants`, granted
to the administrator.

The constant carries the gate's three-part reasoning at its declaration, so the next reader finds
why it is not `SwitchToAnyTenant` without going to a pass report: switching is **serial**, switching
is a **write** that re-parents everything the principal subsequently creates, and switching to a
non-member tenant is **not reachable** from the UI at all.

**Granted, and the grant is the interesting half.** The bootstrap administrator is seeded into every
tenant and the grid was previously unfiltered, so a default installation looks exactly as it did.
What changed is that the capability is now named, enforced and revocable — the move Pass 22 made for
`IsActive`, where a posture that held by accident became one that is stated.

`AdministratorPermissionRegistry.Validate` is what made adding it safe: a constant that is neither
granted nor excluded fails startup and the test run. It was not possible to add this silently.

**"ALL" keeps its meaning.** No UI control changed. `_selectedTenantId` still defaults to empty and
the dropdown still renders an "ALL" item; what changed is the list behind it and the predicate
beneath it.

---

## 3. §B — the grid and the export, one predicate

`CreateSearchPredicate()` had three clauses; it now has four:

```csharp
(unbounded || visibleTenantIds.Contains(x.TenantId!))
```

with `unbounded = _accessRights.ViewAllTenants` and `_visibleTenantIds` taken from
`UserContext.AllowedTenantIds` in `OnInitializedAsync`.

**The bound lives inside the shared method, which is the whole point.** `LoadServerData` (`:346`)
and `ExportUsersAsync` (`:827`) both call it and must go on both calling it — §5.3's
`TheGridAndTheExport_ReturnTheSameRows` is what fails if anyone gives the export its own query.

**Fail closed, three ways.** No ambient principal → empty array → matches nothing. Empty
`AllowedTenantIds` → same. A user whose own `TenantId` is null belongs to no tenant and matches no
allowed id, so they are visible only to a `ViewAllTenants` holder — which falls out of failing
closed rather than being a separate rule, and is asserted so it cannot change silently.

**The ambient principal, not a reconstructed one.** The page injects `IUserContextAccessor`; the hub
filter pushes the context for every circuit invocation, and it is the same context
`AuthorizationBehaviour` already checked to let the page run.

---

## 4. §C — one list, two dropdowns, and the third one left alone

`TenantDataSourceService.LoadAsync` is now bounded by `AllowedTenantIds`, widened by
`ViewAllTenants`, and returns an empty list when there is no ambient principal.

### 4.1 The §C finding: the two dropdowns want the *same* answer

The brief asked whether the filter dropdown and `TenantSelect` want different answers. **They do
not.** Both ask a visibility question — *which tenants may I filter by* and *which tenants may I
assign a user to* — and you cannot assign a user into a tenant you cannot see. They also bind the
**same** `IDataSourceService<TenantDto>`, so one change bounds both.

**That closes the escalation the brief asked about.** Before this pass, an administrator of one
tenant could open the user dialog and move a user into a tenant they had no visibility of.

**A third dropdown does want a different answer, and was left alone as directed.** `TenantSelector`
in the app shell asks *which tenants may I switch into* — bounded by membership and
`SwitchToAnyTenant`, not by visibility — and reads `UserProfile.AvailableTenants`, a different source
entirely. Its defects are A1/A2 from the gate report, deferred to their own pass.

### 4.2 A design correction found by the test suite

The first implementation injected `IPermissionService` into the datasource. **It broke
`Application.IntegrationTests` immediately**, with

> *Unable to resolve service for type `AuthenticationStateProvider` while attempting to activate
> `PermissionService`.*

`PermissionService` resolves the principal through Blazor's `AuthenticationStateProvider`, so an
Infrastructure datasource depending on it cannot be constructed in any non-Blazor host — and that
harness is Infrastructure plus Application and nothing else.

It now uses **`IPermissionQueryService`**, which takes only `IServiceScopeFactory`, reads role claims
directly, and works in any host. The reason is recorded on the constructor. Cost: one permission
query per cache miss, not per read — the answer is baked into the `PerUser` entry the service
caches, so the answer and the list it produced always belong to the same principal.

---

## 5. §D — the autocomplete, and the dead component

**Both halves, and the default is the important one**, as I argued at the gate and you ratified.

- **The component fails closed.** `SearchKeyValues` returns nothing when `TenantId` is absent. The
  old predicate's `|| TenantId == null` made an absent parameter mean *everything*.
- **The call site passes a tenant.** `UserFormDialog` supplies
  `PrimaryTenantRule.Resolve(null, Model.Tenants.Select(t => t.Id))` — the tenant the user is being
  put into, tracked as the selection changes, through the same rule Pass 25's save path uses.

  *A precision note:* `InputModel` carries a tenant **set** with no primary-tenant concept, so this
  is the first selected tenant. For a new account that is exactly the primary; for an existing
  multi-tenant account it is still a tenant the user genuinely belongs to. Conservative rather than
  exact, and never wider than the user's own tenants.

- **`PickUserAutocomplete` deleted** — zero call sites, flagged in Pass 23 A2, Pass 26 A4 and the
  gate report. `grep` confirms no reference remains anywhere in `src/` or `tests/`.

---

## 6. §E — Verification

### 6.1 Red before, green after

Captured by restoring the pre-pass body of each of the three changed sources, then restoring the
fixes from backups (`grep -rn "PRE-PASS-27" src/` → nothing).

**13 red / 10 green → 23 green.** Red across all four surfaces:

| Surface | Red in the reverted state |
|---|---|
| **Grid** | `TheGrid_ShowsNoOtherTenantsUsers`, `APrincipalInTwoTenants_SeesBoth`, `ATenantlessUserIsVisibleOnlyToACrossTenantHolder`, `WithAnEmptyAllowedSet_TheGridIsEmpty`, `WithNoAmbientPrincipal_TheGridIsEmpty` |
| **Export (separately)** | `TheExport_ContainsNoOtherTenantsRows`, `WithNoAmbientPrincipal_TheExportIsEmpty` |
| **Both dropdowns** | `OnlyTheTenantsThePrincipalMaySee_AreOffered`, `APrincipalInSeveralTenants_IsOfferedAllOfThem`, `ATenantThePrincipalIsNotIn_IsNeverOffered_EvenWhenItExists`, `WithNoAmbientPrincipal_NoTenantsAreOffered`, `WithAnEmptyAllowedSet_NoTenantsAreOffered` |
| **Superior autocomplete** | `WithNoTenant_ItSearchesNothing` |

**The ten that stayed green in both states are the evidence, not the tally**, and two groups of them
matter especially:

- **The narrowed-not-emptied controls.** `TheAdministratorStillSeesEveryUserInTheirOwnTenant`,
  `APrincipalInSeveralTenants_IsOfferedAllOfThem`, `ItStillFindsEveryColleagueInThatTenant`. A
  predicate returning nothing would satisfy every red test above; these are what says it did not.
- **The autocomplete's bound tests stayed green in both states**, and that is exactly the gate's
  argument made visible: the old predicate *did* filter correctly when a tenant was passed. Only the
  absent-parameter case leaked, which is why the default was the important half of the fix.

`TheGridAndTheExport_ReturnTheSameRows` is also green in both states — they shared a predicate before
and after, and that is the property being preserved.

### 6.2 Two existing fixtures needed completing — and their failure was evidence

Both failed the moment the scoping landed, for the same reason: **they did not establish an ambient
principal, and the production code now requires one.** That is a fixture completing, not an
expectation relaxing — and the fact that they failed is itself proof the bound applies.

- **`UserDeactivationPermissionComponentTests`** registered `Mock.Of<IUserContextAccessor>()`, whose
  `Current` is null → empty grid → its four assertions would have passed *for the wrong reason*. It
  now supplies a principal who may see the seeded tenant, with a comment saying why.
- **`TenantsServiceTests`** failed first with the DI error of §4.2 and then passed **unmodified**
  once the datasource used `IPermissionQueryService`: the harness user is granted every permission,
  including `ViewAllTenants`, so it legitimately sees all tenants. An intermediate version of this
  pass had extended `Testing.cs` with an allowed-tenants hook; that was **reverted** when it proved
  unnecessary, rather than left as unused fixture machinery.

### 6.3 Counts

| Suite | Start | After | Delta |
|---|---:|---:|---:|
| `Infrastructure.UnitTests` | 202 | **208** | +6 |
| `Application.IntegrationTests` | 9 | 9 | 0 |
| `Application.UnitTests` | 407 (+12 skipped) | 407 (+12 skipped) | 0 |
| `Server.UI.IntegrationTests` | 154 | **171** | +17 |
| **Total passed** | **772** | **795** | **+23** |
| Skipped / Failed | 12 / 0 | 12 / 0 | 0 |

**+23 is exactly the new tests:** `TenantVisibilityTests` 6, `UserTenantScopeComponentTests` 11,
`SuperiorAutocompleteScopeComponentTests` 6. **No test was deleted or renamed, and no expectation was
relaxed** — one fixture gained a principal (§6.2).

### 6.4 Warnings

**10 distinct locations, identical to the start state.** Two new CS8602s appeared in my own
`SuperiorAutocompleteScopeComponentTests` (awaiting a possibly-null `Task` from
`MudAutocomplete.SearchFunc`); both were fixed rather than tolerated, by null-checking the returned
task. No file this pass touched compiles with a new warning.

### 6.5 Pass 26's boundary suite

`DataSourceScopeTests` — **10 tests, green, byte-unmodified** (`git status` shows no change to the
file). §C changes a scoped datasource, so that is the proof the cache partition still behaves.

### 6.6 The live run

Booted against a fresh SQLite database seeded with two tenants ("Default", "Europe"), then drove the
**real** predicate and the **real** `TenantDataSourceService` against it:

```
tenants                    : Default, Europe
users                      : Administrator, probe-b

B  admin of tenant A only   : Administrator
B  admin of tenant B only   : probe-b
B  member of both           : Administrator, probe-b
B  ViewAllTenants holder    : Administrator, probe-b
B  no ambient principal     : ''            (expect empty)

C  dropdown, tenant A only  : Default
C  dropdown, both tenants   : Default, Europe
C  dropdown, ViewAllTenants : Default, Europe
C  dropdown, no tenants     : ''            (expect empty)
C  dropdown, no principal   : ''            (expect empty)
```

Every case correct: bounded, widened by the escape, and empty in both fail-closed positions — on real
seeded data, not a fixture.

### 6.7 Generation probe

```
dotnet pack build/pack.csproj -o .        → GX.Blazor.Template.1.0.0.nupkg
dotnet new install ./GX.Blazor.Template.1.0.0.nupkg
dotnet new gxblazor -n P27 -o P27         → created
dotnet build P27.slnx                     → 0 errors
dotnet test P27.slnx                      → 795 passed, 12 skipped, 0 failed
dotnet new uninstall GX.Blazor.Template   → uninstalled
```

The generated README carries the updated Tenancy table, so the correction ships.

---

## 7. What is still unscoped — and the README says so

The Tenancy table was rewritten rather than appended to, because a table whose value is that it is
true stops being useful the moment one row is stale.

**Now scoped:** Documents; the Users grid; the user export; the tenant filter dropdown;
`TenantSelect`; the superior search.

**Still installation-wide:** audit trails, system logs, roles, picklists, security settings, and
presence/chat/login notifications.

Three places carried the old claim and all three were corrected: the README headline, the Tenancy
table and its Known-limitations entry, and the **nuspec** `<description>` — which said "Tenant
isolation is enforced for Documents only" and is the text NuGet shows on the package listing, where
no reader would see the README.

---

## 8. File map and diffstat

**Modified — source (7)**

| File | Lines | Why |
|---|---:|---|
| `…/Services/MultiTenant/TenantDataSourceService.cs` | 72 | §C — the bound, the escape, the host-neutral permission query |
| `src/Server.UI/Pages/Identity/Users/Users.razor` | 59 | §B — the tenant clause, `_visibleTenantIds`, the accessor |
| `…/Autocomplete/PickSuperiorAutocomplete.razor.cs` | 35 | §D — fail closed on an absent tenant |
| `…/Common/Security/Permissions/Users.cs` | 25 | §A — the constant and its `AccessRights` property |
| `…/Identity/Users/Components/UserFormDialog.razor` | 12 | §D — pass the tenant at the call site |
| `…/Common/Security/AdministratorPermissionRegistry.cs` | 8 | §A — the grant |
| `README.md` / `GX.Blazor.Template.nuspec` | 56 / 5 | §7 |

**Deleted (1)** · `…/Autocomplete/PickUserAutocomplete.razor.cs` — 56 lines, zero call sites.

**Modified — tests (1)** · `UserDeactivationPermissionComponentTests.cs` (10 lines) — §6.2.

**New — tests (3)**

| File | Lines | Tests |
|---|---:|---:|
| `tests/Server.UI.IntegrationTests/UserTenantScopeComponentTests.cs` | 341 | 11 |
| `tests/Infrastructure.UnitTests/Services/TenantVisibilityTests.cs` | 166 | 6 |
| `tests/Server.UI.IntegrationTests/SuperiorAutocompleteScopeComponentTests.cs` | 151 | 6 |

**Diffstat:** `10 files changed, 248 insertions(+), 90 deletions(-)` plus 3 new test files (658
lines). No migration was touched — this pass changes no schema.

### Edit fidelity

- **Line endings unchanged** — LF throughout, verified against untouched files.
- **No BOM added or removed.**
- **No scaffolding left behind** — `grep -rn "PRE-PASS-27" src/` returns nothing.
- **The deleted component appears nowhere** in `src/` or `tests/`.
- **One intermediate change was reverted, not left behind** — the `Testing.cs` allowed-tenants hook
  (§6.2); `git status` shows that file unmodified.

---

## 9. Scratch probe disclosure

| Probe | Purpose | Disposed |
|---|---|---|
| `scratchpad/p27/` | backups of the three changed sources for the red-capture restore | deleted |
| `scratchpad/probe27/` | a console project referencing Infrastructure, driving the real predicate and datasource against the live database | deleted |
| `scratchpad/live27/` | the seeded SQLite business and log databases | deleted |
| `C:\src\P27` | the generated project | deleted, template uninstalled |

No database on any server was created or dropped. `GX.Blazor.Template.1.0.0.nupkg` at the repository
root was rebuilt by §6.7 and is gitignored.

---

## 10. Anomalies

**A1 — the tenant filter dropdown can now be narrower than the grid for one principal.** A
`ViewAllTenants` holder sees every tenant in both, and an ordinary administrator sees their own in
both, so the two agree in every case the permissions produce. They would disagree only if
`AllowedTenantIds` and the dropdown's source diverged — which is why both read the same
`UserContext`, and why `TenantDataSourceService` is `PerUser` rather than `PerTenant`. Recorded
because it is the kind of drift a future change could introduce without any test noticing.

**A2 — `UserDataSourceService` is still unfiltered.** It is `PerTenant`-scoped (Pass 26) but its
query returns every user, and it backs `PickSuperiorAutocomplete`. The autocomplete filters the list
in memory by tenant, so nothing leaks *through the component* — but the full list is in the
circuit's memory and in that principal's cache entry. Bounding the query itself is the natural
follow-up; it was not in this pass's four surfaces, and doing it would change what
`PickUserAutocomplete`'s successor and any future consumer see.

**A3 — the superior list uses the first selected tenant, not the edited user's stored primary.**
`InputModel` does not carry `TenantId` (§5). For a single-tenant user they are the same; for a
multi-tenant user the bound may be a different tenant of that user's own. Never wider than the
user's tenants, so it is conservative — but it is an approximation, and giving `InputModel` a primary
tenant would remove it.

**A4 — `ExportUsersAsync` is exercised by reflection in the tests.** It is private with no other
entry point. Replaying its query would have asserted on a *copy* of the predicate, and the property
under test is precisely that the export and the grid share one — so reflection over the real method
is the honest choice here rather than the lazy one. If the export ever gains a public entry point,
the test should move to it.

---

## 11. What was deliberately not done

**`TenantSelector` and `UserProfile.AvailableTenants` were not touched**, as directed — gate-report
A1 and A2 remain open for their own pass. The consequence is unchanged: `SwitchToAnyTenant` still has
no UI, and the switcher still offers membership-only tenants.

**No other surface was scoped.** Audit trails, system logs, roles, picklists, security settings and
presence remain installation-wide, and the README now says exactly that.
