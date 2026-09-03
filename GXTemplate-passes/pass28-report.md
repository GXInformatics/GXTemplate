# Pass 28 — The Switchable Set, and the Unfiltered User Query

**Nature:** editing pass. §B and Pass 27 A3 were implemented first; **§A was a gate, was ratified
mid-pass, and is now implemented in full**. **No git actions.** **Date:** 2026-09-03.

> §A reached the gate as a recommendation. §2.1-2.4 are left exactly as they were written, so the
> reasoning that was ratified can be read as it was put; **§2.5 records the ratification and what
> was built from it.**

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

### 2.5 Ratified — and what was built

§A was ratified as recommended, with four additions. All four are implemented.

**(i) The ladder is written once, and both answers derive from it.**
`TenantSwitchService` gained a private `SwitchScope` enum — `None` / `Membership` / `All` — and a
private `ScopeForAsync(userId)` that resolves it. `CanSwitchToTenantAsync` and the new
`GetSwitchableTenantsAsync` are both `switch`es over that one result:

```csharp
SwitchScope.All        => await TenantExistsAsync(tenantId),
SwitchScope.Membership => await IsMemberOfAsync(userId, tenantId),
_                      => false
```

and, for the list, `All` → every tenant, `Membership` → the tenants with a `TenantUsers` row,
`None` → `Array.Empty<TenantDto>()`. **Menu-and-service agreement is therefore structural.** It is
not two rules that happen to match: there is one rule, and the only way to make the list disagree
with the check is to change what both read.

That mattered more here than it would for a read. Switching is a **write** —
`SwitchToTenantAsync` persists `ApplicationUser.TenantId`, and the audit interceptor stamps every
subsequently created row from it — so putting a tenant in the menu is offering that mutation. A
superset offers a switch the service will refuse; a subset hides a granted capability. Neither is
reachable now.

**(ii) The property test.** `WhatIsOfferedIsExactlyWhatIsPermitted` asserts
`menu.Contains(t) == CanSwitchToTenantAsync(user, t)` across **every tenant in the installation ×
every principal shape**, `None` included:

| `SwitchTenants` | `SwitchToAnyTenant` | scope | offered |
|:--:|:--:|---|---|
| ✗ | ✗ | `None` | — |
| ✓ | ✗ | `Membership` | A, B |
| ✗ | ✓ | `All` | A, B, C |
| ✓ | ✓ | `All` | A, B, C |

The tenant fixture is deliberately three tenants with membership of two, so "narrowed" and
"emptied" are distinguishable and so is "narrowed to one".

**(iii) `IPermissionQueryService`, per the caveat.** `TenantSwitchService` no longer takes
`IPermissionService`. That interface resolves the principal through Blazor's
`AuthenticationStateProvider`, so a non-Blazor host cannot construct anything depending on it —
which is not hypothetical: Pass 27 hit exactly that when a datasource took the dependency and
`Application.IntegrationTests` stopped being able to build one. The swap also fixes a subtler
mismatch: `IPermissionService` answers about the *current* principal while both methods here take a
`userId` **argument**, which is the defect Pass 25 removed from this very method.

**(iv) §A.4 — absent, not disabled.** `TenantSelector` no longer carries a `Disabled=` attribute.
A principal with neither right gets the organisation name as **plain content**:

```razor
@if (!_canSwitch)
{
    <MudStack …>  …the app name and the organisation name…  </MudStack>
}
else
{
    <MudMenu …>  …the switchable tenants…  </MudMenu>
}
```

The gate removes the **action**, not the **information**. A disabled menu tells a user they are
missing something without telling them what; the organisation name is something everyone needs to
read. This is the template's own precedent — Pass 16A's Security tab, Pass 25's deactivation toggle.

**And the gate itself is now the list.** The component no longer restates the ladder:

```csharp
private bool _canSwitch => _switchableTenants.Count > 0;
```

Its previous gate — `PermissionService.HasPermissionAsync(Permissions.Users.SwitchTenants)` — was
the ladder badly restated in markup, which is why a `SwitchToAnyTenant` holder saw a disabled menu.
`@inject IPermissionService` is gone from the file.

**What was *not* done, per the ratification.** `UserProfile.AvailableTenants` is untouched — still
computed, still a membership projection, still public. `TenantDataSourceService` was not used as the
source: it answers *visibility*, bounded by `AllowedTenantIds` and widened by `Users.ViewAllTenants`,
which is a different question from *switchability*. A principal may legitimately see a tenant without
being able to become it, and the reverse. `SwitchabilityIsNotVisibility` asserts the two are not
collapsed.

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

**§A: 10 red → 10 green**, captured in two halves because the change has two halves.

*The service.* Replacing `GetSwitchableTenantsAsync`'s body with the membership-only list the
selector used to read — no ladder — turned **5 of the 10 `SwitchableTenantsTests` red**, including
the property test:

```
Failed WhatIsOfferedIsExactlyWhatIsPermitted
Failed ACrossTenantHolderIsOfferedEveryTenant_HoldingThatRightAlone
Failed ACrossTenantHolderCanActuallySwitchToANonMemberTenant
Failed APrincipalWithNeitherRightIsOfferedNothing
Failed SwitchabilityIsNotVisibility
```

`TenantSelectorComponentTests` stayed green throughout that capture, which is correct and worth
stating: it mocks `ITenantSwitchService`, so it tests the component's *use* of the list, not the
list's contents. The two halves are independently falsifiable.

*The component.* Restoring `TenantSelector.razor` to its `HEAD` body turned **all 5
`TenantSelectorComponentTests` red** — the two ladder cases, the two narrowed-not-emptied cases, and
§A.4, which fails on the old markup because a disabled `mud-menu-activator` is still present.

**§A's late finding: 1 further red → green.** See §10 A5 — the live run found
`CanSwitchToTenantAsync` answering **true for a tenant id that does not exist**, and
`ATenantThatDoesNotExistIsRefused_EvenForACrossTenantHolder` was written red against it before the
`TenantExistsAsync` guard was added.

**The narrowed-not-emptied controls for §A.** `AMembershipHolderIsOfferedBOTHOfTheirTenants` and
`TheMenuShowsEveryTenantTheServiceOffers`: a list or a menu that returned only the *current* tenant
would satisfy every "does not offer C" assertion above while quietly removing a real capability.
Both are green, at both layers.

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

| Suite | Start | After §B/A3 | After §A | Delta |
|---|---:|---:|---:|---:|
| `Infrastructure.UnitTests` | 208 | 217 | **217** | +9 |
| `Application.IntegrationTests` | 9 | 9 | 9 | 0 |
| `Application.UnitTests` | 407 (+12 skipped) | 407 (+12 skipped) | **418** (+12 skipped) | +11 |
| `Server.UI.IntegrationTests` | 171 | 175 | **180** | +9 |
| **Total passed** | **795** | **808** | **824** | **+29** |
| Skipped / Failed | 12 / 0 | 12 / 0 | **12 / 0** | 0 |

**+29 is exactly the new tests, itemised:** `UserVisibilityTests` 9 and
`SuperiorBoundComponentTests` 4 from §B/A3; then `SwitchableTenantsTests` **11** and
`TenantSelectorComponentTests` **5** from §A. (11, not the 10 the §A plan called for: A5 added one.)

**No test was deleted or renamed.** One was **modified**, and only in its fixture:
`TenantSwitchAuthorizationTests.CreateService` now mocks `IPermissionQueryService` instead of
`IPermissionService`, because the service's constructor changed. Its **12 assertions are byte-identical** — the change is `Mock.Of<IPermissionService>` returning a bool becoming
`Mock<IPermissionQueryService>` returning two `PermissionModel` rows with the same two `Assigned`
flags. The fixture had to follow the constructor; nothing was relaxed to accommodate a result.

### 5.3 Warnings

**10 distinct locations, identical to the start state.** No file this pass touched compiles with a
new warning.

### 5.4 The boundary suites — green and byte-unmodified

`git diff --quiet HEAD` returns clean for every one of them:

| Suite | Result |
|---|---|
| `DataSourceScopeTests` (Pass 26) | unchanged, green — §B changes a scoped datasource |
| `UserTenantScopeComponentTests` (Pass 27) | unchanged, green — the grid and export still agree |
| `TenantVisibilityTests` (Pass 27) | unchanged, green — the **visibility** bound, which §A must not disturb |
| `SuperiorAutocompleteScopeComponentTests` (Pass 27) | unchanged, green — the component clause still narrows |
| `UserVisibilityTests` (Pass 28 §B) | unchanged, green |
| `SuperiorBoundComponentTests` (Pass 28 A3) | unchanged, green |

46 tests across the six. Two of them carry the load for §A specifically: `TenantVisibilityTests`
passing untouched is the evidence that adding a *switchability* bound did not move the *visibility*
bound, which is the collapse §2.5 says was refused. And Pass 27's grid/export tests passing untouched
is the evidence that moving §B's bound out of `CreateSearchPredicate` into `VisibleUsers()` preserved
the behaviour.

### 5.5 The live run

**§B — the user query.** Against a freshly seeded two-tenant database, driving the **real**
`UserDataSourceService`:

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

**§A — the switch cases.** Three principals, each a member of `live-org-1` **only**, differing solely
in which switch permission they hold, driving the **real** `TenantSwitchService` resolved from the
real container against the real SQL Server harness:

```
SEEDED TENANTS: live-org-1/Org One, live-org-2/Org Two
EACH PRINCIPAL IS A MEMBER OF live-org-1 ONLY.

[SwitchTenants only ]  OFFERED = [live-org-1]
    CanSwitchTo(live-org-1)     = True    offered = True
    CanSwitchTo(live-org-2)     = False   offered = False
    CanSwitchTo(no-such-tenant) = False
    SWITCH -> live-org-2: Succeeded=False  PERSISTED TenantId=live-org-1

[SwitchToAnyTenant  ]  OFFERED = [live-org-1, live-org-2]
    CanSwitchTo(live-org-1)     = True    offered = True
    CanSwitchTo(live-org-2)     = True    offered = True
    CanSwitchTo(no-such-tenant) = False
    SWITCH -> live-org-2: Succeeded=True   PERSISTED TenantId=live-org-2

[neither right      ]  OFFERED = []
    CanSwitchTo(live-org-1)     = False   offered = False
    CanSwitchTo(live-org-2)     = False   offered = False
    CanSwitchTo(no-such-tenant) = False
    SWITCH -> live-org-2: Succeeded=False  PERSISTED TenantId=live-org-1
```

Every §C case, live: the membership holder is offered their own tenant and **refused a non-member
tenant with nothing written**; the cross-tenant holder is offered both, switches into a tenant they
have no membership row for, and **the write lands**; the holder of neither is offered nothing and
cannot switch. `offered` equals `CanSwitchTo` on every line — the property test's invariant holding
against a real database, not a mock.

The `no-such-tenant` column is the A5 finding. On the **first** run of this probe that line read
`True` for the cross-tenant holder; the capture above is the re-run after the fix.


### 5.6 Generation probe

```
dotnet pack build/pack.csproj -o .          → GX.Blazor.Template.1.0.0.nupkg
dotnet new install ./GX.Blazor.Template.1.0.0.nupkg
dotnet new gxblazor -n P28A                 → created
dotnet build P28A.slnx                      → Build succeeded, 0 Error(s)
dotnet test  P28A.slnx                      → 824 passed, 12 skipped, 0 failed
dotnet new uninstall GX.Blazor.Template     → uninstalled
```

Identical to source, suite for suite. **Generated at `C:\gxp28`, not in the scratchpad:** the first
attempt failed with `MSB3021 … exceeds the OS max path limit` on a `browser-wasm` native asset. That
is the session scratchpad's own depth plus the repo's nesting, not a template defect — the same
template generates and tests clean two directories down. Recorded in case the next pass generates
into a deep path and reads the error as a regression.

---

## 6. README

**One surface changed status** — the tenant switcher. Three other clauses were made more precise:

- The Users-area paragraph now names `UserTenantVisibility.IsVisibleTo` as the single definition and
  states that the datasource shares it.
- A new sentence records that the bound is **at the query, not the view** — *"a list filtered only as
  it is drawn is still a list the server fetched and held"*, which is the substance of §B.
- The "superior" row now says the list it searches is bounded by the same rule *and then* narrowed to
  the edited user's tenant, which is §3.3's two-layer distinction stated where a reader will meet it.

- The **tenant switcher row** changed status. It read *"n/a — bounded by membership, not visibility"*,
  which was true of the old component only by accident: it was gated on one permission and sourced
  from a membership projection, so it agreed with membership without ever asking the question. It now
  reads **"Yes, on a different bound"** and names the ladder, then states that the menu and the guard
  on the write derive from one rule — which is the property a reader needs in order to trust it.

---

## 7. What remains unscoped

**Audit trails, system logs, roles, picklists, security settings, presence and chat** — unchanged by
this pass and stated as such in the README. Stage 5 takes the first three.

§A is no longer on this list. What it leaves behind is **A1**: `UserProfile.AvailableTenants` is now
computed and unread, kept deliberately and flagged for its own decision.

---

## 8. File map and diffstat

**Modified — source (6)**

| File | Pass part | Why |
|---|---|---|
| `…/Services/Identity/UserDataSourceService.cs` | §B | the bound, the scope correction, the permission query |
| `…/Identity/Users/Components/UserFormDialog.razor` | A3 | `InputModel.TenantId` and the picker's bound |
| `src/Server.UI/Pages/Identity/Users/Users.razor` | §B, A3 | `VisibleUsers()`, the shared rule, A3's model wiring |
| `src/Infrastructure/Services/TenantSwitchService.cs` | §A | `SwitchScope`, `ScopeForAsync`, `GetSwitchableTenantsAsync`, `TenantExistsAsync`, `IPermissionQueryService` |
| `src/Application/Common/Interfaces/ITenantSwitchService.cs` | §A | `GetSwitchableTenantsAsync` on the contract |
| `src/Server.UI/Components/AppShell/TenantSelector.razor` | §A | the gate, the source, §A.4's absent-not-disabled branch |
| `README.md` | §6 | the switcher row and three clauses |

**New — source (1)** · `src/Application/Features/Identity/UserTenantVisibility.cs` — 65 lines, §B's
shared rule.

**New — tests (4)**

| File | Lines | Tests | Pass part |
|---|---:|---:|---|
| `tests/Infrastructure.UnitTests/Services/UserVisibilityTests.cs` | 205 | 9 | §B |
| `tests/Server.UI.IntegrationTests/SuperiorBoundComponentTests.cs` | 218 | 4 | A3 |
| `tests/Application.UnitTests/Identity/Users/SwitchableTenantsTests.cs` | 288 | 11 | §A |
| `tests/Server.UI.IntegrationTests/TenantSelectorComponentTests.cs` | 197 | 5 | §A |

**Modified — tests (1)** · `TenantSwitchAuthorizationTests.cs`, fixture only (§5.2).

**Diffstat, §A alone:** `5 files changed, 240 insertions(+), 50 deletions(-)` plus 2 new test files.
No migration was touched by any part of this pass — it changes no schema.

### Edit fidelity

- **Line endings uniform** — every touched file is wholly CRLF, matching the repo; no file was left
  mixed.
- **No BOM added or removed.** `TenantSwitchService.cs` and `TenantSelector.razor` still carry
  theirs at offset 0, verified with `od`. One edit displaced the BOM to line 2 mid-pass (§10 A6);
  it was restored before the file was built against.
- **No scaffolding left behind** — `grep -rn "PRE-PASS-28" src/ tests/` returns nothing.
- **No test was weakened** to accommodate a change. The one test file modified changed only its
  constructor mock, and only because the constructor changed (§5.2).

---

## 9. Scratch probe disclosure

| Probe | Purpose | Disposed |
|---|---|---|
| `scratchpad/p28/` | backups of §B's two changed sources for the red captures | deleted |
| `scratchpad/probe28/` | a console project driving the real datasource against the live database | deleted |
| `scratchpad/live28/` | the seeded SQLite business and log databases | deleted |
| `scratchpad/p28a/` | backups of §A's two changed sources for the red captures | deleted |
| `tests/…/Services/ScratchLiveTenantSwitchProbe.cs` | §A's live run — three principals against the real SQL Server harness | **deleted from the repo** |
| `C:\gxp28\P28A` | the generated project | deleted, template uninstalled |
| `C:\src\P28` | the earlier generated project | deleted |

The §A live probe is worth naming precisely because it lived **inside the test tree** rather than in
the scratchpad: it needed the `Application.IntegrationTests` container to resolve the real service
against the real database. It reached the harness's private scope factory **by reflection**
specifically so that no shared harness file was modified by a scratch probe. `git status` after
removal shows only the six §A files, no residue.

No database on any server was created or dropped; the probe seeded two tenants into the harness
database the suite already resets between tests. The root `.nupkg` was rebuilt and is gitignored.

---

## 10. Anomalies

**A1 — `AvailableTenants` is now unconsumed.** It had exactly one reader (§2.2), and §A moved that
reader to `GetSwitchableTenantsAsync`. It is now a correct, cheaply-computed, entirely unread field
on a public record — the same shape as `PickUserAutocomplete` before Pass 27 deleted it. **Kept, by
direction:** the ratification said it stays exactly as it is. Removing a public member of
`UserProfile` is visible to every generated project and deserves its own decision; flagged so that
decision gets made rather than forgotten.

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

**A5 — `CanSwitchToTenantAsync` said yes to tenants that do not exist. Found by the live run, fixed.**
The `All` branch returned `true` unconditionally, so a `SwitchToAnyTenant` holder was "permitted" any
string at all. The write was still refused — `SwitchToTenantAsync` looks the tenant up and fails —
so there was no exposure. But two things were wrong anyway, and both are the kind a mock cannot show:

1. **It broke the property §A exists to guarantee.** `permitted` was `true` while `offered` was
   `false`, because a list can only offer tenants that exist. The property test quantifies over the
   installation's real tenants and so was structurally unable to see it. Only the live probe, which
   asked about a fabricated id, did.
2. **It made the refusal message distinguishable** — *"User or tenant not found"* rather than
   *"Insufficient permissions"* — so a caller could tell a real tenant id from an invented one. That
   is precisely the enumeration leak the comment already in `SwitchToTenantAsync` says the uniform
   message exists to prevent. The guard was contradicting its own file's stated intent.

Fixed with `TenantExistsAsync` in the `All` branch; the `Membership` branch never had the hole,
because a `TenantUsers` row implies the tenant. Red captured before the fix, one new test.

**This is the pass's argument for live runs.** Eleven unit tests and five component tests were green
against the defect. It took asking a real database an off-menu question to find it.

**A6 — `sed -i '1i'` displaces a UTF-8 BOM to line 2.** The BOM is bytes, not a line, so an insert-at-1
lands after it. `TenantSwitchService.cs` briefly had `\357\273\277` at the start of line 2 and a
`using` at the start of line 1. Caught with `head -c 20 | od -c` and repaired before the file was
built against. Recorded because it is silent: the file still compiles, and the corruption is only
visible in a byte dump.

**A7 — a Razor edit that removes an `@if` can leave an orphaned `else` block that still builds.**
Deleting `@if (_availableTenants != null)` from `TenantSelector.razor` left a dangling `}` and its
`else { …Loading organizations… }`. **The build succeeded** — Razor treated the fragment as literal
markup — so the build was not evidence of correctness. Found by reading the block, not by compiling
it. Worth remembering wherever a Razor conditional is removed.

**A8 — two bUnit facts about `MudMenu`, each of which cost a cycle.**

*Its items are not in the component's markup.* `MudMenu` renders `ChildContent` into
`MudPopoverProvider`, so asserting on `Render<TenantSelector>().Markup` finds the activator and
nothing else. The fixture renders the provider alongside the component and concatenates both. Same
shape as A4's `MudDialog` finding from earlier this pass — MudBlazor's overlay components
consistently render elsewhere.

*The activator `div` has no click handler.* It carries `onkeydown` only; the click lives on the
chevron `MudIconButton` inside it, wired to `context.ToggleAsync`. `Find(".mud-menu-activator")`
followed by `.Click()` throws `MissingEventHandlerException` naming the keydown handler — a good
error, but only if you read it. `Find(".mud-menu-activator button")` is the working selector.

**A9 — `Testing.RunAsAdministratorAsync` cannot succeed.** Unrelated to this pass, found while
writing the live probe. It resolves `RoleManager<IdentityRole>`, but the application registers
`RoleManager<ApplicationRole>`; `GetService` returns `null` and the next line throws
`NullReferenceException`. `RunAsDefaultUserAsync` is unaffected — it passes an empty roles array, so
the null is never dereferenced — which is why nothing has caught this: **no test calls the
administrator helper.** The probe did, and it failed immediately. It is a one-word fix
(`IdentityRole` → `ApplicationRole`), but it is in a shared harness file and belongs to whichever
pass first needs a role-bearing test principal, not to a scratch probe. **Left as found and recorded
here.**
