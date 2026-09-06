# Pass 41 — ReconnectModal: The Reload That Destroys Work

**Nature:** investigation with a design gate, then implementation of what was ratified.
**Date:** 2026-09-06.

**Result in one line:** the defect is real, worse than the catalogue described, and fires on a path
the catalogue never named — **every login, every tenant switch, every `forceLoad`** — because the
reload was triggered not by a dead circuit but by *the server being reachable*, which it is by
definition when you are posting to it. Replaced with the framework's own semantics: a transient drop
is never acted on. **996 → 1004 tests**, warnings unchanged, Pass 30B's suites green and
byte-unmodified. The browser half of the symptom is on the hand-test list at §7, unproven here and
stated as such.

---

## 1. Start state

| | |
|---|---|
| HEAD | `a538e1b0` — *"Pass40-ClearTheBoard"* |
| Working tree | clean |
| Build | **0 errors, 19 warnings across 10 distinct source locations** (+ `NETSDK1206`) |
| Tests | 229 + 12 + 512 + 243 = **996 passed, 12 skipped, 0 failed** |

Matches Pass 40 §6.1 exactly. Probe work ran from `C:\gxp41`, per the brief's instruction and Pass
40 A3's finding.

---

## 2. §A.1 — What is actually here

### 2.1 The catalogue is right about the symptom and wrong about the mechanism

The catalogue says the component "reloads on any circuit drop, without distinguishing a retrying
connection from a terminally failed one". The first half is true. The second half describes a
component that decides on the CSS class. **This one does not decide on the class at all.**

What it actually did:

1. A `MutationObserver` fired if *any* of three classes appeared.
2. It then began probing `HEAD /_framework/blazor.web.js` every 2000 ms — **and probed once
   immediately**, so there was no grace period whatever.
3. **If the probe returned 200, it called `window.location.reload()`.**

So the trigger was `server.reachable === true`, not `circuit.dead === true`. The class only decided
*whether to start asking*. Every one of the seven states leads to the same place, and it gets there
in milliseconds.

That distinction is not pedantry — it is the whole reason the defect reaches paths the catalogue
never mentions. See §2.4.

### 2.2 .NET 10 does not emit three classes. It emits seven.

Extracted from the running application's own `blazor.web.js` (fetched at `/_framework/blazor.web.js`,
200 KB, read rather than looked up), the reconnect display class is:

| Class | Meaning |
|---|---|
| `components-reconnect-show` | connection down, reconnection beginning |
| `components-reconnect-retrying` | **added alongside** `show` from the second attempt |
| `components-reconnect-paused` | **.NET 9/10 pause-and-resume** — a *graceful* pause, resumable |
| `components-reconnect-hide` | **reconnected successfully** |
| `components-reconnect-failed` | retries exhausted |
| `components-reconnect-resume-failed` | a paused circuit could not be resumed |
| `components-reconnect-rejected` | the server refused the circuit; its state is gone |

Plus a `components-reconnect-state-changed` `CustomEvent` carrying the state in `detail` — a
supported API the component ignored entirely.

`paused` and `resume-failed` are the sleeping-tab case, which is one of the two symptoms the
catalogue reports. **The component had never heard of them.** The catalogue's three-class model is
the .NET 8 shape and was inherited without being re-checked, which is exactly what the brief warned
against.

### 2.3 Blazor's own default is *less* aggressive than the template's

Also read off `blazor.web.js`, the framework's fallback display (used when no dialog element is
found):

```js
rejected() { location.reload() }
failed()   { …"Failed to rejoin.<br />Please retry or reload the page." + a Retry button… ;
             this.document.addEventListener("visibilitychange", this.retryWhenDocumentBecomesVisible) }
```

**The framework auto-reloads on `rejected` and nowhere else.** On `failed` it offers a button and
retries when the tab becomes visible again. The template had replaced a conservative default with an
aggressive one, which is worth stating plainly: this was not a gap the framework left open.

### 2.4 There is no unload guard, and that is where login comes in

`blazor.web.js`, the circuit's `onclose` handler:

```js
n.onclose(e => { …; this._disposed || this._renderingFailed || t
                     || this._options.reconnectionHandler.onConnectionDown(…) })
```

Guards: disposed, rendering-failed, already-pausing. **Nothing about page unload.** So a circuit torn
down by a *deliberate navigation* announces a disconnection exactly like a network failure.

Every one of these paths therefore raised the modal and, under the old script, a racing reload:

| Path | |
|---|---|
| `auth.js` `f.submit()` | **the login POST** — §2.5 |
| `TenantSelector.razor:193` | `NavigateTo("/", true)` — the tenant switch |
| `RedirectToLogin.razor` ×2 | unauthenticated redirect |
| `ErrorPageComponent.razor:148` | "retry this page" |
| `ChangePassword` ×2, `LoginWith2fa`, `LoginWithRecoveryCode`, `LinkExternalLogin` | identity flows |
| `gxIdleTimeout.js` ×2 | `location.assign` on idle sign-out |

### 2.5 The login page is a full-page form POST — more so than the catalogue said

The catalogue's Firefox claim rests on a precondition the brief rightly asked me to re-check, since
Passes 4B-H, 17 and 22 all touched that path. **It holds, and in a sharper form.**

`Login.razor` is an `EditForm` with `OnValidSubmit`, running over the circuit. It validates the
credentials in C#, and then hands off to JavaScript:

```csharp
await _auth.InvokeVoidAsync("postLogin", new { userName, password, rememberMe, returnUrl });
```

`wwwroot/js/auth.js` builds a hidden `<form method="POST" action="/pages/authentication/login">`,
appends the antiforgery token, and calls `f.submit()`. Confirmed live: the endpoint routes (POST
returns 500 for a tokenless request, not 404), and `curl` of `/account/login` shows the reconnect
dialog and its script present on the anonymous login page.

So: **the login submit is itself the navigation that kills the circuit**, and the server it is
posting to is trivially reachable. The old script's probe returned 200 on its first, immediate try
and reloaded. **This fired on every single successful login attempt, not on a rare network fault.**

### 2.6 `wwwroot/js/boot.js` was a second, dead reconnection implementation

93 lines implementing `Blazor.start({ reconnectionHandler: … })` — the documented approach — with a
retry loop and its own reload rules. **Zero references anywhere in the repository**, and it addresses
`document.getElementById('reconnect-modal')`, an id that does not exist (the dialog is
`components-reconnect-modal`), so it would have thrown on its first line had it ever run. It also
calls `Blazor.start()`, which would throw because `App.razor` loads `blazor.web.js` without
`autostart="false"`.

Deleted — see §5.3 for why that is in scope rather than scope creep.

---

## 3. §A.2 — Reproduced

### 3.1 What was run

No browser automation is installed here and installing browser binaries is not a probe I will run
unasked, so I reproduced the thing that is actually in question: **what the component decides.** The
script was extracted verbatim from `ReconnectModal.razor` at HEAD and executed in Node against a DOM
shim, driven through the state sequences §2.2 establishes that .NET 10 really emits.

### 3.2 Result — it reloads in every state

```
RELOADED  | 1. transient drop, Blazor reconnects successfully (server up throughout)
            log: SHOW_MODAL, RELOAD, CLOSE_MODAL
RELOADED  | 2. graceful pause (components-reconnect-paused), resumable
RELOADED  | 3. deliberate navigation tears down the circuit (login POST / forceLoad)
            log: SHOW_MODAL, RELOAD
RELOADED  | 4. terminal failure (components-reconnect-failed)
RELOADED  | 5. rejected (components-reconnect-rejected)

6. server genuinely down, then recovers
            while down: SHOW_MODAL
            Blazor then RECONNECTS successfully: SHOW_MODAL
            => modal.open = true
```

Case 1 is the data-loss defect, and note *where* the reload sits in the log: **before** `retrying`
was ever reached. The page reloads while Blazor's first retry is still in flight — a retry that, in
the overwhelmingly common case, would have succeeded and preserved everything.

Case 3 is the login race.

**Case 6 is a defect nobody had named.** Once a probe failed, `serverReallyDown` latched, and a
subsequent *successful reconnection* was explicitly discarded:

```js
// If previously confirmed server was down, absolutely do not allow closing…
console.log('[Reconnect] Ignored fake reconnection signal. Waiting for reload.');
if (!modal.open) modal.showModal();
```

Blazor had restored the circuit **with all state intact**; the component overrode it, held the user
behind a modal on a working page, and waited to reload and destroy their work anyway.

### 3.3 What could not be reproduced, stated plainly

**I could not drive a browser here.** No Playwright, no Puppeteer. So:

- **The Firefox login failure is unproven either way by this pass.** What is proven is that a reload
  is *issued*, concurrently with a committed form-submit navigation. Whether it wins that race is
  browser behaviour — and a difference in exactly that behaviour is what the catalogue's
  "Chromium-invisible" claim describes. **The absence of a Chromium reproduction proves nothing**,
  which is why I did not attempt one and call it evidence.
- Everything about *what the code decides* is proven, because that is what was executed.

Both symptoms are on the hand-test list at §7.

---

## 4. §A.3 — The tenant switch

Pass 30 §8.2 made the forced reload load-bearing for presence isolation: `ServerHub` groups by
connection id, so only a circuit teardown moves a switched user out of their old tenant's presence
group.

**The interaction exists, and it resolves in the reassuring direction.**

- `SwitchToTenantAsync` completes *before* `NavigateTo("/", true)`, so no server-side work is at
  risk from a racing reload.
- A `location.reload()` destroys the circuit **just as thoroughly** as the forced navigation does.
  Presence isolation cannot be broken by either outcome of the race. The only cost was landing on the
  current URL instead of `/`.
- Therefore **suppressing the modal's reload cannot weaken Pass 30's property**, because the property
  is carried by `NavigateTo(…, true)`, which this pass does not touch.

The brief asked whether a modal that *suppresses* a reload it should not suppress is as much a defect
as one that reloads wrongly. It would be — and it is not the case here, because the reload being
suppressed was never the one doing the work. `ServerHubTenantIsolationTests` and
`TenantSelectorComponentTests` are byte-unmodified and green (§6.3).

---

## 5. §B — The implementation

### 5.1 What was ratified

Framework semantics, and the Jint test approach. Both chosen at the gate.

| State | Action |
|---|---|
| `show`, `retrying` | dialog + "Reconnecting…". **No probe, no reload, no navigation.** |
| `paused` | dialog + "Session paused" + a **Resume** button |
| `hide` | close the dialog. Everything the user had is still there |
| `failed`, `resume-failed` | **Retry** + **Reload** buttons; auto-retry when the tab becomes visible; start the server probe |
| `rejected` | reload — matching Blazor's own default, and only here |

**The login race disappears without any unload detection**, which is the part of this design worth
keeping in mind. A page that is navigating away only ever reaches the first transient state before
the document is gone; if nothing acts on transient states, nothing races the navigation. No
`beforeunload` hook, no flag threaded through eight `forceLoad` call sites, no ordering assumption
about when a socket closes relative to unload.

### 5.2 The one place I departed from what I proposed at the gate, and why

The gate preview said `failed` → buttons **+ probe: auto-reload when the server comes back**. Taken
literally that is wrong, and it would have half-rebuilt the defect: in the common `failed` case the
*client's* network died and the server was reachable all along, so the probe returns 200 at once and
reloads — destroying the values the same design promises are "still on screen".

Implemented so that **the probe reloads only if the server was observed DOWN and has since come
back**:

```js
if (!ok) { sawServerDown = true; …; return; }

// THE CONDITION THAT MAKES THIS SAFE. …A server that was reachable all along means the failure was
// on this end, and reloading then would destroy what is on screen to fix nothing that Retry does
// not fix better.
if (sawServerDown) { doReload(); }
```

That is a faithful reading of *"when the server comes back"* — it requires a coming-back — and it is
the deployment case the original script was written for and got right in intent. The old script had
the identical `serverReallyDown` latch and used it to override successful reconnections; the same
fact is now used for the one thing it is evidence of. Flagged here rather than quietly implemented.

### 5.3 The logic moved out of the component

`ReconnectModal.razor`'s inline `<script>` could not be executed by any test in this repository:
bUnit renders markup and never runs script, and the logic cannot move to C# because it must keep
working while the circuit — and therefore all C# — is gone. The decisions now live in
`wwwroot/js/gxReconnect.js`, following `gxIdleTimeout.js`'s established shape; the markup and CSS
stay in the component, which gains three buttons hidden until a terminal state needs them.

`boot.js` was deleted. That is squarely inside §A.1.4's question — *"whether anything else in the
template reloads on a circuit event"* — and it is a second reconnection implementation with its own
reload rules sitting one `<script>` tag away from being live. Leaving it would leave the next reader
a working-looking alternative to the file this pass just made correct.

### 5.4 A hardening the test surfaced

Routing every reload through a `doReload()` latch, so probes already in flight when one decides to
reload cannot decide again. A second reload is at best wasted and at worst another race of exactly
the kind this file exists to stop. Found because the Jint test asserted `reloads == 1` and got 2.

---

## 6. §C — Verification

### 6.1 The reproduction, no longer reproducing

The same harness, the same scenarios, against the fixed file:

| Scenario | Before | After |
|---|---|---|
| 1. transient drop, Blazor reconnects | **RELOADED** | **no reload**, modal closed |
| 2. graceful pause | **RELOADED** | **no reload**, Resume offered |
| 3. deliberate navigation (login POST / `forceLoad`) | **RELOADED** | **no reload** |
| 4. terminal failure, server reachable throughout | RELOADED | **no reload**, Retry + Reload offered |
| 5. rejected | RELOADED | RELOADED — intended, matches Blazor |
| 6. server down, then Blazor reconnects | **modal stuck open forever** | **no reload**, modal closed |
| 7. server down and back, unattended | — | **RELOADED** — the deployment case still works |

### 6.2 The automated test

`ReconnectUiTests` — 8 tests, running the **real** `gxReconnect.js`, linked into the test project by
relative path rather than copied, so the suite cannot drift onto a stale duplicate. Jint 4.16.1: pure
C#, no Node, no browser, no native dependency; it restores and passes in a generated project too.

Each test sets **the CSS class Blazor itself sets** and then asserts what happened to the page. No
test asserts which class a branch reads — the distinction Pass 39 §B.2 drew.

| Test | Property |
|---|---|
| `ATransientDrop_DoesNotReloadThePage` | the defect: a recovered drop leaves the page untouched |
| `ADeliberateNavigation_DoesNotReloadThePage` | the login race |
| `AGracefulPause_OffersResume_AndDoesNotReload` | the .NET 9/10 state the old script never knew |
| `ASuccessfulReconnection_ClosesTheModal_EvenAfterTheServerWasSeenDown` | §3.2 case 6 |
| `ATerminalFailure_OffersButtons_AndDoesNotReloadWhileTheServerWasNeverDown` | §5.2's condition |
| `ATerminalFailure_ReloadsOnceTheServerHasGoneAndComeBack` | narrowed, not broken |
| `ARejectedCircuit_ReloadsImmediately` | matches Blazor's own rule |
| `AGenuinelyDeadCircuit_StillRecovers` | §C.3 — recovery both automatic and manual |

**§C.3 deserves its own note.** A fix that never reloads satisfies every "my work was not destroyed"
assertion perfectly and leaves the user staring at a dead page. `AGenuinelyDeadCircuit_StillRecovers`
is the counter-test: unattended recovery still happens when the server returns, and a Reload button
is present for a user who will not wait.

### 6.3 Boundary suites

`ServerHubTenantIsolationTests`, `TenantSelectorComponentTests`, `IdleTimeoutDialogComponentTests`,
`SystemMenuGateComponentTests`, `FileEndpointsAuthorizationTests` — all **byte-unmodified**
(`git diff --quiet`). The first three run as a filtered set: **28 passed, 0 failed.**

`git status` shows two modifications, one deletion and two additions, and no other test file touched.

### 6.4 Runtime smoke check

The application was started against a scratch SQLite database and the served page inspected:

```
/account/login                        → 200
  <script src="js/gxReconnect.dauis8nj9h.js"></script>   (fingerprinted through @Assets)
/js/gxReconnect.dauis8nj9h.js         → 200
/js/boot.js                           → 302  (gone; falls through to auth)
  id="reconnect-retry" / "reconnect-resume" / "reconnect-reload"  all present
```

### 6.5 Counts

| | Before | After | Delta |
|---|---|---|---|
| `Infrastructure.UnitTests` | 229 | 229 | — |
| `Application.IntegrationTests` | 12 | 12 | — |
| `Application.UnitTests` | 512 (+12 skipped) | 512 (+12 skipped) | — |
| `Server.UI.IntegrationTests` | 243 | **251** | **+8** |
| **Total** | **996 passed, 12 skipped** | **1004 passed, 12 skipped** | **+8, 0 failed** |

The +8 is exactly `ReconnectUiTests`. No pre-existing test changed count or outcome.

**Warnings: unchanged.** 19 across the same 10 distinct source locations plus `NETSDK1206`, measured
on a clean build of the generated project. Jint introduced none.

### 6.6 Generation probe

From `C:\gxp41\gen`, a short path, per the brief and Pass 40 A3. Uninstalled before installing —
the registry held zero registrations, checked rather than assumed:

```
dotnet new uninstall (0 found) → dotnet pack build/pack.csproj → dotnet new install
  → dotnet new gxblazor -n P41
  → wwwroot/js/gxReconnect.js present; boot.js ABSENT
  → the test project links the real file (2 references in the csproj)
  → build: 0 Error(s), 19 Warning(s)
  → dotnet test: 229 + 12 + 512 + 251 = 1004 passed, 12 skipped, 0 failed
  → dotnet new uninstall; probe directory removed
```

---

## 7. Hand-test list — **Yoab's to run**

Nothing below is covered by any test in this repository, and no assertion above should be read as
implying it is. Each needs a browser and a real circuit.

1. **Log in with Firefox.** The reported symptom. Expect: sign-in completes. Previously the login
   POST raced a reload issued by the modal.
2. **Log in with Chromium**, to confirm nothing regressed on the browser where the defect was
   reportedly invisible.
3. **Mid-form transient drop.** Open any form (a picklist or document create dialog), type into it
   without saving, then kill the network — disable the adapter, or stop the server for ~3 seconds and
   restart it fast enough that Blazor's retries succeed. **Expect: the dialog appears, then
   disappears, and your text is still there.** This is the defect.
4. **Laptop lid / tab sleep.** Leave a form open, sleep the machine or leave the tab backgrounded for
   several minutes, return. Expect: reconnection or a Resume button, not a reload.
5. **Genuine server death.** Stop the server and leave it stopped. Expect: "Could not rejoin the
   server" with Retry and Reload buttons, and your typed values still visible. Then start the server:
   expect the page to come back on its own.
6. **Tenant switch**, on a multi-tenant installation with two browsers signed in as different
   tenants. Expect: the switch still lands on `/`, and presence still shows only the new tenant's
   users. This is Pass 30's property and the one thing §B was told not to break.
7. **Idle sign-out** (`gxIdleTimeout.js`): expect the redirect to the login page, with no reconnect
   dialog flashing over it.

---

## 8. File map, diffstat and edit fidelity

### 8.1 File map

**Modified (2):**

| File | |
|---|---|
| `src/Server.UI/Components/Feedback/ReconnectModal.razor` | inline script replaced by a `<script src>`; three terminal-state buttons and their CSS added; markup and existing CSS otherwise unchanged |
| `tests/Server.UI.IntegrationTests/Server.UI.IntegrationTests.csproj` | Jint 4.16.1, and the linked `gxReconnect.js` content item |

**New (2):**

| File | |
|---|---|
| `src/Server.UI/wwwroot/js/gxReconnect.js` | the reconnect state machine, with the defect it replaces written on it |
| `tests/Server.UI.IntegrationTests/ReconnectUiTests.cs` | 8 behavioural tests over the real module |

**Deleted (1):**

| File | |
|---|---|
| `src/Server.UI/wwwroot/js/boot.js` | dead second reconnection implementation, zero references, targeting an element id that does not exist |

### 8.2 Diffstat

Tracked changes:

```
 src/Server.UI/Components/Feedback/ReconnectModal.razor             | 151 +++++-----------
 src/Server.UI/wwwroot/js/boot.js                                   |  59 -------
 tests/Server.UI.IntegrationTests/Server.UI.IntegrationTests.csproj |  13 ++
 3 files changed, 64 insertions(+), 159 deletions(-)
```

Plus two untracked files: `gxReconnect.js` (322 lines) and `ReconnectUiTests.cs` (312 lines).

**The component shrank.** 151 lines changed on a file that lost its 94-line script and gained 30
lines of button CSS and 12 lines of markup — the decisions did not grow, they moved somewhere they
can be run.

### 8.3 Edit fidelity

- **No git actions.** Only `status`, `log`, `diff` and `grep`.
- **The tenant-switch reload was not touched.** `TenantSelector.razor` is unmodified;
  `ServerHubTenantIsolationTests` and `TenantSelectorComponentTests` are byte-unmodified and green.
- **The dialog's markup and CSS are otherwise unchanged** — same id, same `data-nosnippet`, same
  glass panel, same animations. Only the three buttons and their styles are new.
- **`auth.js` was not touched.** The login POST is the *victim* here, not the defect; changing it
  would have been a second body of work.
- **No new dependency outside the test project.** Jint is a test-only package reference; the shipped
  application gained one JavaScript file and lost another.
- The gate was answered before any implementation began, and §5.2 records the one place the
  implementation departs from what I proposed there.

---

## 9. Scratch probe disclosure

All under `C:\gxp41`, removed:

1. **A scratch SQLite installation** (`app.db`, `logs.db`) and the application run against it, used
   to fetch `blazor.web.js` from the framework and for §6.4's smoke check. A generated administrator
   password was printed to that log; the database and log are deleted and the account never existed
   anywhere else.
2. **`blazor.web.js`**, fetched from the running app — the source for §2.2, §2.3 and §2.4. Read, not
   modified.
3. **`harness.js` / `harness-after.js`** — the §3 and §6.1 reproductions. Copies kept in the session
   scratchpad for this report; not added to the repository, because `ReconnectUiTests` supersedes
   them.
4. **A Jint restore probe**, to confirm the package was actually obtainable before offering it as a
   gate option.
5. **The generation probe** at `C:\gxp41\gen` — packed nupkg, installed template, generated `P41`,
   template uninstalled.

No external database was touched this pass. The nupkg in the repository root is a gitignored build
artifact.

---

## 10. What remains

| Item | Status |
|---|---|
| **`ReconnectModal` reloading on transient drops** | **closed.** Transient states are never acted on; 8 tests pin it |
| **The Firefox login failure** | **mechanism removed; symptom unverified here.** The racing reload is no longer issued. Hand test 1 |
| **`boot.js`** | **deleted.** Dead second implementation found by §A.1.4 |
| **The integration harness pinned to LocalDB** | open. `tests/Application.IntegrationTests` ignores `--Database`, so nine tests fail rather than skip without LocalDB |
| **The Respawn question** | open. Whether database reset between integration tests should move to Respawn, and what that costs on three providers |
| **The circularity sweep** | open. Tests deriving their expectation from the thing they check — Pass 33 met it twice, Pass 38 A1 in another form |
| **Picklist duplicate reporting** (Pass 40 §5.6) | open. `AddEditPicklistSetCommand` has no duplicate check, so duplicates surface as `DbUpdateException` on both partitions |
| **The upstream contribution** | open. Which of these forty-one passes' findings belong back in `CleanArchitectureWithBlazorServer` |

---

## 11. Anomalies

**A1 — the catalogue described the symptom correctly and the mechanism wrongly, and the mechanism
was where the scope was.** "Reloads on any circuit drop, without distinguishing retrying from
failed" implies a component that branches on state and branches badly; the fix that description
suggests is "add the missing branch". The actual trigger was `probe returned 200`, which is true
whenever the server is up — so the defect reached **every deliberate navigation in the application**,
including the login POST, the tenant switch and six identity flows, none of which the catalogue
mentions. Recorded because acting on the described mechanism would have produced a component that
still reloaded during login: the class-based fix alone does not remove a probe that ignores classes.
**A defect report's symptom is evidence; its mechanism is a hypothesis, and only one of those
survives contact with the artifact.**

**A2 — the framework had already solved this, more conservatively, and the template overrode it.**
Blazor's default reconnection display auto-reloads on `rejected` and offers a Retry button on
`failed`. The template replaced that with something strictly more destructive. It is worth naming the
shape: **a customised version of a framework affordance is a place to check what the default did
before assuming the default was inadequate.** Pass 41's ratified design is, in substance, the
framework's own behaviour with a deployment-restart probe added — which is the *one* thing the
original author was genuinely adding value on, and the one thing every rewrite of this component
would have been at risk of dropping.

**A3 — the component was untestable, and that is why a defect this large survived this long.** An
inline `<script>` in a `.razor` file is invisible to bUnit, to C#, and to every tool in this
repository. The code had shipped through forty passes of a programme that reads everything, because
there was no artifact to run and nothing that failed. Pass 19 §B.6 triaged this as "likely present"
and never verified it — not from carelessness, but because verifying it required either a browser or
a JS engine, and neither was to hand. **The eight tests added here cost one test-only package
reference; the defect cost a login flow in one browser and unsaved work in all of them.** The
general form: *a file that no test can execute is not low-risk because nothing has gone wrong in
it — it is unmeasured, and the two look identical from the outside.*

**A4 — the state I was most confident about was the one I got wrong at the gate.** The design I
proposed said `failed` → buttons plus "auto-reload when the server comes back". Writing the test for
it exposed that in the ordinary `failed` case the server never went anywhere — the *client's* network
did — so the probe returns 200 immediately and reloads, destroying the values the same design
promises are preserved. The fix is one condition (§5.2). Recorded because the error was in the half
of the design I had already argued for in the gate, and it took an executable assertion, not a
re-reading, to find it.
