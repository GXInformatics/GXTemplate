# Pass 39 — The Publisher's Frozen Context, and Its Lifetime

**Nature:** investigation with a design gate, then implementation of what was ratified.
**Date:** 2026-09-06.

**Result in one line:** every notification handler now runs under the `ExecutionContext` of the
publisher that **raised** the event rather than the one that constructed it — fixing three unrelated
kinds of frozen ambient state, one of which was a live cache-eviction bug, not just log labelling.
**The lifetime stays Scoped**, on measurements taken here. **989 → 995 tests**, warnings unchanged,
Pass 5's boundary suites byte-unmodified.

---

## 1. Start state

| | |
|---|---|
| HEAD | `074e06e2` — *"Paass38"*, containing both Pass 37 and Pass 38 |
| Working tree | clean |
| Build | **0 errors, 19 warnings across 10 distinct source locations** |
| Tests | 228 + 12 + 506 + 243 = **989 passed, 12 skipped, 0 failed** |

Measured against Pass 38's report §5.6, which delivered exactly 989 + 12 skipped. No expected count
was asserted by this brief, correctly — and this is the first pass in five where the precondition
held on the first check.

---

## 2. §A.1 — The freeze, reproduced and its consequences established

### 2.1 Reproduced at HEAD, against the real publisher

Pass 34 measured this on a shape-replica. This ran the **real `ChannelBasedNoWaitPublisher` with the
real `UserContextAccessor`**:

```
FREEZE constructed=A     published=B     -> handler saw: tenant-A
FREEZE constructed=none  published=B     -> handler saw: <null>
FREEZE constructed=A     published=none  -> handler saw: tenant-A
CULTURE constructed=de-DE published=fr-FR -> handler saw: de-DE
```

**Two of these four lines are new.** Pass 34 measured only the first. The third is a *leak in the
other direction*: a publisher built inside a circuit and then published from a background or HTTP
path with no principal hands the handler the circuit's tenant. The fourth establishes that the
freeze is not confined to the user context at all.

### 2.2 The handlers, and what each actually depends on

Six handlers exist. Three of them read ambient state, and only one of those three does it visibly.

| Handler | Ambient state | Consequence of the freeze |
|---|---|---|
| `DocumentCreatedEventHandler` | reads `IUserContextAccessor.Current?.UserName` **directly** | logs the wrong **user name** in the message body, not merely a wrong column |
| `PicklistSetChangedEventHandler` | `IDataSourceService<PicklistSetDto>.RefreshAsync()` | **evicts the wrong tenant's cache** — see §2.3 |
| `ResetPasswordNotificationHandler`, `SendWelcomeNotificationHandler`, `UserActivationNotificationHandler` | `IStringLocalizer` → `CultureInfo.CurrentUICulture` | a welcome or reset email **rendered in the wrong language** |
| `DocumentDeletedEventHandler` | none | log labelling only |

### 2.3 The consequence that is not about logging

`PicklistDataSourceService.Scope` is `CacheScope.PerTenant`
(`PicklistDataSourceService.cs:53`), and `DataSourceServiceBase.RefreshAsync` composes its key from
the ambient context:

```csharp
var key = EffectiveKey();          // reads _userContextAccessor.Current
if (key is not null) _fusionCache.Remove(key);
```

`EffectiveKey` returns `t:{TenantId}|ALL-PicklistSetDto`, or **null** when the scope needs a
principal and there is none. So before this pass:

- a **tenant-B** picklist edit evicted **tenant A's** entry — the editor kept seeing a stale list
  while an unrelated tenant's cache was needlessly dropped;
- and when the publisher had been constructed with no principal, `EffectiveKey()` returned null,
  `Remove` was skipped, and **no cache was evicted at all** — the edit reached no picker until the
  entry expired.

That is a user-visible correctness bug, and it is the fact that decided §A.2.

### 2.4 The landmine, confirmed unarmed

**No handler writes through `ApplicationDbContext` today** — confirmed by inspection of all six
(zero references to `IApplicationDbContext`, `SaveChangesAsync` or a context factory). Pass 34 said
so and it still holds. But that is a property of the current handler set, not of the design: the
moment one writes, `AuditableEntityInterceptor` stamps from the ambient context (Pass 24) and Pass
29's filter routes the row to whichever tenant that context named. §B.3's test is what turns that
from a comment into a failure.

### 2.5 What the correct tenant *is*

**The publishing scope's**, explicitly. A notification records something that happened; the tenant
that happened is the one whose action raised the event, not the one that happened to warm a DI
scope. That is what §A.2's fix delivers and what every assertion below checks.

---

## 3. §A.2 — The recommendation, and the null-versus-wrong argument

**Ratified: capture and restore the `ExecutionContext` per message.**

### 3.1 Why not suppress flow

Suppressing flow is the more obviously "fail-closed" option and this template's instinct is
fail-closed — but on this codebase's facts it is the **worse** of the two, and §2.3 is why:

- With flow suppressed, handlers see a null ambient context **always**.
- `EffectiveKey()` then returns null **always**.
- So `RefreshAsync` would **never** evict any tenant's picklist cache.

That converts a sometimes-wrong eviction into one that never happens. Silently-hidden is not better
than silently-wrong here, because the wrong case is at least self-correcting — the next edit from
the right tenant fixes it — while the never case is permanent until a TTL expires. And for the audit
interceptor a null tenant means "installation event", which Pass 29's filter makes invisible to
*every* tenant principal: a business write from a handler would become a row nobody can see.

Fail-closed is the right instinct when the alternative is *serving* something wrongly. Here the
alternative is *recording* something wrongly, and a record filed under nobody is not safer than one
filed under the wrong name — it is merely lost.

### 3.2 Why not capture the `UserContext` alone

It would have fixed one of the three affected mechanisms and left a mail handler still localising
into the language ambient when the publisher was built. `ExecutionContext` carries all of them —
`IUserContextAccessor`'s `AsyncLocal`, `CultureInfo.CurrentUICulture`, and `Activity.Current` for
trace correlation — in one restore.

### 3.3 Why moving the `Task.Run` alone fixes nothing

Starting the consumer lazily or from a hosted service only changes *which* context is frozen. The
capture is inherent to starting a long-lived loop; the fix has to be at the message, not at the loop.

---

## 4. §A.3 — The lifetime, measured here

**Ratified: leave it Scoped.**

```
one circuit, 20 notifications @10ms   scoped=316ms    naive-singleton=316ms     +4=78ms     +24=15ms
4 circuits, 50 each @20ms             scoped=1576ms   naive-singleton=6328ms    +4=1581ms   +24=283ms
8 circuits, 75 each @40ms             scoped=3550ms   naive-singleton=28519ms   +4=7127ms   +24=1188ms
```

The third row is MNEFleets' exact shape — 600 notifications at a 40 ms handler. **Their naive
singleton was 28,535 ms; mine is 28,519 ms.** The shape of the finding transfers precisely.

**Their conclusion does not.** Four reasons, in order of weight:

1. **Scoped never loses.** It equals a naive singleton on a single circuit and beats it 8× under
   concurrency. Since a naive singleton is what a careless conversion produces, the conversion's
   most likely outcome is an 8× regression.
2. **The pool's win is on work nobody waits on.** Publishing is fire-and-forget by construction —
   `Publish` returns as soon as the callbacks are queued. Turning 3,550 ms of background drain into
   1,188 ms is real and buys nothing a user can perceive at this template's volumes, which are a
   handful of identity and document events rather than workflow traffic.
3. **The blocking objection is not throughput — it is Pass 5-INV's hazard, and it is worse here than
   at MNEFleets.** Their pattern requires handlers to resolve their own scope through
   `IServiceScopeFactory`. **None of these six does**; they take scoped services directly —
   `IDataSourceService<PicklistSetDto>`, `IMailService`, `IStringLocalizer`. A singleton publisher
   would invoke them holding dependencies from scopes that may already be disposed. Converting means
   rewriting all six handlers first.
4. **§A.4: it would move the drain.** Today `DisposeAsync` runs at **scope** disposal, so a
   notification queued by a circuit drains deterministically while that circuit's dependencies are
   still alive. A singleton moves that to process shutdown — which is hazard 3 again, by another
   route.

This is the fourth refusal in this programme, and the first with a number attached in both
directions. The measurement and the reasoning are now recorded on the constructor, so the question is
not reopened blind.

---

## 5. §A.4 — Drain and shutdown

Pass 5 §C's work holds at HEAD. `ChannelBasedNoWaitPublisher` implements `IAsyncDisposable` **and**
`IDisposable` (the latter because a service implementing only the former makes
`IServiceScope.Dispose()` throw), `BeginDispose` completes the channel exactly once through an
`Interlocked.Exchange`, and both paths await the drain.

Asserted, not assumed, by suites this pass left byte-unmodified: `PublisherDisposalTests`
(`ThePublisherIsAsyncDisposable`, `DisposingTheScopeCompletesAndDrainsTheChannel`,
`DisposingTwiceIsSafe`) and `ChannelBasedNoWaitPublisherTests.DisposeAsync_ShouldDrainQueuedHandlers`
— all green after the change. Since the lifetime is unchanged, nothing about shutdown draining moved.

---

## 6. §B — The implementation

### 6.1 The change

The channel's message type gained the publisher's context:

```csharp
private readonly record struct QueuedHandler(
    Func<CancellationToken, ValueTask> Callback,
    string NotificationType,
    ExecutionContext? Context);
```

`Publish` captures **once per notification**, not per handler — every handler of one event observes
the same ambient state, which is the state of the caller that raised it:

```csharp
var context = ExecutionContext.Capture();
```

And the consumer restores it:

```csharp
private static ValueTask InvokeAsync(QueuedHandler queued)
{
    if (queued.Context is null)
    {
        return queued.Callback(CancellationToken.None);
    }

    ValueTask pending = default;
    ExecutionContext.Run(
        queued.Context,
        state => pending = ((QueuedHandler)state!).Callback(CancellationToken.None),
        queued);

    return pending;
}
```

**The delegate is not awaited inside `ExecutionContext.Run`.** That method takes a synchronous
`ContextCallback`, so the callback only *starts* the handler and the returned task is awaited
outside. That is sufficient and standard: the handler begins under the restored context, and every
continuation inside it inherits from there, so an `AsyncLocal` read after an `await` still sees the
publisher's values. `AHandlerObservesThePublishingTenant_NotTheConstructingOne` would fail if it were
not.

**A null capture is not an error.** `ExecutionContext.Capture()` returns null when flow is suppressed
at the publish site; the handler then runs on the consumer loop's context, which is the pre-Pass-39
behaviour and the right answer — a caller that suppressed flow asked for exactly that.

The `Task.Run` stays in the constructor, with a comment saying why that is now harmless: its capture
never reaches a handler.

### 6.2 What was documented rather than changed

The constructor carries §A.3's measurement and the three reasons the lifetime stays Scoped, so the
next reader meets the numbers rather than the question.

---

## 7. §C — Verification

### 7.1 Red before, green after — behaviour, not structure

`PublisherAmbientContextTests` (5 tests) publishes under one ambient state from a publisher
constructed under another and asserts what the handler **observes**. A test asserting the `Task.Run`
had moved would have pinned the implementation; these pass for any mechanism that gets it right.

With `ExecutionContext.Capture()` replaced by `null` — the single changed line — **all five go red**:

```
Application.UnitTests   Failed: 5,  Passed: 0
```

| Claim | Test |
|---|---|
| The handler sees the publishing **tenant** | `AHandlerObservesThePublishingTenant_NotTheConstructingOne` |
| …and the publishing **user** | `AHandlerObservesThePublishingUser` |
| …and the publishing **culture** | `AHandlerObservesThePublishingCulture` |
| A constructing tenant does **not** leak into an unauthenticated publish | `AConstructingTenantDoesNotLeakIntoAnUnauthenticatedPublish` |
| **The landmine** | `TheLandmine_AHandlerWritingToTheDatabaseStampsThePublishingTenant` |

### 7.2 The landmine test

A handler that writes a `PicklistSet` through a **real `ApplicationDbContext` with the real
`AuditableEntityInterceptor`**, published under tenant B from a publisher constructed under tenant A.
It asserts the stored row carries `TenantId = "tenant-B"` and `CreatedById = "u-b"`.

This is the case that does not exist today and would have been silently wrong tomorrow. Pass 32 A1's
lesson is that a comment addressed to a future pass has no failure mode; this has one, and it was red
before the fix. Pass 32 A5's trap is respected — the fixture creates the author's user row, because
the audit row carries a real foreign key to `AspNetUsers`.

### 7.3 The log rows, sampled from a real log database

`PublishedNotificationLogTenantTests` writes through the **real Serilog SQLite sink into a real log
database**, with the real `UserInfoEnricher`, from inside a notification handler. Before the fix, the
row read out of that database was:

```
Assert.Contains() Failure: Sub-string not found
String:    "{"UserName":"","TenantId":"tenant-A","Cli"···
Not found: ""TenantId":"tenant-B""
```

After: `"TenantId":"tenant-B"`, and `tenant-A` absent from the row entirely.

**Why the assertion is on `Properties` rather than the `TenantId` column**, stated in the fixture:
Pass 34 A4 established that the SQLite sink is a third-party package with a fixed `INSERT` and cannot
write that column at all — it is permanently null on this provider. It *does* write `Properties`, and
the enriched tenant appears there as JSON, which is exactly what Pass 34 sampled
(`"TenantId":null` on every row it captured). Same value, same real database, through the one column
this provider can carry it in. A run on SQL Server or PostgreSQL would populate the dedicated column,
but neither can produce a tenanted notification row without a live circuit — which is why the
publisher is driven directly.

### 7.4 The lifetime

No lifetime change landed, so §C.4's "measured comparison repeated after the change" does not apply.
The drain assertion is §A.4's, and those four tests are byte-unmodified and green.

### 7.5 Boundary suites

**All 12 byte-unmodified** (`git diff --quiet`), including the two the brief singled out because §B
touches what handlers observe when they write — `TransactionalAuditTests` and
`InterceptorOrderingTests` — plus the publisher's own two disposal suites, the notification smoke
tests, `TenantStampingTests`, `AuditTrailTenantFilterTests`, `LogTenantStampingTests`,
`SinkColumnDriftTests`, `DocumentTenantIsolationTests` and `FileEndpointsAuthorizationTests`. Run as
a filtered set: **102 passed, 0 failed** (24 + 78).

### 7.6 Counts

| | Before | After | Delta |
|---|---|---|---|
| `Infrastructure.UnitTests` | 228 | **229** | **+1** |
| `Application.IntegrationTests` | 12 | 12 | — |
| `Application.UnitTests` | 506 (+12 skipped) | **511** (+12 skipped) | **+5** |
| `Server.UI.IntegrationTests` | 243 | 243 | — |
| **Total** | **989 passed, 12 skipped** | **995 passed, 12 skipped** | **+6, 0 failed** |

The +6 is exactly the two new files. **No pre-existing test changed count or outcome** — notably the
four publisher tests, which pass unchanged against a rewritten `ProcessNotifications`.

**Warnings: unchanged.** 19 across the same 10 distinct source locations, plus `NETSDK1206`.

### 7.7 Generation probe

Uninstalled before installing, per Pass 35 A5:

```
dotnet new uninstall → dotnet pack → dotnet new install → dotnet new gxblazor -n P39
  → ChannelBasedNoWaitPublisher carries ExecutionContext.Capture (2 references)
  → build: 0 Error(s), 19 Warning(s)
  → dotnet test: 229 + 12 + 511 + 243 = 995 passed, 12 skipped, 0 failed
  → dotnet new uninstall; probe directory removed
```

---

## 8. File map, diffstat and edit fidelity

### 8.1 File map

**Modified (1):**

| File | |
|---|---|
| `src/Application/Common/PublishStrategies/ChannelBasedNoWaitPublisher.cs` | `QueuedHandler` record struct, per-message capture, `InvokeAsync` restore, and §A.3's measurement recorded on the constructor |

**New (2):**

| File | |
|---|---|
| `tests/Application.UnitTests/Common/PublishStrategies/PublisherAmbientContextTests.cs` | **5 tests** — tenant, user, culture, the leak direction, and the landmine |
| `tests/Infrastructure.UnitTests/Logging/PublishedNotificationLogTenantTests.cs` | **1 test** — the log row, sampled from a real SQLite log database |

No handler changed, no registration changed, no migration, no permission, no README change. The
README says nothing about notification-handler ambient state, and after this pass it does not need
to: the behaviour is now what a reader would already assume.

### 8.2 Diffstat

```
 .../Common/PublishStrategies/ChannelBasedNoWaitPublisher.cs | 109 ++++++++++++++++++--
 1 file changed, 99 insertions(+), 10 deletions(-)
```

Plus two new test files.

### 8.3 Edit fidelity

- **No git actions.** Only `status`, `log`, `show` and `diff`.
- **Both red-before demonstrations were reverted byte-identically**, verified by `diff` against a
  copy taken beforehand.
- **No existing test file was touched.** The +6 is entirely in two new files.
- **Three temporary probe fixtures were written inside `tests/` and deleted** once their output was
  captured; none appears in a recorded count, and the suite was re-run at baseline after removal to
  confirm it.

---

## 9. Scratch probe disclosure

Four, all removed:

1. **`TempFreezeProbe.cs`** — drove the real publisher with the real `UserContextAccessor` to produce
   §2.1's four-line capture.
2. **`TempCultureProbe.cs`** — the culture half of the same capture.
3. **`TempLifetimeBenchmark.cs`** — §4's scoped / naive-singleton / pooled comparison, including a
   `PooledPublisher` written only to be measured against and never proposed for the repository.
4. **A green-file backup** of `ChannelBasedNoWaitPublisher.cs` in the session scratchpad, for the two
   red-before demonstrations.

Plus the generation probe (packed nupkg, installed template, generated `P39` at `C:\gxp39`; template
uninstalled, directory removed). No application run and no external database this pass. The nupkg in
the repository root is a gitignored build artifact.

---

## 10. What remains

| Surface | Status |
|---|---|
| **The frozen `ExecutionContext`** | **closed.** Handlers observe the publishing scope's tenant, user, culture and trace context; the landmine has a test |
| **The publisher's lifetime** | **decided, not deferred.** Stays Scoped, on measurements taken here, with the numbers and the three reasons recorded on the constructor. Reopening it requires the six handlers to resolve their own scopes first |
| The dead `ClaimsPrincipalExtensions` surface | unchanged (Pass 38 §4.2). Eight methods with zero callers, two carrying warnings. A small pass of its own |
| SQLite/PostgreSQL shared-picklist duplicate gap | unchanged. Both treat NULLs as distinct in a unique index; closing it portably needs a partial unique index with per-provider filter SQL |
| `Roles.*` description survey (Pass 35 §3.3) | unchanged. Eight descriptions on an installation-wide surface, all reading as tenant-scoped |
| Menu by role, page by permission | unchanged (Pass 35 §4.2). Cosmetic — every page checks its own permission |

---

## 11. Anomalies

**A1 — the freeze had a second direction nobody had looked for.** Pass 34 measured "constructed under
A, published under B → sees A". The inverse — **constructed under A, published under *nothing* → sees
A** — is the more dangerous one, because it hands a tenant to a path that legitimately has none:
background work, an HTTP endpoint, a hosted service. That is the direction that would have stamped a
business write with a tenant having nothing to do with it, and it is now pinned by
`AConstructingTenantDoesNotLeakIntoAnUnauthenticatedPublish`. Recorded because the original
measurement was correct and incomplete, and the incompleteness was in the direction that mattered
more.

**A2 — the defect was never only about logging, and three passes described it as if it were.** Pass
34, Pass 37 and this brief all framed it as "~25 log sites are labelled with the wrong tenant".
`PicklistSetChangedEventHandler` calls `RefreshAsync()` on a `PerTenant`-scoped cache whose key comes
from the ambient context, so a picklist edit evicted the wrong tenant's entry — or, when the
publisher was constructed with no principal, evicted nothing at all and left every picker stale until
a TTL expired. That is a user-visible bug that existed for the whole time the defect was catalogued
as a labelling issue. Recorded because the lesson is about how the catalogue entry was written:
"handlers see the wrong tenant" was filed under logging because logging was where it was *noticed*,
and nobody asked what else reads the ambient context.

**A3 — fail-closed was the wrong instinct here, and it took the picklist finding to see it.**
Suppressing `ExecutionContext` flow is the more obviously conservative fix and it is what this
template's posture would suggest. It would have made `EffectiveKey()` return null permanently and
stopped the picklist cache being evicted at all. The distinction worth keeping: fail-closed protects
when the alternative is *serving* something wrongly; when the alternative is *recording* something
wrongly, a record filed under nobody is not safer than one filed under the wrong name — Pass 29's
filter makes a null-tenant row invisible to every tenant principal, so it is lost rather than safe.

**A4 — MNEFleets' number reproduced to four significant figures, and its conclusion still did not
transfer.** Their naive singleton was 28,535 ms; mine 28,519 ms on the same shape. It would have been
easy to take the matching number as licence to take the matching fix. The reason it does not transfer
has nothing to do with performance: their handlers resolve their own scopes and these six do not.
Recorded because a benchmark agreeing that precisely is exactly when the *other* preconditions stop
being checked.
