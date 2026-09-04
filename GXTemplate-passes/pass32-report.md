# Pass 32 — Who May Change Installation-Wide Data

**Nature:** §A an editing section building a ratified decision; §B an investigation that stops.
**Date:** 2026-09-04.

**Result in one line:** shared picklist rows are now writable only by holders of
`Permissions.PicklistSets.ManageShared`, guarded in the command handlers and reflected in the grid;
along the way the pass found and fixed a **Pass 31 defect that made a documented behaviour false** —
the unique index still spanned tenants. §B recommends **option (a), global roles with editing gated
on a named right**, and stops. **877 → 899 tests**, warnings unchanged.

---

## 1. Start state

**The precondition failed on first check and this pass stopped.** HEAD was `0b0e64e0` *"Pass30"*, the
tree carried Pass 31's work uncommitted, and the brief's expected count of 891 did not match the 877
Pass 31 delivered. You then authorised the commit, confirmed 877 as the baseline, and settled the
standing conflict: **each brief now authorises committing the preceding pass as its first step.**

Pass 31 was committed as **`b23c54ec` — "Pass31-Picklists"**, distinct from `Pass30` and following
the `Pass29-QueryFilter` precedent. Pass 32's own report was deliberately excluded from it.

| | |
|---|---|
| HEAD after the authorised commit | `b23c54ec` — *"Pass31-Picklists"* |
| Working tree | clean apart from this report |
| Spot-check: `PicklistSet` has a query filter | present (`ApplicationDbContext.cs:148`) |
| Spot-check: `PicklistDataSourceService.Scope` is `PerTenant` | present (line 53) |
| Build | **0 errors, 19 warnings across 10 distinct source locations** |
| Tests | **224 + 12 + 437 + 204 = 877 passed, 12 skipped, 0 failed** |

The eleventh raw warning line is `NETSDK1206`, SDK-emitted once per project with no source location.

---

## 2. §A — The shared-picklist write right

### 2.1 The permission

`Permissions.PicklistSets.ManageShared`, following the section's own naming rather than the name the
brief suggested.

**One right for create, edit and delete rather than three.** The section's other constants are
per-verb, but this is not a verb — it is a **partition**. Splitting it would invite a grant that lets
a principal delete a shared value but not fix it.

**Not `EditAllTenants`,** which would have matched the `ViewAllTenants` shape and been wrong: this
grants nothing over another tenant's rows. It is a write right over the *shared* partition, which no
filter hides from anyone.

Granted to the administrator in `AdministratorPermissionRegistry`, for the reason Pass 29 gave for
`AuditTrails.ViewAllTenants`: **it preserves the posture that already held.** Before this pass any
holder of `PicklistSets.Edit` could change a shared value; granting the new right keeps the shipped
administrator able to do what it could do, while making the capability named and revocable. The
divergence assertion made adding it a deliberate act, which is the mechanism working as designed.

### 2.2 Why not read-only — the single-tenant case, stated

Pass 31 §5 established that `EnsureAdministratorAsync` assigns the bootstrap administrator
`Tenants.First()`, so it is itself tenant-scoped. A blanket "shared rows are read-only to a
tenant-scoped principal" would therefore have **frozen the seeded values for the life of the
installation** — and it would bite hardest in the single-tenant deployment that is the common case,
where the sole administrator would face reference data they cannot edit beside their own rows which
they can. The right avoids that: default-granted, so a single-tenant installation works out of the
box; revocable, so a multi-tenant one can stop a customer's administrator redefining the
installation's reference data.

### 2.3 Why this is not the escape Pass 31 §C declined

§C declined a cross-tenant **read** escape — nobody needs to see another tenant's *private*
picklists — and that stands untouched. This is a **write** right over the *shared* partition, which
is visible to everyone by design. The two are orthogonal, and
`AHolderStillCannotReachAnotherTenantsPrivateRow` asserts it: a holder editing another tenant's row
by id gets **not found**, not a permission refusal, because the query filter they cannot drop makes
the row invisible. Read-across and write-installation-wide are kept as different rights, the same
separation Pass 27 drew between `Users.ViewAllTenants` and `Users.SwitchToAnyTenant`.

### 2.4 The guards, and where they are

**One definition, three consumers** — `SharedPicklistWrite`, a new Application-layer type. Pass 28's
precedent, and the stake is Pass 29's: two copies of this rule would not disagree about which rows to
touch, they would disagree about **whether to check at all**.

Before, `AddEditPicklistSetCommandHandler`:

```csharp
var item = await db.PicklistSets.FindAsync(request.Id, cancellationToken);
if (item == null) return await Result<int>.FailureAsync($"PicklistSet with id: [{request.Id}] not found.");
item = _objectMapper.Map(request, item);
```

After:

```csharp
var item = await db.PicklistSets.FindAsync(request.Id, cancellationToken);
if (item == null) return await Result<int>.FailureAsync($"PicklistSet with id: [{request.Id}] not found.");

if (!await SharedPicklistWrite.IsAllowedAsync([item.TenantId], _permissionQueryService, userId))
    return await Result<int>.FailureAsync(SharedPicklistWrite.Refused);

item = _objectMapper.Map(request, item);
```

Three properties of that shape are deliberate:

- **The tenant checked is the STORED one**, read back before the guard runs. Taking it from the
  request would let a client claim a row was private and edit a shared value through the claim — the
  DTO round-trips through the browser.
- **`IPermissionQueryService`, not `IPermissionService`.** The latter resolves the principal through
  Blazor's `AuthenticationStateProvider`; Pass 27 and Pass 28 both hit that when an Application-layer
  type took the dependency and a non-Blazor host could no longer construct it.
- **The refusal is a `Result` failure carrying a stated reason**, which is the posture
  `TenantSwitchService.SwitchToTenantAsync` established — not an exception, and not a silent no-op
  that reports success while changing nothing.

`DeletePicklistSetCommandHandler` guards **all or nothing**, over every affected row's stored tenant.
One shared row in a multi-row selection refuses the whole command: a half-applied delete would leave
the caller to work out which rows survived, and a retry would then be a different request from the
one they issued.

### 2.5 §A.5 — Creation, and the answer is yes, it is already possible

The brief expected a holder to be able to create a shared row and asked what that would take. **It
takes nothing: they already can, and this pass had to guard it rather than enable it.**

`AuditableEntityInterceptor` stamps from the ambient principal, so the tenant a new row will carry is
the caller's own — and a caller with **no tenant** produces a **shared** row without ever touching an
existing one. Creating installation-wide reference data is the same capability as editing it, so the
create path is guarded on the prospective tenant:

```csharp
if (!await SharedPicklistWrite.IsAllowedAsync(
        [_userContextAccessor.Current?.TenantId], _permissionQueryService, userId))
    return await Result<int>.FailureAsync(SharedPicklistWrite.Refused);
```

A tenant-scoped caller creates a private row and never reaches the permission query at all.

**What remains not possible is a tenant-scoped holder creating a shared row.** Pass 31 recorded that
shared rows come only from seeding; that is still true for anyone with a tenant, because the
interceptor stamps unconditionally. Making it possible needs an explicit "create this as shared"
intent the interceptor honours — a change to Pass 24's stamping rule that every `IMayHaveTenant`
entity depends on. **That is more than a small change, so per the brief it is reported and left.** In
a single-tenant installation it does not arise: the seeded values are the shared ones and the holder
can edit them.

**One consequence worth naming:** the guard's "the tenant this row WILL carry" is a *second copy* of
the interceptor's stamping rule. They agree today and nothing makes them agree tomorrow.
`ANonHolderStillCreatesARowInTheirOwnTenant` runs the real interceptor and asserts the created row's
tenant, so the two are checked against each other rather than assumed — see A2.

### 2.6 The DTO and the grid

`PicklistSetDto` gains `TenantId` and an `IsShared` convenience that delegates to
`SharedPicklistWrite` rather than restating the rule. Pass 31 A5 established this was the binding
constraint — the page could not tell a shared row from a private one, and the obstacle sat one layer
away from where the question was being asked.

The grid now marks shared rows with a chip and a tooltip, renders their editors as read text for a
non-holder, and disables their delete button. `CommittedItemChanges` refuses a shared commit with
`DataGridEditFormAction.KeepOpen`.

**It is the second line and the code says so.** Both commands go through Mediator and are reachable
by any caller, so a rule enforced only by what the grid renders is not a rule.

**One finding that only rendering could have produced:** the grid is
`EditMode="DataGridEditMode.Cell"` with `ReadOnly="false"`, so **every cell renders its
`EditTemplate` permanently and the `CellTemplate` is never reached**. The marker was first written
into the `CellTemplate`, where it compiled, read correctly, and was invisible. It is now in the
`EditTemplate`, with a comment saying why.

---

## 3. The Pass 31 defect found while building §A

**`PicklistSetConfiguration` still carried `HasIndex(t => new { t.Name, t.Value }).IsUnique(true)`,
and Pass 24 had left a comment in that exact spot saying it must be widened when picklists were
scoped:** *"or the first two tenants to want the same brand name will collide on a constraint that
has no business spanning them."* Pass 31 scoped them and left it alone.

**The consequence was a documented behaviour that was false.** `README.md` said *"Two tenants may
import the same picklist name and value without the second silently losing its rows to the first"*
and Pass 31 §8.4 claimed the same. The duplicate **check** returns false correctly — the query filter
hides the other tenant's row — and the **insert** then fails on the index. The import reports success
and throws.

**Why Pass 31's own tests missed it, which is the part worth keeping.**
`TheImportDuplicateCheckIsNowPerTenant` asserted the `AnyAsync`, and never attempted the write. The
general shape:

> **A query filter narrows what a query SEES; a unique index constrains what the table HOLDS.**
> Scoping reads does not scope constraints, and a duplicate check written against the filtered view
> disagrees with the index precisely when the hidden rows are the ones that matter.

**Fixed**: the index is now `(TenantId, Name, Value)`, and all three `InitialCreate` migrations were
regenerated through the procedure the README documents. The regenerated SQLite migration differs from
the old one in exactly two lines — the index name and its columns — verified by diffing them with the
timestamps stripped.

**A known gap remains, named rather than papered over.** SQLite and PostgreSQL treat NULLs as
*distinct* in a unique index, so the **shared** partition is not protected from holding the same
value twice; SQL Server treats them as equal and is. Closing it portably needs a second, *partial*
unique index over `(Name, Value) WHERE TenantId IS NULL`, whose filter SQL differs per provider. It
was judged out of proportion here — shared rows come from idempotent seeding, or from a
`ManageShared` holder who also has no tenant — and
`TheSharedPartitionIsNotProtectedFromDuplicatesOnThisProvider` asserts the gap so it cannot widen
unnoticed. If that test ever fails, the gap has been closed and the fixture should assert the
protection instead.

---

## 4. §B — GATE: roles and tenancy

### 4.1 Every surface that reads or writes roles

`ApplicationRole` has **no `TenantId`**, and `IdentityUserConfiguration` puts a unique index on
`NormalizedName` across the whole installation — so two tenants cannot even hold roles of the same
name. Pass 23 §2.5 re-confirmed.

| Surface | Read / Write | Notes |
|---|---|---|
| `Roles.razor` grid — `_roleManager.Roles` + `CreateSearchPredicate` | **read** | no tenant term; every tenant sees the same list |
| `Roles.razor` → `RoleFormDialog` → `_roleManager.UpdateAsync` | **write** | renames and re-describes a role |
| `Roles.razor` → `RoleFormDialog` → `_roleManager.CreateAsync` | **write** | |
| `Roles.razor` → `_roleManager.DeleteAsync` (single and bulk) | **write** | |
| `Roles.razor` import (`_roleManager.CreateAsync` per row) | **write** | |
| Permissions editor → `PermissionAssignmentService.AssignRoleAsync` / `AssignRoleBulkAsync` | **write** | adds/removes permission claims on a role |
| `UserFormDialog` role assignment → `IdentityService` | **write, on the user** | assigns existing roles; does not change what a role means |
| `Users.razor` role column / filter | **read** | |
| `RoleDataSourceService.LoadAsync` | **read** | backs the pickers; `CacheScope.Global` |
| `ApplicationDbContextInitializer.EnsureRolesAsync` | **write** | provisioning; installation-wide by construction |
| `AdministratorPermissionRegistry` | neither | a compile-time list, not a query |
| `ApplicationUserClaimsPrincipalFactory`, `AuthorizationBehaviour`, `PermissionQueryService` | **read** | resolve a principal's claims through their roles |
| `AdministratorProtectionService` | guard | the only existing constraint — see below |
| `Register.razor`, `NavigationMenu`, `MenuService` | **read** | by role name |

### 4.2 What a tenant administrator can do to another tenant today — concretely

**All three, and nothing prevents any of them.**

- **Rename a role tenant B relies on.** `RoleFormDialog` sets `existingRole.Name` and calls
  `UpdateAsync`. Gated on `Roles.Edit` alone. Every tenant's users are in that same role row.
- **Revoke a permission from it.** `PermissionAssignmentService.AssignRoleAsync` removes the claim.
  Gated on `Roles.ManagePermissions` alone. **Users in every tenant lose the capability at once.**
- **Delete it.** `_roleManager.DeleteAsync`, gated on `Roles.Delete` alone.

**The one guard that exists is `AdministratorProtectionService`, and it is not a tenancy guard.** It
refuses deleting the `Admin` role, modifying its permissions, and removing its last member — rules
that keep the *installation* administrable. Every other role, including `Basic` and any a customer
adds, is unprotected. So a tenant administrator can today revoke `Documents.View` from `Basic` and
every ordinary user in every tenant stops seeing documents, with an audit trail that records who did
it and no rule that stopped them.

**Severity in context:** this is a **write** capability across tenants, which is the class of
capability this programme has been most careful about — Pass 27 separated seeing across tenants from
acting across them, and Pass 30 refused a read escape for presence on weaker grounds than these.
Roles are the one place where the cross-tenant *write* still exists unnamed.

### 4.3 The three options, with their real costs

**(a) Global roles, editing gated on a named right** — the Pass 23 §7.3 shape.

- **Cost: small.** One constant (`Roles.ManageDefinitions` or similar), a grant in the registry, and
  guards at four write paths — `RoleFormDialog`'s create/update, the delete paths, the import, and
  `PermissionAssignmentService.AssignRoleAsync`/`AssignRoleBulkAsync`. Exactly the shape §A just
  built for picklists, at roughly twice the surface because role administration bypasses Mediator and
  each page calls `RoleManager` directly — so the guards go where `AdministratorProtectionService`'s
  already do, which is the precedent for "no chokepoint, so every call site checks".
- **What a tenant administrator keeps:** assigning users to roles. That is the operation they
  actually need, and it is on the *user*, not the role.
- **The single-tenant case, which the brief rightly flags:** the sole administrator would need the
  right to manage roles at all. **Granting it by default solves this exactly as §A did for
  `ManageShared`** — default-granted keeps the single-tenant deployment working out of the box, and
  revoking it is the multi-tenant operator's deliberate act. The trap §A avoided is a *blanket
  read-only rule*, not a default-granted right; (a) is the same escape from the same trap.
- **Nothing about the data model changes**, so no migration, no seeding change, and no
  role-name-collision question.

**(b) Per-tenant roles** — `ApplicationRole` gains a tenant.

- **Cost: large, and larger than it looks.** What breaks:
  - **`RoleNameIndex` on `NormalizedName` is unique installation-wide** and would have to become
    `(TenantId, NormalizedName)`. That index is ASP.NET Core Identity's, not this template's;
    `RoleManager.FindByNameAsync` and `RoleExistsAsync` look up by normalized name **with no tenant
    term**, so they would return an arbitrary tenant's role. Every one of those call sites would need
    replacing — and they are inside Identity, not only in this codebase. **This is the load-bearing
    obstacle**, and it is the same one Pass 32 §3 just met in miniature on picklists: scoping reads
    does not scope constraints, and here the constraint belongs to a framework.
  - **`AdministratorPermissionRegistry` and `EnsureRolesAsync`** provision one `Admin` and one
    `Basic`. Per-tenant roles means provisioning a set per tenant, at tenant-creation time — a path
    that does not exist today (`EnsureDefaultTenantAsync` creates a tenant and no roles).
  - **The bootstrap**: `EnsureAdministratorAsync` calls `GetUsersInRoleAsync(Roles.Admin)` and
    `AddToRoleAsync(administrator, Roles.Admin)` — by name, no tenant.
  - **Existing role claims** are keyed by role id, so they survive; but every role would need
    duplicating per tenant, and the permission sets would then drift per tenant with nothing
    reconciling them. `AdministratorPermissionRegistry`'s divergence assertion checks the *constants*,
    not what a given database holds — it would not catch the drift.
  - **`AdministratorProtectionService`** matches on role *name*, so "the Admin role" becomes "the
    Admin role of which tenant", and "must keep at least one member" becomes a per-tenant invariant.
  - **Every lookup by name**: `Register.razor`, `NavigationMenu`, `MenuService`,
    `ApplicationUserClaimsPrincipalFactory`.
- **And a data migration** for any existing installation, since roles would have to be forked per
  tenant.

**(c) Leave global and unguarded, document it.**

- **Cost: none in code, and it is the honest status quo** — but it leaves a cross-tenant *write*
  reachable by a permission whose description says nothing about tenancy. Every other cross-tenant
  capability this programme has found has ended up either named or removed; this would be the first
  left as an absence of code, and the README would have to say so plainly.

### 4.4 §B.4 — What the seeder does, and what a second tenant gets

`ProvisionAsync` calls `EnsureRolesAsync` once, creating `Admin` (granted
`AdministratorPermissionRegistry.Granted`) and `Basic` (granted `Documents.View` and
`Documents.Download`). It is idempotent **per item**, not per run — a permission added later reaches
an existing database on the next start.

**A second tenant created after installation gets no roles of its own under (a) and (c)** — it uses
the installation's two, which is coherent: they are installation-wide by construction. **Under (b) it
would need a fresh set**, created at tenant-creation time by a path that does not exist today. That
is the difference a consumer notices first, and it is an argument *for* (a): under (a) nothing about
tenant creation changes at all.

### 4.5 §B.5 — `RoleDataSourceService.Scope`

Currently `CacheScope.Global`, and its comment already says the reasoning is a claim about the data
and names Pass 23 §2.5 as the open question. Pass 31 A1's lesson applies exactly: **a scope is a
claim about a query's inputs, and scoping a query invalidates the declaration without touching the
line that declares it.**

| Option | `RoleDataSourceService.Scope` must become |
|---|---|
| (a) global roles, gated editing | **`Global` — unchanged.** The list is still identical for everyone; only who may *write* changes, and writes do not enter a read's cache key |
| (b) per-tenant roles | **`PerTenant`**, in the same change — and `PerUser` if a cross-tenant escape is ever added, per Pass 28's finding |
| (c) unchanged | **`Global` — unchanged** |

That (a) requires no scope change is a genuine point in its favour: it is a pure authorization change
with no cache-partition consequence, which is the failure mode Pass 31 §8.5 showed no query test can
see.

### 4.6 Recommendation

**Option (a): global roles, with role *definition* gated on a new named right, granted to the
administrator by default.**

Three reasons, in order of weight:

1. **It closes a cross-tenant write for a small, precedented change.** §4.2 established that a tenant
   administrator can today rename, re-permission or delete a role every other tenant depends on.
   That is the strongest cross-tenant capability left in the template, and (a) names it.
2. **Roles genuinely are installation-wide, and (b) fights that rather than expressing it.** The
   unique index on `NormalizedName` is Identity's, and `FindByNameAsync` has no tenant term — so (b)
   is not "add a column", it is "replace the framework's role lookup". The cost is out of proportion
   to a template whose two shipped roles are `Admin` and `Basic`.
3. **The single-tenant case comes out right**, which is the case both §A and Pass 31 §5 nearly got
   wrong. Default-granted, the sole administrator manages roles exactly as today; revoking it is the
   multi-tenant operator's deliberate act. **The trap to avoid is a blanket prohibition, not a
   default-granted right** — that is precisely the lesson §A encodes.

**What a tenant administrator keeps under (a):** assigning users to roles, which is the operation
their job actually requires and which is on the user rather than the role.

**What I would NOT do:** split it per verb. One right over "role definitions" — create, rename,
delete, re-permission — for the same reason `ManageShared` is one right: a grant that lets someone
delete a role but not fix it is worse than either.

**Not built. §B stops here for ratification.**

---

## 5. §C — Verification

### 5.1 §A's evidence

**Through the handlers, not the rule.** Every assertion in `SharedPicklistWriteTests` sends a real
command and then reads the database to see whether the write happened — because the grid was the
thing that used to decide, and a guard proven only at `SharedPicklistWrite.IsAllowedAsync` would
prove the rule and not its enforcement.

| Claim | Test |
|---|---|
| A non-holder cannot edit a shared row **through the handler** | `ANonHolderCannotEditASharedRow` — and the stored value is re-read, so a refusal beside a write that happened anyway fails |
| …nor delete one | `ANonHolderCannotDeleteASharedRow` |
| …nor create one | `ATenantlessNonHolderCannotCREATEASharedRow` |
| A mixed delete is refused wholesale | `AMixedDeleteIsRefusedWholesaleRatherThanPartiallyApplied` |
| **A non-holder still edits their own tenant's row** | `ANonHolderStillEditsTheirOwnTenantsRow` |
| …still deletes it | `ANonHolderStillDeletesTheirOwnTenantsRow` |
| …still creates in their own tenant, and it is stamped private | `ANonHolderStillCreatesARowInTheirOwnTenant` |
| A holder edits and deletes shared rows | `AHolderEditsASharedRow`, `AHolderDeletesASharedRow` |
| A holder gains no sight of another tenant's private row | `AHolderStillCannotReachAnotherTenantsPrivateRow` |
| The rule fails closed | `TheRuleFailsClosedWithNoPrincipal` |
| The permission query is skipped when nothing shared is touched | `TheRuleSkipsThePermissionQueryWhenNoSharedRowIsInvolved` — asserted with a service that **throws**, so a future unconditional query fails rather than merely costing a round trip |

**Narrowed, not emptied** is the control that matters most here: the failure mode of a permission
guard is over-refusal, and the single-tenant deployment is the one it breaks first. Three separate
"still can" tests carry it.

**The grid, circuit-level** (`SharedPicklistGridComponentTests`): the shared row is marked, the
marker shows whether or not the principal may write, exactly one delete button is disabled for a
non-holder (the caller's own row keeps its button), and none is disabled for a holder. An HTTP test
cannot see any of it — the application renders at `InteractiveServerRenderMode(prerender: false)`.

**One honest limit, stated in the fixture:** a cell only enters edit mode under a real click, which
bUnit's static render does not reproduce, so the per-column read-only editors are covered by the
disabled-delete assertion plus a rule-agreement test rather than by asserting the editors directly.

### 5.2 Red before, green after — three separate demonstrations

**A — the §A guards removed** (both handler guards and the grid's `CanWrite`), restored
byte-identically afterwards and verified by `diff`:

```
Application.UnitTests      Failed: 4,  Passed: 13
Server.UI.IntegrationTests Failed: 1,  Passed: 4
```

Red: `ANonHolderCannotEditASharedRow`, `ANonHolderCannotDeleteASharedRow`,
`ATenantlessNonHolderCannotCREATEASharedRow`, `AMixedDeleteIsRefusedWholesaleRatherThanPartiallyApplied`,
`WithoutManageShared_TheDeleteButtonOnASharedRowIsDisabled`. The "still can" controls stayed green,
which is the point of having them.

**B — the unique index, measured before the fix** (the defect as Pass 31 shipped it):

```
PicklistTenantUniquenessTests   Failed: 2,  Passed: 2
```

Red: `TwoTenantsMayHoldTheSameNameAndValue`, `ATenantMayShadowNothing_TheDuplicateCheckAndTheIndexAgree`.
Green: the within-tenant duplicate protection, which the fix had to preserve.

**C — the interceptor, found by a failing assertion rather than by reading.** The first draft of
`ANonHolderStillCreatesARowInTheirOwnTenant` failed with the created row's tenant `null`, because the
test context did not register `AuditableEntityInterceptor`. Wiring it in (as `TenantStampingTests`
does) turned the test into an end-to-end check that the guard's prediction and the interceptor's
behaviour agree. See A2.

### 5.3 Boundary suites (§C.3)

**No existing test file was modified** — `git status tests/` shows three additions and nothing else.
Every Pass 26–31 scope, isolation, presence and filter suite confirmed byte-unmodified with
`git diff --quiet` per file, **including all three of Pass 31's**, which are the proof the brief
names because §A touches a filtered entity's write path:

```
HarnessPrincipalTests            AuditTrailTenantFilterTests       SwitchableTenantsTests
TenantSwitchAuthorizationTests   TenantVisibilityTests             UserVisibilityTests
DataSourceScopeTests             PicklistDataSourceScopeTests      PicklistSetTenantFilterTests
PicklistSeedVisibilityTests      OnlineUsersTrackerComponentTests  ServerHubTenantIsolationTests
SuperiorAutocompleteScopeComponentTests   SuperiorBoundComponentTests   TenantSelectorComponentTests
UserDeactivationPermissionComponentTests  UserTenantScopeComponentTests
```

Run as a filtered set: **131 passed, 0 failed** (32 + 42 + 3 + 54).

That Pass 31's picklist suites pass unchanged against a widened unique index and a guarded write path
is the useful result: the read-side contract they pin is undisturbed.

### 5.4 Counts (§C.2)

| | Before | After | Delta |
|---|---|---|---|
| `Infrastructure.UnitTests` | 224 | 224 | — |
| `Application.IntegrationTests` | 12 | 12 | — |
| `Application.UnitTests` | 437 (+12 skipped) | **454** (+12 skipped) | **+17** |
| `Server.UI.IntegrationTests` | 204 | **209** | **+5** |
| **Total** | **877 passed, 12 skipped** | **899 passed, 12 skipped** | **+22, 0 failed** |

The +22 is exactly the three new files: 13 `SharedPicklistWriteTests`, 4
`PicklistTenantUniquenessTests`, 5 `SharedPicklistGridComponentTests`. No pre-existing test changed
count or outcome — including `AddEditPicklistCommandTests` and `DeletePicklistTests`, whose harness
grants every permission constant reflectively and so acquires `ManageShared` automatically.

**Warnings: unchanged.** `dotnet build --no-incremental` gives **19 warnings across the same 10
distinct source locations** — `DescriptionAttributeExtensions.cs` ×4, `MapsterConfiguration.cs` ×2,
`MudDateTimeField.razor`, `TenantSelect.razor`, `Dashboard.razor`, `AuditTrails.razor` — plus
`NETSDK1206`. **No new warning location. 0 errors.**

### 5.5 Generation probe (§C.4)

```
dotnet pack (nuspec) → dotnet new install → dotnet new gxblazor -n P32
  → build: 0 Error(s), 19 Warning(s)
  → dotnet test: 224 + 12 + 454 + 209 = 899 passed, 12 skipped, 0 failed
  → dotnet new uninstall; probe directory removed
```

Identical to source, suite for suite. The generated project carries `ManageShared`, the guarded
handlers, and the regenerated `IX_PicklistSets_TenantId_Name_Value` index.

---

## 6. README

- Tenancy table, Picklists row: now says **"No cross-tenant READ escape, deliberately; WRITING a
  shared value needs `PicklistSets.ManageShared`"**.
- The limitation *"A shared row is editable by any principal holding the picklist Edit permission"*
  is **replaced** by a statement of the new right, what it does and does not grant, and when to
  revoke it.
- New bullet on **the unique index**, the reads-vs-constraints lesson, and the named NULL-distinctness
  gap.
- New Tenancy paragraph: **`ManageShared` is not the escape §C declined** — reading across tenants
  and writing installation-wide data are different capabilities, kept as different rights, the same
  separation Pass 27 drew between `ViewAllTenants` and `SwitchToAnyTenant`.

**No roles change**, since §B is not ratified.

---

## 7. File map, diffstat and edit fidelity

### 7.1 File map

**New (4):**

| File | |
|---|---|
| `src/Application/Features/PicklistSets/SharedPicklistWrite.cs` | the single rule: `Refused`, `IsShared`, `MayManageSharedAsync`, `IsAllowedAsync` |
| `tests/Application.UnitTests/Features/PicklistSets/SharedPicklistWriteTests.cs` | **13 tests**, through the real handlers with the real interceptor |
| `tests/Application.UnitTests/Features/PicklistSets/PicklistTenantUniquenessTests.cs` | **4 tests**, the index defect and its residual gap |
| `tests/Server.UI.IntegrationTests/SharedPicklistGridComponentTests.cs` | **5 tests**, circuit-level |

**Modified (7 + README):**

| File | |
|---|---|
| `src/Application/Features/PicklistSets/Security/PicklistSetsPermissions.cs` | `ManageShared` constant + `AccessRights` property |
| `src/Application/Common/Security/AdministratorPermissionRegistry.cs` | granted, with the single-tenant reason |
| `src/Application/Features/PicklistSets/Commands/AddEdit/AddEditPicklistSetCommand.cs` | guards on edit and create |
| `src/Application/Features/PicklistSets/Commands/Delete/DeletePicklistSetCommand.cs` | all-or-nothing guard |
| `src/Application/Features/PicklistSets/DTOs/PicklistSetDto.cs` | `TenantId`, `IsShared` |
| `src/Infrastructure/Persistence/Configurations/PicklistSetConfiguration.cs` | index widened to `(TenantId, Name, Value)` |
| `src/Server.UI/Pages/PicklistSets/PicklistSets.razor` | marker, read-only editors, disabled delete, commit refusal |
| `README.md` | §6 |

**Migrations regenerated (3 providers × 3 files).** Deleted `20260903*_InitialCreate*` and
`ApplicationDbContextModelSnapshot`, regenerated as `20260904*` through the README's documented
`dotnet ef migrations add` procedure with the provider's own connection strings. **Diffing the old
and new SQLite migration with timestamps stripped shows exactly two changed lines** — the index name
and its columns. The snapshots changed by one line each.

### 7.2 Diffstat (source, excluding regenerated migrations)

```
 README.md                                                        |  44 +++++---
 src/Application/Common/Security/AdministratorPermissionRegistry.cs| 11 +++
 .../PicklistSets/Commands/AddEdit/AddEditPicklistSetCommand.cs   |  32 ++++++-
 .../PicklistSets/Commands/Delete/DeletePicklistSetCommand.cs     |  19 +++-
 src/Application/Features/PicklistSets/DTOs/PicklistSetDto.cs     |  29 +++++-
 .../PicklistSets/Security/PicklistSetsPermissions.cs             |  40 +++++++-
 .../Persistence/Configurations/PicklistSetConfiguration.cs       |  25 +++--
 src/Server.UI/Pages/PicklistSets/PicklistSets.razor              | 105 ++++++++++++++++--
 8 files changed, 274 insertions(+), 31 deletions(-)
```

### 7.3 Edit fidelity

- **One git action, explicitly authorised**: the `Pass31-Picklists` commit. Nothing else was staged,
  committed, stashed or reset; this pass's own work is uncommitted. `pass32-report.md` was
  deliberately excluded from that commit.
- **Both red-before demonstrations were reverted byte-identically**, verified by `diff` against
  copies taken beforehand.
- **No existing test file was touched.** The +22 is entirely in three new files.
- The migration regeneration used the procedure the README documents, not hand-editing, and the
  result was diffed against the previous output rather than trusted.

---

## 8. What remains

| Surface | Status |
|---|---|
| **Shared picklist writes** | **closed by this pass** — `ManageShared`, guarded in the handlers |
| **The picklist unique index** | **fixed** — `(TenantId, Name, Value)`, with the NULL-distinctness gap named |
| Creating a shared picklist as a tenant-scoped holder | **not possible; reported, not built** (§2.5). Needs an explicit intent the stamping interceptor honours |
| Duplicate SHARED picklist values on SQLite/PostgreSQL | **known gap, asserted** (§3). Needs a partial unique index with per-provider filter SQL |
| **Roles** | **pending §B's ratification.** A tenant administrator can today rename, re-permission or delete a role every other tenant depends on. Recommendation: option (a) |
| System logs | unscoped, and unreachable by the global filter — `SystemLog` is on `LogDbContext`. A separate design, not a deferred switch |
| Security settings (idle policy) | unscoped — one row per installation, by design. A product question whose answer is plausibly "leave it" |

---

## 9. Scratch probe disclosure

Three, all removed: green-file backups for the two red-before demonstrations; the generation probe
(packed nupkg, installed template, generated `P32` at a short path — template uninstalled, directory
removed); and two throwaway SQLite files (`regen.db`, `regen-logs.db`) created by the design-time
host during migration regeneration. The nupkg in the repository root was rebuilt by `dotnet pack`
and is a gitignored build artifact.

---

## 10. Anomalies

**A1 — Pass 24 predicted the defect, in the right file, and it shipped anyway.**
`PicklistSetConfiguration` carried a comment naming exactly what would go wrong and who had to fix
it. Pass 31 read that file (it is where `ToTable` and the property lengths live), scoped the entity,
and did not act on it. Recorded because the lesson is not "leave better comments": it is that **a
comment addressed to a future pass has no failure mode.** A test asserting the intended behaviour
would have gone red the moment picklists were scoped; the comment did not.

**A2 — the create guard is a second copy of the interceptor's stamping rule.** The handler reasons
about "the tenant this row WILL carry" as `_userContextAccessor.Current?.TenantId`;
`AuditableEntityInterceptor.SetCreationAuditInfo` independently decides the same thing. They agree
today. This programme has met the two-copies-of-one-rule defect at least four times, and the usual
remedy — extract it — does not apply cleanly here, because one copy is a *prediction made before* the
save and the other is the *act* performed during it. What was done instead is to run the real
interceptor in the test and assert the created row's tenant, so the two are checked against each
other. Recorded because the coupling is invisible from either file.

**A3 — a `CellTemplate` on a cell-edit grid is unreachable, and it compiles.** With
`EditMode="DataGridEditMode.Cell"` and `ReadOnly="false"`, MudBlazor renders every cell's
`EditTemplate` permanently. The shared-row marker was written into the `CellTemplate` first, where it
was correct, readable, and invisible — found only because a rendering test asserted the markup.
Recorded because it generalises: on this page any future column decoration must go in the
`EditTemplate`.

**A4 — `DataGridEditFormAction` has two members, not the three the obvious code assumes.** The commit
refusal was first written as `DataGridEditFormAction.Cancel`, which does not exist; the enum is
`Close` and `KeepOpen`, and `KeepOpen` is documented as "prevent the commit and keep the row in edit
mode" — which is the right behaviour for a refusal anyway. Minor, but recorded because the member was
not discoverable through the package's XML docs via reflection (the type failed to load standalone)
and had to be read out of `MudBlazor.xml` directly.

**A5 — the interceptor's audit row has a real FK to `AspNetUsers`, which makes "every successful
write" fail in a naive test fixture.** Every refusal test passed and every success test failed with
`SQLite Error 19: FOREIGN KEY constraint failed`, because the fixture had no user rows. A fixture
that only tested refusals would have been entirely green while proving that nothing works. Recorded
as a trap for anyone testing a guarded write path in this codebase.

**A6 — there is no test that the model and the migrations agree.** The README states that a second
`dotnet ef migrations add` produces an empty migration, and `GxTableNamingTests` mentions it in a
comment — but nothing asserts `HasPendingModelChanges`. Changing the index in the configuration
without regenerating would have left the model and the schema silently divergent, and only a real
`database update` would have shown it. Not fixed here (it needs a design decision about which
provider's migration a test should check), but it is the mechanism that would have caught A1's fix
being incomplete.
