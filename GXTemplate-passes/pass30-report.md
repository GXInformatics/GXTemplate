# Pass 30 — Presence and Chat: Isolation Over SignalR

**Nature:** investigation with a design gate at §A, then implementation. **The gate was presented,
ratified in full as recommended, and built.** **No git actions by me** — see the precondition note.
**Dates:** §A 2026-09-03; re-verification, ratification and §B–§D 2026-09-04.

**Result in one line:** four of the six `Clients.All` broadcasts deleted with the dead features that
owned them, the surviving two bounded by per-tenant SignalR groups, the cross-tenant user directory
that `GetOnlineUsers` returned bounded separately, the roster given the permission gate it never had,
and the undeclared `forceLoad` dependency pinned. **838 → 859 tests**, warnings unchanged.

---

## 1. Start state

| | |
|---|---|
| HEAD | `a05570a3` — *"Pass29-QueryFilter"* |
| Working tree | **clean** |
| Build | 0 errors |
| Warning locations | **10 distinct** |
| Tests | **838 passed, 12 skipped, 0 failed** |
| Spot-check `QueryFilters.Tenant` | present (`QueryFilters.cs:31`) |
| Spot-check `AuditTrailTenantScope` | present |

**Re-verified 2026-09-04**, on a fresh session before presenting the gate: HEAD unchanged at
`a05570a3`; tree clean apart from this report; `dotnet build --no-incremental` succeeds with
**19 warnings across 10 distinct source locations** (plus `NETSDK1206`, which is emitted once per
project from the SDK targets and has no source location — it is the eleventh raw line and is not one
of the ten); `dotnet test` gives **217 + 12 + 429 + 180 = 838 passed, 12 skipped, 0 failed**. Every
§A claim below was re-established by reading source in this session, not carried over. Two wording
corrections were made in the process, noted at §15 A6.

**Precondition note.** The tree was dirty again: Pass 29's work was uncommitted, because "No git
actions" has been in force every pass. I stopped; you authorised the commit explicitly and it was
made as **`Pass29-QueryFilter`**, distinct from the pre-existing `Pass29` commit that contains Pass
28's §A work. Both now sit in the history with names that say what they hold.

---

## 2. §A.1 — What is actually broadcast

Pass 23 §3.6 is re-confirmed and **undercounts the surface**. Its claim:

> *"`ServerHub` broadcasts to `Clients.All` at six sites… a tenant administrator with
> `ViewOnlineStatus` watches another tenant's users sign in, sign out and navigate."*

The six sites are real and correctly listed. But §3.6 catalogued the *mechanism*, not the *payload*,
and the brief is right that severity is a function of payload. The worst disclosure in this file is
**not a broadcast at all**.

### The catalogue

| # | Site | Trigger | Recipients | What a recipient learns |
|---|---|---|---|---|
| 1 | `ServerHub.cs:35` `Clients.All.Connect` | any user's first connection | **every connected client** | a username signed in, + connection id |
| 2 | `ServerHub.cs:52` `Clients.All.Disconnect` | last connection closes | **every connected client** | a username signed out |
| 3 | `ServerHub.cs:61` `Clients.All.SendMessage` | client calls `SendMessage` | **every connected client** | sender's username + arbitrary text |
| 4 | `ServerHub.cs:67` `Clients.User(to)` | client calls `SendPrivateMessage` | the named user | **`to` is client-supplied and unchecked** — cross-tenant direct messaging |
| 5 | `ServerHub.cs:72` `Clients.All.SendNotification` | client calls `SendNotification` | **every connected client** | arbitrary client-supplied text, no sender attribution |
| 6 | `ServerHub.cs:85` `Clients.Caller.PageComponentOpened` | opening a tracked component | the caller | **who else is on that page**, across tenants |
| 7 | `ServerHub.cs:89` `Clients.All.PageComponentOpened` | same | **every connected client** | user id, username, which page |
| 8 | `ServerHub.cs:106` `Clients.All.PageComponentClosed` | closing | **every connected client** | user id, username, which page |
| 9 | **`ServerHub.cs:109` `GetOnlineUsers()`** | **any client, on demand** | **the caller** | see below |

**Site 9 is the finding.** `GetOnlineUsers` is not a broadcast, so a `Clients.All` census misses it.
It is a **client-invocable hub method** returning `List<UserContext>` built from the process-wide
`OnlineUsers` dictionary, and for **every online user in the installation** it returns:

```csharp
UserId, UserName, DisplayName,
TenantId,                    // the tenant map itself
Email,                       // PII
ProfilePictureDataUrl,
SuperiorId                   // reporting structure
```

That is a cross-tenant user directory with email addresses and org structure, available to **any
authenticated connection**. The hub carries `[Authorize(AuthenticationSchemes = "Identity.Application")]`
and nothing more — authentication, not authorisation. Because it is invocable directly over the
WebSocket, **no UI gate can constrain it**: whatever the components choose to render is irrelevant to
what a client can ask for and receive.

Sites 1 and 2 leak a username. Site 9 leaks a directory. They are not the same defect and should not
get the same remedy.

### Two features are dead, and they account for four of the six broadcast sites

**Chat is dead** (sites 3, 4, 5). `SendMessage`, `SendPrivateMessage` and `SendNotification` have no
caller outside the hub's own plumbing; on the client, `MessageReceivedEvent` and
`NotificationReceivedEvent` have **zero** subscribers and `HubClient.SendAsync` /
`NotifyAsync` — the client-side senders for chat and notification — have **zero** external
references. Nothing in the application can reach any
of it. The AI chatbot Pass 7-2 removed was the last thing that might have.

**Page-component presence is dead too** (sites 6, 7, 8) — this one the brief did not anticipate.
`ActiveUserSession.razor` is the only consumer of `NotifyPageComponentOpen`/`Close`, and
**`ActiveUserSession` is never rendered anywhere**: the only references to its name are inside the
file itself. So the `ComponentUsers` dictionary, both `PageComponent*` broadcasts, both client
events, and the component are all unreachable.

**What is live** is exactly: sites 1, 2 and 9, consumed by `UserLoginState.razor` (the sign-in
toasts) and `OnlineUsersTracker.razor` (the avatar roster).

Per the brief — *"If chat is dead code, say so — deleting it is a better fix than isolating it"* —
I say so, and extend it: **deleting the dead surface removes four of the six broadcast sites, the
unchecked cross-tenant direct-message channel, and an unauthenticated-text-to-every-client
notification vector, with no isolation work at all.**

---

## 3. §A.2 — What the hub knows, and when

### 3.1 The tenant IS knowable at connect time — the load-bearing answer, and it is positive

`UserContextHubFilter` (registered at `Server.UI/DependencyInjection.cs:92`) implements
`IHubFilter.OnConnectedAsync`, and its body runs **before** `await next(context)` — that is, before
`ServerHub.OnConnectedAsync`. It:

1. reads `context.Context.User`;
2. if authenticated, resolves `IUserContextLoader.LoadAsync(principal)`;
3. stores the resulting `UserContext` in `context.Context.Items["__user_ctx"]`.

So by the time the hub's `OnConnectedAsync` runs, an authoritative `UserContext` — **including
`TenantId`** — is already on the connection. Pass 23 §3.6 guessed the hook point was there; it is,
and it is better than it needed to be.

**Three properties matter, and all three are favourable:**

- **It is server-resolved, not client-supplied.** `IUserContextLoader` reads the database (through
  its cache); nothing the client sends contributes. §B.1's constraint is satisfiable without effort.
- **It is not the cookie's stale claim.** `ApplicationUserClaimsPrincipalFactory` does **not** add a
  tenant claim; the only writer of `ApplicationClaimTypes.TenantId` is
  `TenantSwitchService.RefreshUserClaimsAsync`, which writes to `AspNetUserClaims` — a store the
  *current* cookie was already minted from, so the principal in `Context.User` can carry a tenant
  that is out of date, or none at all for any user who has never switched. **Had the hub keyed groups
  off `Context.User`, it would have been wrong for most users and silently stale for the rest.**
  The filter's DB-backed load is the correct source and the only correct source.
- **`IUserContextAccessor.Current` is NOT available in `OnConnectedAsync`.** The filter pushes the
  ambient `AsyncLocal` only in `InvokeMethodAsync` — i.e. for hub *method* invocations. The lifetime
  callbacks set `Context.Items` and nothing else. So the answer to the brief's question — *does the
  Pass 24 §A.3 finding that the accessor is readable outside an HTTP context hold inside a hub
  method?* — is: **yes for hub methods, no for the connect/disconnect callbacks.** `Context.Items` is
  the source that works in both.

`Items["__user_ctx"]` is currently keyed by a `private const` inside the filter, so the hub cannot
read it without duplicating the literal. That is an implementation detail to fix in §B, not an
obstacle.

### 3.2 Reconnect

`HubConnectionFactory` configures `.WithAutomaticReconnect()` (`IHubConnectionFactory.cs:69`).
**SignalR group membership does not survive a reconnect** — a reconnect is a new connection with a
new `ConnectionId`, and groups are keyed by connection. Re-adding in `OnConnectedAsync` is therefore
both necessary and sufficient, and it is the standard pattern rather than a workaround.

The filter's `OnConnectedAsync` runs on every reconnect too, so the `UserContext` is re-resolved
rather than carried over — which also means a reconnect picks up a tenant change for free.

Nothing caches connection state that would go stale: `OnlineUsers` is keyed by `ConnectionId` and the
old entry is removed by `OnDisconnectedAsync`. **`ReconnectModal` does not interact** — it is a pure
UI affordance over the Blazor circuit's own reconnection, with no reference to `HubClient`,
`ServerHub` or presence.

### 3.3 The tenant switch — covered, but only incidentally

`TenantSelector.razor:183` calls `Navigation.NavigateTo("/", true)` — **`forceLoad: true`**. That is a
full browser navigation, which destroys the Blazor circuit; `HubClient` is **scoped** (circuit-
scoped), so it is disposed and a new hub connection is established. `OnConnectedAsync` runs again,
the filter re-resolves the `UserContext`, and — because `SwitchToTenantAsync` calls
`_userContextLoader.ClearUserContextCache(userId)` **before** the component navigates — the resolve
reads the new tenant rather than a cached old one.

So the case the brief calls "most likely to be missed" is, in fact, already handled. **But it is
handled by accident, not by design.** Nothing anywhere states that presence isolation depends on that
`true`. A future change to a soft navigation — an obvious-looking improvement, since a forced reload
is a visible flicker — would silently leave the connection in the previous tenant's group with no
test failing and no comment to warn anyone. The dependency needs to be made explicit and pinned.

### 3.4 Connections with no tenant

`UserContext.TenantId` is nullable. Seeding assigns the bootstrap administrator a tenant
(`ApplicationDbContextInitializer.cs:244`, `TenantId = tenant.Id`), so the shipped state contains no
tenantless user — but a user created
without one, or with a null column, is representable. An unauthenticated connection cannot occur: the
hub's `[Authorize]` attribute stops it before `OnConnectedAsync`.

---

## 4. §A.3 — Mechanism

**Recommended: groups per tenant, joined in `OnConnectedAsync` from `Context.Items["__user_ctx"]`,
for the two live broadcasts — plus server-side scoping of `GetOnlineUsers`, which groups alone do not
fix.**

Filtering at the send site was considered and rejected: the send sites are `OnConnectedAsync` and
`OnDisconnectedAsync`, where there is no recipient list to filter — the sender *is* the event. Groups
are the primitive that fits.

**Groups do not fix site 9.** `GetOnlineUsers` reads the process-wide `OnlineUsers` dictionary
directly and returns a projection; no group membership constrains a method's return value. It needs
its own bound, applied server-side, from the connection's own `UserContext`. Two halves, one design.

**The no-tenant connection** joins a sentinel group of its own, so tenantless users see each other and
nobody else. That mirrors Pass 29's decision that null is a real value meaning "installation-level",
rather than either "sees everyone" or "sees nobody".

### The escape: I recommend there is none

The brief invites a recommendation that presence have **no** cross-tenant escape, and that is my
recommendation.

- **`Users.ViewOnlineStatus` is not a cross-tenant right and must not become one.** Its description is
  *"Allows viewing users' online status"* — it distinguishes people who may see presence from people
  who may not; it says nothing about whose.
- **There is no administrative task that requires it.** Cross-tenant *data* rights earned their place
  because an administrator must configure, audit and support across tenants. Watching another
  tenant's staff sign in and out in real time supports no task — it is ambient observation of people,
  which is a different kind of capability from reading their records. Pass 27 already drew this line
  for a different reason: *"seeing across tenants and acting across them are different capabilities."*
  This is a third category — seeing across tenants *continuously*, without a request or a record.
- **It would be unauditable.** Every other cross-tenant right this programme has added gates a query
  whose use leaves a trace. A presence feed leaves none.

If a cross-tenant operator genuinely needs to know whether someone is online, that is a support
question answered by asking, not by a permanent surveillance channel. **If you disagree, the
extension point is one line** — but it should be a decision taken deliberately, not inherited.

---

## 5. §A.4 — What the UI does with it, and the gap that matters

| Component | Rendered at | Consumes | Gate |
|---|---|---|---|
| `UserLoginState.razor` | `AppLayout.razor:42` | `LoginEvent` / `LogoutEvent` | **`Permissions.Users.ViewOnlineStatus`** |
| `OnlineUsersTracker.razor` | `ThemesMenu.razor:158` | `GetOnlineUsers()` + both events | **none** |
| `ActiveUserSession.razor` | **nowhere** | page-component events | n/a — dead |

**The roster is ungated and the toast is not.** `UserLoginState` checks `ViewOnlineStatus` before
showing a sign-in snackbar. `OnlineUsersTracker` — which renders the actual list of who is online,
with avatars and usernames in `title` attributes — sits inside the **theme picker drawer** and checks
nothing. Every authenticated user can open the theme menu and see the roster.

So the brief's question — *is that permission's holders the population the isolation protects, or the
population it protects from?* — has an uncomfortable answer: **neither, because the permission is not
on the surface that matters.** The population seeing the cross-tenant roster today is *every
authenticated user*, and `ViewOnlineStatus` is not what stands between them and it.

Pass 23 §3.6 says *"a tenant administrator with `ViewOnlineStatus` watches another tenant's users sign
in"*. Re-confirmed and **corrected**: any authenticated user does, and they do not need the
permission, because the roster does not check it.

**Recommendation:** gate `OnlineUsersTracker` on `Users.ViewOnlineStatus`, in the same change. The
permission exists, its description is exactly this surface, and one of its two consumers already
honours it. This is not scope creep — a tenant bound on a surface that should not be visible at all to
most of its current audience is half a fix.

---

## 6. What I recommend you ratify

1. **Delete the dead surface**: chat (`SendMessage`, `SendPrivateMessage`, `SendNotification` and
   their client events and methods) and page-component presence (`NotifyPageComponentOpen`/`Close`,
   both `PageComponent*` broadcasts, `ComponentUsers`, and `ActiveUserSession.razor`). Four of six
   `Clients.All` sites, the unchecked cross-tenant DM channel, and the arbitrary-text broadcast
   vector all go with it.
2. **Groups per tenant** for `Connect`/`Disconnect`, joined in `OnConnectedAsync` from
   `Context.Items["__user_ctx"]` — server-resolved, never from `Context.User`'s claims. Tenantless
   connections join a sentinel group.
3. **Scope `GetOnlineUsers` server-side** to the caller's tenant. Groups do not reach it, and it is
   the most severe disclosure in the file.
4. **No cross-tenant escape for presence** (§4).
5. **Gate `OnlineUsersTracker` on `Users.ViewOnlineStatus`** (§5).
6. **Pin the tenant-switch dependency**: `forceLoad: true` is what re-groups the connection, and
   nothing says so. A comment at the call site and a test that fails if it becomes a soft navigation.

**Open question for you, and the only one:** point 1 deletes public members of a template
(`HubClient.SendAsync`, `NotifyAsync`, and a component file). Generated projects that
have already been created keep their copies, but future ones lose an extension point that a consumer
might have wanted as scaffolding for their own chat. My view is that dead code that broadcasts to
every tenant is worse than a missing example, and the four surfaces this template has already deleted
set the precedent — but it is your template.

---

## 7. The gate outcome

All six points ratified as recommended, including the one open question: the dead public members go.
The three decisions put to you were answered **delete both dead surfaces**, **no cross-tenant escape
for presence**, and **gate the roster in this pass**. Everything below is that design built.

---

## 8. §B — Implementation

### 8.1 The `Clients.All` inventory, resolved site by site

Every site from §2's catalogue, and what happened to it. The numbering is §2's.

| # | Site | Resolution |
|---|---|---|
| 1 | `Clients.All.Connect` | **Bounded.** Now `Clients.Group(group)` where `group = GroupFor(user?.TenantId)` — `ServerHub.cs:112` |
| 2 | `Clients.All.Disconnect` | **Bounded.** Now `Clients.Group(GroupFor(connectionUser.TenantId))`, from the tenant recorded at connect time — `ServerHub.cs:130` |
| 3 | `Clients.All.SendMessage` | **Deleted** with `SendMessage` |
| 4 | `Clients.User(to)` — client-supplied recipient | **Deleted** with `SendPrivateMessage`. This one was never a `Clients.All`, which is why a broadcast census missed it; it was the only site that took a recipient from the client |
| 5 | `Clients.All.SendNotification` | **Deleted** with `SendNotification` |
| 6 | `Clients.Caller.PageComponentOpened` | **Deleted** with `NotifyPageComponentOpen` |
| 7 | `Clients.All.PageComponentOpened` | **Deleted** with `NotifyPageComponentOpen` |
| 8 | `Clients.All.PageComponentClosed` | **Deleted** with `NotifyPageComponentClose` |
| 9 | `GetOnlineUsers()` | **Bounded separately.** Filtered to the caller's group before the projection is built — `ServerHub.cs:156` |

**No `Clients.All` survives**, so no exemption comment was needed. `ServerHubContainsNoBroadcastToEveryClient`
holds that: it strips comment lines and then requires the literal to be absent from code, so a future
installation-wide broadcast has to be added deliberately and the test rewritten to allow it. (The
file's own remarks name `Clients.All` in order to forbid it — a scan that could not tell the
prohibition from a violation would be permanently red, and a permanently red test gets deleted rather
than believed.)

### 8.2 The mechanism as built

**One definition of "same audience".** `ServerHub.GroupFor(string? tenantId)` returns
`"tenant:" + tenantId`, or the sentinel `"tenant-none"` when there is no tenant. `GetOnlineUsers`
scopes by comparing *group names* rather than tenant ids, so the roster a client can pull and the
events it is broadcast cannot disagree — including about what a null tenant means. The sentinel is a
separate namespace rather than a reserved id: every tenanted group starts with `"tenant:"` and the
sentinel does not, so no tenant id can collide with it. `TheSentinelGroupCannotCollideWithAnyTenantsGroup`
pins that structurally rather than by assuming ids are GUIDs.

**The source of the tenant.** New file `HubUserContext.cs` (Infrastructure) owns the
`Context.Items` key that `UserContextHubFilter` writes and exposes
`HubCallerContext.GetUserContext()`. `UserContextHubFilter` now reads the key from there rather than
holding its own literal — a duplicated key that drifted would not fail to compile, it would silently
yield `null`, and a hub failing closed on `null` would quietly stop isolating rather than break. The
file's remarks record all three wrong sources (a hub method parameter, `Context.User`'s tenant claim,
`IUserContextAccessor.Current` in the lifetime callbacks) and why each is wrong, because the wrong
ones are the more discoverable.

**Constraint §B.1 — never from the client.** `OnConnected_IgnoresTheTenantClaimOnThePrincipalAndUsesTheResolvedContext`
gives the principal a tenant-B claim while the filter's context says tenant A, and requires the
connection to join A and never B. `NoHubMethodTakesATenantFromTheClient` requires no hub method to
take a tenant-shaped parameter at all.

**Constraint §B.2 — reconnect.** Membership is set only in `OnConnectedAsync`, which is the only
place it can be: a SignalR group is keyed by connection id and does not survive a reconnect. This is
the standard pattern, not a workaround. `AReconnectRejoinsTheGroupUnderItsNewConnectionId` asserts
the rejoin happens under the *new* id.

**Constraint §B.3 — the tenant switch.** §3.3 found this working by accident. It now works on
purpose: `TenantSelector.razor`'s `NavigateTo("/", true)` carries a comment saying `forceLoad: true`
is load-bearing for presence isolation and why a soft navigation would break it silently, and
`TheTenantSwitchStillForcesAFullPageLoad` fails if it is changed. Two hub-level tests assert the
behaviour that depends on it — the new connection joins the new tenant's group and never the old
one, and the snapshot flips from the old tenant's colleagues to the new tenant's.

**Constraint §B.5 — the dead code.** Deleted, not isolated: `SendMessage`, `SendPrivateMessage`,
`SendNotification`, `NotifyPageComponentOpen`, `NotifyPageComponentClose`, the `ComponentUsers`
dictionary, five `ISignalRHub` members, four `HubClient` events with their handlers, four `HubClient`
send methods, three `EventArgs` classes, and `ActiveUserSession.razor` with its four `.resx` files.
**Chat is gone. It is stated plainly here and in the README's limitations list**, with a pointer to
`ServerHub` as the shape to follow for anyone adding it back deliberately.

### 8.3 Two things changed that the brief did not ask for, both stated

**The roster's permission gate** (ratified separately). `OnlineUsersTracker` now checks
`Permissions.Users.ViewOnlineStatus` and, without it, **starts no connection and pulls no snapshot** —
subscribing and then declining to draw would still put presence data on the wire. It applies no
tenant filter of its own, deliberately, and a test pins that: the hub is invocable directly over the
WebSocket, so a filter in the component would constrain the display without constraining the
disclosure, and would read as though the bound lived in the UI.

**One ordering fix inside `OnConnectedAsync`.** The connection is now registered in `OnlineUsers`
*before* the `Connect` broadcast rather than after. The broadcast is what makes clients call
`GetOnlineUsers`, so under the old ordering that snapshot could race the insert and come back without
the user it was announcing. It is a correctness fix rather than a tenancy one, and the
"narrowed, not emptied" tests depend on it being deterministic.

---

## 9. §C — Verification, and what it cannot reach

### 9.1 What can and cannot be tested in-process — plainly

**Reachable, and reached.** A hub method is an ordinary method on an ordinary object: `Context`,
`Clients` and `Groups` are settable properties. So the hub is driven through its real connection
lifecycle in-process, and **every recipient decision it makes is asserted** — which group a
connection joins, which group each event is addressed to, and what `GetOnlineUsers` returns.

**Not reachable, and not claimed.** That the ASP.NET Core group manager actually delivers only to a
group's members; that a browser's WebSocket drops and re-enters `OnConnectedAsync`; that a forced
page load really tears the circuit down and disposes the scoped `HubClient`. Those are SignalR's and
the browser's behaviour, not this template's. **No test here asserts delivery.** The hand-test list at
§10 is where that is covered, and it is Yoab's to run.

This is the standing lesson stated back: green tests over a surface no test can reach is how three
defects shipped. The line this suite holds is stated in the fixture's own remarks so that a future
reader cannot mistake its scope.

### 9.2 Both halves, per §C.2

Asserting that an event went to `Clients.Group("x")` proves the send site and nothing about
membership: a connection never added to `"x"` passes that assertion while receiving nothing, and one
added to the wrong group passes it while receiving someone else's events. So **group assignment is
asserted at `Groups.AddToGroupAsync` as well**, against the same connection id. Three tests assert
assignment (tenanted, tenantless, claim-ignoring), four assert the sends, and the tenant-switch and
reconnect tests assert assignment again under a changed connection.

The recipient double is `MockBehavior.Strict`, so any recipient set the hub reaches for that is not
set up — `Clients.All` above all — fails the test rather than quietly returning null.

### 9.3 Narrowed, not emptied — per §C.4

A hub that broadcast to nobody would satisfy every isolation assertion, so each negative has a
positive beside it in the same test:

- `AColleagueInTheSameTenantStillSeesTheArrivalAndTheDeparture` — a second tenant-A user's connect
  *and* disconnect still reach group A.
- `GetOnlineUsers_ReturnsTheCallersTenantAndOnlyTheCallersTenant` — with five connections across two
  tenants and a tenantless one, a tenant-A caller gets exactly `alice, anne, bob`: the strangers are
  absent **and the colleagues are present**.
- `GetOnlineUsers_FromATenantlessConnectionSeesOnlyOtherTenantlessConnections` — the sentinel group
  is a real audience, not a black hole.
- `WithViewOnlineStatus_TheComponentConnectsAndAsksForTheRoster` — a permission holder must still
  reach the hub, so the new gate cannot be a gate that denies everyone.

### 9.4 Red before, green after — per §C.5

Demonstrated by reintroducing the defect in place and running the new suites, then restoring.
Reverted: the `AddToGroupAsync` call, both `Clients.Group(...)` sends back to `Clients.All`, the
`GetOnlineUsers` filter, the roster's permission gate, and `forceLoad: true`.

```
with defects reintroduced:  Failed: 16, Passed: 5, Total: 21
after restoring:            Failed:  0, Passed: 21, Total: 21
```

The 16 red are every behavioural assertion. The 5 that stayed green are the ones with no defect
reintroduced for them — the deletion pins, the sentinel-collision proof, the no-tenant-parameter
scan, the roster's source pin, and the "a holder still connects" positive, which correctly still
passes when the gate is removed. **The restore was verified byte-identical** by diffing the three
files against copies taken before the edit.

### 9.5 The boundary suites — per §C.6

**No existing test file was modified.** Every one of Passes 26–29's isolation and scope suites was
confirmed byte-unmodified with `git diff --quiet` per file, and all run green:

| Suite | |
|---|---|
| `HarnessPrincipalTests`, `AuditTrailTenantFilterTests`, `SwitchableTenantsTests`, `TenantSwitchAuthorizationTests` | unmodified |
| `TenantVisibilityTests`, `UserVisibilityTests` | unmodified |
| `SuperiorAutocompleteScopeComponentTests`, `SuperiorBoundComponentTests`, `TenantSelectorComponentTests`, `UserDeactivationPermissionComponentTests`, `UserTenantScopeComponentTests` | unmodified |

Run as a filtered set: **82 passed, 0 failed.** `UserLoginStateComponentTests` — which constructs a
real `HubClient` and raises its private callbacks by reflection — was also untouched and still
passes, which is the useful negative result for a pass that deleted four of that class's events: the
two it uses survived.

### 9.6 Counts

| | Before | After | Delta |
|---|---|---|---|
| `Infrastructure.UnitTests` | 217 | 217 | — |
| `Application.IntegrationTests` | 12 | 12 | — |
| `Application.UnitTests` | 429 (+12 skipped) | 429 (+12 skipped) | — |
| `Server.UI.IntegrationTests` | 180 | **201** | **+21** |
| **Total** | **838 passed, 12 skipped** | **859 passed, 12 skipped** | **+21, 0 failed** |

The +21 is exactly the two new files: 18 in `ServerHubTenantIsolationTests`, 3 in
`OnlineUsersTrackerComponentTests`. No pre-existing test changed count or outcome.

**Warnings: unchanged.** `dotnet build --no-incremental` gives **19 warnings across the same 10
distinct source locations** as the start state — `DescriptionAttributeExtensions.cs` ×4,
`MapsterConfiguration.cs` ×2, `MudDateTimeField.razor`, `TenantSelect.razor`, `Dashboard.razor`,
`AuditTrails.razor` — plus `NETSDK1206`, which the SDK targets emit once per project and which has no
source location. **No new warning location. 0 errors.**

### 9.7 Generation probe — per §C.9

```
dotnet pack (nuspec) → dotnet new install → dotnet new gxblazor -n P30
  → build: 0 Error(s), 19 Warning(s)
  → dotnet test: 217 + 12 + 429 + 201 = 859 passed, 12 skipped, 0 failed
  → dotnet new uninstall; probe directories removed
```

Identical to source, suite for suite. Two things the probe confirmed that are worth naming:

- **`ActiveUserSession.razor` and its four `.resx` files are absent from a generated project**, and
  `HubUserContext.cs` and both new test files are present. The deletion propagates.
- **The new `InternalsVisibleTo` is renamed by the template.** `Server.UI.csproj` gained
  `<InternalsVisibleTo Include="CleanArchitecture.Blazor.Server.UI.IntegrationTests" />`, and the
  generated project carries `P30.Server.UI.IntegrationTests` — matching its renamed assembly. Had the
  rename not applied, `ServerHubTenantIsolationTests` would have failed to compile in every generated
  project while passing here. It was checked rather than assumed.
- The source-reading tests find their files in the generated project too, because they are anchored
  on the path under `src/` rather than on a solution file or namespace — both of which are renamed
  and the folder layout is not. That is `GetDateRangeKindTests.SourcePath`'s pattern, followed
  deliberately.

---

## 10. §C.8 — Hand-test list: what only two browsers can confirm

**These are Yoab's to run.** Nothing above asserts delivery, reconnection or circuit teardown, and
nothing above can. Two browsers, two users in different tenants, plus a third user in the same tenant
as the first.

Set-up: users **A1** and **A2** in tenant A, user **B1** in tenant B. Give A1, A2 and B1 the
`Users.ViewOnlineStatus` permission; keep a fourth user **A3** in tenant A *without* it.

| # | Do | Expect | Covers |
|---|---|---|---|
| 1 | A1 signed in. Sign B1 in. | A1 sees **no** sign-in toast and B1 does not appear in A1's roster (theme drawer). | Site 1 delivery is group-bounded |
| 2 | A1 signed in. Sign A2 in. | A1 **does** see the toast and A2 **does** appear in A1's roster. | Narrowed, not emptied — the half a broken hub would also pass |
| 3 | With A1, A2, B1 all online, open A1's theme drawer. | Exactly A1 and A2. B1 absent — no avatar, no name in a `title`. | Site 9 delivery |
| 4 | Sign B1 out. | A1 sees no sign-out toast and no roster change. A2's view is likewise unchanged. | Site 2 delivery |
| 5 | Sign A2 out. | A1 sees the sign-out toast and A2 leaves A1's roster. | Site 2, narrowed not emptied |
| 6 | As A3 (no `ViewOnlineStatus`), open the theme drawer while A1 and A2 are online. | **No roster at all.** No avatars, no empty container. | The new permission gate, delivered |
| 7 | As A1, kill the network (devtools offline) for ~30s, then restore. | The roster returns and A1 still sees only tenant A. Sign A2 out during the outage and back in after; A1 sees it. | **Reconnect re-grouping** — the case in-process tests cannot reach |
| 8 | A1 belongs to both tenants. With B1 online, switch A1 to tenant B via the app-shell switcher. | The page reloads. A1's roster now shows B1 and no longer shows A2. Sign A2 in — **A1 gets no toast**. | **The tenant switch** — the case most likely to be missed, and the one resting on `forceLoad` |
| 9 | Reverse test 8: switch A1 back to tenant A. | B1 disappears from A1's roster; a B1 sign-out produces no toast for A1. | The switch in both directions |
| 10 | With two tabs open as A1 (two circuits, two connections), sign A2 in. | Both tabs show it; signing one tab out does not produce a spurious sign-out toast for A1's own user until the last tab closes. | Multi-connection per user — `OnlineUsers` is keyed by connection, the toast by user |

**Test 8 is the one to run first if you run only one.** It is the case Pass 28 made reachable, it is
the one that rests on an undeclared dependency, and it is the only one where a wrong answer means a
user is watching a tenant they have left.

---

## 11. §D — README and package metadata

**The Tenancy table has a new row** and the surrounding prose changed with it:

- New row: **Online presence and login notifications — "Yes, and with no escape"**, naming
  `ServerHub`'s per-tenant groups, the `GetOnlineUsers` bound, and that chat was deleted rather than
  scoped. Placed above the unscoped block.
- The old row *"Presence, chat and login notifications | No — broadcast to every connected client"*
  is **gone**.
- The warning *"treat everything below the **Audit trails** row as installation-wide"* now says
  **below the Online presence row**.
- The contract line, *"Only Documents is filtered by it"*, became **"Four surfaces are filtered by
  it — Documents, the Users area, audit trails and online presence."**
- New paragraph **"Presence is bounded at the connection, for the same reason"**, stating the group
  mechanism, the two properties that make it hold (server-resolved source, re-established on every
  connect) and that `GetOnlineUsers` needs its own bound because no group constrains a return value.
- New paragraph on **why presence has no escape**, and where the extension point is if you disagree.
- New limitations bullet: **"There is no chat."**

**Two stale statements corrected while I was in them**, both predating this pass:

- The intro paragraph still said *"a holder of the relevant permission still sees every tenant's
  **audit trails**, system logs, roles and picklists"* — false since Pass 29. Now it lists Documents,
  the Users area, audit trails and presence as scoped, and only system logs, roles and picklists as
  not.
- **`GX.Blazor.Template.nuspec`'s `<description>`** said the same thing, and it ships in the package
  and on the NuGet listing. Corrected to match. Flagged rather than done silently because it is
  outside the README the brief named — but it is the same claim, published to a wider audience, and
  leaving it wrong would have been the more surprising choice.

---

## 12. File map, diffstat and edit fidelity

### 12.1 File map

**New (3):**

| File | |
|---|---|
| `src/Infrastructure/Services/Identity/HubUserContext.cs` | 66 lines — owns the `Context.Items` key; `HubCallerContext.GetUserContext()`; records the three wrong sources |
| `tests/Server.UI.IntegrationTests/ServerHubTenantIsolationTests.cs` | 529 lines, **18 tests** |
| `tests/Server.UI.IntegrationTests/OnlineUsersTrackerComponentTests.cs` | 181 lines, **3 tests** |

**Modified (6 + 2 docs):**

| File | |
|---|---|
| `src/Server.UI/Hubs/ServerHub.cs` | tenant groups; `GroupFor`; `ConnectionUser` gains `TenantId`; `GetOnlineUsers` scoped; five methods and `ComponentUsers` deleted |
| `src/Server.UI/Hubs/ISignalRHub.cs` | five members deleted; three remain |
| `src/Server.UI/Hubs/HubClient.cs` | four events, four handlers, four send methods and three `EventArgs` classes deleted |
| `src/Server.UI/Components/Presence/OnlineUsersTracker.razor` | `Users.ViewOnlineStatus` gate; no connection started without it |
| `src/Server.UI/Components/AppShell/TenantSelector.razor` | comment pinning `forceLoad: true` as load-bearing |
| `src/Infrastructure/Services/Identity/UserContextHubFilter.cs` | key now sourced from `HubUserContext.ItemsKey` |
| `src/Server.UI/Server.UI.csproj` | `InternalsVisibleTo` for the Server.UI test assembly, with the reason |
| `README.md`, `GX.Blazor.Template.nuspec` | §11 |

**Deleted (5):** `src/Server.UI/Components/Presence/ActiveUserSession.razor` and its four `.resx`
files (`.resx`, `.en`, `.de-DE`, `.zh-CN`). The `Resources/Components/Presence/` directory is now
empty of files.

### 12.2 Diffstat (tracked files; the three new files are untracked and listed above)

```
 GX.Blazor.Template.nuspec                                     |   7 +-
 README.md                                                     |  60 +++++--
 src/Infrastructure/Services/Identity/UserContextHubFilter.cs  |   4 +-
 src/Server.UI/Components/AppShell/TenantSelector.razor        |  12 +-
 src/Server.UI/Components/Presence/ActiveUserSession.razor     |  66 --------
 src/Server.UI/Components/Presence/OnlineUsersTracker.razor    |  30 +++-
 src/Server.UI/Hubs/HubClient.cs                               | 125 +++-----------
 src/Server.UI/Hubs/ISignalRHub.cs                             |  19 ++-
 src/Server.UI/Hubs/ServerHub.cs                               | 182 +++++++++++++--------
 src/Server.UI/Resources/.../ActiveUserSession.de-DE.resx      | 123 --------------
 src/Server.UI/Resources/.../ActiveUserSession.en.resx         | 123 --------------
 src/Server.UI/Resources/.../ActiveUserSession.resx            | 123 --------------
 src/Server.UI/Resources/.../ActiveUserSession.zh-CN.resx      | 123 --------------
 src/Server.UI/Server.UI.csproj                                |   4 +
 14 files changed, 239 insertions(+), 762 deletions(-)
```

**Net −523 lines of shipped source.** A pass that fixes a disclosure defect by deleting more than it
adds is the shape to want, and it is only possible because four of the six sites had no callers.

### 12.3 Edit fidelity

- **No git actions.** Nothing staged, committed, stashed or reset. `git show`, `git diff` and
  `git log` were used read-only, to identify Passes 26–29's test files and to confirm they are
  unmodified.
- **The red-before demonstration was reverted byte-identically**, verified by `diff` against copies
  taken beforehand, not by re-editing from memory.
- **No existing test file was touched.** The +21 is entirely in two new files.
- Two corrections were made to §A's own text before the gate was presented (§14, A6); nothing else in
  §§2–6 changed.

---

## 13. What remains unscoped

| Surface | Status | Changed by this pass? |
|---|---|---|
| **Presence and login notifications** | **scoped — per-tenant SignalR groups, no cross-tenant escape** | **yes: unscoped → scoped** |
| **Chat** | **deleted** | **yes: dead code → gone** |
| **Page-component presence** | **deleted** | **yes: dead code → gone** |
| Picklists | decided (shared + per-tenant), **still not implemented** | no |
| System logs | unscoped, and **unreachable** by the Pass 29 filter — `SystemLog` is on `LogDbContext`, not `ApplicationDbContext`. Scoping them is a separate design, not a deferred switch | no |
| Roles | unscoped — `ApplicationRole` has no tenant at all, and role names are unique across the installation | no |
| Security settings (idle policy) | unscoped — one row per installation, by design | no |

**Three statuses changed, all in this pass's own area.** Nothing else moved. The remaining three
unscoped surfaces are unchanged in both status and reason, and the README's "treat everything below
this row as installation-wide" warning now points at the right row.

---

## 14. Scratch probe disclosure

Three, all removed:

1. **Green-file backups** for the red-before demonstration — copies of `ServerHub.cs`,
   `OnlineUsersTracker.razor` and `TenantSelector.razor` under the session scratchpad, restored from
   and then deleted.
2. **The generation probe** — a packed nupkg, an installed template, and a generated `P30` solution.
   Template uninstalled, directories removed. The nupkg in the repository root was rebuilt by
   `dotnet pack`; it is a gitignored build artifact and does not appear in `git status`.
3. **A second generation probe directory abandoned**, see A7.

No database was created or written. The working tree contains only the intended changes.

---

## 15. Anomalies

**A1 — `GetOnlineUsers` is a directory disclosure that a `Clients.All` census cannot find.** Pass 23
counted broadcast sites, which was the right question for establishing the mechanism and the wrong
one for establishing severity. Recorded because the same blind spot would recur on any future hub:
**the return value of a client-invocable method is a broadcast to whoever asks.**

**A2 — the tenant is knowable at connect time, but not from the principal.** Keying groups off
`Context.User`'s tenant claim is the obvious implementation and would have been wrong: the claim is
written only by `TenantSwitchService`, so it is absent for every user who has never switched tenant,
and stale for those who have until their cookie is reissued. The correct source is the filter's
DB-backed `UserContext`. Recorded because the wrong source is the more discoverable one.

**A3 — `ActiveUserSession` is dead, and its deadness is invisible from the hub.** The hub's
page-component broadcasts look live: they have a client handler, an event, and a component that
subscribes. The component is simply never placed. Nothing in the hub, the client or the component
says so — it took a repository-wide search for the component's own name to establish it.

**A4 — the ungated roster contradicts a gated toast.** Two components consume the same presence
stream; one checks `Users.ViewOnlineStatus` and the other does not, and it is the *more* revealing one
that does not. Recorded separately from the tenancy defect because it is a distinct bug that would
survive a perfect tenant-grouping implementation.

**A5 — the tenant-switch case passes today for a reason nobody wrote down.** `forceLoad: true` is
load-bearing for presence isolation and is not documented as such at either end. This is the shape of
defect this programme keeps finding: correct behaviour resting on an undeclared dependency, where the
obvious future edit breaks it silently.

**A6 — two errors in the first draft of this report, corrected on re-verification.** It named the
client-side chat senders `HubClient.SendMessageAsync` / `SendNotificationAsync`; they are actually
`SendAsync` / `NotifyAsync`. It also contained a sentence saying seeding assigns the bootstrap
administrator a tenant "so the shipped state has none", which is self-contradictory — seeding *does*
assign one, so the shipped state contains no tenantless user. Both are recorded rather than silently
fixed because the first is a deletion inventory: a wrong member name in a list of things to delete is
the kind of error that survives into the implementation.

**A7 — the generation probe failed the first time at MSB3021, and it is a documented limitation of
this template.** Generating into the session scratchpad path put
`tests/.../bin/Debug/net10.0/runtimes/browser-wasm/nativeassets/net9.0/e_sqlite3.a` past Windows'
260-character limit, and three test projects failed to build. The README already carries a
limitations bullet naming exactly this failure. Regenerating at a short path gave a clean build and
an identical suite. **Not a defect introduced by this pass, and not a new one** — but recorded
because the first result looked like a template break caused by the new files, and a shorter path was
the whole fix.

**A8 — the roster's permission gate is observed by whether the component connects, not by markup.**
An empty roster and a forbidden roster both render nothing, so markup cannot distinguish them. The
test builds the hub connection over a message handler that always throws a marked exception, which
makes "the component tried to connect" a deterministic, socket-free observation. Recorded because the
obvious test — assert the markup is empty — would pass against a component that fetched every
tenant's presence and then declined to draw it, which is a fake gate and the exact failure this pass
exists to remove.

**A9 — `UserContextHubFilter.OnDisconnectedAsync` clears the ambient accessor but not
`Context.Items`.** That is why `OnDisconnectedAsync` in the hub can still address the right group at
all. It is relied upon only as a fallback — the hub addresses the disconnect from the tenant it
recorded in `OnlineUsers` at connect time, not by re-reading the connection — but the dependency is
worth naming, because a future filter change that cleared `Items` would break the more obvious
implementation and not this one.
