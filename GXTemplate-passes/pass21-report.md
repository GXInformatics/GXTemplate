# Pass 21 — The Identity Surface

**Nature:** editing pass with one decision gate at §A. **No git actions.**
**Date:** 2026-09-02.
**Evidence base:** the Pass 19 report; every citation re-confirmed at the point of change.

---

## 1. Start state

| | |
|---|---|
| HEAD | `b6bfc7c894e53dc7ab74c66bbdabbb1a7340e247` — *"Strengthening"* — the Pass 20 commit |
| Working tree | clean |
| Build | succeeded, 0 errors |
| Tests | **681 passed, 12 skipped, 0 failed** — matches expectation |
| Warnings | **10 distinct locations** — matches expectation |

Per suite: `Infrastructure.UnitTests` 183, `Application.UnitTests` 356 (+12 skipped),
`Server.UI.IntegrationTests` 125, `Application.IntegrationTests` 9.

**Spot-checks both pass.** `ResetLinkDisclosureComponentTests.cs` is present (268 lines, in the
commit). `ForgotPasswordConfirmation.razor` has no `ResetPasswordLink` property — the only textual
match is the explanatory comment Pass 20 left behind, which is documentation, not a parameter.
Pass 20 landed in `b6bfc7c8` exactly as written: 15 files, 1844 insertions, 78 deletions.

No mismatch. Proceeded.

---

## 2. §A — the gate

### 2.1 What the investigation found

Beyond what Pass 19 established, four things materially shaped the recommendation:

1. **The data is not merely transmitted — it is painted.** `wwwroot/js/orgchart.js:125-126` reads
   `d.data.email` and `d.data.phoneNumber`, and lines 175-179 render the email onto every node with
   a `✉` glyph and a `title` tooltip. The phone number is transmitted to the browser whether or not
   it is drawn.

2. **There is no "current user's tenant" scoping idiom for user lists anywhere in this template.**
   The admin grid at `Users.razor:325` filters on `_selectedTenantId`, which initialises to
   `string.Empty` (`:265`) — i.e. **no filter**, all tenants — and the tenant dropdown
   (`:49-51`) lists `TenantService.DataSource`, every tenant. So `/identity/users` already shows
   every user in every tenant to any `Permissions.Users.View` holder. The only tenant-scoping idiom
   that exists is on Documents (`VisibleDocumentSpecification.cs:21`,
   `AdvancedDocumentsSpecification.cs:12-15`), filtering by `filter.CurrentUser.TenantId`.

3. **The template does have a cross-tenant permission concept, and the Users area does not use it.**
   `Permissions.Users.SwitchToAnyTenant` is described as an "admin privilege" and is checked only by
   `TenantSwitchService.cs:112` and `TenantSelector.razor:100` — never by the Users grid.

4. **The component had a latent bug of its own.** `OrgChartTab.razor:34` set `Area = user.Tenant?.Name`,
   but the query (`:20-23`) never `.Include`d `Tenant`, so `Area` was always null.

**The tension this created, reported because it changes what a fix can honestly claim:** tenant
isolation for *user* data is not implemented anywhere in this template. Tenant-filtering the org
chart alone would have made a self-service tab stricter than the admin page showing identical data,
and would not have closed the door — a `Users.View` holder simply uses `/identity/users` instead.
Making the admin grid tenant-scoped is a different and much larger change: the tenant selector
exists deliberately, implying cross-tenant admin viewing is an intended capability. **That is a
product decision, not a defect, and it is recorded for Pass 22 rather than made here.**

### 2.2 Recommendation given, and the outcome

I recommended **option 3** (tenant-filter plus dropping email and phone), on the reasoning that
`/user/profile` is reachable by every authenticated user by design, so whatever it shows must be
safe for the least-privileged one, and that names/roles/reporting lines scoped to one's own tenant
is what the feature is evidently for.

**The decision was option 1 — remove the tab entirely.** Implemented as chosen.

It is the stronger answer, and on reflection the better one: it leaves no residual, no new
permission to get wrong, and no dependence on a tenant boundary this template does not otherwise
enforce. An org chart of the whole organisation is not "your profile"; if one is wanted later it
belongs on its own page with its own gate, where the question of who may see it has to be answered
explicitly instead of inherited from whoever can see their own profile.

**§B–§E proceeded while the gate was open**, as the brief permitted. They were complete and building
green before §A was implemented.

### 2.3 What was removed

| File | Action |
|---|---|
| `src/Server.UI/Pages/Identity/Users/Profile.razor` | tab panel removed, replaced by a comment recording why |
| `src/Server.UI/Pages/Identity/Users/Components/OrgChartTab.razor` | **deleted** (51 lines) |
| `src/Server.UI/Services/JsInterop/OrgChart.cs` | **deleted** (19 lines) |
| `src/Application/Common/Models/OrgItem.cs` | **deleted** (20 lines) — the shape carrying Email and PhoneNumber |
| `src/Server.UI/wwwroot/js/orgchart.js` | **deleted** (202 lines) |
| `src/Server.UI/Services/JsInterop/JSInteropConstants.cs` | `CreateOrgChart` constant removed |
| `Profile.{resx,en,de-DE,zh-CN}` | `"Org Chart"` string removed — 69 → 68 entries each |
| `tests/…/ProfileSecurityTabComponentTests.cs` | stub registration removed; one assertion inverted (§6.2) |

**Before** (`Profile.razor:32-34`):

```razor
    <MudTabPanel Text="@L["Org Chart"]">
        <OrgChartTab />
    </MudTabPanel>
```

**After** — a comment in its place, so the next reader does not restore it:

```razor
    @* An Org Chart tab used to sit here. It loaded EVERY user in the installation - no permission
       check, no tenant filter - and painted each one's email and phone number onto a node. This
       page carries no [Authorize(Policy = ...)], only the route fallback policy, so every
       authenticated user reaches it: a self-registered account holding nothing but Documents.View
       had the complete directory of every tenant. It was removed rather than gated or filtered,
       because an org chart of the whole organisation is not "your profile". If one is wanted, it
       belongs on a page of its own with its own permission, where who may see it is answered
       explicitly instead of inherited from whoever can see their own profile. *@
```

---

## 3. §B — the activation URL in the log store

### 3.1 The change

`UserActivationCommand.cs`, **before**:

```csharp
_logger.LogInformation(
    "Activation email sent to {Email}, Activation Callback URL: {ActivationUrl}.",
    notification.Email, notification.ActivationUrl);
```

**After** — the shape the reset handler already used, with the reasoning left in place:

```csharp
// The ACTIVATION URL IS NOT LOGGED, and must not be added back as a debugging
// convenience. It carries userId plus the base64url confirmation token, and this
// logger reaches the database sink - SerilogExtensions excludes only two
// property-marked categories and applies no level filter - so the token would be
// readable from /system/logs by any Permissions.Logs.View holder. Anyone who could
// read it could confirm an address they do not control. The address alone is enough
// to answer "did the mail go out?", which is what this line is for.
_logger.LogInformation("Activation email sent to {Email}.", notification.Email);
```

### 3.2 The sweep — and one finding outside the named handlers

Every log statement in `Features/Identity/Notifications/` and `Infrastructure/Services/Mail/` was
enumerated. Pass 19's two "clean" siblings re-confirmed:

| Handler | Log line | Verdict |
|---|---|---|
| `ResetPasswordCommand.cs:39` | `"Password reset email sent to {Email}."` | clean |
| `SendWelcomeCommand.cs:42` | `"Welcome email sent to {Email}."` | clean |
| `SendMailCommand.cs:79` | `"Sent '{Template}' to {Email}."` | clean |
| `MailgunMailService.cs:69` | `"Sent '{Template}' to {Email}"` | clean |
| `SinkMailService.cs:67` | template, email, subject, path — no body | clean |
| `UserActivationCommand.cs` | **was** logging the callback URL | **fixed** |

A wider sweep — every `Log*` call in `src/` whose message template contains a placeholder named
`*Url*`, `*Token*`, `*Code*`, `*Link*`, `*Callback*`, `*Secret*` or `*Password*` — surfaced two more:

- **`CustomError.razor:118` — a real leak, fixed.** The variable was named `sanitizedUri`, but the
  sanitising was `.Replace("\n","").Replace("\r","")` — CR/LF stripping for log-injection only. The
  **query string was left intact**. So any unhandled exception raised while a user sat on
  `/account/reset-password?userId=…&token=…` wrote a live reset token into the log database, in
  exactly the way §B is about. Now:

  ```csharp
  internal static string SanitiseForLog(string uri)
  {
      var withoutQuery = Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
          ? parsed.GetLeftPart(UriPartial.Path)
          : uri.Split('?', '#')[0];

      return withoutQuery.Replace("\n", "").Replace("\r", "");
  }
  ```

  The fallback matters: a value that will not parse as a URI has everything from the first `?` or
  `#` cut, so a malformed URL cannot smuggle a query string through by failing to parse. The page
  identity — which is the diagnostic value — is kept.

  **This is beyond the brief's stated scope** (it named the notification handlers). It is the same
  defect, reached by the same sink, so it is fixed here rather than filed.

- **`IdentityComponentsEndpointRouteBuilderExtensions.cs:369`** logs a `returnUrl` for external
  login. A return URL is user-supplied but not a secret, and no token flows through it. **Left
  alone**, recorded.

### 3.3 Both halves verified

The token still reaches the outbound message and no longer reaches the log:
`UserActivationNotificationHandler` passes `notification.ActivationUrl` into
`_mailService.SendAsync(...)` at `:29-34` — untouched — and only the log statement changed. The
symmetrical check for the reset flow is asserted mechanically by
`IdentityEnumerationComponentTests.OnlyTheConfirmedAccount_ActuallyReceivesAReset`, which fails if
the flow stops sending.

---

## 4. §C — forgot-password enumeration

### 4.1 The change

Three early returns collapsed into one path. **Before**: `if (user is null) { snackbar; return; }`,
`if (!confirmed) { different snackbar; return; }`, then the send and navigation. **After**:

```csharp
Logger.LogInformation("Password reset requested.");

var user = await _userManager.FindByEmailAsync(_formModel.Email);

if (user is not null && await _userManager.IsEmailConfirmedAsync(user))
{
    var code = await _userManager.GeneratePasswordResetTokenAsync(user);
    …
    await Mediator.Publish(new ResetPasswordNotification(callbackUrl, user.Email!, user.UserName!));
}

Navigation.NavigateTo(ForgotPasswordConfirmation.PageUrl);
```

No snackbar in any branch. `_statusMessage` became dead and was removed. Two now-orphaned strings
were removed from all four locales (`Forgot.*.resx`, 10 → 8 entries each) — the same residue
discipline Pass 20 used, for the same reason: leaving them invites restoring the block that used
them.

### 4.2 The log line — recommendation and choice

The brief offered "remove it, or log unconditionally with no address". **I logged unconditionally
with no address**, placed outside every branch. Reasoning: removing it entirely loses the only
signal that the endpoint is being exercised at all, which is what makes a spraying attempt visible;
logging per-request with no address keeps that signal while answering nothing about which addresses
exist. The `"Rest password email sent to…"` typo died with the line it was in.

**One thing deliberately not changed:** `ResetPasswordCommand.cs:39` still logs the address when
mail is actually sent, so a `Logs.View` holder can still infer which addresses are confirmed
accounts. That is an operational record of outbound mail — you need to know what you sent — and it
is a different thing from the anonymous request path recording what a stranger typed. Stated rather
than silently kept.

### 4.3 The identity table — captured values

Component test `AllThreeCases_AreIndistinguishable`, plus a live run for the two cases reachable
without creating an account:

| Case | Landed URL | Snackbar | Page text |
|---|---|---|---|
| unknown | `/account/forgotpasswordconfirmation` | *(none)* | "Check Your Inbox If an account with the provided email exists, a password reset link has been sent. Please che…" |
| known, unconfirmed | `/account/forgotpasswordconfirmation` | *(none)* | identical |
| known, confirmed | `/account/forgotpasswordconfirmation` | *(none)* | identical |

The unconfirmed row is from the **component test**, which constructs a user with
`EmailConfirmed = false` in-memory — no scratch database was needed and the dev database was not
mutated. The other two rows are corroborated live (§7.3).

The test asserts all three are equal on URL, snackbar and visible text, and separately that the URL
carries no query string.

### 4.4 The trade about the unconfirmed branch — stated plainly

**A user whose address is unconfirmed now gets silence instead of an explanation.** Before, they
were told "Your email address has not been confirmed. Please check your inbox for a confirmation
email or request a new one." Now they see the same "check your inbox" page as everyone else, and no
reset mail arrives — so the page is, to them, misleading.

That is a deliberate trade of helpfulness for non-disclosure, and it is **not** the whole answer.
The behaviour is unchanged: an unconfirmed address still receives no reset link. Whether it *should*
be able to recover is Pass 22's policy question (Pass 19 §B.4), and this pass deliberately does not
pre-empt it. If Pass 22 decides unconfirmed addresses may reset, this branch disappears and the
awkwardness with it.

### 4.5 The timing residual — accepted, not fixed

The known-and-confirmed path does measurably more work than the two that skip it: a token
generation and a notification enqueue. The three cases therefore remain distinguishable by response
time to an attacker who can measure it. Closing that means not doing the work on the request path,
which changes what happens when mail fails. **Out of scope**, and recorded in a comment in
`Forgot.razor` as well as here so it is not mistaken for an oversight.

---

## 5. §D — the login page oracle

### 5.1 Branch inventory, before

| # | Condition | Checked | Message | Reveals existence? |
|---:|---|---|---|:---:|
| 1 | `user == null` | before password | "The specified user does not exist." | **yes** |
| 2 | `IsLockedOutAsync` | **before** password | "The account is locked due to multiple failed attempts…" | **yes** |
| 3 | `!user.IsActive` | **before** password | "Your account is inactive. Please contact support…" | **yes** |
| 4 | password valid | — | → `postLogin` | success |
| 5a | wrong password, now locked | after password | "The account has been locked due to multiple failed login attempts." | **yes** |
| 5b | wrong password | after password | "The username or password is incorrect. Please try again." | no |

Four of the six answers told an anonymous caller that the account exists, three of them without any
credential at all.

### 5.2 Branch inventory, after

| Condition | Message | Reveals existence? |
|---|---|:---:|
| unknown name | "The username or password is incorrect. Please try again." | no |
| wrong password | same | no |
| wrong password on a **locked** account | same | no |
| wrong password on an **inactive** account | same | no |
| **correct password**, locked | "The account is locked due to multiple failed attempts…" | n/a — password proven |
| **correct password**, inactive | "Your account is inactive. Please contact support…" | n/a — password proven |
| correct password, active, unlocked | → `postLogin` | success |

### 5.3 What was not collapsed, and why

The brief warned against collapsing a branch a legitimate user genuinely needs. **The lockout and
inactive messages are not deleted — they are relocated behind a correct password.** That is the
point at which the person asking has proved the account is theirs and can be told the truth about
it. A locked-out user who types their real password still gets "you are locked out, try later"; an
attacker who does not have it learns nothing.

This works because `UserManager.CheckPasswordAsync` verifies the hash only and does not consider
lockout, so the password can be checked first and lockout interpreted afterwards. That is stated in
a comment at the call site, because it is the assumption the whole reordering rests on.

`AccessFailedAsync` still runs on every wrong password for a real user, so the lockout policy is
unchanged — the caller is simply not told the outcome. Branch 5a's message ("the account has now
been locked") is genuinely gone: it confirms existence and adds nothing a legitimate user cannot
get on their next attempt with the correct password.

**Not changed: whether an inactive account may sign in at all.** Only *when* the user is told. That
is Pass 19's #8 and belongs to Pass 22.

Two strings became dead and were removed from all four Login locales (21 → 19 entries each):
`"The specified user does not exist."` and
`"The account has been locked due to multiple failed login attempts."`. The relocated lockout string
`"The account is locked due to multiple failed attempts. Please try again later."` is retained and
still used.

### 5.4 Residual

An unknown name skips the password hash comparison and the `AccessFailedAsync` write, so the cases
remain distinguishable by response time. Closing it means hashing against a dummy for unknown names.
**Accepted, not fixed**, and recorded in a doc-comment on `ShowGenericFailure`.

---

## 6. §E — the unencoded `ReturnUrl`

### 6.1 The sweep

Every string-interpolated URL in `src/` was enumerated. Three hits:

| Site | State | Action |
|---|---|---|
| `LoginWith2fa.razor:58` | `?ReturnUrl={ReturnUrl}` unencoded | **fixed** |
| `RedirectToLogin.razor:11` | `?returnUrl={returnUrl}` unencoded | **fixed — not named in the brief** |
| `IdentityComponentsEndpointRouteBuilderExtensions.cs:161` | already `Uri.EscapeDataString(...)` | left alone; the idiom followed |

**`RedirectToLogin.razor` is the more consequential of the two, and the sweep is the only reason it
was found.** Its `returnUrl` is `uri.PathAndQuery` — a path *and query*. Interpolated raw, the
second parameter of the page being returned to became a parameter of the **login** URL instead:

```
original page : /pages/documents?a=1&b=2
produced      : /account/login?returnUrl=pages/documents?a=1&b=2
parsed as     : returnUrl = "pages/documents?a=1"   +   b = "2"
```

So the return URL silently truncated, and after signing in the user landed somewhere they had not
asked for — whenever the page they were sent away from had more than one query parameter. Both are
now `Uri.EscapeDataString`, matching the codebase's own existing correct instance.

Neither was ever an open redirect: both hrefs begin with a compile-time constant. Pass 20's
judgement on `LoginWith2fa` is confirmed, not overturned.

---

## 7. §F — verification

### 7.1 Red before, green after

**§A and §C** were captured together against a restored pre-fix tree, with the **final** test code
(see §9.2 for the disclosure about a first, stale capture):

```
Failed!  - Failed: 5, Passed: 1, Skipped: 0, Total: 6
  Failed AllThreeCases_AreIndistinguishable
     Expected unknown.LandedUrl to be "http://localhost/account/forgotpasswordconfirmation" …
     but "http://localhost/" has a length of 17
  Failed TheProfilePage_OffersNoOrgChartTab
  Failed TheOrgChartComponents_AreGoneFromServerUi("OrgChartTab", …)
     … but found {"…Pages.Identity.Users.Components.OrgChartTab"}
  Failed TheOrgChartComponents_AreGoneFromServerUi("OrgChart", …)
     … but found {"…Services.JsInterop.OrgChart"}
  Failed TheOrgItemProjection_IsGone
     … but found {"…Application.Common.Models.OrgItem"}
```

`unknown.LandedUrl` being `http://localhost/` is the navigation tell itself: the unknown case never
navigated at all.

**The one test that passed in the red run should have passed.**
`OnlyTheConfirmedAccount_ActuallyReceivesAReset` guards behaviour the fix must *not* break — the old
code also sent only to confirmed accounts. It is a guard against a broken fix, not a red-before
test, and it would have caught a "fix" that simply stopped sending anything.

**§D** was captured separately against a restored `Login.razor`, and shows all four answers:

```
Expected answers to contain a single item … Observed:
  unknown user    => Error:The specified user does not exist.
  wrong password  => Error:The username or password is incorrect. Please try again.
  locked out      => Error:The account is locked due to multiple failed attempts. Please try again later.
  inactive        => Error:Your account is inactive. Please contact support for assistance.
```

**Green after:** all 7 new tests pass.

### 7.2 The tests

New file `ProfileDirectoryExposureComponentTests.cs` (4 tests) — the profile page offers no Org
Chart tab (**rendered output**, per §F.1), and `OrgChartTab`, `OrgChart` and `OrgItem` are absent
from their assemblies. The type assertions are the durable half: they stop the tab returning by
restoring one line to `Profile.razor`.

New file `IdentityEnumerationComponentTests.cs` (3 tests) — the §C three-case identity table, the
§C behavioural guard, and the §D four-branch collapse.

All are **component tests**, per this project's standing lesson: the app renders at
`InteractiveServerRenderMode(prerender: false)`, so an HTTP response carries the shell and none of
the component tree, and an HTTP-level test would have passed against every one of these defects.

### 7.3 Live run

Instance on `http://localhost:5199`, Chromium:

```
§C unknown            url=…/account/forgotpasswordconfirmation  snackbar="(none)"
                      text="Check Your Inbox If an account with the provided email exists, …"
§C known+confirmed    url=…/account/forgotpasswordconfirmation  snackbar="(none)"
                      text="Check Your Inbox If an account with the provided email exists, …"

§D unknown user       "The username or password is incorrect. Please try again."
§D known, wrong pw    "The username or password is incorrect. Please try again."
```

Two §A results needed a second look and both are correct:

- `GET /js/orgchart.js` → **302** to the login page. Playwright first reported 200 because it
  follows redirects; `curl -D -` shows the redirect. The script is genuinely gone from source *and*
  from `bin/Debug/net10.0/wwwroot/`, and the unmatched route now falls to the deny-by-default
  fallback policy.
- `GET /user/profile` → **302 `…/account/login?ReturnUrl=%2Fuser%2Fprofile`**. Correct, and the
  ReturnUrl is properly encoded.

### 7.4 Counts and warnings

| Suite | Baseline | After | Delta |
|---|---:|---:|---:|
| `Infrastructure.UnitTests` | 183 | 183 | 0 |
| `Application.UnitTests` | 356 (+12 skipped) | 356 (+12 skipped) | 0 |
| `Application.IntegrationTests` | 9 | 9 | 0 |
| `Server.UI.IntegrationTests` | 125 | **132** | **+7** |
| **Total passed** | **681** | **688** | **+7** |
| Skipped / Failed | 12 / 0 | 12 / 0 | 0 |

The +7 is exactly the new tests (4 + 3). No test was deleted; one existing assertion was inverted
(§9.1).

**Warnings: 10 distinct locations before, 10 after, identical.** One was introduced and removed
during the pass — see §9.3.

---

## 8. File map and diffstat

```
 src/Application/Common/Models/OrgItem.cs                          |  20 --   (deleted)
 src/Application/.../UserActivation/UserActivationCommand.cs       |  11 +-
 src/Server.UI/Components/Errors/CustomError.razor                 |  26 ++-
 src/Server.UI/Components/Routing/RedirectToLogin.razor            |  10 +-
 src/Server.UI/Pages/Identity/Forgot/Forgot.razor                  |  58 +++---
 src/Server.UI/Pages/Identity/Login/Login.razor                    |  88 +++++----
 src/Server.UI/Pages/Identity/Login/LoginWith2fa.razor             |   7 +-
 src/Server.UI/Pages/Identity/Users/Components/OrgChartTab.razor   |  51 --    (deleted)
 src/Server.UI/Pages/Identity/Users/Profile.razor                  |  13 +-
 src/Server.UI/Resources/.../Forgot/Forgot.{,en,de-DE,zh-CN}.resx  |  24 --    (4 files)
 src/Server.UI/Resources/.../Login/Login.{,en,de-DE,zh-CN}.resx    |  24 --    (4 files)
 src/Server.UI/Resources/.../Users/Profile.{,en,de-DE,zh-CN}.resx  |  12 --    (4 files)
 src/Server.UI/Services/JsInterop/JSInteropConstants.cs            |   1 -
 src/Server.UI/Services/JsInterop/OrgChart.cs                      |  19 --    (deleted)
 src/Server.UI/wwwroot/js/orgchart.js                              | 202 --    (deleted)
 tests/Server.UI.IntegrationTests/ProfileSecurityTabComponentTests |  10 +-
 25 files changed, 150 insertions(+), 426 deletions(-)

 new: tests/Server.UI.IntegrationTests/IdentityEnumerationComponentTests.cs
 new: tests/Server.UI.IntegrationTests/ProfileDirectoryExposureComponentTests.cs
```

**Edit fidelity.** Five files deleted outright; the rest are surgical. Of the 150 insertions, a
large majority are explanatory comments and the two new test files' content is not counted above.
Net **−276 lines** in `src/`. All 16 edited `.resx` files re-validated as well-formed XML, with BOM
presence and CRLF endings preserved (whole `<data>` blocks removed by range, never rewritten).

---

## 9. Anomalies

1. **An existing test asserted the defect.** `ProfileSecurityTabComponentTests.TheOtherTabs_AreUnaffectedInEveryState`
   asserted `markup.Should().Contain("Org Chart")` in all three idle-timeout states. Removing the
   tab correctly broke it. The assertion was **inverted, not deleted** — it now asserts
   `NotContain` in all three states, so it still catches the tab returning through a state nobody
   thought to check, and a comment records what it used to say and why. The stub registration for
   the deleted component was removed from the same fixture. This is the only existing test whose
   expectation changed.

2. **The first red-before capture was stale, and was redone.** After capturing red for §A/§C I
   changed one assertion in `IdentityEnumerationComponentTests` — comparing visible text rather than
   raw markup, because bUnit stamps an incrementing `blazor:onsubmit` handler id into the markup so
   two identical renders never match byte-for-byte. That made the earlier capture evidence for code
   that no longer existed. The pre-fix state was restored a second time and red re-captured with the
   final test code; only that second capture is cited in §7.1. §D's red was captured once, with
   final code.

3. **I introduced a warning and removed it.** The §E comment on `LoginWith2fa.razor` was initially
   placed *between attributes* of `<MudButton>`, and MudBlazor's analyzer reads a Razor comment
   there as an illegal attribute — `MUD0002`, taking the count 10 → 11. Moved above the element;
   back to 10. A note in the comment records the constraint.

4. **Three test-harness false starts**, none of which reached the evidence: a missing
   `IClientInfoAccessor` registration; `using var ctx` disposing a MudBlazor container synchronously
   when it registers `IAsyncDisposable`-only services; and `.Input()` versus `.Change()` — the
   forgot-password field is `Immediate="true"` and binds on `oninput`, the login fields are not and
   bind on `onchange`. All are noted in the test code so the next author does not repeat them.

5. **Two fixes were outside the brief's stated scope**, both found by the sweeps it asked for and
   both the same defect class as the section that found them: `CustomError.razor`'s query string
   reaching the log store (§3.2), and `RedirectToLogin.razor`'s unencoded return URL (§6.1). Fixed
   rather than filed, and flagged here.

6. **The work was committed mid-pass, by the user, not by me.** HEAD was `b6bfc7c8` throughout the
   editing and verification described above; while this report was being written it became
   `651c6424 "reinforcement"`, carrying exactly the 27 files listed in §8 (611 insertions, 426
   deletions — the extra insertions over §8's figure are the two new test files, which §8's
   `git diff --stat` excluded as untracked). **I took no git actions.** All measurements in this
   report were taken against the working tree that became that commit.

7. **Scratch:** a Playwright install under the session scratchpad, deleted. Pre-fix files were
   restored twice via `git show HEAD:<path> > <path>` — a read-only query used as a file read; **no
   git state was modified** and the tree was returned to the fixed state and re-verified each time.
   The app ran on port 5199 and is confirmed stopped (port closed). One reset token was generated
   for `administrator@localhost` during the live check — unredeemed, single-use, time-limited, mail
   delivery is the Sink in Development. The dev database was not mutated: the unconfirmed-account
   case was covered in-memory by the component test rather than by creating an account.

---

## 10. Policy questions, not defects — for Pass 22

Stated separately so none is mistaken for something this pass fixed.

1. **May an unconfirmed address reset its password?** (Pass 19 §B.4.) Unchanged here; §C changed the
   response only. The current combination leaves such a user with silence and no route back except
   an administrator.
2. **What does `IsActive` mean at registration, and should an inactive account be refused login at
   all?** (Pass 19 §B.5.) §D changed only *when* the user is told, never the refusal.
3. **Should the Users admin area be tenant-scoped?** New, from §2.1. `/identity/users` shows every
   user in every tenant to any `Users.View` holder, and `Permissions.Users.SwitchToAnyTenant`
   exists but is not consulted there. Removing the org chart closed the *self-service* exposure;
   the administrative one is a deliberate-looking capability that only its owner can decide on.
4. **Should `ResetPasswordCommand` keep logging the recipient address?** (§4.2.) It is a legitimate
   record of outbound mail and it is also, for a `Logs.View` holder, a list of confirmed accounts.

---

## 11. Close

Four surfaces of one threat model are closed: an attacker can no longer enumerate addresses at the
login page, cannot distinguish outcomes at forgot-password, cannot read the staff directory from a
profile page, and cannot recover an activation token from the log store. Build green, 688 passed /
12 skipped / 0 failed, warnings unchanged at the 10 baseline locations, verified red-before and
green-after for §A, §C and §D, and confirmed against a running instance.
