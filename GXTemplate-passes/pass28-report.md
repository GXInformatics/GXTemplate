# Pass 28 — The Switchable Set, and the Unfiltered User Query

**Nature:** editing pass (§B and Pass 27 A3 implemented) with a **decision gate at §A, not
implemented**. **No git actions.** **Date:** 2026-09-03.

> **§A is a gate and stops for review.** §B and A3 are independent of it and were implemented, as
> the brief permits. **`TenantSelector` and `AvailableTenants` were not touched.**

---

## 1. Start state

| | |
|---|---|
| HEAD | `6a016a3bdcf7f7d0e0c73192ff002fb11e606fb2` — *"Pass27"* |
| Working tree | **clean** |
| Build | 0 errors |
| Warning locations | **10 distinct** |
| Tests | **795 passed, 12 skipped, 0 failed** |
| Spot-check `Permissions.Users.ViewAllTenants` | present (`Users.cs:78`) |
| Spot-check `PickUserAutocomplete` | absent |

---

## 2. §A — GATE: where the switchable list should come from

### 2.1 The facts, re-confirmed

**The permission gate is wrong**, exactly as recorded. `TenantSelector.razor:18` disables the whole
menu on `!_hasSwitchPermission`, and `:100` computes that from `Permissions.Users.SwitchTenants`
**alone** — `SwitchToAnyTenant` is never consulted.

**The source is membership-only**, confirmed through the whole chain:
`TenantSelector:93` → `UserProfile.AvailableTenants` → `ApplicationUserDto.cs:100` →
`MapsterConfiguration:20` = `src.TenantUsers.Select(tu => tu.Tenant)`.

**The service's rule** (`CanSwitchToTenantAsync`, Pass 25) is a three-way ladder:

| Principal holds | May switch to |
|---|---|
| `SwitchToAnyTenant` | **any tenant** — checked first, subsumes the other |
| `SwitchTenants` only | tenants with a `TenantUsers` row |
| neither | nothing |

So the menu is wrong twice over for a `SwitchToAnyTenant` holder: disabled, and — were it enabled —
listing only the tenants they could already reach.

### 2.2 Every consumer of `AvailableTenants` — the census the brief asked for

| Site | What it is |
|---|---|
| `UserProfile.cs:24` | the record parameter — the declaration |
| `ApplicationUserDto.cs:100` | the producer |
| `TenantSelector.razor:93` | **the only reader** |
| `TenantDataSourceService.cs:65` | a comment |
| `TenantVisibilityTests.cs:27` | a comment |

**`AvailableTenants` exists solely to feed the tenant switcher.** There is no second consumer whose
meaning would break — which removes the concern behind §A.2 entirely, and also means that whatever
the selector stops reading becomes unconsumed.

### 2.3 Recommendation

**Give `ITenantSwitchService` a `GetSwitchableTenantsAsync(userId)`, and have the menu read that.**

**Why not `TenantDataSourceService`.** It is bounded by *visibility* and cached `PerUser` under a
visibility-shaped key. Serving switchability from it means either a second bound behind the same
cache entry — two questions in one cached list, which is the "two services in one" the brief warns
against — or a parallel key. The bounds also genuinely differ: a principal may hold
`ViewAllTenants` without `SwitchTenants`, or the reverse.

**Why not change `AvailableTenants`' meaning.** It is produced by a Mapster projection of
`TenantUsers` with no access to the permission stack; making it the switchable set would put a
permission check inside profile construction. Leave it as "tenants I belong to" — a true fact, and
one the switch rule still uses for the `SwitchTenants`-only case.

**Why the service.** It already owns `CanSwitchToTenantAsync`, so putting the list beside it lets
**one private rule** produce both answers — the mode (`All` / `Membership` / `None`) resolved once,
with `CanSwitchToTenantAsync` and `GetSwitchableTenantsAsync` deriving from it. Agreement becomes
structural rather than coincidental, which is what §A.5 requires; the property test then asserts, for
every tenant in the installation, that `menu.Contains(t) == CanSwitchToTenantAsync(user, t.Id)`.

**§A.3 — the ladder holds at this surface.** `SwitchToAnyTenant` implies `SwitchTenants`; a holder of
the escalated right alone gets an enabled menu listing every tenant. The gate becomes "is the
switchable set non-empty?" rather than a permission check in its own right — which is the same
question stated where it can only be answered consistently.

**§A.4 — a principal with neither right: the menu should be absent, not disabled.** But the activator
also displays the current organisation name, which is *information*, not a control. So the
recommendation is precise: **render the organisation name as plain content rather than as a menu
activator.** That follows the template's own two precedents — Pass 16A's *"absent, never disabled: an
empty tab invites a support call"*, and Pass 25 §D's deactivation gate, where *"the gate removes the
action, not the information"*. A greyed-out switcher tells a user they are missing something without
telling them what.

**§A.5 — the list must be exactly what the service permits.** Because switching is a **write** that
re-parents everything the principal subsequently creates, a superset offers a mutation that will be
refused, and a subset hides a granted capability. One rule, two derivations, one property test.

### 2.4 One caveat to weigh before ratifying

`TenantSwitchService` depends on `IPermissionService`, which resolves the principal through Blazor's
`AuthenticationStateProvider` — the coupling Pass 27 §4.2 hit when a datasource took the same
dependency and broke `Application.IntegrationTests`. Adding a method does not worsen it (the service
is already Blazor-only and only ever resolved from the UI), but if you want it host-neutral, switch
it to `IPermissionQueryService` in the same change, as both datasources now do. **My recommendation
is to switch it**, for consistency and because the cost is one line.

**Ratify or amend, and I will implement §A with §C.1–§C.4.**

---

## 3. §B — the user query, bounded

### 3.1 The predicate is now shared, not restated

**It could be shared, and it was.** `UserTenantVisibility.IsVisibleTo(viewAllTenants, visibleTenantIds)`
is the single definition, following `VisibleDocumentSpecification.IsVisibleTo` as precedent. Three
consumers apply it: the users grid, the user export, and `UserDataSourceService`.

**One structural change was needed to make sharing possible.** Pass 27 put the bound *inside*
`CreateSearchPredicate`. A shared `Expression` cannot be spliced into another expression tree by
compiling and invoking it — EF cannot translate a delegate call — so the bound moved into its own
`Where`, behind a new single entry point:

```csharp
private IQueryable<ApplicationUser> VisibleUsers() =>
    _userManager.Users.Where(
        UserTenantVisibility.IsVisibleTo(_accessRights.ViewAllTenants, _visibleTenantIds));
```

Both the grid and the export now start from `VisibleUsers()`, so Pass 27's property — one place, two
callers — is preserved, and `TheGridAndTheExport_ReturnTheSameRows` still guards it.

### 3.2 The cache scope was wrong, and confirming it is what found that

**`UserDataSourceService` was `PerTenant`; it is now `PerUser`.** Pass 26 declared `PerTenant` while
the query was unfiltered, on the reasoning that the list is *"who exists in a tenant, the same answer
for everyone in it"*. **Bounding the query made that false.** Two principals sitting in the same
tenant get different answers if one of them also belongs to a second tenant, or holds
`ViewAllTenants`. Under a per-tenant key one would have been served the other's list.

This is the general lesson worth recording: **a cache partition is a claim about who may share an
entry, and changing what a query returns can invalidate that claim without touching the line that
declares it.** The brief said "confirm rather than assume", and confirming is what caught it.

`PerUser` rather than `PerUserAndTenant`: the bound is a function of the principal alone.
`DataSourceServiceBase`'s `_loadedKey` reload (Pass 26 §2.4) still does the right thing — the key
carries the user id, so a principal whose identity changes mid-circuit reloads;
`TwoPrincipalsInTheSameTenantWithDifferentReach_DoNotShareAnEntry` demonstrates the partition.

### 3.3 Which layer is load-bearing — and the brief's framing needs one correction

**Neither layer is redundant, because they answer different questions.**

- **The query bound is load-bearing for isolation.** It decides which users this *principal* may see,
  and it is now what keeps foreign rows out of the circuit's memory and cache entirely.
- **`PickSuperiorAutocomplete`'s tenant clause is load-bearing for correctness**, and is *not* merely
  a second line of defence. It narrows from "all tenants I may see" to "the one tenant this edited
  user is in" — a strictly narrower question the query bound does not answer. A principal who may see
  two tenants would otherwise be offered superiors from both.
- **Its fail-closed default is load-bearing for callers not yet written** (Pass 27 §D).

So: **do not remove either.** The query bound is the isolation boundary; the component clause is the
per-edit narrowing; and they are not two implementations of one rule.

---

## 4. Pass 27 A3 — closed with a model change

**It wanted a model change, not a UI change**, so no improvisation was needed.

`InputModel` now carries a `TenantId`, populated in `EditUserAsync` from `ApplicationUserDto.TenantId`
and passed through `PrimaryTenantRule.Resolve(Model.TenantId, …)`. The picker is therefore bounded by
the user's **actual** primary tenant rather than by whichever of their tenants sorted first.

**The one risk, handled explicitly.** Pass 25 removed a divergence between two records of a user's
tenancy, and this adds a second copy of the primary to the dialog. It is safe only because **nothing
writes it and nothing persists from it**: no control binds it, and `SubmitAsync` still re-derives the
primary from the database row it is about to update, which is authoritative where a dialog-open
snapshot is not. That constraint is written on the field, with the instruction that if either
changes, the field must go.

---

## 5. §C — Verification

### 5.1 Red before, green after

Captured by restoring each pre-pass body, then restoring from backups
(`grep -rn "PRE-PASS-28" src/` → nothing).

**§B: 7 red / 2 green → 9 green.** Red: the bound, the two-tenant case, the tenantless user, both
fail-closed cases, the partition demonstration, and the scope assertion.

**§A3: 1 red / 3 green → 4 green.** Only `TheBoundIsTheUsersActualPrimaryTenant_NotTheFirstSelected`
moved — *"Expected "tenant-b", but "tenant-a" differs"* — which is precisely A3.

**The green-in-both controls are the evidence.** For §B: `EveryColleagueInTheTenantIsStillLoaded` and
`ACrossTenantHolder_LoadsEveryUser` — a bounded query returning nothing would satisfy every isolation
assertion, and these are what says it did not. For A3: the new-user, deselected-primary and
no-tenants cases behave identically under both versions, so the change is exactly as narrow as
claimed.

### 5.2 Counts

| Suite | Start | After | Delta |
|---|---:|---:|---:|
| `Infrastructure.UnitTests` | 208 | **217** | +9 |
| `Application.IntegrationTests` | 9 | 9 | 0 |
| `Application.UnitTests` | 407 (+12 skipped) | 407 (+12 skipped) | 0 |
| `Server.UI.IntegrationTests` | 171 | **175** | +4 |
| **Total passed** | **795** | **808** | **+13** |
| Skipped / Failed | 12 / 0 | 12 / 0 | 0 |

**+13 is exactly the new tests:** `UserVisibilityTests` 9, `SuperiorBoundComponentTests` 4. **No test
was modified, renamed or deleted.**

### 5.3 Warnings

**10 distinct locations, identical to the start state.** No file this pass touched compiles with a
new warning.

### 5.4 The boundary suites — green and byte-unmodified

`git status` shows no change to any of them:

| Suite | Result |
|---|---|
| `DataSourceScopeTests` (Pass 26) | green — §B changes a scoped datasource |
| `UserTenantScopeComponentTests` (Pass 27) | green — the grid and export still agree |
| `TenantVisibilityTests` (Pass 27) | green |
| `SuperiorAutocompleteScopeComponentTests` (Pass 27) | green — the component clause still narrows |

33 tests across the four. That Pass 27's grid/export tests pass untouched is the specific proof that
moving the bound out of `CreateSearchPredicate` into `VisibleUsers()` preserved the behaviour.

### 5.5 The live run

Against a freshly seeded two-tenant database, driving the **real** `UserDataSourceService`:

```
tenants : Default, Europe
users   : Administrator, probe-a, probe-b

B  tenant A only        : Administrator, probe-a
B  tenant B only        : probe-b
B  both tenants         : Administrator, probe-a, probe-b
B  ViewAllTenants       : Administrator, probe-a, probe-b
B  empty allowed set    : ''            (expect empty)
B  no ambient principal : ''            (expect empty)

B  cache scope          : PerUser       (expect PerUser)
```

Bounded, widened by the escape, empty in both fail-closed positions, and narrowed-not-emptied
visible in the first three lines.

*The switch cases are not in this capture* — they belong to §A, which is not implemented.

### 5.6 Generation probe

```
dotnet pack → install → dotnet new gxblazor -n P28 → build (0 errors)
             → dotnet test: 808 passed, 12 skipped, 0 failed → uninstall
```

---

## 6. README

No surface changed status, so the table's rows stand. Two clauses were made more precise:

- The Users-area paragraph now names `UserTenantVisibility.IsVisibleTo` as the single definition and
  states that the datasource shares it.
- A new sentence records that the bound is **at the query, not the view** — *"a list filtered only as
  it is drawn is still a list the server fetched and held"*, which is the substance of §B.
- The "superior" row now says the list it searches is bounded by the same rule *and then* narrowed to
  the edited user's tenant, which is §3.3's two-layer distinction stated where a reader will meet it.

---

## 7. What remains unscoped

**Audit trails, system logs, roles, picklists, security settings, presence and chat** — unchanged by
this pass and stated as such in the README. Stage 5 takes the first three.

Also still open, and deferred by direction: **`TenantSelector` (§A)**, which remains gated on the
wrong permission and sourced from a membership-only list.

---

## 8. File map and diffstat

**Modified — source (3)**

| File | Lines | Why |
|---|---:|---|
| `…/Services/Identity/UserDataSourceService.cs` | 75 | §B — the bound, the scope correction, the permission query |
| `…/Identity/Users/Components/UserFormDialog.razor` | 37 | A3 — `InputModel.TenantId` and the picker's bound |
| `src/Server.UI/Pages/Identity/Users/Users.razor` | 63 | §B — `VisibleUsers()`, the shared rule, A3's model wiring |
| `README.md` | 15 | §6 |

**New — source (1)** · `src/Application/Features/Identity/UserTenantVisibility.cs` — 65 lines, the
shared rule.

**New — tests (2)**

| File | Lines | Tests |
|---|---:|---:|
| `tests/Infrastructure.UnitTests/Services/UserVisibilityTests.cs` | 205 | 9 |
| `tests/Server.UI.IntegrationTests/SuperiorBoundComponentTests.cs` | 218 | 4 |

**Diffstat:** `4 files changed, 139 insertions(+), 51 deletions(-)` plus 3 new files. No migration was
touched — this pass changes no schema.

### Edit fidelity

- **Line endings unchanged** — LF throughout.
- **No BOM added or removed.**
- **No scaffolding left behind** — `grep -rn "PRE-PASS-28" src/` returns nothing.
- **No test was edited** to accommodate a change; the two fixture completions this programme has
  needed before were not needed here.

---

## 9. Scratch probe disclosure

| Probe | Purpose | Disposed |
|---|---|---|
| `scratchpad/p28/` | backups of the two changed sources for the red captures | deleted |
| `scratchpad/probe28/` | a console project driving the real datasource against the live database | deleted |
| `scratchpad/live28/` | the seeded SQLite business and log databases | deleted |
| `C:\src\P28` | the generated project | deleted, template uninstalled |

No database on any server was created or dropped. The root `.nupkg` was rebuilt and is gitignored.

---

## 10. Anomalies

**A1 — `AvailableTenants` will become unconsumed if §A is ratified.** It has exactly one reader
today (§2.2), and the recommendation moves that reader elsewhere. It would then be a correct,
cheaply-computed, entirely unread field on a public record — the same shape as
`PickUserAutocomplete` before Pass 27 deleted it. **Not deleted here**, because removing a public
member of `UserProfile` is visible to generated projects and deserves its own decision; flagged so
that decision gets made rather than forgotten.

**A2 — `UserDataSourceService` now costs one permission query per cache miss.** Small, and bounded by
the `PerUser` cache entry, but it is a database round trip inside a datasource load that previously
had none. Both datasources now pay it. If the cross-tenant right ever needs checking on a hot path,
carrying it on `UserContext` — computed once by `UserContextLoader` with everything else — would be
the natural move.

**A3 — the superior picker still narrows in memory.** By design (§3.3), and now genuinely a second
question rather than a second implementation. Recorded so the two layers are not mistaken for
duplication.

**A4 — `SuperiorBoundComponentTests` renders through `MudDialogProvider`, not directly.** A direct
`Render<UserFormDialog>` produces a component whose body is never in the render tree, because
`MudDialog` hands its content to the dialog instance rather than rendering it inline. The first
attempt failed exactly that way. Recorded because the next person to test a dialog will hit it.
