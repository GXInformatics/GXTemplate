# Pass 40 — Clear the Board

**Nature:** editing pass, four independent items. §D turned out to be a change rather than a report.
**Date:** 2026-09-06.

**Result in one line:** the dead `ClaimsPrincipalExtensions` surface is gone — **twelve of fourteen
methods, not the eight Pass 38 counted**; the fifteen `Roles.*` descriptions now say that roles are
installation-wide and that five of the constants do nothing; the menu-by-role split is written down
in two places so a third pass does not rediscover it; and the shared-picklist duplicate gap is
**closed on all three providers with one filter string** — a gap that, measured here, was never the
two-provider divergence Pass 32 recorded. **995 → 996 tests**, warnings unchanged, boundary suites
byte-unmodified but for the one the brief named.

---

## 1. Start state

| | |
|---|---|
| HEAD | `9c73c09c` — *"Pass39-PublisherContext"* |
| Working tree | clean |
| Build | **0 errors, 19 warnings across 10 distinct source locations** |
| Tests | 229 + 12 + 511 + 243 = **995 passed, 12 skipped, 0 failed** |

The precondition held: Pass 39 was committed and the tree was clean, so all four items began from a
single known baseline. The 995 figure matches Pass 39 §7.6 exactly.

---

## 2. §A — The dead claims surface, deleted

### 2.1 The census, re-verified rather than inherited

The brief said "eight". **It is twelve**, and Pass 38 was not wrong about what it counted — it never
enumerated the whole class. Its §2.2 table listed seven zero-caller methods plus `GetTenantId`, and
silently omitted four others.

Counted at `9c73c09c` with `git grep -n "\.<name>()" HEAD -- src`, excluding the declaring file:

| Method | Callers at HEAD | Verdict |
|---|---|---|
| `GetUserId` | **4** — `FileEndpoints`, `AppLayout`, `Users.razor` ×2 | **kept** |
| `GetUserName` | **1** — `CustomError.razor` | **kept** |
| `GetTenantId` | 1, and it is a *comment* Pass 38 wrote | deleted |
| `GetDisplayName` | 4, and **none of them is this method** | deleted |
| `GetEmail`, `GetPhoneNumber`, `GetProvider`, `GetProfilePictureDataUrl`, `GetSuperiorName`, `GetSuperiorId`, `GetTenantName`, `GetStatus`, `GetAssignRoles`, `GetRoles` | 0 | deleted |

**`GetDisplayName` is why a grep-shaped census undercounts here.** Its four hits are
`UploadType.GetDisplayName()` in `StorageKeys` and `FileEndpoints`, and `PageStatus.GetDisplayName()`
twice in `NavigationMenu.razor` — every one of them the *enum* extension of the same name. A fifth
apparent hit, `UserActionInfo.razor`, has a private local method called the same thing. A method that
shares its name with a live extension on a different receiver reads as live and is not; that is the
one in the class that had to be resolved by looking at each call site rather than by counting them.

The brief said: *"If any of the eight turns out to have a caller, keep it and say so."* None did. Two
that were never in the eight — `GetUserId` and `GetUserName` — have callers and are kept.

### 2.2 What replaced it

`ClaimsPrincipalExtensions.cs` goes from 14 methods to 2, with a class-level `<remarks>` that records
three things a future reader needs and the diff alone would not tell them:

- that twelve methods were deleted and why a caller-free method is not free — *"it invites the next
  reader to call it, and in one case that had already cost a cross-tenant read escape"*;
- **`GetTenantId` in particular**, with the whole Pass 36 → Pass 38 chain: the tenant claim is written
  by `TenantSwitchService.RefreshUserClaimsAsync` alone, reachable only from `SwitchToTenantAsync`, so
  a user who has never switched tenant carries none — *"a method that cannot be called correctly
  should not be callable, so it is gone"*;
- **where to get a tenant instead**: `IUserContextAccessor.Current.TenantId` inside a circuit or a
  Mediator handler, `IUserContextLoader.LoadAsync` on an HTTP path, which reads the user row and is
  already invalidated on tenant switch.

The two survivors read `ClaimTypes.NameIdentifier` and `ClaimTypes.Name` — framework claims Identity
always issues, so neither has the absent-for-most-users problem that sank the tenant claim. That is
stated on the class rather than left as a coincidence.

### 2.3 The one follow-on edit

`FileEndpoints.cs:119` named the deleted method in a comment Pass 38 had written
(``It used to read `user.GetTenantId() ?? string.Empty` ``). Rewritten to name it as *deleted* rather
than as callable. One line becomes two; no code changed. Leaving it would have left a comment
pointing at a method that no longer exists, which is exactly the kind of stale signpost this pass is
removing.

---

## 3. §B — The `Roles.*` descriptions

### 3.1 Before and after

`[Description]` text is what the role editor renders under a permission's name at the moment an
administrator decides to grant it. Every other permission group in this template describes a
tenant-scoped capability, so the reader's default is wrong here, and none of the old strings said so.

| Constant | Before | After |
|---|---|---|
| *module* | `Set permissions for role operations` | `Set permissions for role operations - roles are installation-wide` |
| `View` | `Allows viewing role details` | `Allows viewing the installation's roles - every tenant shares them` |
| `Create` | `Allows creating new roles` | `Allows creating a role, for every tenant - also needs Manage Definitions` |
| `Edit` | `Allows modifying existing roles` | `Allows renaming a role every tenant shares - also needs Manage Definitions` |
| `Delete` | `Allows deleting roles` | `Allows deleting a role every tenant shares - also needs Manage Definitions` |
| `Search` | `Allows searching for role records` | `Allows searching the installation's roles` |
| `Import` | `Allows importing role data` | `Allows importing roles for the whole installation - also needs Manage Definitions` |
| `Export` | `Allows exporting role data` | `Allows exporting the installation's roles - reading only, not defining` |
| `ManagePermissions` | `Allows managing role permissions` | `Allows re-permissioning a role for every tenant at once - also needs Manage Definitions` |
| `ManageClaimsInRole` | `Allows managing role claims` | `Not implemented in this template - there is no claims-in-role administration` |
| `ManageUsersInRole` | `Allows managing users in role` | `Not implemented in this template - assign users to roles from the Users page` |
| `ViewPermissions` | `Allows viewing role permissions` | `Not implemented in this template - viewing happens inside the Set Permissions dialog` |
| `ViewClaimsInRole` | `Allows viewing role claims` | `Not implemented in this template - there is no claims-in-role viewer` |
| `ViewUsersInRole` | `Allows viewing users in role` | `Not implemented in this template - see the Users page's role column` |
| `ManageDefinitions` | `Allows defining roles - creating, renaming, deleting, re-permissioning and importing them` | `Allows DEFINING the installation's roles - create, rename, delete, re-permission, import` |

All fifteen fit the drawer's width — longest is 88 characters, checked rather than eyeballed.

### 3.2 Three decisions inside those strings

**Every write that needs `ManageDefinitions` says so.** Pass 33 introduced the pairing; nothing told
an operator about it, so the only way to learn it was to grant `Roles.Edit`, watch a rename be
refused, and go looking. Five descriptions now carry *"also needs Manage Definitions"*. `View`,
`Search` and `Export` deliberately do not — they read, they do not define, and adding the phrase
would have been false.

**Five constants say plainly that they do nothing.** `ManageClaimsInRole`, `ManageUsersInRole`,
`ViewPermissions`, `ViewClaimsInRole` and `ViewUsersInRole` are EXCLUDED in
`AdministratorPermissionRegistry` — but exclusion keeps them out of the administrator's *grant*, not
out of the role editor's *list*. They are still rendered, still tickable, and still inert. Each now
begins `Not implemented in this template - `, and each says where the real capability lives if there
is one. A permission that appears grantable and does nothing costs an administrator a decision for
no capability, and costs the next auditor a search.

**`Export` says what it is not.** `Allows exporting the installation's roles - reading only, not
defining` is the one description whose value is in the negation: export is the operation most likely
to be read as harmless *and* most likely to be read as part of the definition surface. It is neither.

### 3.3 The class-level remarks

Added, covering: why these descriptions state installation scope when no other group does;
**definition versus assignment** — assigning a user to an existing role is not in this group at all,
it is `Permissions.Users.ManageRoles`, an operation on the *user*; and the five inert constants, with
the exclusion-is-not-hiding point spelled out. This is the file a future pass will read before
touching the strings again.

`RolesAccessRights` was not touched. Its property names are what `PermissionService` turns into claim
strings, and the existing comment already says what a mismatch costs.

---

## 4. §C — The menu-by-role split, written down

**No mechanism changed**, as the brief required. Two passes have now examined this and found it
sound; the cost has been that each of them had to re-derive it.

### 4.1 What is actually there

`NavigationMenu.razor` filters at three levels — section, item and sub-item — on
`x.Roles == null || x.Roles.Any(r => Roles.Contains(r))`, over the signed-in user's assigned role
names. **There is exactly one gate in the whole menu**: `Roles = [Admin]` on the MANAGEMENT section.
No section item and no sub-item carries a gate of its own, so all eleven entries under it —
Multi-Tenant, Users, Roles, Profile, Login History, Picklist, Security Settings, Audit Trails, Email
Templates, Logs, Jobs — inherit that one.

`MenuSectionSubItemModel` has a `Roles` array and **no permission field**, so the menu cannot express
a permission even in principle. That is the fact that settles it: this is not a mechanism that was
half-applied, it is a different mechanism.

### 4.2 The two places it is now recorded

**`MenuService.cs`**, class-level `<remarks>`, opening:

> **The menu gates by ROLE; every page gates by PERMISSION. That is deliberate, and it is the one
> thing to know before reading an entry below.**

and continuing to the consequence that matters:

> **An entry visible to someone who cannot use the page is therefore EXPECTED, not a gap.**

It also records the history explicitly — Pass 34 §2.3 reported the "Logs" entry as ungated and
inconsistent with its neighbours; Pass 35 A4 established that the neighbours carry no permission
either and the gate sits one level up — and names the consequence that makes the record worth
keeping: **adding an item-level gate to Logs alone would have CREATED the inconsistency the finding
described.** It cites `SystemMenuGateComponentTests.NoMenuEntryCarriesAGateOfItsOwn`, which fails a
future "fix" of that shape rather than letting it land.

**`README.md`**, in the deny-by-default authorization section, a new paragraph beginning:

> **The navigation menu gates by ROLE, not by permission, and that is deliberate.**

It goes further than the source comment in one respect, because the README has a reader the source
does not: it says that a principal holding `Admin` with a customised permission set — *"which is
exactly what the Tenancy section recommends building for a customer administrator"* — will see links
they are then refused at. That is the exact configuration the Logs guidance tells an operator to
build, so it is the configuration in which the menu looks broken. Saying so where that advice is
given is the point of putting it in the README at all.

---

## 5. §D — The shared-picklist duplicate gap

The brief's condition: *"If it is a clean `HasFilter` on one index, do it. If it needs per-provider
filter SQL or hand-edited migrations, report and stop."* **It is one filter string on one index, on
all three providers.** So it was done, including the three-chain regeneration the brief listed as an
expected cost. No migration was hand-edited.

### 5.1 The gap, and the correction to Pass 32

`(TenantId, Name, Value)` constrains each tenant's rows. It cannot constrain the **shared** ones,
whose key is a NULL. Pass 32 recorded this as a two-provider divergence: *"SQL Server treats them as
equal and does block it."*

**That is wrong, and the evidence is in SQL Server's own generated migration.** EF adds a filter of
its own to a unique index over nullable columns. The MSSQL chain emits:

```csharp
name: "IX_PicklistSets_TenantId_Name_Value",
columns: new[] { "TenantId", "Name", "Value" },
unique: true,
filter: "[TenantId] IS NOT NULL AND [Value] IS NOT NULL");
```

Confirmed against a live database (`sys.indexes` on a LocalDB copy built from that migration):

```
IX_PicklistSets_TenantId_Name_Value | unique | filtered | ([TenantId] IS NOT NULL AND [Value] IS NOT NULL)
```

Shared rows were **never in that index**. SQL Server never got the chance to treat their NULLs as
equal. **The gap was on all three providers, not two** — and the provider Pass 32 named as the safe
one was as exposed as the others.

### 5.2 Why it mattered more than Pass 32 judged

Pass 32 called the gap narrow because shared rows came only from idempotent seeding, or from a
`ManageShared` holder who also had no tenant. **Pass 33 §C then gave a tenant-scoped `ManageShared`
holder a switch in the create dialog.** Since that pass there has been an ordinary UI path to a
duplicate shared value, and a duplicated shared value renders twice in *every* tenant's picker. The
narrowness argument expired one pass after it was written, in a pass that did not revisit it.

### 5.3 The change

```csharp
builder.HasIndex(t => new { t.TenantId, t.Name, t.Value }).IsUnique(true);
builder.HasIndex(t => new { t.Name, t.Value }).IsUnique(true).HasFilter("\"TenantId\" IS NULL");
```

**One filter string, three providers.** `"TenantId" IS NULL` is ANSI identifier quoting: EF emits it
verbatim; SQLite and PostgreSQL take it as written; SQL Server normalises it to `([TenantId] IS
NULL)`. Verified rather than assumed — see 5.4. That is why this is one `HasFilter` and not a
per-provider branch, and it is what kept the item inside the brief's condition.

### 5.4 Proved on each provider

Not by reading the migration, but by writing rows.

**SQL Server** (LocalDB, database built from the regenerated migration):

```
IX_PicklistSets_Name_Value | unique | filtered | ([TenantId] IS NULL)

INSERT (Brand, acme, NULL tenant)          → first shared row OK
INSERT (Brand, acme, NULL tenant)          → Msg 2601: Cannot insert duplicate key row … 'IX_PicklistSets_Name_Value'
INSERT (Brand, acme, tenant-a)             → tenant row OK
```

**PostgreSQL** (built from the regenerated migration):

```
CREATE UNIQUE INDEX "IX_PicklistSets_Name_Value" ON public."PicklistSets" ("Name","Value") WHERE ("TenantId" IS NULL)

INSERT shared                              → INSERT 0 1
INSERT shared again                        → ERROR: duplicate key value violates unique constraint "IX_PicklistSets_Name_Value"
INSERT tenant-a, same name and value       → INSERT 0 1
```

**SQLite** — the test fixture, which runs on it and was one of the unprotected providers.

The third line of each block is the half that is easy to skip: the index had to be **narrowed, not
introduced**. A tenant holding a value that also exists as a shared one is Pass 31's whole shape —
shared reference data plus per-tenant additions — and an unfiltered `(Name, Value)` index would have
broken it on every provider at once.

### 5.5 The fixture, inverted as it asked to be

`TheSharedPartitionIsNotProtectedFromDuplicatesOnThisProvider` asserted the gap, with a comment
saying: *"If it ever starts failing, the gap has been closed and this fixture should assert the
protection instead of the gap."* It failed. It is now
**`TheSharedPartitionIsProtectedFromDuplicates`**, asserting `ThrowAsync` where it asserted
`NotThrowAsync`.

**This is the mechanism Pass 32 designed working exactly as designed**, and that is worth more than
the assertion. A test that pins a known gap turns the gap's closure into a *failing build* — the
change cannot land silently, and the person closing it is handed the instruction for what to do next
by the test itself.

One test was **added**: `TheSharedIndexDoesNotConstrainATenantsOwnRows`, pinning 5.4's third line so a
future widening of the filter fails rather than quietly forbidding per-tenant additions.

### 5.6 What was deliberately not done

`AddEditPicklistSetCommand` has **no duplicate check at all** — verified, not assumed. So a duplicate
created through the dialog reaches the index and surfaces as a `DbUpdateException`. That is not a
regression this pass introduced: it has been the behaviour for the *tenant* partition since Pass 32,
and the shared partition now simply behaves the same way. Making both paths report a friendly
duplicate error is a real improvement and a different piece of work; growing this item to include it
is what the brief's *"stop on any one that turns out bigger than described"* forbids. Recorded in §8.

The import path is unaffected: its `AnyAsync` runs through the global filter, which *admits* shared
rows, so an import already sees an existing shared value as a duplicate and skips it.

---

## 6. Verification

### 6.1 Counts

| | Before | After | Delta |
|---|---|---|---|
| `Infrastructure.UnitTests` | 229 | 229 | — |
| `Application.IntegrationTests` | 12 | 12 | — |
| `Application.UnitTests` | 511 (+12 skipped) | **512** (+12 skipped) | **+1** |
| `Server.UI.IntegrationTests` | 243 | 243 | — |
| **Total** | **995 passed, 12 skipped** | **996 passed, 12 skipped** | **+1, 0 failed** |

The +1 is exactly `TheSharedIndexDoesNotConstrainATenantsOwnRows`. The inverted test is a rename, not
a count change. **No pre-existing test changed outcome** — including
`ModelMatchesMigrationsTests`, which is the guard Pass 33 built for precisely this kind of change and
which passes against three regenerated chains.

### 6.2 Warnings

**Unchanged: 19, across the same 10 distinct source locations**, plus `NETSDK1206` from the SDK
targets. Measured on the generated project, which is where the 19 figure has always been taken; a
`--no-incremental` build of the repository reports each of them twice and yields the same 10 distinct
locations.

### 6.3 Boundary suites

40 tenancy, permission, menu, migration and picklist suites checked individually with
`git diff --quiet`: **39 byte-unmodified**. The one modification is
`PicklistTenantUniquenessTests.cs`, which the brief named in advance as an expected cost of §D
(*"the existing gap-assertion test inverted"*).

Run as a filtered set — `FileEndpointsAuthorizationTests`, `AdministratorPermissionRegistryTests`,
`RoleDefinitionRightTests`, `PicklistSetTenantFilterTests`, `SharedPicklistCreationTests`,
`SharedPicklistWriteTests`, `TenantStampingTests`, `DocumentTenantIsolationTests`,
`LogPermissionScopeTests`, `ModelMatchesMigrationsTests`, `PicklistDataSourceScopeTests`,
`LogTenantStampingTests`, `SystemMenuGateComponentTests`, `SharedPicklistGridComponentTests`,
`PicklistSeedVisibilityTests`, `RoleDefinitionComponentTests`, `FileEndpointMatrixTests` —
**151 passed, 0 failed** (98 + 17 + 36).

### 6.4 Generation probe

Uninstalled before installing, per Pass 35 A5 — the registry held zero registrations before the
install, checked rather than assumed:

```
dotnet new uninstall (0 found) → dotnet pack build/pack.csproj → dotnet new install → dotnet new gxblazor -n P40
  → ClaimsPrincipalExtensions: 2 methods (GetUserId, GetUserName); GetTenantId survives only in two comments
  → Roles.cs: 11 lines mentioning "installation"
  → MenuService.cs: the "gates by ROLE" remarks present
  → README.md: the menu paragraph present
  → PicklistSetConfiguration.cs: HasFilter("\"TenantId\" IS NULL") present
  → all three migration chains carry IX_PicklistSets_Name_Value
  → build: 0 Error(s), 19 Warning(s)
  → dotnet test: 229 + 12 + 512 + 243 = 996 passed, 12 skipped, 0 failed
  → dotnet new uninstall; probe directory removed
```

The §A census therefore holds in a generated project, which the brief asked for specifically: the
deletion is not something the rename machinery could reintroduce.

The first attempt at this probe failed for a reason that is not about the template — see A3.

---

## 7. File map, diffstat and edit fidelity

### 7.1 File map, kept separable by item

**§A — the dead claims surface (2 files):**

| File | |
|---|---|
| `src/Server.UI/Extensions/ClaimsPrincipalExtensions.cs` | 14 methods → 2, plus the remarks explaining the deletion and where to get a tenant |
| `src/Server.UI/Endpoints/FileEndpoints.cs` | one comment, which named the deleted method |

**§B — the role descriptions (1 file):**

| File | |
|---|---|
| `src/Application/Common/Security/Permissions/Roles.cs` | 15 `[Description]` strings, plus class-level remarks |

**§C — the menu split (2 files):**

| File | |
|---|---|
| `src/Server.UI/Services/Navigation/MenuService.cs` | class-level remarks; **no mechanism change** |
| `README.md` | the menu paragraph in the authorization section |

**§D — the picklist gap (11 files):**

| File | |
|---|---|
| `src/Infrastructure/Persistence/Configurations/PicklistSetConfiguration.cs` | the second, filtered index and the Pass 32 correction |
| `src/Migrators/Migrators.{SqLite,PostgreSQL,MSSQL}/Migrations/` | 3 chains regenerated: 3 deleted migrations + 3 deleted designers, 3 new migrations + 3 new designers, 3 snapshots updated |
| `README.md` | the picklist index bullet, rewritten |
| `tests/Application.UnitTests/Features/PicklistSets/PicklistTenantUniquenessTests.cs` | the inverted test and one new one |

`README.md` is the only file touched by two items; the two edits are in different sections.

### 7.2 Diffstat

```
 GXTemplate-passes/pass40-report.md                               | 531 +++++
 README.md                                                        |  27 +-
 src/Application/Common/Security/Permissions/Roles.cs             |  61 ++-
 .../Configurations/PicklistSetConfiguration.cs                   |  33 +-
 …{20260904111910 → 20260906061143}_InitialCreate.Designer.cs     |   6 +-
 …{20260904111910 → 20260906061143}_InitialCreate.cs              |   7 +
 Migrators.MSSQL/…/ApplicationDbContextModelSnapshot.cs           |   4 +
 …{20260904111855 → 20260906061136}_InitialCreate.Designer.cs     |   6 +-
 …{20260904111855 → 20260906061136}_InitialCreate.cs              |   7 +
 Migrators.PostgreSQL/…/ApplicationDbContextModelSnapshot.cs      |   4 +
 …{20260904111834 → 20260906061121}_InitialCreate.Designer.cs     |   6 +-
 …{20260904111834 → 20260906061121}_InitialCreate.cs              |   7 +
 Migrators.SqLite/…/ApplicationDbContextModelSnapshot.cs          |   4 +
 src/Server.UI/Endpoints/FileEndpoints.cs                         |   3 +-
 src/Server.UI/Extensions/ClaimsPrincipalExtensions.cs            | 132 ++---
 src/Server.UI/Services/Navigation/MenuService.cs                 |  32 ++
 .../PicklistSets/PicklistTenantUniquenessTests.cs                |  47 +-
 17 files changed, 772 insertions(+), 145 deletions(-)
```

**The regenerated chains cost 6–7 lines each, not the ~3,950 the unstaged view suggests.** Each
chain's single `InitialCreate` was regenerated at a new timestamp, so in `git status` the tracked file
is *deleted* and an untracked one appears; the working-tree diffstat reports that as 3,952 deletions.
Once staged, rename detection pairs them and the real change is visible: **one `CreateIndex` call and
its snapshot entry, three times over.** Worth recording because the alarming number is an artifact of
when the diff is taken, and a reviewer looking at `git status` on a migration regeneration will meet
it every time.

### 7.3 Edit fidelity

- **No git actions.** Only `status`, `log`, `diff`, `show` and `grep`.
- **Four separable items**, in the brief's order, each landing in its own set of files but for the
  README's two sections.
- **`VisibleDocumentSpecification` was not touched** — the standing Pass 38 constraint.
- **The menu mechanism was not touched** — `MenuService`'s diff is 32 added comment lines and nothing
  else; `NavigationMenu.razor` and `MenuSectionSubItemModel` are unmodified.
- **`RolesAccessRights` was not touched**, so no claim string moved.
- **No migration was hand-edited.** All three were produced by `dotnet ef migrations add` against the
  changed model.
- **One pre-existing test file was modified**, the one the brief named. Its four other tests are
  byte-identical and all four still pass.

---

## 8. What remains

The tenancy queue is now empty. What is left is the non-tenancy queue, which is what this programme
turns to next.

| Item | Status |
|---|---|
| **The dead `ClaimsPrincipalExtensions` surface** | **closed.** 12 deleted, 2 kept, verified in a generated project |
| **`Roles.*` descriptions** | **closed.** All 15, plus the five inert constants named as inert |
| **Menu by role, page by permission** | **closed as a question.** Unchanged by design, now documented in `MenuService` and the README, with a test that fails a future "fix" |
| **Shared-picklist duplicate gap** | **closed on all three providers**, and Pass 32's SQL-Server claim corrected |
| **The `ReconnectModal` defect** | **open, and the largest thing left.** Login fails outright in Firefox and unsaved data is lost in both browsers. User-visible, not architectural |
| **The integration harness pinned to LocalDB** | open. `tests/Application.IntegrationTests` ignores `--Database`, so nine tests fail rather than skip on a machine without LocalDB |
| **The Respawn question** | open. Whether database reset between integration tests should move to Respawn, and what that costs on three providers |
| **The circularity sweep** | open. Tests that derive their expectation from the thing they check — the shape Pass 33 met twice and Pass 38 A1 met in a different form. A survey, not a fix |
| **The upstream contribution** | open. Which of these forty passes' findings belong back in `CleanArchitectureWithBlazorServer` |
| Picklist duplicate reporting | **new, small.** `AddEditPicklistSetCommand` has no duplicate check, so a duplicate on either partition surfaces as a `DbUpdateException` rather than a validation message (§5.6) |

---

## 9. Scratch probe disclosure

All removed, verified empty afterwards:

1. **`GXP40Probe`** on LocalDB — the §5.1 `sys.indexes` reading that corrected Pass 32. Dropped.
2. **`GXP40Verify` / `GXP40Verify_Logs`** on LocalDB, and **`GXP40Verify`** on PostgreSQL — the §5.4
   live insert proofs, built by `dotnet ef database update` against the regenerated chains. All
   dropped; `SELECT … WHERE name LIKE 'GXP40%'` returns nothing on either server.
3. **`p40.db` / `p40-logs.db`** and a **green-file backup** of `PicklistSetConfiguration.cs` in the
   session scratchpad. Removed; the scratchpad is empty.
4. **The generation probe** — packed nupkg, installed template, generated `P40`. Template uninstalled,
   both probe directories removed.

The `GXApplication` / `GXApplication_Logs` databases on the PostgreSQL server were **not** touched:
their file timestamps are 2026-08-31, so they pre-date this pass. The nupkg in the repository root is
a gitignored build artifact.

No application run this pass.

---

## 10. Anomalies

**A1 — Pass 38's census was not wrong, it was incomplete, and the incompleteness was invisible.** Its
§2.2 table named seven zero-caller methods plus `GetTenantId` and read as exhaustive; four methods —
`GetPhoneNumber`, `GetProvider`, `GetDisplayName`, `GetRoles` — never appeared in it at all. A table
that lists what was found looks identical to a table that lists what exists, and nothing in its
presentation distinguished them. **`GetDisplayName` is the one that explains the omission**: an
unrelated enum extension of the same name has four live call sites, so a grep for the name returns
hits and the method reads as used. The general form is worth keeping: *a census of a class must
enumerate the class, not accumulate the members that turned up in searches* — and where a name is
shared across receivers, the call sites have to be opened rather than counted. Recorded because this
pass began by trusting the number in its own brief, and the brief inherited it.

**A2 — the safe provider was the one nobody checked.** Pass 32 recorded SQL Server as protected
against shared duplicates. It was not, and the evidence was sitting in the migration that pass
generated: EF had written `filter: "[TenantId] IS NOT NULL AND [Value] IS NOT NULL"` onto the very
index the claim was about. The claim was reasoned from how SQL Server treats NULLs in a unique index
— which is correct, and irrelevant, because the rows never reached the index. The lesson is narrower
than "measure, don't reason": **a correct fact about the database engine was applied to a schema EF
had silently modified.** Where an ORM generates the DDL, reasoning about the DDL you *specified* is
reasoning about the wrong artifact. Recorded because the same shape — EF adding a filter to a unique
index over nullable columns — will apply to every future unique index in this template that includes
a nullable column, and `TenantId` is nullable everywhere.

**A3 — the generation probe failed for a reason that had nothing to do with the template, and would
have read as a regression.** Run from the session scratchpad, the generated project reported **61
failures** in `Infrastructure.UnitTests`, all with empty assertion messages and 1 ms durations. The
cause: `System.DllNotFoundException : Unable to load DLL 'e_sqlite3' … The filename or extension is
too long`. The scratchpad path plus
`P40/P40/tests/Infrastructure.UnitTests/bin/Debug/net10.0/runtimes/win-x64/native/e_sqlite3.dll`
exceeds `MAX_PATH`. Regenerated at a short path, the same commit gives **996 passed, 0 failed** —
identical to the repository. Recorded for two reasons: the failure presents as 61 broken tenancy and
security tests rather than as a path problem, and earlier passes used a short root directory
(`C:\gxp39`) for exactly this reason without recording *why*. It is now written down.

**A4 — the gap-assertion test paid for itself, and the payment was the point.** Pass 32 could have
closed this gap, judged it out of proportion, and written a comment. Instead it wrote a *test that
asserts the defect*, with an instruction inside it for whoever made it fail. The result is that this
pass could not close the gap quietly: the build went red, the red test explained itself, and the
inversion took one edit. Recorded as a technique rather than an incident — **an asserted gap converts
"we decided not to fix this" from a comment nobody reads into a build failure nobody can ignore**, and
it is the right instrument whenever a known defect is deferred rather than denied.
