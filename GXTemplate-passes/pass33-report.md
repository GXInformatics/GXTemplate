# Pass 33 — Role Definitions, and the Guard That Would Have Caught It

**Nature:** three editing sections. §A a ratified build, §B an investigation that built, §C an
investigation that built.
**Date:** 2026-09-05.

**Result in one line:** defining a role — create, rename, delete, re-permission, import — now needs
`Permissions.Roles.ManageDefinitions`, guarded at all six write paths, closing the strongest
cross-tenant *write* left in the template; `ModelMatchesMigrationsTests` asserts model/migration
agreement for all three providers with **no database**, and found along the way that a bare
`DbContextOptionsBuilder` builds a *different* model from the application's; and a tenant-scoped
`ManageShared` holder can now create a shared picklist row through a narrow, typed, per-instance
opt-out. **899 → 946 tests**, warnings unchanged, boundary suites byte-unmodified.

---

## 1. Start state

**The precondition's test count did not match, and this pass stopped and asked.** Everything else
checked out; the discrepancy was between the brief and Pass 32's ratified result, not between the
brief and the repository.

| | |
|---|---|
| HEAD | `c72efc1d` — *"Pass32a"* ✓ |
| Working tree | clean |
| Spot-check: `Permissions.PicklistSets.ManageShared` | present — `PicklistSetsPermissions.cs:65`, granted at `AdministratorPermissionRegistry.cs:111` ✓ |
| Spot-check: picklist index is `(TenantId, Name, Value)` | present — `PicklistSetConfiguration.cs:40` ✓ |
| Build | **0 errors, 19 warnings across 10 distinct source locations** ✓ |
| Tests | **224 + 12 + 454 + 209 = 899 passed, 12 skipped, 0 failed** — brief expected 918 ✗ |

`git show --stat HEAD` matches Pass 32's §7.1 file map line for line — three new test files, eight
modified source files, README, and all three regenerated `InitialCreate` sets — and Pass 32 §5.4
states 899 as delivered (877 → 899, +22). Nothing was missing. **You confirmed 899 as the baseline**,
as in Pass 32 §1 where the brief expected 891 against 877. Every delta below is measured against 899.

---

## 2. §A — The role-definition right

### 2.1 The constant

`Permissions.Roles.ManageDefinitions`, in the roles section following its existing naming, with an
`AccessRights` property spelled identically (`PermissionService` turns the property name into the
claim string — `LogsAccessRights` is the precedent for what a mismatch costs, and
`TheAccessRightsPropertyIsSpelledLikeTheConstant` pins it).

**One right, not one per verb.** The section's other constants are per-verb, but this is not a verb —
it is the boundary between administering the installation's roles and administering your own
tenant's users. Splitting it invites a grant that lets someone delete a role but not fix it. Same
reasoning as `ManageShared`.

**Granted in `AdministratorPermissionRegistry`**, which the divergence assertion makes a deliberate
act. The reason recorded in the file is the one §A gave: it *preserves the posture that already
held*, and it keeps the single-tenant deployment working, because `EnsureAdministratorAsync` assigns
the bootstrap administrator `Tenants.First()` — the sole administrator is itself tenant-scoped, so a
blanket prohibition would have left the installation's only roles unmanageable by anyone.

### 2.2 The rule, and where it lives

**`RoleDefinitionWrite`**, a new Application-layer static type — `Refused`, `MayDefineRolesAsync`,
`EnsureAllowedAsync`. Two deliberate departures from `SharedPicklistWrite`:

- **It throws `ForbiddenAccessException` rather than returning a `Result` failure.** Its callers are
  a dialog, a page and a service, not Mediator handlers, and every existing refusal on those same
  buttons — `AdministratorProtectionService`'s three, `PermissionAssignmentService`'s
  grant-what-you-hold — is already that exception, caught and surfaced in a snackbar. A second
  refusal shape on the same buttons would have been the inconsistency.
- **It lives at `Features.Identity`, not `Features.Identity.Roles`.** A namespace ending in `.Roles`
  collides with `Common.Constants.Roles`, which `_Imports.razor` imports globally and which
  `AdministratorProtectionService` uses as `Roles.Admin`. `UserTenantVisibility` already sits at
  `Features.Identity`, so this follows it.

`IPermissionQueryService`, not `IPermissionService` — the latter resolves the principal through
Blazor's `AuthenticationStateProvider`, and `PermissionAssignmentService` is reachable outside a
circuit. Pass 27 and Pass 28 both hit that.

### 2.3 The guards, site by site

Pass 32 §4.1 inventoried the write paths. All six are guarded, **beside**
`AdministratorProtectionService`'s guards rather than in a second location — because role
administration bypasses Mediator, so `AuthorizationBehaviour`'s deny-by-default never runs and there
is no chokepoint to hook.

#### (1) and (2) `RoleFormDialog.Submit` — create and rename

Before:

```csharp
private async Task Submit()
{
    if (_editContext.Validate())
    {
        var existingRole = await _roleManager.FindByIdAsync(Model.Id);
```

After:

```csharp
private async Task Submit()
{
    if (_editContext.Validate())
    {
        // Roles are installation-wide, so creating one or renaming one changes what every
        // tenant sees. Checked BEFORE the lookup and before anything is written, so a refused
        // save leaves the role exactly as it was - and covers BOTH branches below with one
        // guard, because create and rename are the same capability.
        try
        {
            await RoleDefinitionWrite.EnsureAllowedAsync(
                _permissionQueryService, _userContextAccessor.Current?.UserId);
        }
        catch (ForbiddenAccessException ex)
        {
            Snackbar.Add(ex.Message, Severity.Error);
            return;
        }

        var existingRole = await _roleManager.FindByIdAsync(Model.Id);
```

One guard above the `if/else`, not one per branch: create and rename are the same capability, and two
guards would be two places to forget.

#### (3) `Roles.OnDelete` — single

```csharp
try
{
    await EnsureMayDefineRolesAsync();
    _administratorProtection.EnsureRoleCanBeDeleted(dto.Name);
}
```

Placed **before the confirmation prompt**, where `EnsureRoleCanBeDeleted` already was, so a refused
caller is told immediately rather than after agreeing to a deletion that cannot happen.

#### (4) `Roles.OnDeleteChecked` — bulk

```csharp
// All or nothing, and checked before the prompt: one protected role in the selection
// refuses the whole command, so the caller is never left working out which rows
// survived. The definition guard is asked once - it does not vary per row.
await EnsureMayDefineRolesAsync();
foreach (var selected in _selectedRoles)
{
    _administratorProtection.EnsureRoleCanBeDeleted(selected.Name);
}
```

#### (5) `Roles.ProcessImportedRolesAsync` — import

```csharp
// The guard is on the WRITE path, not on the file picker: an import creates roles every
// tenant will then share, and it is the only path that creates several at once. Checked
// before the plan is built, so a refused import reads nothing and writes nothing.
await EnsureMayDefineRolesAsync();
```

`OnImportData` gained a `catch (ForbiddenAccessException)` ahead of its general handler, so the
refusal reads as a refusal rather than as *"Import failed: &lt;reason&gt;"*, which would suggest a
malformed file.

#### (6) `PermissionAssignmentService.AssignRoleAsync` / `AssignRoleBulkAsync`

```csharp
_administratorProtection.EnsureRolePermissionsCanBeModified(role.Name);
// Re-permissioning a role is DEFINING it: roles are installation-wide, so removing a claim
// here takes the capability from every ordinary user in every tenant at once. A different
// guarantee from the line above, which keeps the installation administrable - both run.
await RoleDefinitionWrite.EnsureAllowedAsync(
    _permissionQueryService, _userContextAccessor.Current?.UserId);
```

Both dependencies were already on the service. The bulk method is checked **once for the batch and
before any claim is written**, so a refused bulk grant leaves the role exactly as it was.

**Ordering is asserted, not assumed.** The definition guard runs before `GetActorAsync`, so a refused
caller costs one permission query rather than a claims-principal rebuild, and the message they get is
the broadest true reason rather than a narrower one that happens to fire first.
`TheDefinitionGuardIsAskedBeforeTheActorIsBuilt` uses an actor who would *also* fail
`EnsureNotTargetingAHeldRole` and `EnsureActorHolds`, and asserts which refusal speaks.

### 2.4 `RoleDataSourceService.Scope` stays `Global`

Unchanged, and the comment that named Pass 23 §2.5 as an open question now records the answer:

> **Pass 23 §2.5's open question is now CLOSED, and the answer left this line alone.** […] That is a
> pure authorization change: who may WRITE a role changed, and writes do not enter a read's cache
> key. […] That it needed no change here was a point in the option's favour, weighed in Pass 32 §4.5.

The paragraph on what *would* change it — per-tenant roles → `PerTenant`, a cross-tenant escape →
`PerUser` per Pass 28 — is kept, because neither would fail to compile.
`ANonHolderCanStillREADTheRoleList` carries the same claim as a test.

### 2.5 The UI — second line, and Pass 32 A3 checked rather than assumed

Every affordance is gated on `ManageDefinitions` **as well as** its own right: create, bulk delete,
import, and the per-row menu (whose `else` branch already renders the `NoAllowed` button). **Export
is deliberately not gated** — reading the installation's roles is not defining them, and the grid
already lists every one.

A `MudAlert` above the grid says *why*, and says what the reader can still do:

> Roles are shared by every tenant in this installation. Defining them — creating, renaming,
> deleting, re-permissioning and importing — requires the 'manage role definitions' permission.
> Assigning users to these roles does not, and is done from the Users page.

Without it, a principal holding `Roles.Edit` sees a grid of roles and no way to edit any of them,
which reads as a bug rather than a decision — and the thing they *can* still do is the part they
would otherwise go looking for in the wrong place.

**Pass 32 A3 was checked, and does not apply here.** That anomaly said a `CellTemplate` on a
`DataGridEditMode.Cell` grid is unreachable. The roles grid is **not** in cell-edit mode, so its
`CellTemplate` *is* reached; `WithoutTheRight_TheRowMenuIsReplacedByTheNotAllowedButton` proves it by
rendering rather than by reading the razor. A comment in the file records the distinction so the next
reader does not have to re-derive it.

---

## 3. §B — The model/migration guard

`ModelMatchesMigrationsTests`, in `Infrastructure.UnitTests` beside `GxTableNamingTests` (whose
comment already mentioned the empty-migration claim). **4 tests, ~1 second, no database.**

### 3.1 Can `HasPendingModelChanges` be asserted without a live database? — Yes, confirmed

It compares the context's model with the snapshot compiled into the migrations assembly. Both are
in-memory artifacts. The connection strings are parseable placeholders that are never opened:

```
SQLite      DataSource=:memory:
SQL Server  Server=(local);Database=GxModelCheck;Trusted_Connection=True;
PostgreSQL  Host=localhost;Database=GxModelCheck;Username=gx;Password=gx
```

Confirmed by running it, not by reading documentation. The whole fixture is **929 ms**.

### 3.2 Where it belongs, and what it costs

`tests/Infrastructure.UnitTests/Persistence/`. The migration assemblies are resolved **by name** at
runtime — `UseDatabase` calls `MigrationsAssembly("CleanArchitecture.Blazor.Migrators.*")` — so the
test project needed three `ProjectReference`s to get their DLLs into its output directory. Those are
the only references there that exist for a test rather than for the code under test, and the csproj
says so.

**No design-time host is needed, per provider or at all.** That was the outcome the brief asked about
and it is the reason this is affordable: `dotnet ef` builds the application's real service provider,
which is why regenerating for a non-configured provider needs connection-string overrides. This test
needs none of that. **Cost: under one second added to a 96-second suite.**

### 3.3 The finding that made it possible — and it nearly went the other way

**The first draft failed on all three providers at a HEAD whose migrations were correct.** The
difference, extracted with `IMigrationsModelDiffer`, was a single operation:

```
OP DropTableOperation :: Name=AspNetUserPasskeys, Schema=, IsDestructiveChange=True
```

`IdentityDbContext.OnModelCreating` maps `IdentityUserPasskey` only when
`IdentityOptions.Stores.SchemaVersion` is Version 3 or later, and it reads that from
`IOptions<IdentityOptions>` on the DbContext options' **application service provider**. A context
built from a bare `DbContextOptionsBuilder` therefore has a *different model from the running
application's* — quietly, with no error.

**The migrations were right and the test was wrong, and that was established rather than assumed:**
running the README's own procedure —

```
DatabaseSettings__DBProvider=sqlite dotnet ef migrations add Probe33 \
  --project src/Migrators/Migrators.SqLite --startup-project src/Server.UI \
  --context ApplicationDbContext
```

— produced an **empty** migration. Had that check not been made, this pass would have "regenerated"
three correct migration chains to match a defective test. The probe migration was removed with
`ef migrations remove`, which rewrote the snapshot's `ToTable("X")` as `ToTable("X", (string)null)`
on five lines; that cosmetic churn was reverted with `git checkout` and the snapshots are byte-identical to HEAD.

The fix required a small source change, and its shape matters: `DependencyInjection` now exposes

```csharp
public static void ConfigureIdentityOptions(IdentityOptions options)
{
    options.SignIn.RequireConfirmedAccount = true;
    options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
}
```

which `AddIdentityCore` calls and which the test calls. **The test does not restate the value.** A
second copy of a model-affecting setting would make the test green while the application and its
migrations disagreed — the exact failure the test exists to catch, reintroduced inside the test.
`TheModelUnderTestIsTheApplicationsModel` asserts `AspNetUserPasskeys` is in the model under test, so
a future "fix" that drops `UseApplicationServiceProvider` — making all three pass by comparing a
smaller model against itself — fails instead.

### 3.4 The failure message

Named provider, named project, the exact command, and the two things that are *not* the fix:

```
The PostgreSQL migrations no longer match the model.

Something in the entities or in an IEntityTypeConfiguration changed without the
migrations being regenerated. Every other test will stay green - they build their
schema from the MODEL - and only a real `dotnet ef database update` would have shown
this, at deployment time.

Regenerate, from the repository root:

  DatabaseSettings__DBProvider=postgresql dotnet ef migrations add <Name> \
    --project src/Migrators/Migrators.PostgreSQL --startup-project src/Server.UI \
    --context ApplicationDbContext

Then do the same for the OTHER TWO providers - all three are regenerated together, and
the README's "If you change the model" section carries the connection-string overrides
a non-configured provider needs.

Do NOT hand-edit a migration or its snapshot to make this pass. The snapshot is what
the next migration diffs against, so an edited one is wrong for every migration after
this point, not just this one.
```

### 3.5 Red before, green after

A second index added to `PicklistSetConfiguration` without regenerating:

```
ModelMatchesMigrationsTests   Failed: 3,  Passed: 1
```

All three providers red; `TheModelUnderTestIsTheApplicationsModel` correctly stayed green, since the
model was still the application's. Reverted and verified against HEAD with `git diff --quiet`.

---

## 4. §C — Shared picklist creation

**Implemented.** The mechanism turned out to be smaller than Pass 32's description of the problem
suggested, because that description was slightly wrong.

### 4.1 The obstacle was not what Pass 32 §2.5 said it was

Pass 32 recorded that *"the stamping interceptor writes the ambient tenant unconditionally"*. **It
does not, and had not since Pass 24:**

```csharp
if (entity is IMayHaveTenant mayTenant && mayTenant.TenantId==null) mayTenant.TenantId = tenantId;
```

It stamps only when `TenantId` is already null. The real obstacle is subtler and is why the gap
existed at all: **null is the sentinel for "not set yet" *and* the value that means "shared"**, so a
tenant-scoped principal had no way to say which one they meant. That is the whole problem, and the
mechanism is exactly that distinction and nothing more.

### 4.2 The mechanism

`IMayBeShared : IMayHaveTenant` with one member, `bool CreateAsShared`, implemented by `PicklistSet`
as `[NotMapped]`. The interceptor:

```csharp
if (entity is IMayHaveTenant mayTenant && mayTenant.TenantId==null && !IsDeliberatelyShared(entity))
    mayTenant.TenantId = tenantId;

private static bool IsDeliberatelyShared(IAuditableEntity entity) =>
    entity is IMayBeShared shared && shared.CreateAsShared;
```

The command gains `bool IsShared`, and the handler:

```csharp
var prospectiveTenantId = request.IsShared
    ? null
    : _userContextAccessor.Current?.TenantId;

if (!await SharedPicklistWrite.IsAllowedAsync(
        [prospectiveTenantId], _permissionQueryService, userId))
    return await Result<int>.FailureAsync(SharedPicklistWrite.Refused);

var keyValue = _objectMapper.Map<PicklistSet>(request);
keyValue.CreateAsShared = SharedPicklistWrite.IsShared(prospectiveTenantId);
```

**The flag is set from the prediction, not from the request**, and after the guard — so the tenant the
guard authorised and the tenant the interceptor will produce are one decision rather than two that
happen to agree. It is unreachable on any path that did not pass the check.

Of the brief's three options this is (b), *a property the interceptor honours*, carried on the entity
rather than the command. **Not (a), an opt-out the handler sets**, because "opt-out" is ambient by
nature and there is no scope to switch off. **Not (c), post-insert correction** — the audit row would
record the wrong tenant, and `TheFlagGrantsNothing_ARefusedCallerNeverReachesTheInterceptor` asserts
there is *no row at all* after a refusal, which a correction would not have given.

### 4.3 Why this does not become a general escape from stamping

Pass 24 made stamping deliberate and the brief's constraint is the right one. Four containments, and
**two of them are tests rather than claims**:

| | Containment | Held by |
|---|---|---|
| 1 | **Opt-in by TYPE.** Only `IMayBeShared` implementers, today `PicklistSet` alone. `Document`, `AuditTrail` and every `IMustHaveTenant` entity are structurally out of reach | `NothingElseInTheDomainCanOptOutOfStamping` asserts the list of implementers is exactly `{PicklistSet}` |
| 2 | **Per INSTANCE.** No ambient switch, no scope, no service to resolve. Marking one row says nothing about the next, and nothing can turn stamping off for a save, a request or a process | the shape of the API |
| 3 | **`[NotMapped]`.** Never a column, so it cannot arrive from a client, cannot survive a DTO round-trip, and is never true on a row read back | `TheFlagNeverReachesTheDatabase`; and `ModelMatchesMigrationsTests` would go red on all three providers if the attribute were forgotten |
| 4 | **It grants nothing.** The handler still checks the right over the tenant the row will carry, and refuses before the entity is added | `TheFlagGrantsNothing_ARefusedCallerNeverReachesTheInterceptor` |

`IMustHaveTenant` is untouched and stays unconditional: *may have no tenant* and *must have one* are
different contracts, and only the first has a shared partition to opt into.
`IMustHaveTenantEntitiesAreStructurallyOutOfReach` asserts no implementer is both.

**Creation only.** The edit path ignores the flag entirely — moving a row between partitions changes
who sees it and which rows the unique index constrains it against, the DTO round-trips through the
browser, and nobody asked for it. `TheEditPathIgnoresTheSharedFlag` pins it, asserting the edit *did*
take effect while the tenant did not move.

### 4.4 Pass 32 A2, tightened rather than left

A2 recorded that the create guard's "the tenant this row will carry" is a second copy of the
interceptor's stamping rule. This pass makes the coupling tighter — the handler now sets the flag the
interceptor reads — so the test that runs the real interceptor and asserts the created row's tenant is
kept and extended. Every assertion in `SharedPicklistCreationTests` goes through the real interceptor
and reads the stored tenant back, and two tests split the coupling deliberately:
`AFlaggedRowSavedDIRECTLYIsNotStamped` fails if the *interceptor* stops honouring the flag,
`ATenantScopedHolderCanNowCreateASharedRow` if the *handler* stops setting it.

### 4.5 The UI

The create dialog gains a "share this value with every tenant" switch with an explanatory caption,
rendered **only for a holder** — a toggle that produces a refusal on Save is worse than no toggle. The
page passes `_accessRights.ManageShared` as a parameter rather than the dialog resolving it, so
there is one answer rather than two free to differ.

### 4.6 Red before, green after

Interceptor guard removed (`!IsDeliberatelyShared(entity)` deleted):

```
SharedPicklistCreationTests   Failed: 3,  Passed: 10
```

Red: `ATenantScopedHolderCanNowCreateASharedRow`, `TheSharedRowIsThenVisibleToAnotherTenant`,
`AFlaggedRowSavedDIRECTLYIsNotStamped`. The ten controls — including every "still creates a private
row" assertion — stayed green, which is the point of having them. Restored and verified byte-identical
by `diff`.

---

## 5. Verification

### 5.1 §A's evidence — through the service and through the components

**Re-permissioning is proved through the real `PermissionAssignmentService`**, which is the one path
that has a service. Every assertion sends a real call and then reads the role's claims back.

| Claim | Test |
|---|---|
| A non-holder cannot re-permission a role **through the service** | `ANonHolderCannotRePermissionARoleThroughTheService` — claims re-read, so a refusal beside a write that happened anyway fails |
| …nor in bulk | `ANonHolderCannotRePermissionARoleInBulk` |
| …nor **revoke**, which is the direction that reaches every tenant | `ANonHolderCannotREVOKEAPermissionEither` |
| A holder can, singly and in bulk | `AHolderCanRePermissionARole`, `AHolderCanRePermissionARoleInBulk` |
| The coarse gate speaks first | `TheDefinitionGuardIsAskedBeforeTheActorIsBuilt` |
| The rule fails closed | `TheRuleFailsClosedWithNoPrincipal` — and asserts the permission query is not even reached |
| An **unassigned** grant is not a grant | `AnUnassignedGrantIsNotAGrant` |
| The old per-verb rights do not imply the new one | `AnUnrelatedPermissionIsNotThisOne` |
| The refusal says what is still allowed | `TheRefusalSaysWhatIsStillAllowed` |
| The administrator holds it by default | `TheAdministratorHoldsTheRightByDefault` |

**Create, rename, delete and import are proved through the real components against a real
`RoleManager` over SQLite, reading the role store back** — because those paths have no service and a
test asserting which buttons render would prove the decoration rather than the guard.

| Claim | Test |
|---|---|
| A non-holder's dialog Submit creates no role | `WithoutTheRight_TheDialogCreatesNoRole` |
| …and does not rename an existing one | `WithoutTheRight_TheDialogDoesNotRenameAnExistingRole` |
| A holder's does both | `WithTheRight_TheDialogCreatesTheRole`, `WithTheRight_TheDialogRenamesTheRole` |
| A non-holder's import creates no role | `WithoutTheRight_TheImportPathCreatesNoRole` |
| A holder's creates them | `WithTheRight_TheImportPathCreatesTheRoles` |
| A non-holder's delete deletes nothing **and asks for no confirmation** | `WithoutTheRight_TheSingleDeletePathDeletesNothing`, `WithoutTheRight_TheBulkDeletePathDeletesNothing` |
| A holder's delete really deletes | `WithTheRight_TheSingleDeletePathDeletesTheRole`, `WithTheRight_TheBulkDeletePathDeletesTheSelection` |
| The page says why, and says what is still allowed | `WithoutTheRight_ThePageSaysWhyRatherThanShowingAnEmptyToolbar`, `WithTheRight_ThePageShowsNoSuchNotice` |
| The row menu is replaced by `NoAllowed` | `WithoutTheRight_TheRowMenuIsReplacedByTheNotAllowedButton` |

The delete tests are genuinely end-to-end because the fixture registers an `IDialogService` whose
confirmation answers **yes** immediately — so the holder's delete actually executes and the assertion
is a role that is really gone, not a prompt that was really opened. The refusals happen before the
prompt is ever requested, which a separate `_confirmationsShown` counter asserts.

**The assignment control — the whole point of the option.**

| Claim | Test |
|---|---|
| A non-holder can still **assign** a user to an existing role | `ANonHolderCanStillAssignAUserToAnExistingRole` — runs the sequence `UserFormDialog.SubmitAsync` runs (`EnsureRoleRewriteKeepsAnAdministratorAsync`, then the `UserManager` rewrite) with an actor who cannot define roles, and then re-asserts that the actor could not have defined the role they just assigned |
| …and still remove one | `ANonHolderCanStillRemoveAUserFromARole` |
| …and still read the role list | `ANonHolderCanStillREADTheRoleList` |

**`AdministratorProtectionService`'s rules are unchanged and are held apart from this one.** They are
a different guarantee — the installation stays administrable — and they bind the holder of the new
right as much as anyone: `AHolderIsStillBoundByAdministratorProtection` (a holder still cannot
re-permission `Admin`) and `AHolderIsStillRefusedOnTheProtectedAdministratorRole` (a holder still
cannot delete it). Its own suite, `AdministratorProtectionTests`, is byte-unmodified and green.

**Pass 32 A5 noted.** A fixture testing only refusals is green while proving nothing works. Both new
fixtures create real user rows through `UserManager` / `ApplicationUser`, and every success path
writes and is read back.

### 5.2 §A red before, green after

Guards removed at all six call sites, the UI gating reverted, and the alert deleted:

```
Application.UnitTests       Failed: 4,  Passed: 39   (of the four filtered fixtures)
Server.UI.IntegrationTests  Failed: 7,  Passed: 7
```

Red: `ANonHolderCannotRePermissionARoleThroughTheService`, `ANonHolderCannotRePermissionARoleInBulk`,
`ANonHolderCannotREVOKEAPermissionEither`, `TheDefinitionGuardIsAskedBeforeTheActorIsBuilt`,
`WithoutTheRight_TheDialogCreatesNoRole`, `WithoutTheRight_TheDialogDoesNotRenameAnExistingRole`,
`WithoutTheRight_TheImportPathCreatesNoRole`, `WithoutTheRight_TheSingleDeletePathDeletesNothing`,
`WithoutTheRight_TheBulkDeletePathDeletesNothing`,
`WithoutTheRight_ThePageSaysWhyRatherThanShowingAnEmptyToolbar`,
`WithoutTheRight_TheRowMenuIsReplacedByTheNotAllowedButton`.

Every "with the right" and every "still can" control stayed green. Restored byte-identically from
copies taken beforehand, verified by `diff`.

**One control failed to fail, and was fixed — see A2.** In the first run
`ANonHolderCannotRePermissionARoleInBulk` stayed green with the guard removed, because its actor did
not hold `Documents.Download` and grant-what-you-hold refused it anyway. The actor now holds both
permissions in their claims principal, so the refusal is attributable to the definition guard and to
nothing else; re-running the demonstration turned it red.

### 5.3 Boundary suites — green and byte-unmodified

`git diff --quiet` per file, all **22** unmodified, covering Passes 26–32's scope, isolation, filter,
presence and guard suites plus the two this pass reasons hardest about:

```
HarnessPrincipalTests            AuditTrailTenantFilterTests       SwitchableTenantsTests
TenantSwitchAuthorizationTests   TenantVisibilityTests             UserVisibilityTests
DataSourceScopeTests             PicklistDataSourceScopeTests      PicklistSetTenantFilterTests
PicklistSeedVisibilityTests      OnlineUsersTrackerComponentTests  ServerHubTenantIsolationTests
SuperiorAutocompleteScopeComponentTests   SuperiorBoundComponentTests   TenantSelectorComponentTests
UserDeactivationPermissionComponentTests  UserTenantScopeComponentTests
SharedPicklistWriteTests         PicklistTenantUniquenessTests     SharedPicklistGridComponentTests
TenantStampingTests              AdministratorProtectionTests
```

Run as a filtered set: **179 passed, 0 failed** (38 + 79 + 3 + 59).

That `TenantStampingTests` and `SharedPicklistWriteTests` pass **unchanged** against a modified
interceptor and a modified create handler is the useful result: §C widened stamping without
disturbing what Pass 24 and Pass 32 pinned.

### 5.4 Counts

| | Before | After | Delta |
|---|---|---|---|
| `Infrastructure.UnitTests` | 224 | **228** | **+4** |
| `Application.IntegrationTests` | 12 | 12 | — |
| `Application.UnitTests` | 454 (+12 skipped) | **483** (+12 skipped) | **+29** |
| `Server.UI.IntegrationTests` | 209 | **223** | **+14** |
| **Total** | **899 passed, 12 skipped** | **946 passed, 12 skipped** | **+47, 0 failed** |

The +47 is exactly the four new files: 4 `ModelMatchesMigrationsTests`, 16
`RoleDefinitionRightTests`, 13 `SharedPicklistCreationTests`, 14 `RoleDefinitionComponentTests`. **No
pre-existing test changed count or outcome**, including the two in `PermissionAssignmentGuardTests`
that briefly went red — see §7.3.

**Warnings: unchanged.** `dotnet build --no-incremental` gives **19 warnings across the same 10
distinct source locations** — `DescriptionAttributeExtensions.cs` ×4, `MapsterConfiguration.cs` ×2,
`MudDateTimeField.razor`, `TenantSelect.razor`, `Dashboard.razor`, `AuditTrails.razor` — plus
`NETSDK1206`, which is SDK-emitted once per project with no source location. **No new warning
location. 0 errors.**

### 5.5 Generation probe

```
nuget pack (nuspec) → dotnet new install → dotnet new gxblazor -n P33
  → build: 0 Error(s), 19 Warning(s)
  → dotnet test: 228 + 12 + 483 + 223 = 946 passed, 12 skipped, 0 failed
  → dotnet new uninstall; probe directory removed
```

Identical to source, suite for suite. **One result worth naming:** the generated project's
`ModelMatchesMigrationsTests` passes against the generated migrations, and the template's rename
machinery rewrote the assembly names inside it — `"P33.Migrators.SqLite"` in the test, matching
`SQLITE_MIGRATIONS_ASSEMBLY = "P33.Migrators.SqLite"` in the generated `DependencyInjection`. A guard
that resolved assemblies by name could easily have become a no-op in a generated project; it did not.

---

## 6. README and package metadata

- **Tenancy table, Roles row:** now *"Reading them is unrestricted; DEFINING one — create, rename,
  delete, re-permission, import — needs `Roles.ManageDefinitions`. Assigning a user to an existing
  role does not: that stays on `Users.*`"*.
- **New Tenancy passage on roles**: the concrete harm, the five write paths, why it is
  default-granted, what a tenant administrator keeps, and why roles were deliberately *not* made
  per-tenant (Identity's own `RoleNameIndex` and `FindByNameAsync`), including that
  `RoleDataSourceService.Scope` stays `Global`.
- **Picklists bullet rewritten**: *"Shared rows come from seeding, **or from a `ManageShared` holder
  who asks for one**"* — the switch, who is offered it, that editing never moves a row, and the
  `IMayBeShared` mechanism with its containment.
- **New passage under the migrations section**: `ModelMatchesMigrationsTests`, what a failure means,
  and the `AspNetUserPasskeys` / application-service-provider trap for anyone building an
  `ApplicationDbContext` from a bare options builder.
- **`GX.Blazor.Template.nuspec` description** corrected: it said *"system logs and roles remain
  installation-wide"*, which now understates. It reads *"Roles stay installation-wide by design, but
  DEFINING one needs a named, revocable right"*, and names the shared-picklist right too.

---

## 7. File map, diffstat and edit fidelity

### 7.1 File map

**New (6):**

| File | |
|---|---|
| `src/Application/Features/Identity/RoleDefinitionWrite.cs` | the rule: `Refused`, `MayDefineRolesAsync`, `EnsureAllowedAsync` (123 lines) |
| `src/Domain/Common/Entities/IMayBeShared.cs` | the shared-creation marker and its containment argument (60) |
| `tests/Application.UnitTests/Identity/Roles/RoleDefinitionRightTests.cs` | **16 tests** — the rule, the service, the assignment control (488) |
| `tests/Server.UI.IntegrationTests/RoleDefinitionComponentTests.cs` | **14 tests** — the four component write paths, store-level (495) |
| `tests/Infrastructure.UnitTests/Persistence/ModelMatchesMigrationsTests.cs` | **4 tests** — three providers plus the model-identity control (214) |
| `tests/Application.UnitTests/Features/PicklistSets/SharedPicklistCreationTests.cs` | **13 tests** — the mechanism and its containment (360) |

**Modified (14 + 2 metadata):**

| File | |
|---|---|
| `src/Application/Common/Security/Permissions/Roles.cs` | `ManageDefinitions` constant + `AccessRights` property |
| `src/Application/Common/Security/AdministratorPermissionRegistry.cs` | granted, with the single-tenant reason |
| `src/Application/Features/PicklistSets/Commands/AddEdit/AddEditPicklistSetCommand.cs` | `IsShared`; create branch predicts the tenant and sets the flag |
| `src/Domain/Entities/PicklistSet.cs` | implements `IMayBeShared`; `[NotMapped] CreateAsShared` |
| `src/Infrastructure/Persistence/Interceptors/AuditableEntityInterceptor.cs` | honours the flag; `IMustHaveTenant` untouched |
| `src/Infrastructure/Services/Identity/PermissionAssignmentService.cs` | guards on both role methods |
| `src/Infrastructure/Services/Identity/RoleDataSourceService.cs` | `Scope` comment: Pass 23 §2.5 closed |
| `src/Infrastructure/DependencyInjection.cs` | `ConfigureIdentityOptions` extracted and made public |
| `src/Server.UI/Pages/Identity/Roles/Roles.razor` | three guards, UI gating, the alert |
| `src/Server.UI/Pages/Identity/Roles/Components/RoleFormDialog.razor` | the create/rename guard |
| `src/Server.UI/Pages/PicklistSets/Components/CreatePicklistDialog.razor` | the shared switch |
| `src/Server.UI/Pages/PicklistSets/PicklistSets.razor` | passes `CanManageShared` |
| `tests/Application.UnitTests/Identity/PermissionAssignmentGuardTests.cs` | stub grants `ManageDefinitions` — see §7.3 |
| `tests/Infrastructure.UnitTests/Infrastructure.UnitTests.csproj` | three migrator `ProjectReference`s |
| `README.md`, `GX.Blazor.Template.nuspec` | §6 |

**No migrations were regenerated, and none needed to be** — asserted, for the first time, rather than
assumed. `[NotMapped]` keeps `CreateAsShared` out of the schema and
`ModelMatchesMigrationsTests` proves it on all three providers.

### 7.2 Diffstat (source and tests, excluding the six new files)

```
 GX.Blazor.Template.nuspec                                        |  7 ++-
 README.md                                                        | 55 ++++++++++++++--
 src/Application/Common/Security/AdministratorPermissionRegistry.cs| 15 +++++
 src/Application/Common/Security/Permissions/Roles.cs             | 38 +++++++++++-
 .../PicklistSets/Commands/AddEdit/AddEditPicklistSetCommand.cs   | 53 +++++++++++++---
 src/Domain/Entities/PicklistSet.cs                               | 18 +++++-
 src/Infrastructure/DependencyInjection.cs                        | 32 +++++++++--
 .../Persistence/Interceptors/AuditableEntityInterceptor.cs       | 32 ++++++++++-
 .../Services/Identity/PermissionAssignmentService.cs             | 10 ++++
 .../Services/Identity/RoleDataSourceService.cs                   | 17 ++++--
 .../Identity/Roles/Components/RoleFormDialog.razor               | 20 +++++++
 src/Server.UI/Pages/Identity/Roles/Roles.razor                   | 67 ++++++++++++++++++--
 .../PicklistSets/Components/CreatePicklistDialog.razor           | 22 +++++++
 src/Server.UI/Pages/PicklistSets/PicklistSets.razor              |  1 +
 tests/Application.UnitTests/Identity/PermissionAssignmentGuardTests.cs | 28 ++++++-
 tests/Infrastructure.UnitTests/Infrastructure.UnitTests.csproj   |  7 +++
 16 files changed, 392 insertions(+), 30 deletions(-)
```

Plus 1,740 lines across the six new files.

### 7.3 Edit fidelity

- **No git actions**, as the brief required. Nothing staged, committed, stashed or reset. The one
  `git checkout` was on `src/Migrators/`, to undo the cosmetic snapshot churn left by the §B probe's
  `ef migrations remove` — restoring HEAD's own bytes, not this pass's work.
- **All three red-before demonstrations were reverted byte-identically**, verified by `diff` against
  copies taken beforehand (§A's six guards and UI; §B's index; §C's interceptor line).
- **One existing test file was modified, and it had to be.** `PermissionAssignmentGuardTests`'s
  `StubPermissionQueryService` returned an empty list, which the new guard reads — so
  `AnActorCanAdministerARoleTheyDoNotHold` and `AnActorCannotChangePermissionsOnARoleTheyHold` were
  refused on the new right before reaching the rule they exist to test. The stub now grants exactly
  `Roles.ManageDefinitions` and nothing else, with a comment saying why; granting only one permission
  rather than all of them keeps visible that `EnsureActorHolds` reads the actor's *claims principal*
  and not this service. Both tests pass again with no change to what they assert. **It is not a
  boundary suite** and is not on any pass's byte-unmodified list.
- **The three migrator `ProjectReference`s** are the only references in `Infrastructure.UnitTests`
  that exist for a test rather than for the code under test; the csproj comment says so.
- `ConfigureIdentityOptions` is a **source change made for a test**, and deliberately so: the
  alternative was a second copy of a model-affecting setting inside the test, which would have made
  the test green while the model and migrations disagreed.

---

## 8. What remains

| Surface | Status |
|---|---|
| **Role definitions** | **closed by this pass** — `Roles.ManageDefinitions`, guarded at six write paths, default-granted |
| **Model/migration agreement** | **closed by this pass** — asserted for all three providers, no database, ~1 s |
| **Creating a shared picklist as a tenant-scoped holder** | **closed by this pass** — `IMayBeShared.CreateAsShared`, opt-in by type, per instance, `[NotMapped]`, authorising nothing |
| Duplicate SHARED picklist values on SQLite/PostgreSQL | **known gap, still asserted** (Pass 32 §3). Both treat NULLs as distinct in a unique index, so the shared partition is unprotected there; SQL Server is protected because it treats NULLs as equal. Closing it portably needs a second, *partial* unique index over `(Name, Value) WHERE TenantId IS NULL`, whose filter SQL differs per provider. `TheSharedPartitionIsNotProtectedFromDuplicatesOnThisProvider` fails if it is ever closed, at which point that fixture should assert the protection instead. **§C makes this slightly more reachable than it was** — a holder can now create shared rows deliberately rather than only through seeding — but it does not change the shape of the fix |
| **System logs** | unscoped, and **not reachable by the global filter**: `SystemLog` is on `LogDbContext`, not `ApplicationDbContext`. A separate design, not a deferred switch. A system log remains readable in full by any holder of its view permission, whichever tenant they belong to |
| **Security settings (idle policy)** | unscoped — one row per installation. Plausibly correct as-is: it is a property of the deployment rather than of a customer. A product question whose answer is probably "leave it", but it should be answered rather than assumed |
| Per-tenant roles | **decided against, with the cost measured** (Pass 32 §4.3, ratified here). It is not "add a column": it means replacing Identity's `RoleNameIndex` and every `FindByNameAsync`/`RoleExistsAsync` lookup, provisioning a role set per tenant at creation time, and a data migration forking existing roles. §A's right closes the cross-tenant write without any of it |
| Moving a picklist row between partitions | not possible, deliberately. Would change who sees it and which rows the unique index constrains it against |

---

## 9. Scratch probe disclosure

Four, all removed:

1. **Green-file backups** for the three red-before demonstrations, under the session scratchpad.
2. **`TempDiffProbe.cs`**, a throwaway xunit test in `Infrastructure.UnitTests` that dumped
   `IMigrationsModelDiffer` output to find the `AspNetUserPasskeys` difference. Deleted; it does not
   appear in any count above.
3. **A real `dotnet ef migrations add Probe33`** against the SQLite migrator, to establish that the
   migrations were correct and the test was wrong. Removed with `ef migrations remove`; the cosmetic
   snapshot churn that left behind was reverted with `git checkout src/Migrators/`, and
   `git status` shows the migrations untouched. Two throwaway SQLite files (`probe33.db`,
   `probe33-logs.db`) were created by the design-time host under the system temp directory.
4. **The generation probe**: packed nupkg, installed template, generated `P33` at `C:\gxp33` —
   template uninstalled, directory removed. The nupkg in the repository root was rebuilt by
   `nuget pack` and is a gitignored build artifact.

---

## 10. Anomalies

**A1 — a bare `DbContextOptionsBuilder` does not build the application's model, and nothing says
so.** `IdentityDbContext.OnModelCreating` maps `AspNetUserPasskeys` only when
`IdentityOptions.Stores.SchemaVersion` ≥ Version 3, read from `IOptions<IdentityOptions>` on the
options' *application* service provider. Seventeen places in this codebase construct
`ApplicationDbContext` with nothing but options — every interceptor suite among them — and all of them
are quietly working with a model the application never has. It does not matter for those tests, which
`EnsureCreated` from that same model, and it mattered enormously for §B, whose entire job is to
compare that model with something built elsewhere. Recorded because the failure is silent in both
directions: no error, no warning, and a diff that reads as drift in the *migrations*.

**A2 — a red-before demonstration found a control that could not have failed for the reason it
names.** `ANonHolderCannotRePermissionARoleInBulk` stayed green with the guard removed, because its
actor did not hold the second permission being granted and grant-what-you-hold refused the batch
anyway. The test asserted the right outcome for the wrong reason and would have gone on doing so
forever. Recorded because it generalises: **a refusal test is only evidence when the subject would
otherwise have been allowed**, and the only cheap way to know that is to remove the guard and watch
which tests actually move. Two of the eleven red-before failures were worth more than the assertions
themselves.

**A3 — EF's model cache is shared between contexts whose application service providers differ.** The
application service provider is not part of EF's internal-service-provider cache key, so the first
`ApplicationDbContext` built anywhere in a test assembly decides which model every later one gets.
`ModelMatchesMigrationsTests` passed in isolation and failed in a full-assembly run — another fixture
built a bare context first and its passkey-less model was cached and reused. Fixed with
`EnableServiceProviderCaching(false)`, at well under a second. Recorded because the shape is the worst
a false failure can take: green alone, red together, and the ordering is not under the author's
control.

**A4 — awaiting a MudBlazor confirmation dialog with no provider rendered hangs the test host, it
does not fail.** `DialogServiceHelper.ShowDialogAsync` awaits `dialog.Result`, which nothing ever
completes when no `MudDialogProvider` is in the tree. The first draft of the delete tests hung for
240 seconds and the runner reported *"Test host process crashed"* — no test name, no stack. Fixed by
registering an `IDialogService` whose confirmation answers yes immediately, which turned out to be
strictly better anyway: the holder's delete now really executes and the assertion is a role that is
gone. Recorded as a trap for anyone testing a page path behind a confirmation in this codebase.

**A5 — a no-principal context is not an unfiltered one, and for picklists it is nearly the
opposite.** `PicklistSet`'s filter is `TenantId == null || TenantId == CurrentTenantId`; with no
principal `CurrentTenantId` is null, so such a context sees **shared rows only**. Every readback in
`SharedPicklistCreationTests` that asserted a *private* row's tenant found nothing and threw. It needs
`IgnoreQueryFilters()`. Recorded because "read it back with no principal" is the obvious way to check
a stamped tenant and is wrong for exactly the entity this pass is about — and because a readback that
can only see the value the test hopes for is not a readback.

**A6 — Fluent Assertions' `Should().Equal(x, "reason")` treats the reason as a second expected
element.** The params overload wins over the `(IEnumerable, string)` one for a single-item
collection, so `Should().Equal(ExistingRole, "the role must not exist")` asserts a two-element
collection and fails with a message quoting the reason as data. It cost three separate debugging
rounds in this pass. `BeEquivalentTo(new[] { x }, "reason")` is unambiguous. Minor, but recorded
because the failure output is actively misleading — it names the right expectation and the wrong
count.

**A7 — Pass 32 §2.5 described the obstacle slightly wrong, and the wrong description made it look
bigger.** It said the interceptor *"writes the ambient tenant unconditionally"*. It has not since Pass
24: `SetCreationAuditInfo` stamps only when `TenantId` is null. The real obstacle is that null is both
the "unset" sentinel and the "shared" value — which is a much smaller thing to fix, and is why §C
turned out to be implementable rather than reportable. Recorded because the estimate in a prior
report is the input to the next brief's *"implement if it is small"*, and here it was one word away
from steering this pass into stopping.
