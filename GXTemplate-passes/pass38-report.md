# Pass 38 — The File Endpoint's Tenant Source

**Nature:** editing pass fixing a live cross-tenant read escape.
**Date:** 2026-09-05.

**Result in one line:** `/files` resolved its tenant from a **claim that is absent for every user who
has never switched tenant**, which dropped `VisibleDocumentSpecification`'s tenant clause and served
every public document in the installation to any `Documents.Download` holder. **Reproduced by
running it** (`served tenant-B document = True`), then fixed by resolving the tenant from the user
row through `IUserContextLoader`, failing closed on an unresolvable principal. **984 → 989 tests**,
warnings unchanged, the four Mediator consumers byte-unmodified.

**The finding of the pass is not the defect but why nothing caught it:** the existing fixture's
`Principal()` helper manufactured a `TenantId` claim, so
`ADocumentInAnotherTenant_IsRefused_EvenThoughItIsPublic` asserted the right thing about a principal
the application never produces — and went red the moment the harness was made honest.

---

## 1. Start state

| | |
|---|---|
| HEAD | `331f1f7f` — *"Pass36-PolicyScope"* |
| Working tree | **Pass 37 uncommitted** — 8 modified, 4 untracked, recorded per the brief and proceeded on. This makes two bodies of work in one tree, not three |
| Build | **0 errors, 19 warnings across 10 distinct source locations** |
| Tests | 228 + 12 + 501 + 243 = **984 passed, 12 skipped, 0 failed** |

Measured against Pass 37's report §7.5, which delivered exactly 984 + 12 skipped. No expected count
was asserted by this brief, correctly.

---

## 2. §A — The blast radius, established before fixing

### 2.1 The defect, reproduced by running it

A throwaway fixture was written against **untouched** code: two tenants, a public document in tenant
B, a user in tenant A, and a principal in the shape the application actually produces — a name
identifier and **no tenant claim**. It asserted the *defective* behaviour, so that passing meant the
defect was real:

```
DEFECT REPRODUCTION: tenant-A user served tenant-B document = True
  Passed TenantAUserWhoNeverSwitched_CanFetchTenantBsPublicDocument [2 s]
```

Pass 34 A7's lesson applied: a probe that proves the fix should first prove the defect. The
throwaway fixture was deleted once captured; its assertions live on, inverted, in
`AUserWhoHasNeverSwitchedTenant_IsStillConfinedToTheirTenant`.

**One incidental confirmation of Pass 32 A5** on the way: the first run failed with
`DbUpdateException` because the document's `CreatedById` pointed at a user row that did not exist —
the audit/identity foreign key again.

### 2.2 Every consumer of `GetTenantId()`, and the wider class

`GetTenantId()` had **exactly one caller**: the defective line. So the method had **zero correct
uses**.

Searching more widely for the *class* of defect Pass 37 A2 predicted — an HTTP-side path resolving a
tenant — found no other instance. No middleware, filter, endpoint handler or `HttpContext`-based
service reads a tenant; a grep for `HttpContext` combined with `tenant` returns nothing. The
prediction was right about the shape and, today, wrong about there being more than one.

The extension class it lives in is largely dead, which is its own finding:

| Method | Callers |
|---|---|
| `GetUserId` | 4 |
| `GetUserName` | 1 |
| **`GetTenantId`** | **1 → 0 after this pass** |
| `GetTenantName`, `GetSuperiorId`, `GetSuperiorName`, `GetProfilePictureDataUrl`, `GetStatus`, `GetAssignRoles`, `GetEmail` | **0** |

### 2.3 What else `FileEndpoints` serves

One route only — `MapGet(RoutePattern, HandleAsync)` on `/files/{**key}`. There is no thumbnail,
metadata or `HEAD` handler with visibility logic of its own. Within it there are two paths, and the
distinction is deliberate and documented: a key whose first segment is not `Documents` returns
`true` immediately (avatars, which seven render sites show for *other* users), and a document key
gets `Documents.Download` plus the specification. The defect was confined to the second.

### 2.4 What `IsPublic` means — and why the defect was as large as it was

`Document.IsPublic` is *public within the tenant*, and **the tenant clause is the only thing that
makes that true**. Drop it and the same flag silently means *public across the installation*. The
distinction is not otherwise expressible today: there is one boolean, and its scope comes entirely
from the predicate it is evaluated in.

That matters for severity, because **`UploadDocumentCommand` sets `IsPublic = true`** on every
uploaded document. So the exposed set was not a corner of the data — it was nearly all of it.

---

## 3. §B — The fix

### 3.1 Before and after

**Before:**

```csharp
await using var db = await dbContextFactory.CreateAsync(cancellationToken);
return await db.Documents
    .Where(x => x.StorageKey == key)
    .WithSpecification(new VisibleDocumentSpecification(userId, user.GetTenantId() ?? string.Empty))
    .AnyAsync(cancellationToken);
```

**After:**

```csharp
var context = await userContextLoader.LoadAsync(user, cancellationToken);

if (context is null)
{
    return false;
}

await using var db = await dbContextFactory.CreateAsync(cancellationToken);
return await db.Documents
    .Where(x => x.StorageKey == key)
    .WithSpecification(new VisibleDocumentSpecification(userId, context.TenantId ?? string.Empty))
    .AnyAsync(cancellationToken);
```

`IUserContextLoader` is threaded through `HandleAsync` (by endpoint DI) into `IsPermittedAsync`,
which stays `public static` and principal-taking so the decision remains assertable without a running
host — the property its own remarks were written for, and the reason this pass could reproduce the
defect in seconds.

Re-confirmed at the point of reliance (Pass 36 §3.3): the loader takes a `ClaimsPrincipal`, reads
`UserContext.TenantId` from the **user row**, caches per user id for an hour in FusionCache, is
registered `AddSingleton` (so injecting it anywhere is lifetime-safe), and is **already invalidated
on tenant switch** — `TenantSwitchService:118` calls `ClearUserContextCache(userId)` today.

### 3.2 The fail-closed decision, and its argument

**Chosen: fail closed. A null from the loader serves nothing.**

The argument, and the reason it is not close:

1. **The alternative is the bug.** Falling back to ownership-and-publicity is precisely the behaviour
   being repaired. A fix whose error path reinstates the defect is not a fix.
2. **It is this template's established posture** — Pass 27 §B, Pass 29's filter, and
   `AuthorizationBehaviour`'s deny-by-default all refuse when they cannot establish who is asking.
3. **It discloses nothing new.** `HandleAsync` already reports refusal and absence identically as
   `404`, deliberately, so that keys cannot be probed by comparing responses. Failing closed adds no
   signal.
4. **The recovery is fast.** `UserContextLoader` caches a genuine "no such user" for
   `NotFoundCacheDuration` — one minute — rather than the one-hour success duration, so a transient
   failure does not persist.
5. **The asymmetry of costs.** Failing closed wrongly means a document does not render for up to a
   minute. Failing open wrongly means cross-tenant disclosure.

**A distinction the fix draws deliberately.** A loader returning `null` (the principal cannot be
resolved to a user at all) is *not* the same as a resolved user whose `TenantId` is `null` (a
genuinely tenantless principal — a real, supported state). The first fails closed; the second passes
`string.Empty` and keeps `VisibleDocumentSpecification`'s documented no-tenant behaviour.
`AGenuinelyTenantlessPrincipal_KeepsTheSpecificationsDocumentedBehaviour` pins it.

### 3.3 `VisibleDocumentSpecification` is untouched, as instructed

Its conditional is sound for a principal that genuinely has no tenant, and its own remark says so.
Four consumers depend on that branch. The defect was a caller that could not read a tenant, not a
specification mishandling one — changing the specification would have fixed this call site by
breaking the contract the other four rely on. `git diff` shows the file unmodified.

---

## 4. §C — Preventing the next instance

### 4.1 Implemented: the warning where it would be reached for

`GetTenantId()` now carries remarks stating that the claim is **absent for most users and stale for
the rest**, naming the single writer, naming this defect as what it already cost, and naming the two
correct sources — `IUserContextAccessor.Current.TenantId` inside a circuit or handler,
`IUserContextLoader.LoadAsync` on an HTTP path. `GetTenantName()` inherits it by `<inheritdoc>`.

This is Pass 32 A1's lesson applied prospectively. The hazard **was already documented** — in
`HubUserContext`'s remarks, where it was written for hubs — and it did not help, because it was not
where someone reaching for a tenant would look. It is now on the method itself.

### 4.2 Recommended, not implemented: delete the dead surface

Removing `GetTenantId()` is stronger than commenting it, and this template has deleted six dead or
misleading surfaces on exactly that reasoning. It was **not done here**, for a reason that only
appeared on inspection: **it is not one dead method but eight** (§2.2). Deleting one while leaving
seven siblings equally dead would be arbitrary; deleting all eight is an unrelated cleanup, and
smuggling it into a security fix would make this pass's diff harder to review for the thing that
matters. Recommended as a small pass of its own.

### 4.3 Reported, not implemented: a structural guard

A test asserting that no source file outside `TenantSwitchService` reads the tenant claim is **not
cheaply expressible here**, and the reason is worth recording rather than the guard being faked:

- It would have to scan **source text**. Nothing in this suite does — `GxTableNamingTests` and
  `ModelMatchesMigrationsTests` both work over assemblies and reflection, because call sites are not
  reflectable.
- A source scan needs a path to the tree, and Pass 33 established that the template **renames**
  projects and namespaces on generation. A scan hard-coding paths or namespaces would pass in this
  repository and silently find nothing in a generated one — a guard that fails open.

The substitute is behavioural and stronger for this call site:
`AUserWhoHasNeverSwitchedTenant_IsStillConfinedToTheirTenant` fails if anything reintroduces a
claim-derived tenant here. It does not generalise to a future second call site, which is exactly what
§4.2's deletion would fix and why the deletion is the better follow-up.

---

## 5. §D — Verification

### 5.1 Red before, green after

The single changed line reverted to `user.GetTenantId() ?? string.Empty` and the null guard removed,
the new parameter kept so it still compiled — isolating exactly the fix:

```
Application.UnitTests   Failed: 4,  Passed: 10
```

Red: **`ADocumentInAnotherTenant_IsRefused_EvenThoughItIsPublic`** (pre-existing),
`AUserWhoHasNeverSwitchedTenant_IsStillConfinedToTheirTenant`, `AUserWhoHASSwitchedTenant_StillWorks`,
`WhenTheLoaderCannotResolveThePrincipal_NothingIsServed`.

**The pre-existing test going red is the most useful line in this report.** It was asserting the
correct behaviour all along and passing for the wrong reason; once the fixture stopped manufacturing
a claim, it became a true test of the endpoint and immediately failed against the unfixed code.

Green throughout the red run:
`AGenuinelyTenantlessPrincipal_KeepsTheSpecificationsDocumentedBehaviour`,
`AProfilePictureIsStillServedWithoutResolvingATenant`, and every ownership and permission test —
correctly, since none depends on the tenant source. Restored byte-identically, verified by `diff`.

### 5.2 Narrowed, not emptied

The control that matters most: a fix that served nothing would satisfy every negative assertion.

| Claim | Test |
|---|---|
| The owner still fetches their own **private** document | `TheOwner_MayFetchTheirOwnPrivateDocument` |
| A colleague still fetches a **public** document in the same tenant | `APublicDocument_IsReadableAcrossUsersInsideTheTenant` |
| A colleague still **cannot** fetch a private one | `AnotherUserInTheSameTenant_MayNotFetchAPrivateDocument` |
| Avatars still need only authentication | `AProfilePictureKey_NeedsOnlyAuthentication` |
| …even when the principal is unresolvable | `AProfilePictureIsStillServedWithoutResolvingATenant` |
| A tenantless principal keeps ownership-and-publicity | `AGenuinelyTenantlessPrincipal_KeepsTheSpecificationsDocumentedBehaviour` |

### 5.3 The four Mediator consumers

`VisibleDocumentSpecification` is unmodified and the four consumers are untouched.
`DocumentTenantIsolationTests` and `GetFileStreamQueryHandlerTests` are **byte-unmodified**
(`git diff --quiet`) and green: **19 passed**.

### 5.4 The fail-closed case

`WhenTheLoaderCannotResolveThePrincipal_NothingIsServed` — a loader returning null serves neither a
public document nor the caller's **own private** document, *"because we no longer know who they
are."*

### 5.5 The switched user — the regression the fix could have introduced

`AUserWhoHASSwitchedTenant_StillWorks` covers the one population for whom reading the claim was
correct. It asserts something stronger than "still works": the principal carries a `TenantId` claim
naming **tenant-2** while its user row says **tenant-1**, and the endpoint confines it to tenant-1.
**A stale or forged claim can no longer widen visibility** — the claim is not consulted at all.

### 5.6 Counts

| | Before | After | Delta |
|---|---|---|---|
| `Infrastructure.UnitTests` | 228 | 228 | — |
| `Application.IntegrationTests` | 12 | 12 | — |
| `Application.UnitTests` | 501 (+12 skipped) | **506** (+12 skipped) | **+5** |
| `Server.UI.IntegrationTests` | 243 | 243 | — |
| **Total** | **984 passed, 12 skipped** | **989 passed, 12 skipped** | **+5, 0 failed** |

The +5 is exactly the five tests added to `FileEndpointsAuthorizationTests`. **No pre-existing test
changed outcome** — nine were rewired to a claimless principal and all nine still pass, which is the
point.

**Warnings: unchanged.** `dotnet build --no-incremental` gives **19 warnings across the same 10
distinct source locations**, plus `NETSDK1206`. 0 errors.

### 5.7 Generation probe

Uninstalled before installing, per Pass 35 A5:

```
dotnet new uninstall → dotnet pack → dotnet new install → dotnet new gxblazor -n P38
  → FileEndpoints carries userContextLoader.LoadAsync; the only remaining "GetTenantId"
    is the comment recording what it used to read
  → build: 0 Error(s), 19 Warning(s)
  → dotnet test: 228 + 12 + 506 + 243 = 989 passed, 12 skipped, 0 failed
  → dotnet new uninstall; probe directory removed
```

---

## 6. File map, diffstat and edit fidelity

### 6.1 File map

**Modified (3, of this pass's own work):**

| File | |
|---|---|
| `src/Server.UI/Endpoints/FileEndpoints.cs` | the fix: `IUserContextLoader` threaded through, tenant from the user row, fail-closed guard |
| `src/Server.UI/Extensions/ClaimsPrincipalExtensions.cs` | §C.1 — the warning on `GetTenantId()`/`GetTenantName()` |
| `tests/Application.UnitTests/Endpoints/FileEndpointsAuthorizationTests.cs` | the harness made honest, plus **5 new tests** |

**New:** none. **Deleted:** none. No migration, no permission, no README change — the fix repairs a
call site rather than changing a rule, and the README's Documents row was already accurate.

The diffstat below also shows Pass 37's eight files, which are uncommitted in this tree and are not
this pass's work.

### 6.2 Diffstat (this pass only)

```
 src/Server.UI/Endpoints/FileEndpoints.cs                        | 44 +++++++++-
 src/Server.UI/Extensions/ClaimsPrincipalExtensions.cs           | 36 +++++++++
 tests/Application.UnitTests/Endpoints/FileEndpointsAuthorizationTests.cs | 160 ++++++++++++++++++--
 3 files changed, 227 insertions(+), 13 deletions(-)
```

### 6.3 Edit fidelity

- **No git actions.** Only `status`, `log` and `diff`.
- **The red-before demonstration was reverted byte-identically**, verified by `diff` against a copy
  taken beforehand.
- **One pre-existing test file was modified, necessarily**: `IsPermittedAsync` gained a parameter, and
  the fixture's `Principal()` helper was the thing hiding the defect. Nine existing assertions were
  kept verbatim; only the principal they run against changed, and all nine still pass.
- **The temporary reproduction fixture was deleted** once its output was captured; it appears in no
  count.
- `VisibleDocumentSpecification` and the four Mediator consumers were not touched.

---

## 7. Scratch probe disclosure

Three, all removed:

1. **`TempDefectReproduction.cs`**, a throwaway NUnit fixture inside `tests/` that reproduced the
   defect against untouched code (§2.1). Deleted immediately after its output was captured; it never
   appeared in a recorded count.
2. **A green-file backup** of `FileEndpoints.cs` for the red-before demonstration, in the session
   scratchpad.
3. **The generation probe** — packed nupkg, installed template, generated `P38` at `C:\gxp38`;
   template uninstalled, directory removed.

No application run and no database this pass. The nupkg in the repository root is a gitignored build
artifact.

---

## 8. What remains

| Surface | Status |
|---|---|
| **`/files` cross-tenant escape** | **closed.** Tenant from the user row, fail-closed on an unresolvable principal, and a stale or forged claim can no longer widen visibility |
| **`ChannelBasedNoWaitPublisher`'s frozen `ExecutionContext`** | **next.** `Task.Run(ProcessNotifications)` in the constructor captures the ambient context, so ~25 log sites — including all fourteen that record an email address — are labelled with the tenant ambient when the publisher was first resolved in that scope, and a tenant switch does not move it. A correctness bug, confined to `SystemLog.TenantId` today only because no notification handler writes through `ApplicationDbContext` |
| **The dead `ClaimsPrincipalExtensions` surface** | **new, recommended** (§4.2). Eight methods with zero callers, two of them now carrying warnings instead of being removed. A small pass of its own |
| SQLite/PostgreSQL shared-picklist duplicate gap | unchanged. Both treat NULLs as distinct in a unique index, so the shared partition is unprotected there; closing it portably needs a partial unique index with per-provider filter SQL |
| `Roles.*` description survey (Pass 35 §3.3) | unchanged. Eight descriptions on an installation-wide surface, all reading as tenant-scoped |
| Menu by role, page by permission | unchanged (Pass 35 §4.2). Cosmetic — every page checks its own permission |
| **Pass 37 uncommitted** | recorded at §1. Two bodies of work now sit in one tree; a third should not start before they are committed |

---

## 9. Anomalies

**A1 — the fixture manufactured a claim no real principal carries, and that is why nine tests passed
over a live escape.** `Principal(userId, tenantId)` added a `TenantId` claim to every principal it
built. Production adds one only after a tenant switch — Pass 36 measured `AspNetUserClaims` as empty
in a freshly seeded installation. So `ADocumentInAnotherTenant_IsRefused_EvenThoughItIsPublic`
asserted exactly the right property against a population that, in the default state of every
installation, is **empty**. The test was not wrong; its *input* was unreachable. Recorded because the
generalisation is sharp and this programme keeps meeting it in new forms: **a fixture that constructs
a principal the application cannot produce is testing a system that does not exist.** The
corresponding question — "could the real application ever produce this input?" — is worth asking of
every hand-built `ClaimsPrincipal`, `UserContext` and DTO in the suite.

**A2 — the hazard was already documented, in the wrong file.** `HubUserContext`'s remarks say, in
terms, that the tenant claim "is absent or stale for most users" and that keying off it "would be
silently wrong". That was written for hubs, and the endpoint made the identical mistake afterwards.
The warning existed and did not work because it was not on the thing being reached for. Pass 32 A1
said a comment addressed to a future pass has no failure mode; this refines it — **a warning placed
away from the API it warns about has no readership either.** §C.1 moves it onto `GetTenantId()`
itself, and §C.2 recommends the stronger fix of deleting the method.

**A3 — `IsPublic` carries no scope of its own, and the predicate supplies all of it.** A document
"public within its tenant" and one "public across the installation" are the same row with the same
flag; only the expression evaluating it distinguishes them. That is why a dropped clause changed the
meaning of every uploaded document at once rather than exposing a corner case, and why
`UploadDocumentCommand` defaulting `IsPublic = true` made the blast radius nearly total. Recorded
because the flag reads as self-describing and is not: anything that evaluates `IsPublic` without a
tenant term is asserting installation-wide publicity, whether or not its author meant to.

**A4 — the predicted class had exactly one member.** Pass 37 A2 predicted that any HTTP-side path
needing a tenant would face this choice and that nothing steers it. The prediction's reasoning was
right and its scope was one: a survey found no middleware, filter or `HttpContext`-based service
resolving a tenant, and `GetTenantId()` had a single caller. Recorded as a correction to that
anomaly — the class is real but currently unpopulated, which is why §C.2's deletion is the durable
answer and §C.1's comment is only the immediate one.
