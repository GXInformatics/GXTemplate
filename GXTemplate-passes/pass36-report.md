# Pass 36 — Security Settings: The Last Row Answered "By Design"

**Nature:** investigation with a design gate. **No production code changed.**
**Date:** 2026-09-05.

**Recommendation in one line: name the right, do not build per-tenant rows — yet.** Add
`Permissions.SecuritySettings.ManageInstallationPolicy`, granted to the administrator by default,
gating the edit of the installation's policy. That closes the last unnamed cross-tenant write in the
template for the cost of one constant and one grant, and — the part that makes it a decision rather
than a deferral — **it is the exact right option 1 would need anyway**, so nothing is thrown away if
per-tenant policy is ever built. Per-tenant rows are not recommended now, and §C.4 states the
condition under which that changes.

**§B's obstacle turned out to be cheaper than the brief feared, and one estimate in the codebase is
wrong in the other direction.** `IUserContextLoader` already resolves a tenant from a
`ClaimsPrincipal` on an HTTP request, cached for an hour and already invalidated on tenant switch —
so the enforcer *can* get a tenant. But `SecurityPolicy`'s own comment, *"Adding a tenant column
later is a migration plus a cache key, not a redesign"*, understates it: §C.1 counts a migration, a
signature change across 22 call sites, a tag-based cache flush, a second permission, a UI concept
that does not exist, and the first second-migration this template has ever shipped.

---

## 1. Start state

**The precondition failed and this pass stopped to ask.** HEAD was `3f2f3034` *"pass34"* and the tree
carried **Pass 35's work, uncommitted** — green at 964 tests, exactly its delivered figure, against
the brief's expected 950 (a number no state of this repository has ever had). This brief says *"No
git actions"*; Pass 32 §1's standing convention says each brief authorises committing the preceding
pass first. You settled it: commit Pass 35, then investigate.

Committed as **`ed32eda9` — "Pass35-LogScope"**, distinct from `pass34`. Its own report was included,
since it was already written.

| | After the authorised commit |
|---|---|
| HEAD | `ed32eda9` — *"Pass35-LogScope"* |
| Working tree | clean |
| Build | **0 errors, 19 warnings across 10 distinct source locations** |
| Tests | 228 + 12 + 489 + 235 = **964 passed, 12 skipped, 0 failed** |

The tree is clean at `ed32eda9` at the end of this pass; the only git commands after the commit were
`status`, `log` and `diff`.

---

## 2. §A — The three-level model as it actually is

### 2.1 The three levels

| Level | Stored | Set by | Bound |
|---|---|---|---|
| **Configured bounds** | `appsettings.json`, `SecuritySettings:IdleTimeout`, bound to `IdleTimeoutSettings` | the deployment (a file, not a screen) | `Enabled`, `MinIdleTimeoutMinutes` (1), `MaxIdleTimeoutMinutes` (120, ceiling `AbsoluteMaxIdleTimeoutMinutes` = 480), `Default*`, `AllowUserOverride`, `CookieGraceMinutes` |
| **Administered policy** | `SecurityPolicies`, **one row**, `IdleTimeoutMinutes` + `CountdownSeconds` | `Permissions.SecuritySettings.Edit`, via `/system/security-settings` | clamped into the configured bounds |
| **User preference** | `ApplicationUser.IdleTimeoutMinutes`, nullable | the user, on their profile's Security tab, only when `AllowUserOverride` | **tighten-only** |

**The resolution formula**, from `IdleTimeoutPolicyProvider.GetEffectiveAsync`:

```
effective.IdleMinutes    = Clamp( min(preference ?? administered, administered), Min, Max )
effective.CountdownSeconds = administered.CountdownSeconds        // never user-adjustable
TotalWindow              = IdleMinutes + CountdownSeconds
```

Two details that matter later. The administered value is **clamped on the way out as well as in**, so
a row written before the bounds were tightened is still held to them. And the countdown is
deliberately not user-adjustable — it is how long the warning shows, not how long a session may sit
idle.

### 2.2 `IIdleTimeoutPolicyProvider` — what it reads, caches and invalidates

| | Administered | Per-user preference |
|---|---|---|
| Source | `db.SecurityPolicies.OrderBy(Id).FirstOrDefault()` | `db.Users.Where(Id == userId).Select(u => u.IdleTimeoutMinutes ?? 0)` |
| Cache key | `"security-policy:idle-timeout"` — **a constant** | `"security-policy:idle-timeout:user:{userId}"` |
| Duration | 12 hours, explicitly a backstop | same |
| Invalidated by | `Invalidate()` ← `UpdateSecurityPolicyCommandHandler` | `InvalidateUser(id)` ← `SecurityTab.PersistAsync` |
| Context | `IDbContextFactory<ApplicationDbContext>` (raw EF factory) | same |

**Pass 31 A1's lesson applies in advance, and here the declaration is a comment rather than a
`CacheScope`.** The provider says:

> One key, because there is one row. A multi-tenant deployment keys this by tenant and changes
> nothing else — which is why every reader goes through this type rather than querying the table.

That is a claim about the query's inputs, and adding a tenant dimension falsifies it. It is also the
estimate this pass was asked to test; see §C.1, where it comes out low.

**One divergence worth naming now:** the provider reads through the *raw* `IDbContextFactory`, while
`UpdateSecurityPolicyCommandHandler` reads through `IApplicationDbContextFactory`. Both resolve an
optional `IUserContextAccessor` by DI, so both would honour a global query filter — but see A2 for
why a filter would be the wrong mechanism here regardless.

### 2.3 The lazy seed — measured, not read

`LoadAdministeredAsync` writes the row on first read. A probe run of the real application shows
exactly when that happens:

```
SecurityPolicies:  Id=1  Idle=15  Countdown=60  CreatedAt=2026-09-05 19:31:57  CreatedById=<null>
run.log:           application started 19:31:01, first authenticated request ~19:31:57
```

So the row is seeded **inside the cookie pipeline on the first authenticated HTTP request**, not at
startup. `CreatedById` is null, which is independent evidence for §B's obstacle: the audit
interceptor found no ambient principal even though a real user was signed in.

**Under a per-tenant model there would be no lazy seed per tenant, and there should not be.** The
picklist shape answers it: a tenant with no row of its own falls back to the installation row, which
is already seeded. A tenant row appears only when a tenant administrator saves one. What the cache
must then hold is the *resolved* value, so "tenant T has no row" costs one lookup, not one per
request.

### 2.4 The startup validation would not change

`IdleTimeoutSettings.Validate` runs under `ValidateDataAnnotations().ValidateOnStart()` and validates
the **configured bounds only** — `Min < Max`, `Max ≤ 480`, defaults inside the band, countdown inside
`[10, 600]`. Those are deployment configuration and stay installation-wide under every option.

Nor would the cookie: `CookieLifetime = Max + Grace + DefaultCountdown` is sized from the
**configured ceiling**, not from the administered value, precisely because a cookie is issued once at
sign-in and cannot be shortened. Any per-tenant policy is necessarily inside that envelope, so the
cookie needs no change either. **Both of these are genuinely free**, and they are the two places one
would most expect a per-tenant policy to hurt.

---

## 3. §B — Where the enforcer runs, and how it would get a tenant

**This is the section that decides the cost, and the answer is: it can, cheaply, through a service
that already exists.**

### 3.1 What the enforcer has

`IdleSessionEnforcer.IsStillValidAsync(CookieValidatePrincipalContext)` is chained onto
`OnValidatePrincipal` after Identity's security-stamp validator
(`Infrastructure/DependencyInjection.cs:569-591`) and resolved from
`context.HttpContext.RequestServices`. It therefore holds:

- **`context.Principal`** — a real `ClaimsPrincipal`, from which the **user id** is reliably available
  (`ClaimTypes.NameIdentifier`); the provider already reads exactly that in `ReadPreferenceAsync`;
- `context.HttpContext`, so the request scope and `RequestAborted`;
- `context.Properties`, carrying the ticket's `gx:idle:lastActivity` stamp.

What it does **not** have:

- **`IUserContextAccessor.Current`** — null. Pass 34 §2.4 established that
  `UserContextHubFilter.cs:52` is the only place in the codebase that pushes a user context, and it
  is a SignalR hub-method filter. Re-confirmed by grep at the point of reliance, and confirmed
  incidentally by the seeded row's null `CreatedById` in §2.3.
- **A tenant claim.** Measured rather than assumed:

```
SELECT ClaimType, COUNT(*) FROM AspNetUserClaims  ->  (no rows)
SELECT Id, UserName, TenantId FROM AspNetUsers
  4e40ce84-…  Administrator  01a0730d-86bd-7243-8df1-e6a7f1fd0066
```

The signed-in administrator has a real tenant on their user row and **no `TenantId` claim at all**.
Static analysis says why, and says it exhaustively: across `src/`, the only writers of user claims
are `PermissionAssignmentService` (permission claims only) and
`TenantSwitchService.RefreshUserClaimsAsync` — which has exactly one caller,
`SwitchToTenantAsync:121`. `ApplicationUserClaimsPrincipalFactory` adds only `MustChangePassword`.
So **a user who has never switched tenant carries no tenant claim, ever.** `HubUserContext`'s remarks
already say this; this pass measured it.

### 3.2 Could the claim be made reliable?

**No, and Pass 22 §A.3's reasoning applies here unchanged.** Writing a claim onto the principal
requires reissuing the authentication cookie, which means `SignInManager.RefreshSignInAsync` — and
that cannot run inside a Blazor circuit, because the response has already started when a component's
event handler runs. Tenant switching happens in a circuit. So a tenant claim would be correct only
after the user's *next* real HTTP sign-in, which is precisely the staleness that made the same
approach wrong for the user preference and made `/pages/authentication/refresh-signin` necessary for
`MustChangePassword`.

Note the shape of what `RefreshUserClaimsAsync` actually does: it writes the claim to
**`AspNetUserClaims`**, so it is durable and does appear on the *next* principal build. That makes it
worse, not better — the claim is absent for most users and stale for the rest, which is the failure
mode that looks like it works.

### 3.3 The cheap answer: `IUserContextLoader`, which already exists

```csharp
public interface IUserContextLoader
{
    Task<UserContext?> LoadAsync(ClaimsPrincipal principal, CancellationToken ct = default);
    void ClearUserContextCache(string userId);
}
```

Every property this needs is already true:

| Requirement | Status |
|---|---|
| Takes what the enforcer has | `ClaimsPrincipal` — exactly `context.Principal` |
| Resolves the user the same way | `principal.FindFirst(ClaimTypes.NameIdentifier)` |
| Returns the tenant | `UserContext.TenantId`, read from the user row — **not** from a claim |
| Cheap enough for the hot path | FusionCache, `ContextCacheDuration` = **1 hour**, keyed by user id |
| Already warm | the hub filter loads it for every circuit, so any user with a browser open has an entry |
| Already invalidated on tenant switch | `TenantSwitchService:118` calls `ClearUserContextCache(userId)` **today** |
| Lifetime-compatible | registered `AddSingleton<IUserContextLoader, UserContextLoader>()`; the provider is scoped, so singleton-into-scoped is legal |
| Layering-legal | both `UserContextLoader` and `IdleTimeoutPolicyProvider` live in Infrastructure |

**So the answer to "how would a per-tenant lookup obtain the tenant in the one place the policy is
enforced" is: `await _userContextLoader.LoadAsync(principal)`, on a cache that is already warm and
already invalidated by the one operation that changes the answer.** No new claim, no cookie reissue,
no ambient context.

**Two costs that are real and should not be waved away.** First, on a cold cache this is a database
round-trip *inside cookie validation* — the hottest path in the application, on every authenticated
request. It is cached for an hour and warm for anyone with a circuit, but the enforcer would now have
a database dependency it does not have today, and the failure mode of a cache miss storm is a
sign-in stampede. Second, `ReadPreferenceAsync` returns early when `AllowUserOverride` is false and
does no user read at all; a tenant lookup would be needed regardless, so a deployment with the user
override switched off gains a per-user read it does not currently have.

**A design note that saves a third invalidation site.** The tenant could instead be folded into the
provider's own per-user cache entry — `Select(u => new { u.TenantId, u.IdleTimeoutMinutes })`, the
same query, the same round-trip, apparently free. It is not: that entry is invalidated only by
`InvalidateUser`, which `TenantSwitchService` does **not** call, so a tenant switch would leave the
enforcer on the old tenant's policy for up to twelve hours with nothing failing. Using
`IUserContextLoader` inherits an invalidation that already exists and is already correct. **Prefer
the loader.**

### 3.4 Verdict on the estimate

`SecurityPolicy`'s comment — *"a migration plus a cache key, not a redesign"* — is **too optimistic,
but not by an order of magnitude, and the part it was most likely to be wrong about is the part it
gets right.** Getting a tenant into the enforcer is genuinely cheap. What the estimate omits is
everything else: §C.1.

---

## 4. §C — The three options

### 4.1 Option 1: per-tenant rows, installation default as fallback

The picklist shape. `SecurityPolicies` gains a nullable `TenantId`; a null row is the installation
default; a tenant row overrides; effective = `tenant row ?? installation row`, still clamped.

**Measured cost:**

| Item | Cost |
|---|---|
| Entity + configuration | `TenantId` on `SecurityPolicy`; a **unique index** on it, or nothing stops two rows per tenant |
| **Migration** | a real one — and it would be the **first second migration this template has ever shipped**: every provider currently has exactly one, `InitialCreate`. Pass 33's `ModelMatchesMigrationsTests` now *forces* all three chains to be regenerated together, which is a benefit (the guard works) and a cost (three chains, the README's procedure, per-provider connection strings) |
| Interface signature | `GetAdministeredAsync()` takes no principal today and would need one. **22 call sites** of the two read methods across `src/` and `tests/`; the awkward one is `GetSecurityPolicyQueryHandler`, which has no principal at all and would need `IUserContextAccessor` — available there, since it runs in a circuit |
| Cache | the key stops being a constant. `Invalidate()` must become a **tag flush** (`RemoveByTagsAsync`, already used by `CacheInvalidationBehaviour`) or every tenant that inherits the installation default keeps a stale copy for twelve hours |
| Enforcer | the `IUserContextLoader` dependency of §3.3, with its cold-cache cost |
| Permissions | a second right for the installation row, plus a registry grant and the divergence assertion |
| UI | a concept the screen does not have: *"you are editing your organisation's policy, overriding the installation default of 15 minutes"*, plus a revert-to-default affordance and a distinct view for whoever edits the installation row |
| Tests | ~54 tests in the five directly-affected fixtures (`IdleTimeoutPolicyTests` 25, `SecuritySettingsPageMiddlewareTests` 13, `IdleTimeoutDialogComponentTests` 6, `IdleTimeoutWiringTests` 5, `ProfileSecurityTabComponentTests` 5), several of which construct the provider directly |

**Not free, and not a redesign either** — but "a migration plus a cache key" it is not.

### 4.2 Option 2: installation-wide, with a named right

The Pass 33 shape. One row, unchanged. A new right —
**`Permissions.SecuritySettings.ManageInstallationPolicy`** — gates the edit, alongside the existing
`SecuritySettings.Edit`, exactly as `Roles.ManageDefinitions` sits alongside `Roles.Edit`.

**Cost:** one constant with an honest `[Description]`, one `AccessRights` property, one grant in
`AdministratorPermissionRegistry` (a deliberate act, because the divergence assertion makes it one),
one guard in `UpdateSecurityPolicyCommandHandler`, and the screen's save button gated on it. No
migration, no cache change, no signature change, no enforcer change, no new UI concept.

**What it asserts:** that session idle policy is an operator concern. That claim is defensible on
this codebase's own construction, and §4.4 makes the argument rather than assuming it.

### 4.3 Option 3: leave it, and say why

**Rejected.** The brief is right that this would be the only unnamed cross-tenant write left. A tenant
administrator holding `SecuritySettings.Edit` today changes the idle window for every tenant, and
nothing names that capability. Pass 33 closed exactly this shape for roles; leaving it here would
make security settings the single exception, in the one area — session control — where an exception
is least defensible.

There is no argument for *silence*. There is an argument for the policy remaining installation-wide,
and that argument is option 2, which makes the capability named and revocable rather than tacit.

### 4.4 The recommendation, and the argument behind it

**Recommend option 2 now. Do not build option 1 yet.**

Three reasons, in order of weight:

1. **The outer bound is irreducibly installation-wide, so per-tenant policy can only ever vary inside
   a band the operator already sets.** `CookieLifetime` derives from `MaxIdleTimeoutMinutes`, and the
   cookie is issued at sign-in — before any tenant is known and before it could be sized per tenant.
   `Min`/`Max` are deployment configuration, validated at startup. A healthcare tenant wanting five
   minutes and a logistics tenant wanting 120 must both fit inside one configured band chosen by the
   operator. That materially reduces what per-tenant rows buy: not "each customer sets their own
   policy", but "each customer picks a point inside the operator's range".
2. **The enforcement point is the least tenant-aware part of the stack, by construction.** §B shows
   the tenant is *obtainable* there — but obtaining it means adding a cached database dependency to
   the cookie-validation path that runs on every authenticated request. That is a real cost to accept
   for a capability nobody has asked for.
3. **No requirement has been stated.** Every other tenancy decision in this programme was driven by a
   concrete harm: Pass 32 §4.2 could name three things a tenant administrator could do to another
   tenant's roles. Here the harm is real but singular — one administrator sets one number for
   everyone — and naming the right removes it entirely.

**Nothing is wasted if option 1 later becomes right.** Under option 1, editing the installation
default row needs precisely a `ManageShared`-style right, while `SecuritySettings.Edit` continues to
gate a tenant editing its own row. That is the same right option 2 introduces, with the same name and
the same default grant. Option 2 is stage 1 of option 1, not an alternative to it.

**The condition under which option 1 becomes right, stated so it can be recognised:** a customer
requires an idle window materially different from another customer's *within the operator's
configured band*, and says so. At that point §B's route is known, §C.1's costs are itemised, and the
right already exists.

### 4.5 The single-tenant deployment — the case that has nearly gone wrong three times

**Grant the new right to the administrator by default**, in `AdministratorPermissionRegistry`, for
exactly the reason Pass 31 §5 established and Passes 32 §A and 33 §A both restated:
`EnsureAdministratorAsync` assigns the bootstrap administrator `Tenants.First()`, so **the sole
administrator is itself tenant-scoped**. A right that defaulted to ungranted, or a rule of the form
"a tenant-scoped principal may not edit the installation policy", would leave a single-tenant
installation unable to change its own idle timeout — the screen present, the values shown, the save
refused, forever.

The trap is a blanket prohibition, not a default-granted right. Revoking it is the multi-tenant
operator's deliberate act, and the README must say so in the same breath as it names the right.

---

## 5. §D — The per-user preference across a tenant switch

**The `min(preference, administered)` clamp remains correct across a switch, and needs no change.**

A user belonging to two tenants has one preference and, under option 1, two possible administered
policies. Worked through:

| Preference | Tenant A policy | Tenant B policy | Effective in A | Effective in B |
|---|---|---|---|---|
| 45 | 60 | 30 | 45 | **30** — the tenant's policy wins, tighten-only holds |
| 10 | 15 | 120 | 10 | 10 — the user's choice survives a laxer tenant |
| none | 60 | 30 | 60 | 30 |

The preference can never lengthen a window, in either direction of travel, because the clamp is
applied at **read** time rather than only at save time — deliberately, so a value forced into the
database by other means is still held. That property is what makes the tenant switch a non-event.

**Two things would need attention, both small and both in the screen rather than the rule:**

1. **`SecurityTab` displays a stale ceiling.** It loads `_administeredMinutes` for the *current*
   tenant and refuses a save above it. A user who chose 45 under a 60-minute tenant and then switches
   to a 30-minute tenant sees `_chosenMinutes = 45` against a maximum of 30; saving is correctly
   refused, but the screen shows a number that is not in force. It should either display the clamped
   effective value or say *"your preference of 45 minutes is currently limited to 30 by this
   organisation's policy"*. `_effectiveMinutes` is already computed and already correct — it is the
   input control that lies.
2. **Cache invalidation on switch**, but only under one of the two designs. If the tenant is folded
   into the provider's own per-user entry, `TenantSwitchService` must start calling `InvalidateUser`.
   If the tenant comes from `IUserContextLoader` — as §3.3 recommends — the invalidation already
   exists and nothing new is needed. This is the concrete reason to prefer the loader.

Under the recommended option 2 neither arises: there is one administered policy and the switch
changes nothing.

---

## 6. §E — The plan, and what the README must say

### 6.1 Staged

**Stage 1 — the named right (recommended; small).**

- `Permissions.SecuritySettings.ManageInstallationPolicy`, with a `[Description]` that states the
  scope — the module's class-level description already says *"Set permissions for the installation's
  security policy"*, but `Edit`'s own says only *"Allows changing the security policy, including the
  idle timeout"*, which Pass 35 §3.3 already flagged as reading tenant-scoped when it is not.
- `SecuritySettingsAccessRights.ManageInstallationPolicy`, spelled identically — `PermissionService`
  builds the claim from the property name.
- Granted in `AdministratorPermissionRegistry` with the single-tenant reason recorded in place.
- Guarded in `UpdateSecurityPolicyCommandHandler` — the command already goes through Mediator, so
  unlike roles there is a chokepoint and one guard suffices. The screen's save button is the second
  line, not the boundary.
- Tests: the guard through the handler (refusal leaves the stored row unchanged), the
  narrowed-not-emptied control (a holder still saves; `SecuritySettings.View` still reads), the
  default grant, and the description's scope — the Pass 35 shape.
- While there: `SecuritySettings.Edit`'s description should state the scope too, which closes one of
  the three groups Pass 35 §3.3 listed.

**Stage 2 — per-tenant rows (not recommended now).** §C.1 is the itemised estimate; §C.4 is the
trigger. If it is ever built, §3.3 is the route for the enforcer and §5 is the user-preference
answer.

**Stage 3 — not recommended at all.** Making the *configured bounds* per-tenant. They size the
authentication cookie, which is issued once per sign-in for the whole deployment; this is not a cost
question but an impossibility given cookie semantics, and it is worth writing down so nobody costs it
twice.

### 6.2 What the README's Tenancy row must say

The row currently reads:

> | Security settings (idle policy) | No — one row per installation, by design |

"By design" is not an answer. After stage 1 it should say, in substance: **the policy is
installation-wide and stays so deliberately, because the authentication cookie it is bounded by is a
single deployment-wide artifact sized from configured bounds; editing it requires
`SecuritySettings.ManageInstallationPolicy`, granted to the administrator by default and revocable in
a multi-tenant installation where one customer's administrator should not set every customer's
session policy; `SecuritySettings.View` remains ungated by it; and the per-user preference is
unaffected, being tighten-only and per-user.**

The surrounding idle-policy section already contains the sentence *"today one tenant's administrator
sets the policy for all of them"*, which is true and would remain true — but it should stop reading
as an admission and start reading as a named, revocable capability with a stated reason. The
paragraph that calls it *"a deliberate starting point"* should also lose the phrase, or gain the
condition under which the starting point would move: §C.4's trigger.

---

## 7. Scratch probe disclosure

Two, both removed:

1. **A real application run on SQLite**, at `http://localhost:5197`, with both connection strings
   pointed at the session scratchpad. Driven over HTTP: a sign-in as the bootstrap administrator and
   two authenticated page fetches. It produced §2.3's lazy-seed timing and §3.1's claim measurement.
   Stopped explicitly — Pass 29 A7's lesson that a still-listening probe is not evidence that *this*
   run answered — and its databases deleted with the scratchpad.
2. **`DbPeek`**, a scratch console (Microsoft.Data.Sqlite) that read `AspNetUserClaims`, `AspNetUsers`
   and `SecurityPolicies` from the probe database. Deleted.

The application also appended to `src/Server.UI/log/log-*.txt`, which `.gitignore:34` (`[Ll]og/`)
excludes; `git status` is empty. **The repository is byte-identical to `ed32eda9`**, and no
production code, test or document was changed by this pass other than this report.

---

## 8. Anomalies

**A1 — an estimate written into the code was optimistic, and the comment reads as authority.**
`SecurityPolicy`'s remarks say *"Adding a tenant column later is a migration plus a cache key, not a
redesign"*, and `IdleTimeoutPolicyProvider` repeats it as *"A multi-tenant deployment keys this by
tenant and changes nothing else"*. Both are load-bearing enough to have shaped this brief's framing.
§C.1 counts what they omit: a signature change across 22 call sites, a tag-based flush rather than a
key removal, a second permission, a UI concept that does not exist, ~54 affected tests, and the first
second-migration the template has ever shipped. Recorded because the comments are otherwise good and
a reader has no reason to doubt them — an estimate in a doc-comment ages exactly like a comment
addressed to a future pass (Pass 32 A1), with no failure mode.

**A2 — a global query filter would be the wrong mechanism here, and would fail silently in the one
path that matters.** The obvious way to make `SecurityPolicy` tenant-aware is the mechanism Pass 29
and Pass 31 used: a named global filter on `ApplicationDbContext`. It would be a trap. The provider's
context resolves `IUserContextAccessor` from the request scope, and in the cookie pipeline
`Current` is null — so a filter of the picklist form (`TenantId == null || TenantId == current`)
would return **only the installation row** to the enforcer, for every user of every tenant, while
returning the right row everywhere else. Every circuit-side screen would show a tenant its policy and
enforcement would quietly apply the installation's. Per-tenant policy must query by an explicitly
passed tenant, never by ambient filter. Recorded because the filter is the house pattern and would be
the first thing tried.

**A3 — the audit trail caught the ambient-context gap before any reasoning did.** The lazily seeded
`SecurityPolicies` row carries `CreatedById = null` despite being written during an authenticated
request by a signed-in administrator. That single column is direct evidence for Pass 34 §2.4's
finding, arrived at from a completely different direction — and it means the seed of a security
control is recorded as having no author. Harmless today (it is a machine-written default, not a
person's decision) but worth knowing: the *first* value of the idle policy is unattributable, while
every subsequent change is fully audited.

**A4 — `RefreshUserClaimsAsync` swallows its own failure.** It wraps everything in
`try { … } catch (Exception ex) { _logger.LogError(…); }` and returns normally, so a failure to write
the tenant claims leaves `SwitchToTenantAsync` reporting success with the user's claims stale. It
does not affect this pass's recommendation — the claim is not usable as a tenant source either way —
but it is a silent-failure path in the one method that maintains a security-relevant claim, and it is
adjacent to everything §B examined. Recorded rather than acted on.

**A5 — the brief's expected test count was 950 and no such state exists.** Pass 35 delivered 964 and
recorded it; the tree was green at 964 when this pass opened. This is the third brief in five whose
expected count did not match the preceding pass's delivered figure (Pass 32 expected 891 against 877;
Pass 33 expected 918 against 899). Recorded as a pattern rather than an incident: the start-state
check is doing its job every time, but a projected number in a brief is not evidence, and each
occurrence costs a stop-and-ask.
