# Pass 25 — Stage 1: Repair the Ground

**Nature:** editing pass — four independent repairs (§A–§D) and one report-only sweep (§E).
**No git actions.** **Date:** 2026-09-03.

> **Read §1.1 first.** The stated precondition — *"Pass 24 committed; clean tree"* — **was not met**.
> Pass 24's work is still uncommitted, so Pass 24 and Pass 25 now share one working tree. Every
> substantive start-state check passed, the pass was completed on your instruction to continue, and
> §1.1 records exactly what was done to keep the two separable.

---

## 1. Start state

| | |
|---|---|
| HEAD | `4425e1c647000b6301eb7443c743f10bbe5f2466` — *"pass22"* — **not the Pass 24 commit** |
| Working tree | **42 entries uncommitted** (all of Pass 24) |
| Build | **0 errors** |
| Warning locations | **10 distinct** — matches |
| Tests | **720 passed, 12 skipped, 0 failed** — matches |
| Spot-check `AuditTrail.TenantId` | present (`Domain/Entities/AuditTrail.cs:40`) |
| Spot-check `VisibleDocumentSpecification.IsVisibleTo` | present (`:49`) |

### 1.1 The precondition, and what was done about it

**The code is Pass 24's; only the commit is missing.** Both spot-checks pass, the build is clean, the
warning set is unchanged and the test count is exactly 720 + 12. Nothing is wrong with the tree
except that it has never been committed — and committing it is a git action this pass is forbidden.

The brief says *"Mismatch → STOP"*, and the stop was raised. On your instruction to continue, the
pass was completed with two safeguards, because the real cost of the missing commit is that the two
passes are no longer separable by `git diff`:

1. **Pass 24 was snapshotted before anything was touched** — a 232 KB patch of every tracked change
   plus copies of its 11 untracked files. Nothing in Pass 24 can be lost, and no `git checkout` was
   used on any file during this pass (in Pass 24 it was safe; here it would have destroyed
   uncommitted work).
2. **A full pre-pass baseline of `src/`, `tests/`, `README.md` and the nuspec** — 796 files — so
   Pass 25's own diffstat could be computed against it rather than against HEAD. **§7's file map and
   diffstat are Pass 25 alone**, not the two passes conflated.

**What you still need to do:** commit Pass 24 and Pass 25 separately, or accept them as one commit.
The snapshot makes the first possible; without it, it would not have been.

---

## 2. §A — `CanSwitchToTenantAsync` ignored both its parameters

### 2.1 The citation, re-confirmed at the point of change

Exactly as Pass 23 §3.5 recorded. The method read neither `userId` nor `tenantId`; it asked whether
the **current principal** held two permissions and returned the same answer for every tenant in the
installation. `SwitchToTenantAsync` then wrote `user.TenantId = tenantId` for whatever it was handed,
with nothing consulting `TenantUsers`.

### 2.2 The permission relationship — decided, with the evidence

**`SwitchToAnyTenant` implies `SwitchTenants`. The escalated right works alone; the finer-grained
one is bounded by membership.** This is the brief's expectation, and it is what the constants' own
descriptions say:

| Constant | Its own `[Description]` |
|---|---|
| `Users.SwitchTenants` | "Allows switching between **available** tenants" |
| `Users.SwitchToAnyTenant` | "Allows switching to **any** tenant (**admin privilege**)" |

"Available" is membership-bounded; "any" contains "available" and is marked as the escalation. No
other reading survives both sentences, so the evidence supports the brief rather than amending it.

**What the old code did instead** was require *both*, which inverted both meanings: holding
`SwitchTenants` alone granted nothing at all (Pass 22 finding 4), and an administrator revoking
`SwitchToAnyTenant` — intending to leave someone switching only among their own tenants — actually
removed all switching.

### 2.3 The fix

```csharp
if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(tenantId)) return false;

// The escalated right subsumes the other, so no further test applies to its holder.
if (await _permissionService.HasPermissionAsync(Permissions.Users.SwitchToAnyTenant)) return true;
if (!await _permissionService.HasPermissionAsync(Permissions.Users.SwitchTenants)) return false;

await using var db = await _dbContextFactory.CreateAsync();
return await db.TenantUsers.AnyAsync(tu => tu.UserId == userId && tu.TenantId == tenantId);
```

**Membership is read by the `userId` argument, not from the ambient principal's cached context.**
Resolving it from the ambient context would ignore the parameter all over again — the exact defect
being fixed — and would also be wrong the moment this method is called about somebody other than the
caller.

**`SwitchToTenantAsync` enforces rather than trusts.** It already called `CanSwitchToTenantAsync`
first; with that method fixed, the write path inherits a real check. The comment there now says why
the tenant selector's behaviour is not a substitute: it is one component's rendering, and any other
caller reaches the same write through the same method.

**A deliberate non-change:** a `SwitchToAnyTenant` holder switching into a tenant they do not belong
to leaves `TenantId` outside their membership set. That is the capability working, not a defect —
and it is precisely why §C's union is needed. The two changes are load-bearing for each other.

---

## 3. §B — `TenantId` and `TenantUsers` diverged by construction

### 3.1 Every writer of either field

Surveyed at this HEAD rather than taken from the prior report:

| Writer | Writes `TenantId` | Writes `TenantUsers` | Consistent? |
|---|---|---|---|
| `ApplicationDbContextInitializer.EnsureAdministratorAsync` | `tenant.Id` | `[tenant.Id]` | **Yes** |
| `ApplicationDbContextInitializer.SeedSampleTenantAsync` | untouched | adds "Europe" | **Yes** — the primary stays in the set |
| `Register.razor:139, :188` | `_formModel.TenantId` | one row = `user.TenantId` | **Yes** |
| External-login provisioning (`IdentityComponentsEndpoint…:465, :487`) | `tenantId` | `[tenantId]` | **Yes** |
| `UserFormDialog` — **create** (`:278`) | first selected | all selected | **Yes** |
| `UserFormDialog` — **edit** (`:211-214`) | **only when empty** | all selected, unconditionally | **NO — the defect** |
| `TenantSwitchService.SwitchToTenantAsync` | `tenantId` | untouched | **By design** — see §3.4 |

**One administrative writer was broken, not several.**

**A correction to Pass 23 §4.1**, which said provisioning "can produce a `TenantId` with no
`TenantUsers` row". At this HEAD it does not: external-login provisioning sets both, in the same
object initialiser. The only source of divergence was the edit path.

### 3.2 The rule

**After any administrative save, `ApplicationUser.TenantId` is one of the user's `TenantUsers` rows —
or the user belongs to nothing and both say so.** Total, with no state in which the two disagree.

It is expressed once, in **`PrimaryTenantRule.Resolve`**, and called by both the create and the edit
path:

- **An existing primary is kept when it is still selected.** The form offers a *set* with no
  primary-tenant concept (`TenantSelect MultiSelection="true"`, bound to `Model.Tenants`), so "first"
  is whatever order the multi-select returns. Re-deriving unconditionally would let an edit to a
  phone number silently move a multi-tenant user's primary tenant — and with it the tenant every row
  they subsequently create is stamped with.
- **Otherwise it moves to the first of what remains.**
- **An empty selection yields `null`.** `ValidateTenants` refuses that at the form, but the
  membership rewrite deliberately persists an empty set (a Pass-21-era fix), so the empty case is
  handled rather than assumed away.

### 3.3 Why the rule moved out of the `.razor` file

`PrimaryTenantRule` lives in `Application/Features/Tenants/`, not in the dialog. It is an invariant
about the data, not a detail of one form — and a rule inside a `.razor` file can only be tested by
replaying a copy of it, which tests the copy. The dialog is now a two-line adapter over it.

### 3.4 Already-divergent data, and the switcher

**Existing rows are not migrated.** Nothing rewrites historical data, and this pass deliberately does
not: a background fix-up would silently move users between tenants, which is exactly the class of
event an administrator should perform, not discover. What handles them instead is §C's union — a
user whose primary tenant has no membership row still reports it in `AllowedTenantIds`, so they are
readable rather than invisible.

The tenant switcher legitimately produces the same shape for a `SwitchToAnyTenant` holder (§2.3).
**So the invariant is over administrative writes, and the union is what makes the switcher's output
safe to read.** Stating it that way is more honest than claiming divergence is now impossible.

### 3.5 A third copy of the fact, found while fixing the second

The dialog cleared the cached `UserContext` only when **roles** changed — the invalidation sat inside
the role-comparison branch. `UserContextLoader` caches `TenantId` and `AllowedTenantIds` for **an
hour**, so a user moved from A to B went on acting as a member of A for the rest of that hour, with
both database fields agreeing and neither of them being read. Tenancy now has its own trigger:

```csharp
if (!string.Equals(previousTenantId, existingUser.TenantId, StringComparison.Ordinal))
{
    _userContextLoader.ClearUserContextCache(existingUser.Id);
}
```

---

## 4. §C — `AllowedTenantIds` is now the union

```csharp
var allowedTenantIds = string.IsNullOrEmpty(user.TenantId)
    ? memberships.Distinct().ToList()
    : memberships.Append(user.TenantId).Distinct().ToList();
```

**`Distinct`, because the ordinary case has the primary tenant in both sources** — a caller counting
the set would otherwise get the wrong answer, and an `IN` clause a pointless repeat.

**The null/empty distinction is preserved, and now documented where consumers will see it.** The
loader always produces a list, so from it `[]` means "computed, and this principal belongs to
nothing". `null` remains reserved for a context built some other way — a test double, or a caller
constructing the record directly. The `UserContext` record now carries that contract on the
parameter itself:

> Treating `null` as empty would turn "unknown" into "denied everything"; treating empty as `null`
> would turn "belongs to nothing" into "unconstrained".

`ClearUserContextCache` is unchanged and still evicts the whole entry, so the union invalidates with
everything else — asserted by `ClearUserContextCache_MakesTheNextLoadSeeANewTenant`.

---

## 5. §D — `Permissions.Users.Deactivation` now enforces something

### 5.1 The gate

The toggle at `Users.razor:201-222` checked **nothing**. It is now gated on `_accessRights.Deactivation`,
in the page's existing `*AccessRights` idiom, in two places:

- **The cell** renders a **disabled** checkbox without the permission rather than nothing at all.
  Seeing whether an account is active is part of viewing users — an operator asking "why can this
  person not sign in?" needs the answer whether or not they may change it. The gate removes the
  action, not the information.
- **`ToggleUserActiveStatusAsync`** re-checks before doing anything. In Blazor Server the render gate
  is closer to a real boundary than usual — there is no separate endpoint — but a cell template is
  not an authorization boundary and the callback outlives the render.

### 5.2 The last-administrator case — found, and it was a one-liner

**It was unprotected, and it is the same failure as deleting the last administrator.** An inactive
account is refused at sign-in (`IdentityComponentsEndpointRouteBuilderExtensions`, with `Login.razor`
behind it), so deactivating the last administrator would leave nobody able to activate anyone —
locked out permanently, with no route back through the UI.

`AdministratorProtectionService.EnsureNotRemovingLastAdministratorAsync(userId, action)` already
existed, was **already injected into this very page**, and was already used by the delete path at
`:506` and `:550`. It was simply never applied here. The fix is the same four lines the delete path
uses, with `"deactivated"` as the action word.

**Activation deliberately has no guard**: it can only increase the number of usable accounts.

---

## 6. §E — The inert-permission sweep (report only, nothing implemented)

### 6.1 A correction to the count: there are **eleven**, not ten

The sweep was recomputed rather than inherited, because a permission can be enforced by **two**
routes and Pass 23's figure counted only one cleanly:

1. the constant named directly (`RequestAuthorize`, `[Authorize(Policy=…)]`, `HasPermissionAsync`);
2. a matching `bool` on the section's `*AccessRights` class read in a Razor `@if` — which **never
   names the constant**, because `PermissionService` builds the claim string from the property name.

Counting both: **15 inert constants remain**, of which 4 are the knowingly-excluded
`EmailTemplates.*`, leaving **11 actionable**. Pass 23 reported 15 total and "eleven not knowingly
so", which cannot both be right; the true figures were 16 and 12 before §D, and are 15 and 11 now.

`Users.Deactivation` **dropped off the recomputed list**, which is an independent confirmation that
§D landed.

### 6.2 The eleven recommendations

| # | Permission | Finding | Recommendation |
|---|---|---|---|
| 1 | `AuditTrails.Search` | The page **has** a keyword box and an audit-type filter (`AuditTrails.razor:46-55`), and already loads `AuditTrailsAccessRights` — which it then never reads. | **Wire.** One `@if (_accessRights.Search)` around the search controls, exactly as Documents and PicklistSets do. The capability exists and is simply ungated. |
| 2 | `Logs.Search` | Same shape: `SystemLogs.razor:56-57` has a level filter and a keyword box; `LogsAccessRights` is loaded and only `Purge` is read. | **Wire.** Same one-liner. |
| 3 | `Documents.Export` | No button anywhere, and `Queries/Export/ExportDocumentsQuery.cs` is a **3-byte empty file** (Pass 23 A1). | **Delete** — constant, `DocumentsAccessRights.Export`, the registry grant, and the empty file. Exactly the precedent Pass 11B/11C set for `Logs.Export`. |
| 4 | `Documents.Import` | No button; `Queries/GetAll/GetAllDocumentsQuery.cs` likewise empty. | **Delete**, with the same reasoning. |
| 5 | `Roles.ManageUsersInRole` | **No users-in-role UI exists.** The Roles page's only permission feature is `LoadRolePermissionsAsync`, gated by `ManagePermissions`, which *is* enforced. | **Knowingly exclude.** Move from `Granted` to `Excluded` with a stated reason, the `EmailTemplates.*` shape — the name stays reserved for when the surface is built. |
| 6 | `Roles.ViewUsersInRole` | Same — no such surface. | **Knowingly exclude**, same reason. |
| 7 | `Roles.ManageClaimsInRole` | Same — no claims-in-role management. | **Knowingly exclude**, same reason. |
| 8 | `Roles.ViewClaimsInRole` | Same. | **Knowingly exclude**, same reason. |
| 9 | `Roles.ViewPermissions` | There is no read-only permission viewer; viewing happens inside the dialog `ManagePermissions` already gates. | **Knowingly exclude.** Wiring it would mean building a read-only mode that does not exist. |
| 10 | `Dashboards.View` | `Pages/Dashboard/Dashboard.razor` exists and is routed at **`@page "/"`** with no `[Authorize]` policy — it is the landing page for every authenticated user. | **Knowingly exclude, do not wire.** Gating `/` would strand a user without the right on a 403 at sign-in. Reserve the name until the dashboard moves off the root route; then it becomes a one-line page attribute. |
| 11 | `NavigationMenu.View` | The menu is gated by **roles**, not permissions (`MenuSectionItemModel.Roles`), and every destination carries its own permission. | **Delete.** It is a third gating mechanism nothing consults, and if it were consulted it would be a second, weaker gate in front of destinations that are already protected. |

**Net if all eleven are actioned:** 2 wired, 3 deleted, 6 knowingly excluded — after which every
declared permission either enforces something or says in the registry why it does not.

**All eleven are product decisions and none was implemented**, per the brief.

---

## 7. §F — Verification

### 7.1 Red before, green after

Captured by restoring the pre-Pass-25 body of each of the four sites, building, running the new
tests, then restoring the fixes from a backup taken first. **`grep -rn "PRE-PASS-25" src/` returns
nothing**, so no scaffolding survived.

**18 red / 20 green in the reverted state → 38 green after.**

| Area | Test | Failure in the reverted state |
|---|---|---|
| §A | `ANonMemberIsRefused` | *(passed — see note)* |
| §A | `AMemberMaySwitchToTheirOwnTenant` | *"Expected … to be True, but found False"* |
| §A | `SwitchToAnyTenantWorksAlone_BecauseItImpliesTheOther` | *"Expected … to be True, but found False"* |
| §A | `SwitchTenantsWorksAlone_ForATenantTheUserBelongsTo` | *"Expected … to be True, but found False"* |
| §A | `ACrossTenantHolderMaySwitchToATenantTheyDoNotBelongTo` | *"Expected … to be True, but found False"* |
| §A | `TheAnswerDependsOnTheTenantAsked_NotOnlyOnThePermissions` | *"Expected toOwn to be True, but found False"* |
| §A | `SwitchToTenantAsync_MovesAMemberAndPersistsIt` | *"Expected result.Succeeded to be True, but found False"* |
| §B | `MovingAUserFromOneTenantToAnother_MovesThePrimaryTenantWithThem` | *"to be "tenant-b", but "tenant-a" differs"* |
| §B | `AnExistingPrimaryMovesOnlyWhenItIsNoLongerSelected` | *"to be "tenant-a", but "tenant-c" differs"* |
| §B | `AnEmptySelectionYieldsNoPrimaryTenant` | *"to be `<null>`, but found "tenant-a""* |
| §B | `AnEmptySelectionAlwaysYieldsNull_WhateverThePrevious` | *"to be `<null>`, but found """* |
| §B | `TheResultIsAlwaysAMemberOfTheSelectedSetOrNull` | *"Resolve("", []) returned a tenant that was not selected"* |
| §B | `MovingAUserBetweenTenants_MovesBothRecords` | *"Expected primary to be "tenant-b", but "tenant-a" differs"* |
| §B | `ClearingEveryTenantLeavesNoPrimaryEither` | *"Expected primary to be `<null>`, but found "tenant-a""* |
| §B | `RemovingThePrimaryTenantMovesItToOneThatRemains` | failed |
| §B | `ASequenceOfEditsNeverLeavesTheTwoDisagreeing` | *"Expected memberships {"tenant-b"} to contain "tenant-a""* |
| §C | `APrimaryTenantWithNoMembershipRow_IsStillAllowed` | *"to be equal to {"tenant-c"}, but found empty collection"* |
| §C | `BothSourcesAreUnioned` | *"{"tenant-a", "tenant-b"} contains 1 item(s) less"* |
| §D | `WithoutTheDeactivationPermission_TheToggleIsNotClickable` | *"Expected DisabledCheckboxes(page) to be 1 …, but found 0"* |

**The twenty that stayed green in both states are the evidence, not the tally.** They include §A's
`NeitherPermissionGrantsNothing` and `AnUnknownUserIsRefused`, §C's
`NeitherSource_YieldsAnEmptyListRatherThanNull`, and §D's three control tests — so the changes
tightened what they should and left the rest alone. A `CanSwitchToTenantAsync` that returned `false`
for everything would satisfy most of the red tests above; those greens are what says it does not.

*Note on `ANonMemberIsRefused`:* it passes in both states, for opposite reasons — the old code
refused it because it demanded a second permission, the new code because the user is not a member.
That is why `TheAnswerDependsOnTheTenantAsked_NotOnlyOnThePermissions` exists: same principal, same
permissions, two tenants, two answers. **It is the one assertion the old implementation could not
satisfy under any permission configuration**, and it is the honest proof that the arguments are now
read.

### 7.2 Counts

| Suite | Start (Pass 24) | After | Delta |
|---|---:|---:|---:|
| `Infrastructure.UnitTests` | 192 | 192 | 0 |
| `Application.IntegrationTests` | 9 | 9 | 0 |
| `Application.UnitTests` | 372 (+12 skipped) | **406** (+12 skipped) | **+34** |
| `Server.UI.IntegrationTests` | 151 → *see note* 147 | **151** | **+4** |
| **Total passed** | **720** | **758** | **+38** |
| Skipped / Failed | 12 / 0 | 12 / 0 | 0 |

**+38 is exactly the new tests:** `TenantSwitchAuthorizationTests` 12, `PrimaryTenantRuleTests` 9,
`UserTenantConsistencyTests` 7, `UserContextAllowedTenantsTests` 6, and
`UserDeactivationPermissionComponentTests` 4. **No existing test was modified, renamed, deleted, or
had an expectation relaxed.**

### 7.3 Warnings

**10 distinct locations, identical to the start state** — same files, same line and column:

```
AuditTrails.razor(100,72) CS8602      DescriptionAttributeExtensions.cs(23,46) CS8603
Dashboard.razor(202,60)   CS8604      DescriptionAttributeExtensions.cs(33,20) CS8603
DescriptionAttributeExtensions.cs(12,45) CS8600   MapsterConfiguration.cs(26,32) CS8601
DescriptionAttributeExtensions.cs(20,32) CS8600   MapsterConfiguration.cs(28,29) CS8601
MudDateTimeField.razor(1,1) MUD0002               TenantSelect.razor(13,44)      CS8603
```

Every file this pass touched compiles warning-free.

### 7.4 Pass 24's stamping tests and the boundary suites

§B and §C touch the user context and tenancy, so these are the proof nothing moved. Run as a filtered
set: **51 tests, 0 failures** — `TenantStampingTests`, `DocumentTenantIsolationTests`,
`TransactionalAuditTests` and `UserRoleChangeSecurityStampTests` all green.

`UserRoleChangeSecurityStampTests` matters most of the four: it replays the *same dialog* §B changed,
including its own pre-fix demonstrations, and none of its expectations moved.

### 7.5 The live run

The application was booted against a fresh SQLite database, seeded normally (two tenants, "Default"
and "Europe"), and then the **real** `TenantSwitchService` and **real** `UserContextLoader` were
driven against that seeded database by a scratch probe:

```
tenants seeded            : Default, Europe
admin TenantId            : 01a066c7-60a7-79d7-9e3b-5005dfb414a1
admin TenantUsers rows    : 2
B  primary is a membership: YES
C  AllowedTenantIds       : [01a066c7-…414a1, 01a066c7-…45ee0] (count 2, distinct 2)
C  contains own TenantId  : True
A  member  + SwitchTenants: True   (expect True)
A  stranger+ SwitchTenants: False  (expect False)
A  stranger+ AnyTenant    : True   (expect True)
A  member  + no perms     : False  (expect False)
A  switch to other tenant : Succeeded=True
B  after switch TenantId  : 01a066c7-…45ee0 (expected 01a066c7-…45ee0)
B  still a membership     : YES
```

All three switch cases the brief asked for — member, non-member, cross-tenant holder — behave
correctly against real seeded data, and the §B invariant holds both on the seeded rows and after a
real tenant move. The §C union contains the user's own tenant with no duplicate.

**What the live run did not do:** drive the tenant selector or the user dialog through a browser. The
switch cases were exercised at the service the UI calls, and §D's render gate is covered by the
bUnit tests instead, which is the only level at which a render gate is visible at all.

### 7.6 Generation probe

```
dotnet pack build/pack.csproj -o .        → GX.Blazor.Template.1.0.0.nupkg
dotnet new install ./GX.Blazor.Template.1.0.0.nupkg
dotnet new gxblazor -n P25 -o P25         → created
dotnet build P25.slnx                     → 0 errors
dotnet test P25.slnx                      → 758 passed, 12 skipped, 0 failed
dotnet new uninstall GX.Blazor.Template   → uninstalled
```

The generated project was deleted and the template uninstalled.

---

## 8. File map and diffstat — Pass 25 only

Computed against the pre-pass baseline snapshot (§1.1), so Pass 24's changes are excluded.

**Modified (5)**

| File | Changed lines | Why |
|---|---:|---|
| `src/Infrastructure/Services/TenantSwitchService.cs` | 52 | §A — the membership check and the permission ladder |
| `src/Server.UI/Pages/Identity/Users/Components/UserFormDialog.razor` | 48 | §B — the primary-tenant rule on both paths, plus the cache eviction |
| `src/Server.UI/Pages/Identity/Users/Users.razor` | 44 | §D — the render gate, the handler check, the last-administrator guard |
| `src/Infrastructure/Services/Identity/UserContextLoader.cs` | 25 | §C — the union |
| `src/Application/Common/Interfaces/Identity/UserContext.cs` | 13 | §C — the null/empty contract, documented on the parameter |

**New — source (1)**

| File | Lines |
|---|---:|
| `src/Application/Features/Tenants/PrimaryTenantRule.cs` | 74 |

**New — tests (5 files, 38 tests)**

| File | Lines | Tests |
|---|---:|---:|
| `tests/Application.UnitTests/Identity/Users/UserTenantConsistencyTests.cs` | 298 | 7 |
| `tests/Server.UI.IntegrationTests/UserDeactivationPermissionComponentTests.cs` | 260 | 4 |
| `tests/Application.UnitTests/Identity/Users/TenantSwitchAuthorizationTests.cs` | 246 | 12 |
| `tests/Application.UnitTests/Identity/UserContextAllowedTenantsTests.cs` | 209 | 6 |
| `tests/Application.UnitTests/Features/Tenants/PrimaryTenantRuleTests.cs` | 114 | 9 |

**Diffstat:** 6 source files (5 modified, 1 new) totalling **182 changed lines + 74 new**; 5 new test
files totalling **1,127 lines**. No file was deleted. No migration was touched — this pass changes no
schema.

### Edit fidelity

- **Line endings unchanged.** The working tree is LF throughout, verified against files this pass
  never touched. The `LF will be replaced by CRLF` notices from git are the repository's standing
  `core.autocrlf=true` against `* text=auto`, not something introduced here.
- **No BOM added or removed.**
- **No scaffolding left behind.** The red-capture bodies are gone (`grep -rn "PRE-PASS-25" src/` →
  nothing), and the one diagnostic that wrote rendered markup to disk during §D's development was
  removed with the method that carried it.

---

## 9. Scratch probe disclosure

| Probe | Purpose | Disposed |
|---|---|---|
| `scratchpad/pass24-snapshot/` | §1.1 — a restorable copy of uncommitted Pass 24 | **retained deliberately** until you commit Pass 24 |
| `scratchpad/pass25-base/` | §1.1 — pre-pass baseline for computing this pass's own diffstat | **retained** until the report is accepted |
| `scratchpad/pass25-fixed/` | backups for the red-capture restore | deleted |
| `scratchpad/probe25/` | a console project referencing Infrastructure, driving the real services against the live database (§7.5) | deleted |
| `scratchpad/live25/` | the seeded SQLite business and log databases | deleted |
| `scratchpad/markup.txt`, `D-test-backup.cs` | §D rendering diagnostics | deleted |
| `C:\src\P25` | the generated project | deleted, template uninstalled |

No database on any server was created or dropped by this pass. `GX.Blazor.Template.1.0.0.nupkg` at
the repository root was rebuilt by §7.6 and is gitignored (`.gitignore:200`).

**The two retained snapshots are the only scratch artefacts left, and they exist because Pass 24 is
uncommitted.** Once you have committed, both can be deleted.

---

## 10. Anomalies

**A1 — Pass 23's inert-permission tally was wrong, and its method could not have been right.**
Counting only constants named directly misses every permission enforced through the `*AccessRights`
reflection idiom, which never names the constant. The recomputed figures are 15 inert / 11 actionable
(§6.1). The lesson generalises: `PermissionService` builds claim strings from **property names**, so
any audit of this permission set has to inspect both spellings.

**A2 — `CanSwitchToTenantAsync` checks the CURRENT principal's permissions but acts on the `userId`
argument.** With one caller, which passes the current user's own id, the two always coincide. They
would not for an administrator switching somebody else, and the method's signature invites exactly
that. Out of scope here — fixing it means deciding whether that operation should exist at all — but
it is now the only remaining place where this method's parameters and its permission source
disagree.

**A3 — the tenant selector offers `UserProfile.AvailableTenants`, which is still membership-only.**
It is mapped from `TenantUsers` in `MapsterConfiguration:20`, so a `SwitchToAnyTenant` holder is
offered only their own tenants even though the service would now permit any. The escalated permission
therefore has no UI. Not fixed: giving it one is a Stage 4 concern (which tenants a cross-tenant
administrator should see is the same question the Users grid asks), and the service being correct is
the prerequisite for either answer.

**A4 — `Users.razor` renders the status checkbox disabled rather than omitting it**, which is a
deliberate departure from the page's other gates (`_accessRights.Create` etc. omit their buttons
entirely). The reason is that the checkbox is *data* as well as a control — it is the only place the
grid shows whether an account is active. Recorded because it is an inconsistency a reader will notice
and should find explained.

**A5 — `ApplicationUserDto.Tenants` ordering is whatever the multi-select returns.** §B's rule keeps
an existing primary rather than depending on that order, which makes the ordering harmless today. It
would stop being harmless if a future caller derived anything else from `Tenants.First()`.

---

## 11. What was deliberately not done

**No filtering was begun.** Nothing reads `AllowedTenantIds` yet — §C made it correct, not consumed.
Stage 3 (the cache layer, Pass 23 §4.3) remains the prerequisite for Stage 4, because scoping
`ApplicationUser` while `DataSourceServiceBase` caches under process-wide keys would make the system
intermittently wrong rather than consistently open.

**§E was not implemented**, per the brief — eleven product decisions, recommended and left to you.

**Already-divergent tenancy data was not migrated** (§3.4). The union reads it correctly; moving a
user between tenants remains an administrator's act, not a background job's.
