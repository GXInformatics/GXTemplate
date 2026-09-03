# Pass 27 — Stage 4: Scope the Users Surfaces — **§A GATE REPORT**

**Nature:** decision gate. **Nothing was implemented.** **No git actions.**
**Date:** 2026-09-03.

> **Two things to read first.**
> **(1)** The precondition is not met — **Pass 26 is uncommitted** (§1.1). Every substantive check
> passes; only the commit is missing.
> **(2)** §A ends in a mandatory stop, so this report is the whole of what this pass produces until
> you ratify the decision. §B–§E are *prepared* — the findings each of them asked for are
> established below — but **no file was changed**.

---

## 1. Start state

| | |
|---|---|
| HEAD | `9630626656da7d18ab622767fc83fd6471a6c37a` — *"pass25"* — **not the Pass 26 commit** |
| Working tree | **16 entries uncommitted** — exactly Pass 26's work, nothing foreign |
| Build | **0 errors** |
| Warning locations | **10 distinct** — matches |
| Tests | **772 passed, 12 skipped, 0 failed** — matches |
| Spot-check `DataSourceServiceBase` abstract `Scope` | present (`:74`) |
| Spot-check `Permissions.NavigationMenu` | absent |

### 1.1 The precondition

Both spot-checks pass and every number matches, so the *code* is Pass 26's; what is missing is the
commit. The 16 uncommitted entries are Pass 26's 13 modified/deleted files plus its report and two
new test files.

**This did not affect the pass**, because §A implements nothing — it is a read-only investigation
ending in a stop. I have therefore not touched the tree, and Pass 26 remains cleanly committable on
its own. **Commit it before ratifying §A**, so that Stage 4's implementation does not become the
third body of work in one tree.

---

## 2. §A — The gate: which permission grants cross-tenant visibility?

### 2.1 The facts, re-confirmed at source

**Pass 25 A3 holds, and is worse than recorded.** The tenant selector is gated and sourced in two
independent places, and both exclude a `SwitchToAnyTenant` holder:

| Where | What it does |
|---|---|
| `TenantSelector.razor:18` | the whole menu is `Disabled="!_hasSwitchPermission"` |
| `TenantSelector.razor:100` | `_hasSwitchPermission = HasPermissionAsync(Permissions.Users.SwitchTenants)` — **`SwitchToAnyTenant` is not consulted** |
| `TenantSelector.razor:93` | the list is `UserProfile.AvailableTenants` |
| `ApplicationUserDto.cs:100` | `AvailableTenants` ← `Tenants` |
| `MapsterConfiguration.cs:20` | `Tenants` ← `src.TenantUsers.Select(tu => tu.Tenant)` — **membership only** |

So:

| Principal holds | Menu enabled? | Non-member tenants offered? | Can reach another tenant by switching? |
|---|---|---|---|
| `SwitchTenants` only | yes | no | **no** |
| `SwitchToAnyTenant` only | **no** — menu disabled | no | **no** |
| **both** | yes | **no** | **no** |

**Nobody can switch into a non-member tenant from the UI, whatever they hold.** Pass 25 made
`CanSwitchToTenantAsync` permit it; nothing offers it.

**And switching is a write.** `TenantSwitchService:72-77` sets `user.TenantId = tenantId` and calls
`userManager.UpdateAsync`. It is a persistent change to the principal's own record, and since
`AuditableEntityInterceptor` stamps new rows from `UserContext.TenantId`, it also changes which
tenant everything they subsequently create belongs to.

### 2.2 The options, weighed

**Option 1 — reuse `SwitchToAnyTenant`.** Its case is the equivalence argument: a principal who can
become any tenant can already see any tenant's data, so a visibility right adds nothing.

**The equivalence fails on three independent counts, and only the first is fixable by fixing the
selector:**

1. **It is not reachable.** No principal can switch to a non-member tenant today (§2.1). The
   argument's premise is currently false for everyone.
2. **Switching is serial; visibility is simultaneous.** "See tenant B's users" via switching means
   becoming tenant B, looking, and becoming tenant A again — one tenant at a time, never the
   cross-tenant grid the escape is for. An auditor comparing two tenants cannot do it by switching.
3. **Switching is a write; viewing should be a read.** Using the switch as a way to look costs a
   mutation of your own identity record and re-stamps every row you create afterwards. A read
   capability that can only be exercised by writing is the wrong shape, and it pollutes the audit
   trail Pass 24 just made tenant-aware.

Fixing the selector removes objection 1. **It does not touch 2 or 3.**

**Option 2 — a distinct constant.** Its cost is the usual cost of a new permission, and this
template has already removed that cost. `AdministratorPermissionRegistry.Validate` (`:192-235`)
throws on a constant that is *"Neither granted nor excluded"*, is called at startup (`:184`) and by
the test suite, and Pass 26 exercised all three of its divergence directions. **A new constant
cannot be added silently — it fails the build's test run and the application's startup until
somebody decides.**

There is also a mechanical saving. `PermissionService` derives the claim string from the
`*AccessRights` **property name**, so a `Users.`-sectioned constant gets `_accessRights.ViewAllTenants`
for free, in the idiom `Users.razor` already uses for its other fifteen rights. A constant in a
neutral section would need a separate `HasPermissionAsync` call and a second mechanism on the page.

**Option 3 — one installation-wide cross-tenant right** (e.g. `Permissions.Tenants.ViewAcrossTenants`),
governing every area at once. Rejected: it makes one grant omniscient across users, audit trails,
logs, picklists and everything Stage 5+ scopes. For an *isolation escape* that errs permissive, the
granular form is the safer default, and it matches how every other capability here is sectioned
(`Documents.Export`, `Logs.Purge`). Each area's escape should be granted deliberately.

### 2.3 Recommendation

**Option 2. Add `Permissions.Users.ViewAllTenants`, with a matching
`UsersAccessRights.ViewAllTenants`.** This agrees with your lean, and the evidence strengthens rather
than merely permits it: the two capabilities are not equivalent *in principle* (2 and 3 above), and
they are not equivalent *in fact* either (1).

**Grant it to the administrator** in the registry. The bootstrap administrator is seeded into both
tenants, so this preserves today's behaviour for a default install — the pass changes what is
*enforced*, not what the out-of-the-box administrator sees — while making the capability revocable
and named. That is the same posture Pass 22 took for `IsActive`: a default that is stated rather
than inherited.

**And Pass 25 A3 is a separate defect, not an alternative.** Whichever permission is chosen,
`TenantSelector` is wrong: it gates on `SwitchTenants` alone while the service it calls honours
`SwitchToAnyTenant`, so the escalated permission has no UI. **I recommend fixing it in this pass**
(it is the third dropdown, §3.1) — but as a repair of the *switching* surface, not as the escape for
the *visibility* one. They are different questions and the fix does not settle §A.

### 2.4 What the escape means at each surface

**Recommendation: the dropdown's "ALL" continues to mean "everything I may see", and the allowed set
is what changes.** A cross-tenant holder therefore sees all tenants by default and can narrow with
the existing control.

The alternative — default to the principal's own tenant, with the dropdown as a widening control —
is more conservative but worse in all three real cases: a single-tenant administrator gains a step
that changes nothing; a legitimate two-tenant administrator gets a default narrower than their
permission; and an auditor with `ViewAllTenants` has to widen every time to do the only thing the
right is for.

`Users.razor:277` already defaults `_selectedTenantId` to `string.Empty` and `:50` renders an "ALL"
item, so this needs **no UI change** — only the list behind it and the predicate beneath it. If you
prefer the conservative default it is a one-line change to that field, and I will make it instead.

---

## 3. §B–§E — prepared, not implemented

Everything below is a finding, not a change. No file was modified.

### 3.1 §C's question answered: there are **three** tenant dropdowns, and two bounds

The brief asks whether the filter dropdown and `TenantSelect` want different answers. **They do
not — but a third dropdown does, and it is the one Pass 25 A3 is about.**

| # | Control | Question it answers | Source today | Correct bound |
|---|---|---|---|---|
| 1 | `TenantSelector.razor` (app shell) | which tenants may I **switch into** | `UserProfile.AvailableTenants` (membership only) | membership, widened by `SwitchToAnyTenant` |
| 2 | Users page filter (`Users.razor:48-55`) | which tenants may I **filter by** | `TenantService.DataSource` — every tenant | `AllowedTenantIds`, widened by `ViewAllTenants` |
| 3 | `TenantSelect` (`UserFormDialog`) | which tenants may I **assign a user to** | `TenantsService.DataSource` — every tenant | same as 2 |

**2 and 3 share a bound and share a source.** Both bind the same `IDataSourceService<TenantDto>`, so
scoping `TenantDataSourceService.LoadAsync` fixes both in one change. You cannot assign a user into a
tenant you cannot see — the visibility set is the right bound for the write, which closes the
escalation the brief asks about.

**1 is on a different source entirely** (`UserProfile.AvailableTenants`, not the datasource), so it
is untouched by scoping the datasource, and its bound is switchability rather than visibility. That
is the structural reason A3 is a separate repair.

**`TenantDataSourceService` is already `PerUser`** (Pass 26 §3.2) — confirmed, not assumed — which is
the correct partition for a list bounded by `AllowedTenantIds`, since two administrators in the same
tenant can legitimately have different answers.

### 3.2 §D — the fail-closed decision

`PickSuperiorAutocomplete.razor.cs:27` reads
`(x.TenantId != null && x.TenantId.Equals(TenantId) || TenantId == null)`, and its one call site
(`UserFormDialog.razor:65-71`) passes no `TenantId` — so the clause is `|| true`.

**The component's default is the more important half, and I agree with your view that it is both.**
Passing the tenant at the call site fixes this call site; making the default fail closed fixes every
call site that has not been written yet. A filter whose absent-parameter behaviour is "everything"
is not a filter — it is a filter-shaped thing that defaults to the leak. The call-site fix without
the default fix would leave the next caller one forgotten parameter away from the same defect, and
that is exactly how this one arose.

`PickUserAutocomplete` has zero call sites (Pass 23 A2, Pass 26 A4) and should be deleted rather
than fixed.

### 3.3 §B — the shared predicate

`Users.razor:316-327` and `ExportUsersAsync` share `CreateSearchPredicate()`. Scoping inside it is
what makes the grid and the export one change rather than two, and §E.1's separate assertion on the
export is what would catch them being separated later.

### 3.4 What remains unscoped after Stage 4

To be stated in the report and moved in the README's Tenancy table when the work lands:

**Scoped after this pass:** Documents (already), Users grid, user export, the two tenant dropdowns,
the superior autocomplete.

**Still installation-wide:** audit trails, system logs, roles, picklists, security settings,
presence/chat and login notifications.

---

## 4. Verification performed

None beyond the start-state check — **no code was changed**. Build 0 errors, 10 warning locations,
772 passed + 12 skipped, all matching.

## 5. Scratch probe disclosure

**None.** This pass read the working tree and ran the existing build and test commands. Nothing was
created inside or outside the repository; the only file written is this report.

## 6. Anomalies

**A1 — `TenantSelector` gates on the wrong permission.** `:100` consults `SwitchTenants` only, so a
holder of `SwitchToAnyTenant` alone gets a disabled menu even though `CanSwitchToTenantAsync` (Pass
25) would permit them to switch. The permission Pass 25 revived is still unreachable. Recommended
for repair in this pass, separately from §A (§2.3).

**A2 — `UserProfile.AvailableTenants` cannot express a cross-tenant holder's set.** It is mapped
straight from `TenantUsers`, so even a corrected `TenantSelector` would have nothing to show a
`SwitchToAnyTenant` holder without a second source. Whoever fixes A1 has to decide where that list
comes from — the natural answer is the same `TenantDataSourceService` the other two dropdowns use,
bounded by switchability instead of visibility.

---

## 7. The gate

**Recommendation: option 2 — add `Permissions.Users.ViewAllTenants`, granted to the administrator,
with "ALL" continuing to mean "everything I may see".** Plus the separate repair of `TenantSelector`
(A1), which is not part of the §A decision but should not ship unfixed alongside it.

**Ratify or amend, and commit Pass 26 first.** On your word I will implement §B–§E as prepared, with
the export asserted separately from the grid, the fail-closed cases asserted rather than assumed,
and the narrowed-not-emptied controls that stop a predicate returning nothing from passing every
isolation test.
