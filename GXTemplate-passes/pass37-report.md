# Pass 37 — Name the Installation-Policy Write

**Nature:** small editing pass. One permission, one handler guard, one screen, three corrections,
one README section.
**Date:** 2026-09-05.

**Result in one line:** saving the idle policy now requires
`Permissions.SecuritySettings.ManageInstallationPolicy`, guarded in the handler and reflected in a
screen that goes **read-only** rather than refusing on save; the Tenancy table has **no row answered
"by design"**. **964 → 984 tests**, warnings unchanged, boundary suites byte-unmodified. One finding
outside the brief and more serious than anything in it: `FileEndpoints` reads the tenant from a claim
that is absent for every user who has never switched tenant, and silently drops the tenant clause —
A1.

---

## 1. Start state

**Pass 36 was uncommitted, as Pass 35 had been.** HEAD was `ed32eda9` *"Pass35-LogScope"* and
`pass36-report.md` was untracked; Pass 36 changed no code, so the report was the whole of it. You
settled this identical conflict one pass ago in favour of Pass 32 §1's standing convention, so it was
applied rather than re-asked: committed as **`331f1f7f` — "Pass36-PolicyScope"**, and this brief's
*"No git actions"* read as governing Pass 37's own work. No git command after that but `status`,
`log` and `diff`.

**No expected count was asserted by this brief, correctly.** Measured against Pass 35's report §5.4:

| | Pass 35 delivered | Found here |
|---|---|---|
| `Infrastructure.UnitTests` | 228 | 228 ✓ |
| `Application.IntegrationTests` | 12 | 12 ✓ |
| `Application.UnitTests` | 489 (+12 skipped) | 489 (+12 skipped) ✓ |
| `Server.UI.IntegrationTests` | 235 | 235 ✓ |
| **Total** | **964 + 12 skipped** | **964 + 12 skipped** ✓ |
| Build | — | **0 errors, 19 warnings across 10 distinct source locations** |

---

## 2. §A — The right

### 2.1 The constant

**`Permissions.SecuritySettings.ManageInstallationPolicy`** — Pass 36 §4.2's proposal, kept. The
alternatives were considered and rejected: `ManageShared` names a *partition* and there is none here
(every row is shared), and `ManageDefinitions` names an act of definition, which this is not. What
distinguishes this right is **scope**, and the name says so.

The shape is Pass 33's: a second right beside the feature right, not a replacement for it.
`SecuritySettings.Edit` still says "this account administers the security policy at all";
`ManageInstallationPolicy` says "and may set one that binds every tenant". The write needs both — a
comment on `Edit` now says so, so the pair is discoverable from either end.

### 2.2 The description

```
Allows changing the session policy for EVERY tenant - it is installation-wide
```

76 characters, in the register of its neighbours (`PicklistSets.ManageShared` is 81,
`Roles.ManageDefinitions` 85). This is the string the role editor renders under the permission's name
at the moment an administrator decides to grant it — the Pass 35 §B lesson — and
`TheDescriptionStatesThatItSpansEveryTenant` asserts it for meaning rather than for an exact
sentence.

The module's class-level description already read *"Set permissions for the installation's security
policy"*, so the group header was already honest; it is the per-right string that was not.

### 2.3 The access-rights property and the grant

`SecuritySettingsAccessRights.ManageInstallationPolicy`, spelled identically —
`PermissionService` builds the claim from the property name, and
`TheAccessRightsPropertyIsSpelledLikeTheConstant` pins the two together.

Granted in `AdministratorPermissionRegistry`, which the divergence assertion makes a deliberate act,
with the reason recorded in place: it **preserves the posture that already held** (before this pass
any `SecuritySettings.Edit` holder set every tenant's window), and it keeps the single-tenant
deployment working.

### 2.4 Default-granted, re-confirmed at the point of reliance

`ApplicationDbContextInitializer.EnsureAdministratorAsync:237` reads
`var tenant = await _context.Tenants.FirstAsync();` and assigns `TenantId = tenant.Id`. **The sole
administrator of a single-tenant installation is itself tenant-scoped.** A right defaulting to
ungranted — or any rule of the form "a tenant-scoped principal may not edit the installation policy" —
would leave that installation permanently unable to change its own idle timeout: screen present,
values shown, save refused, forever. The trap is a blanket prohibition, not a default-granted right.
`TheAdministratorHoldsTheRightByDefault` is the test.

---

## 3. §B — The guard

### 3.1 Where, and the refusal shape

`InstallationPolicyWrite` — a small Application-layer rule type carrying `Refused` and
`IsAllowedAsync`, consumed by the handler and (for its message) available to the screen. Much smaller
than `SharedPicklistWrite`, and the file says why: **there is no partition to test.** The picklist
rule asks "is this row shared?" and short-circuits the permission query when it is not; here every
row is shared, so there is no short-circuit to get wrong.

The guard is in `UpdateSecurityPolicyCommandHandler`, not only on the screen — the command goes
through Mediator and is reachable by any caller whatever the page renders, the same reasoning Pass 32
§2.4 applied to picklists.

**The refusal shape was established rather than invented.** For a forbidden write behind Mediator
this codebase returns a `Result` failure carrying a stated reason — `AddEditPicklistSetCommandHandler`
does exactly that with `SharedPicklistWrite.Refused`. (`RoleDefinitionWrite` throws instead, because
its callers are pages and a service with no `Result` to return; this handler has one.) So:

```csharp
if (!await InstallationPolicyWrite.IsAllowedAsync(
        _permissionQueryService, _userContextAccessor.Current?.UserId))
{
    return await Result<int>.FailureAsync(InstallationPolicyWrite.Refused);
}
```

The message names the **scope**, not the screen, because the surprising part is not that a save was
refused but that the value reaches every tenant:

> The session policy is installation-wide: it applies to every tenant at once. Changing it requires
> the 'manage installation policy' permission.

### 3.2 Placed before the read, deliberately

The guard sits above `_dbContextFactory.CreateAsync`, ahead of the handler's fresh-database branch
that creates the row when none exists. A guard placed after it would refuse the save **and still
leave a policy row behind** — a write performed by a refusal.
`ARefusedSaveDoesNotSeedARowOnAFreshDatabase` asserts the empty table stays empty.

`SecuritySettings.View` is untouched. Only the write narrows.

---

## 4. §C — The screen, and what a non-holder sees

**Read-only values plus an explanation, and no Save button.** Not a hidden screen, and not an
editable form that refuses.

The precedent is this template's own, and it is unusually explicit in this very feature: when the
idle timeout is switched off the whole page **404s** through `SecuritySettingsPageMiddleware` and the
profile's Security tab is **omitted** — the README's own words are *"Absent, never disabled: an empty
tab invites a support call asking what belongs in it."* A control that produces a refusal on Save is
the one shape ruled out.

Hiding the page outright was wrong here for a different reason: the non-holder still holds
`SecuritySettings.View`, and the values are what an administrator needs to answer *"why was I signed
out?"* — a question that does not depend on who may change the answer.

So a non-holder sees: the effective-value banner, both fields **read-only**, the live-sessions
warning, no Save, and

> This policy is installation-wide: it applies to every organisation at once. Changing it needs the
> 'manage installation policy' permission, which this account does not hold. The values below are
> shown read-only.

**One structural change came with it.** The Save button was wrapped in
`<AuthorizeView Policy="SecuritySettings.Edit">`. Two rights now govern the one affordance, and the
*fields* have to react to the same answer, so both are expressed through
`SecuritySettingsAccessRights` — the idiom the picklist, roles and logs pages already use — behind a
single `MayEdit => _accessRights.Edit && _accessRights.ManageInstallationPolicy`. Nesting a second
`AuthorizeView` would have put one decision in two shapes and left the fields unable to see it.

---

## 5. §D — Three corrections

### 5.1 `SecurityPolicy`'s estimate

**Before:**

> **One row.** The provider reads the first row and seeds one from configuration when the table is
> empty, so a fresh database needs no seeding step of its own. Adding a tenant column later is a
> migration plus a cache key, not a redesign — which is why the reader goes through
> `IIdleTimeoutPolicyProvider` rather than querying the table at its call sites.

**After** (abridged to the two new paragraphs; the first sentence is kept and extended):

> **The reason it is installation-wide is the authentication cookie, not inertia.**
> `IdleTimeoutSettings.CookieLifetime` derives from `MaxIdleTimeoutMinutes`, and the cookie is issued
> at sign-in — before any tenant is known — and cannot be shortened afterwards. So the outer bound is
> irreducibly installation-wide, and per-tenant rows could only ever let a customer pick a point
> inside a band the operator has already fixed in configuration. Writing the row requires
> `Permissions.SecuritySettings.ManageInstallationPolicy` since Pass 37, so the cross-tenant reach is
> named and revocable rather than tacit.
>
> **This remark used to say adding a tenant column later was "a migration plus a cache key, not a
> redesign". That was measured in Pass 36 and it was optimistic**, by enough to have shaped a brief.
> The real cost is a migration — which would be this template's FIRST second migration, on all three
> provider chains, now enforced together by `ModelMatchesMigrationsTests` — plus a signature change
> across roughly 22 call sites of the provider's two read methods, a tag-based cache flush in place
> of a single key removal, a second permission, a UI concept the settings screen does not have ("you
> are overriding the installation default"), and a cached database dependency inside cookie
> validation. Not a redesign; not small either.

### 5.2 `IdleTimeoutPolicyProvider`'s estimate, and the condition

The `CacheKey` remark lost *"A multi-tenant deployment keys this by tenant and changes nothing else"*
and gained three paragraphs: the tag-flush correction, **the condition under which per-tenant policy
becomes right** — *"a customer requires an idle window materially different from another customer's
inside the operator's configured band, and says so"* — and §5.3's trap.

### 5.3 Pass 36 A2's trap, written where it would be tried

> **And if it is ever built: query by an explicitly passed tenant, NEVER by ambient query filter.**
> The filter is this template's house pattern for tenancy (Pass 29 for audit trails, Pass 31 for
> picklists) and it would fail here, silently, in the only path that matters. `IdleSessionEnforcer`
> runs inside the cookie handler's principal validation, where `IUserContextAccessor.Current` is
> **null** — the sole place that pushes it is a SignalR hub-method filter. A picklist-shaped filter
> (`TenantId == null || TenantId == current`) would therefore return only the INSTALLATION row to the
> enforcer, for every user of every tenant, while returning the correct row to every screen:
> enforcement and display would disagree and nothing would fail. The tenant must be resolved
> explicitly — Pass 36 §3.3 found `IUserContextLoader` already does it from a `ClaimsPrincipal`,
> cached and already invalidated on tenant switch.

### 5.4 `RefreshUserClaimsAsync` — **reported and left, with the log made honest**

**The brief's escape clause applies: propagating changes the switch's failure semantics.** By the
time `RefreshUserClaimsAsync` runs, `userManager.UpdateAsync(user)` has **already persisted the new
tenant**. Letting the exception out would be caught by `SwitchToTenantAsync`'s own handler and turned
into `Result.Failure("Failed to switch tenant")` — reporting failure for a switch that succeeded, and
inviting a retry of something already done. That is worse than stale claims. Making it propagate
correctly would require the switch to become transactional across the user row and the claim rows,
which is its own decision and its own pass.

So the catch stays, and two things changed instead. The method gained remarks stating why it swallows
and what it costs when it fires; and the log line now names the consequence:

```
Failed to refresh tenant claims for user {UserId} after switching to tenant {TenantId}.
The switch itself succeeded and was reported as success; only the persisted TenantId/TenantName
claims are stale. They are rewritten by the next successful switch.
```

An operator reading the old line (*"Failed to refresh claims for user X"*) could reasonably have
tried to undo something. **This correction became more important mid-pass**, because the claim turned
out to have one consumer that matters — see A1.

---

## 6. §E — The README, quoted in full

### 6.1 The Tenancy row

**Before:**

> | Security settings (idle policy) | No — one row per installation, by design |

**After:**

> | Security settings (idle policy) | **No, and deliberately not — the authentication cookie makes it installation-wide.** `CookieLifetime` derives from `MaxIdleTimeoutMinutes` and the cookie is issued at sign-in, before any tenant is known, so the outer bound cannot be per-tenant. One row, in force everywhere; **changing it needs `SecuritySettings.ManageInstallationPolicy`**, granted to the administrator by default. `SecuritySettings.View` is unaffected, and the per-user preference is untouched — it is per-user and tighten-only |

**The Tenancy table now has no row answered "by design".** Checked by grep over the table's line
range: no match.

### 6.2 The idle-policy section

**Before** (one paragraph):

> **The policy is installation-wide, not per-tenant.** `SecurityPolicies` holds a single row and the
> cache key is a constant, so every tenant in a multi-tenant deployment shares one idle window. This
> is a deliberate starting point rather than an oversight — every reader goes through
> `IIdleTimeoutPolicyProvider` precisely so that adding a tenant column and keying the cache by
> tenant is a migration plus one cache key, not a redesign — but today one tenant's administrator
> sets the policy for all of them.

**After** (four paragraphs, verbatim):

> **The policy is installation-wide, not per-tenant, and the reason is the authentication cookie.**
> `SecurityPolicies` holds a single row and the cache key is a constant, so every tenant in a
> multi-tenant deployment shares one idle window. That is a decision, not a stage on the way to
> something else: `IdleTimeoutSettings.CookieLifetime` derives from `MaxIdleTimeoutMinutes`, and the
> cookie is issued once at sign-in — **before any tenant is known** — and cannot be shortened
> afterwards. The outer bound is therefore irreducibly installation-wide, and per-tenant rows could
> only ever let a customer pick a point inside a band the operator has already fixed in
> configuration.
>
> **One administrator does set the window for every tenant, and since Pass 37 that is a named,
> revocable capability rather than a side effect.** Saving the policy requires
> **`Permissions.SecuritySettings.ManageInstallationPolicy`** in addition to `SecuritySettings.Edit` —
> `Edit` says an account administers the security policy at all, the new right says it may set one
> that binds every tenant. It is **granted to the administrator by default**, because
> `EnsureAdministratorAsync` assigns the bootstrap administrator `Tenants.First()`: the sole
> administrator of a single-tenant installation is itself tenant-scoped, so a right that defaulted to
> ungranted would leave that installation permanently unable to change its own idle timeout.
> **Revoke it** in a multi-tenant installation where one customer's administrator should not set
> every customer's session policy; they keep `SecuritySettings.View`, and the screen then shows the
> values read-only with a line saying why.
>
> **Reading is untouched, and so is the per-user preference.** `SecuritySettings.View` shows the
> policy to anyone who could see it before — an administrator needs to know the window to answer "why
> was I signed out?", whoever may change it — and a user's own tighten-only preference is per-user
> and unaffected by any of this.
>
> **What would change the decision**, so it can be recognised rather than re-derived: a customer
> requiring an idle window materially different from another customer's *inside the operator's
> configured band*, and saying so. The costs are catalogued on `SecurityPolicy` and
> `IdleTimeoutPolicyProvider` — and note in particular that per-tenant policy must resolve the tenant
> explicitly, never through a global query filter, because the enforcer runs in the cookie pipeline
> where the ambient tenant is null and a filter would silently serve it the installation row for
> every user of every tenant.

*"A deliberate starting point"* is gone; the sentence about one administrator setting the policy for
all of them stays, but now as a named capability with a stated reason rather than as an admission.

---

## 7. §F — Verification

### 7.1 Through the handler

**`InstallationPolicyWriteTests`** — 12 tests, Application.UnitTests. Every assertion sends a real
`UpdateSecurityPolicyCommand` and reads the row back, because a guard proved only at the rule would
prove the rule and not its enforcement. Pass 32 A5's trap is respected: `SecurityPolicy` is
`IAuditable`, so the fixture registers the real `AuditableEntityInterceptor` **and** creates real user
rows for the audit foreign key — without them every refusal would pass and every success would fail
on the constraint.

| Claim | Test |
|---|---|
| A non-holder cannot save — and the stored row is re-read | `ANonHolderCannotSaveThePolicy` |
| A holder can, and the row really changes | `AHolderCanSaveThePolicy` |
| A refused save writes nothing, even on a fresh database | `ARefusedSaveDoesNotSeedARowOnAFreshDatabase` |
| A refused save invalidates no cache | `ARefusedSaveDoesNotInvalidateTheCache` |
| An accepted save does | `AnAcceptedSaveDoesInvalidateTheCache` |
| **A non-holder can still READ the policy** | `ANonHolderCanStillREADThePolicy` |
| The rule fails closed | `TheRuleFailsClosedWithNoPrincipal` |
| Holder vs non-holder at the rule | `ANonHolderIsRefusedEvenWithAPrincipal` |
| The refusal names the scope | `TheRefusalNamesTheScopeRatherThanTheScreen` |
| Granted by default; not excluded | `TheAdministratorHoldsTheRightByDefault` |
| Property spelled like the constant | `TheAccessRightsPropertyIsSpelledLikeTheConstant` |
| The description states the scope | `TheDescriptionStatesThatItSpansEveryTenant` |

The two cache tests use a counting `IIdleTimeoutPolicyProvider`, so "a refusal costs nothing" is
observable rather than assumed — the cached policy is read on every authenticated request, so a
gratuitous invalidation is not free.

### 7.2 The screen, circuit-level

**`SecuritySettingsPermissionComponentTests`** — 8 tests, Server.UI.IntegrationTests. The application
renders at `InteractiveServerRenderMode(prerender: false)`, so an HTTP response carries the shell and
none of this.

| Claim | Test |
|---|---|
| No Save button without the right | `WithoutTheRight_ThereIsNoSaveButton` |
| There is one with it | `WithTheRight_TheSaveButtonIsOffered` |
| Both fields carry `readonly` | `WithoutTheRight_TheFieldsAreReadOnly` |
| Neither does with the right | `WithTheRight_TheFieldsAreEditable` |
| The screen says why, and names the scope | `WithoutTheRight_TheScreenSaysWhy` |
| No such notice for a holder | `WithTheRight_NoSuchNoticeIsShown` |
| **The values are still shown** | `WithoutTheRight_ThePolicyIsStillShown` |
| The two rights are distinct — the scope right alone opens nothing | `HoldingTheScopeRightWithoutEditStillOffersNothing` |

### 7.3 Red before, green after

Handler guard removed and `MayEdit` reduced to `_accessRights.Edit`:

```
Application.UnitTests       Failed: 3,  Passed: 9
Server.UI.IntegrationTests  Failed: 3,  Passed: 5
```

Red: `ANonHolderCannotSaveThePolicy`, `ARefusedSaveDoesNotSeedARowOnAFreshDatabase`,
`ARefusedSaveDoesNotInvalidateTheCache`, `WithoutTheRight_ThereIsNoSaveButton`,
`WithoutTheRight_TheFieldsAreReadOnly`, `WithoutTheRight_TheScreenSaysWhy`.

Green throughout: every "still can" control — `AHolderCanSaveThePolicy`,
`ANonHolderCanStillREADThePolicy`, `WithoutTheRight_ThePolicyIsStillShown`, and both
`WithTheRight_*` — which is the point of having them. Restored byte-identically from copies taken
beforehand, verified by `diff`.

### 7.4 The registry

`AdministratorPermissionRegistryTests` passes unchanged with the new constant granted — the
divergence assertion accepted it because the grant was added deliberately, which is the mechanism
working as designed.

### 7.5 Counts

| | Before | After | Delta |
|---|---|---|---|
| `Infrastructure.UnitTests` | 228 | 228 | — |
| `Application.IntegrationTests` | 12 | 12 | — |
| `Application.UnitTests` | 489 (+12 skipped) | **501** (+12 skipped) | **+12** |
| `Server.UI.IntegrationTests` | 235 | **243** | **+8** |
| **Total** | **964 passed, 12 skipped** | **984 passed, 12 skipped** | **+20, 0 failed** |

The +20 is exactly the two new files. No pre-existing test changed count or outcome.

**Warnings: unchanged.** `dotnet build --no-incremental` gives **19 warnings across the same 10
distinct source locations**, plus `NETSDK1206` which has no source location. 0 errors.

### 7.6 Boundary suites

`git diff --quiet` per file — **all 30 unmodified**, covering Passes 26–35's scope, isolation,
filter, presence, guard, role-definition, migration and log suites, plus `IdleTimeoutPolicyTests` and
`IdleTimeoutWiringTests`, which are the two this pass reasons hardest about. Run as a filtered set:
**252 passed, 0 failed** (52 + 3 + 114 + 83). `git status tests/` shows two additions and no
modifications.

### 7.7 Generation probe

**Uninstalled before installing, per Pass 35 A5** — that anomaly cost a diagnosis last pass when
`--force` left two registrations and generation failed with *"Sequence contains more than one
matching element"*.

```
dotnet new uninstall (twice, to zero) → dotnet pack → dotnet new install → dotnet new gxblazor -n P37
  → README carries ManageInstallationPolicy; no "by design" anywhere
  → LogsPermissions-style constant and AccessRights property present
  → build: 0 Error(s), 19 Warning(s)
  → dotnet test: 228 + 12 + 501 + 243 = 984 passed, 12 skipped, 0 failed
  → dotnet new uninstall; probe directory removed
```

---

## 8. File map, diffstat and edit fidelity

### 8.1 File map

**New (3):**

| File | |
|---|---|
| `src/Application/Features/SecuritySettings/InstallationPolicyWrite.cs` | the rule: `Refused`, `IsAllowedAsync` |
| `tests/Application.UnitTests/Features/SecuritySettings/InstallationPolicyWriteTests.cs` | **12 tests**, through the real handler with the real interceptor |
| `tests/Server.UI.IntegrationTests/SecuritySettingsPermissionComponentTests.cs` | **8 tests**, circuit-level |

**Modified (8):**

| File | |
|---|---|
| `src/Application/Features/SecuritySettings/Security/SecuritySettingsPermissions.cs` | the constant, its description and remarks, the `AccessRights` property |
| `src/Application/Common/Security/AdministratorPermissionRegistry.cs` | granted, with the single-tenant reason |
| `src/Application/Features/SecuritySettings/Commands/UpdateSecurityPolicyCommand.cs` | the guard, before the read |
| `src/Server.UI/Pages/SystemManagement/SecuritySettings.razor` | read-only fields, the explanation, `MayEdit` |
| `src/Domain/Entities/SecurityPolicy.cs` | §D.1 — the corrected estimate and the cookie reason |
| `src/Infrastructure/Services/Security/IdleTimeoutPolicyProvider.cs` | §D.2 and §D.3 — the correction, the condition, the filter trap |
| `src/Infrastructure/Services/TenantSwitchService.cs` | §D.4 — remarks and the honest log line |
| `README.md` | §E |

**No migration**, because nothing about the data model changed — which is the whole point of the
option taken.

### 8.2 Diffstat

```
 README.md                                                        | 40 ++++++++++---
 src/Application/Common/Security/AdministratorPermissionRegistry.cs| 13 +++++
 .../SecuritySettings/Commands/UpdateSecurityPolicyCommand.cs     | 25 +++++++-
 .../SecuritySettings/Security/SecuritySettingsPermissions.cs     | 41 +++++++++++++
 src/Domain/Entities/SecurityPolicy.cs                            | 27 ++++++--
 .../Services/Security/IdleTimeoutPolicyProvider.cs               | 35 +++++++++--
 src/Infrastructure/Services/TenantSwitchService.cs               | 35 ++++++++++-
 src/Server.UI/Pages/SystemManagement/SecuritySettings.razor      | 58 ++++++++++++------
 8 files changed, 243 insertions(+), 31 deletions(-)
```

Plus three new files.

### 8.3 Edit fidelity

- **One git action, on the standing authorisation**: the `Pass36-PolicyScope` commit of the preceding
  pass's report. Nothing of this pass's own work was staged, committed, stashed or reset.
- **The red-before demonstration was reverted byte-identically**, verified by `diff` against copies
  taken beforehand.
- **No existing test file was touched.** The +20 is entirely in two new files.
- The generation probe uninstalled before installing and removed itself.

---

## 9. Scratch probe disclosure

Two, both removed: green-file backups of the two guarded files for the red-before demonstration, in
the session scratchpad; and the generation probe (packed nupkg, installed template, generated `P37`
at `C:\gxp37` — template uninstalled, directory removed). The nupkg in the repository root is a
gitignored build artifact rebuilt by `dotnet pack`. No application run and no database this pass.

---

## 10. What remains

| Surface | Status |
|---|---|
| **The Tenancy table** | **no row is answered "by design".** Every row now carries a decision and a reason |
| **`FileEndpoints`' tenant source** | **NEW, and the most serious thing found this pass — A1.** A cross-tenant read escape on the file-streaming endpoint, live in the default state of every installation. Recommended as the next pass, ahead of the publisher |
| `ChannelBasedNoWaitPublisher`'s frozen `ExecutionContext` | unchanged (Pass 36 A2 / Pass 34 A2). A correctness bug: ~25 log sites, including all fourteen that record an email address, are labelled with the tenant ambient when the publisher was first resolved in that scope, and a tenant switch does not move it |
| `RefreshUserClaimsAsync`'s swallowed failure | **reported and left** (§5.4), because propagating would misreport a committed switch. Needs the switch to become transactional across the user row and the claim rows |
| SQLite/PostgreSQL shared-picklist duplicate gap | unchanged. Both treat NULLs as distinct in a unique index, so the shared partition is unprotected there; closing it portably needs a partial unique index with per-provider filter SQL. `TheSharedPartitionIsNotProtectedFromDuplicatesOnThisProvider` fails if it is ever closed |
| `Roles.*` description survey (Pass 35 §3.3) | unchanged. Eight descriptions on an installation-wide surface, all reading as tenant-scoped. `SecuritySettings.Edit` was the other one flagged, and this pass addressed the group it belongs to |
| Menu by role, page by permission | unchanged (Pass 35 §4.2). Affects all eleven `MANAGEMENT` entries; cosmetic, since every page checks its own permission |

---

## 11. Anomalies

**A1 — `FileEndpoints` reads the tenant from a claim that is absent for almost everyone, and silently
drops the tenant clause. This is a live cross-tenant read escape and it is outside this brief.**

`VisibleDocumentSpecification.IsVisibleTo` is conditional on the caller *having* a tenant:

```csharp
string.IsNullOrEmpty(tenantId)
    ? p => (p.CreatedById == userId && p.IsPublic == false) || p.IsPublic == true
    : p => ((p.CreatedById == userId && p.IsPublic == false) || p.IsPublic == true)
           && p.TenantId == tenantId;
```

Four of its five consumers pass `currentUser.TenantId` from the **ambient `UserContext`**, which is
correct and populated because they run in circuits through Mediator —
`AddEditDocumentCommand`, `DeleteDocumentCommand`, `GetFileStreamQuery`,
`AdvancedDocumentsSpecification`. The fifth is the odd one out precisely because it is the only HTTP
path:

```csharp
// FileEndpoints.cs:116
.WithSpecification(new VisibleDocumentSpecification(userId, user.GetTenantId() ?? string.Empty))
```

`GetTenantId()` reads the `TenantId` **claim** — and Pass 36 measured `AspNetUserClaims` as **empty**
in a freshly seeded installation while the administrator carried a real tenant on their user row. The
only writer of that claim is `TenantSwitchService.RefreshUserClaimsAsync`, reachable only from
`SwitchToTenantAsync`. So for **every user who has never switched tenant** the claim is absent,
`?? string.Empty` selects the no-tenant branch, and the endpoint returns *every public document in
the installation* to any holder of `Documents.Download`.

The specification's own remark says the conditional is "deliberately not changed here… a principal
with no tenant is confined by ownership and publicity alone" — which is sound for a genuinely
tenantless principal and wrong for one whose tenant merely could not be read. The fix has the shape
Pass 36 §3.3 established for the idle enforcer: resolve the tenant through `IUserContextLoader`,
which takes a `ClaimsPrincipal`, reads the user row, is cached for an hour and is already invalidated
on tenant switch. Not repaired here — it needs its own pass, its own tests and a decision about
fail-closed behaviour when the loader returns null.

**A2 — the same root cause produced both this pass's subject and A1.** The ambient `UserContext` is
populated only inside a SignalR hub invocation, so every HTTP-side path needs a different tenant
source; the two that exist chose differently, and the one that chose the claim was wrong. Recorded
because it predicts where the next instance will be: any HTTP endpoint or middleware that needs a
tenant. A grep for `GetTenantId()` finds exactly one caller today, which is why this was a single
defect rather than a class — but nothing stops the next one.

**A3 — `MudNumericField` does not render `type="number"`.** The rendering tests first selected
`input[type=number]` and found zero elements; the control renders a plain `input`. Trivial, but
recorded because the failure message ("Expected inputs to contain 2 item(s) … but found 0") looks
like the fields are missing rather than like the selector being wrong, and a future test asserting
numeric-field state will hit it again.

**A4 — the preceding pass was uncommitted for the second time running.** Pass 35's brief and Pass
36's both asserted a clean tree with the previous pass committed, and neither was true. The standing
convention resolved it both times, but the pattern now costs a decision at the top of every pass.
Worth settling explicitly: either briefs stop asserting the precondition, or the commit becomes the
last step of each pass rather than the first step of the next.
