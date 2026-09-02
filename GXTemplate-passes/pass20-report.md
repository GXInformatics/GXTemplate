# Pass 20 — CRITICAL: Remove the On-Screen Reset Link and Open Redirect

**Nature:** editing pass, deliberately minimal. **This commit does one thing.**
**Date:** 2026-09-02. **No git actions taken.**

---

## 1. Start state

| | |
|---|---|
| HEAD | `0e7d30b73eb82c263e87e433106f96974bcadc90` — *"gg"* — matches the brief |
| Working tree | clean except `GXTemplate-passes/pass19-report.md` (untracked, this pass's predecessor) |
| Build | succeeded, 0 errors, **10 warnings** |
| Tests | **673 passed, 12 skipped, 0 failed** — matches the expected baseline exactly |

Per suite at start: `Infrastructure.UnitTests` 183, `Application.UnitTests` 356 (+12 skipped),
`Server.UI.IntegrationTests` 125, `Application.IntegrationTests` 9.

No mismatch. Nothing to record beyond the untracked Pass 19 report, which is not a code change.

---

## 2. What was wrong, and what was done

`Forgot.razor` appended the real reset callback URL to its own navigation, and
`ForgotPasswordConfirmation.razor` rendered whatever arrived in that query parameter as a button's
`Href`. An unauthenticated visitor who typed a known address was handed a live reset token for that
account. The same unvalidated `Href` was an open redirect. `RegisterConfirmation.razor` carried the
identical shape for the email-confirmation link.

**All of it is deleted, not gated.** No `IsDevelopment()` anywhere in this change. A reset token
does not belong in a URL in any environment: URLs reach browser history, `Referer` headers, proxy
logs, screenshots and shoulders.

---

## 3. The deletions

### 3.1 `ForgotPasswordConfirmation.razor`

The query parameter and the entire conditional block are gone; the page now says one thing to
everyone.

**Before:**

```razor
<MudStack Spacing="2">
	<MudText Typo="Typo.h4" GutterBottom="true">@L["Check Your Inbox"]</MudText>
	@if (ResetPasswordLink is not null)
	{
		<MudText>@L["For testing purposes, you can directly access the password reset link below."]</MudText>
		<MudButton Href="@ResetPasswordLink" Variant="Variant.Filled" Color="Color.Primary" Size="Size.Large">
			<MudText>@L["Go to Reset Password"]</MudText>
		</MudButton>
	}
	else
	{
		<MudText Typo="Typo.body1">@L["If an account with the provided email exists, …"]</MudText>
	}
</MudStack>
…
    [SupplyParameterFromQuery] public string? ResetPasswordLink { get; set; }
```

**After:**

```razor
<MudStack Spacing="2">
	<MudText Typo="Typo.h4" GutterBottom="true">@L["Check Your Inbox"]</MudText>
	<MudText Typo="Typo.body1">@L["If an account with the provided email exists, a password reset link has been sent. Please check your inbox and spam folder."]</MudText>
</MudStack>
```

The property is deleted outright. The `else` branch's wording already assumed no button was
present ("a password reset link has been sent… check your inbox and spam folder"), so it needed no
adjustment — it is now simply the only text.

### 3.2 `Forgot.razor`

**Before:**

```csharp
await Mediator.Publish(new ResetPasswordNotification(callbackUrl, user.Email!, user.UserName!));
Logger.LogInformation("Rest password email sent to {Email}.", _formModel.Email);
var url = Navigation.GetUriWithQueryParameters(
       Navigation.ToAbsoluteUri(ForgotPasswordConfirmation.PageUrl).AbsoluteUri,
       new Dictionary<string, object?> { ["ResetPasswordLink"] = callbackUrl });
Navigation.NavigateTo(url);
```

**After:**

```csharp
await Mediator.Publish(new ResetPasswordNotification(callbackUrl, user.Email!, user.UserName!));
Logger.LogInformation("Rest password email sent to {Email}.", _formModel.Email);

// No query parameters. The callback URL above goes to the mail handler and nowhere else;
// it used to be appended here as well, which put a live reset token in the address bar of
// whoever typed the address in - account takeover with no mailbox access.
Navigation.NavigateTo(ForgotPasswordConfirmation.PageUrl);
```

**The notification publish is untouched** — same line, same arguments, still carrying the real
`callbackUrl`. Verified by test (§4.2) and by the rendered email (§5.2).

### 3.3 `RegisterConfirmation.razor`

Same treatment: the conditional block and the `[SupplyParameterFromQuery] EmailConfirmationLink`
property are both deleted, leaving the "check your email" text unconditionally.

### 3.4 `Register.razor` — and one thing that is not a pure deletion

Both branches stop appending `EmailConfirmationLink`; `["email"]` is kept, because
`RegisterConfirmation` still uses it to look the user up.

**One line was added, and it is not scope creep — it is the deletion's necessary other half.**
The `RequireConfirmedAccount` branch built a `callbackUrl` and then delivered it **only** as the
on-screen button. It never published a notification, so the button *was* the entire delivery
mechanism. Removing the button there without replacing it would have left a user registered and
permanently stranded with no way to confirm — this pass would have introduced a defect while
fixing one. So that branch now publishes the same `UserActivationNotification` the branch above
already publishes:

```csharp
// This branch built the callback URL and then delivered it ONLY as the on-screen
// button, never by email. Removing the button without this publish would leave a user
// registered and stranded with no way to confirm - so the delivery moves to the mail
// path the branch above already uses, rather than disappearing with the button.
await Mediator.Publish(new UserActivationNotification(callbackUrl, _formModel.Email, userId, _formModel.UserName));
```

That branch is unreachable in the shipped configuration — `RequireConfirmedEmail = true`
(`src/Infrastructure/DependencyInjection.cs:458`) means the first branch always wins — so this
changes no shipped behaviour. It matters for a generated project that turns confirmation off.
**Flagged explicitly** so a reviewer sees the one non-deletion in a deletion commit.

### 3.5 The eight `.resx` files

Four strings removed from every locale variant. Whole `<data>` blocks deleted by range, so BOM
presence, CRLF endings and every other byte are untouched; all eight re-verified as well-formed
XML afterwards.

| File | Entries removed | before → after |
|---|---|---:|
| `Forgot/ForgotPasswordConfirmation.resx` | *"For testing purposes, you can directly access the password reset link below."*, *"Go to Reset Password"* | 5 → 3 |
| `Forgot/ForgotPasswordConfirmation.en.resx` | same | 5 → 3 |
| `Forgot/ForgotPasswordConfirmation.de-DE.resx` | same | 5 → 3 |
| `Forgot/ForgotPasswordConfirmation.zh-CN.resx` | same (incl. *"出于测试目的…"*, *"前往重置密码"*) | 5 → 3 |
| `Register/RegisterConfirmation.resx` | *"For testing purposes, you can directly access the email confirmation link below."*, *"Confirm Account"* | 5 → 3 |
| `Register/RegisterConfirmation.en.resx` | same | 5 → 3 |
| `Register/RegisterConfirmation.de-DE.resx` | same | 5 → 3 |
| `Register/RegisterConfirmation.zh-CN.resx` | same | 5 → 3 |

Surviving keys are exactly the three each page still uses. A grep for the four removed strings
across both directories returns nothing.

---

## 4. Regression tests

New file: `tests/Server.UI.IntegrationTests/ResetLinkDisclosureComponentTests.cs` — 8 tests.

### 4.1 Why these are component tests, not HTTP tests

**An HTTP-level test cannot trip this condition, and writing one would have produced a green test
over broken code.** The application renders at `InteractiveServerRenderMode(prerender: false)`
(`src/Server.UI/App.razor:66`), so an HTTP response carries the shell and none of the component
tree. Pass 19 confirmed this by hand: `curl` of the confirmation page with a hostile parameter
returned 200 with the URL nowhere in the body, while a real browser rendered the button. The test
project's own csproj already records the same lesson from Pass 10.

The brief asked for `GET …?ResetPasswordLink=<url>` → no anchor carrying that URL. That assertion
is implemented, but through **bUnit** rather than `HttpClient`, because only rendering can see it.
This is a deliberate deviation from the letter of §B.1 in service of its intent, and it is the
reason the red-before evidence below exists at all.

### 4.2 The tests

| # | Test | Asserts |
|---:|---|---|
| 1 | `ForgotPasswordConfirmation_RendersNoOffSiteLink_WhenGivenAHostileQueryParameter` | `?ResetPasswordLink=https://evil.example/phish` produces no link target containing `evil.example` — covers the open redirect |
| 2 | `ForgotPasswordConfirmation_RendersNoResetToken_WhenGivenOneInTheQueryString` | a token-bearing same-origin URL produces no `token=` and no `reset-password?userId=` in the markup |
| 3 | `ForgotPasswordConfirmation_SaysTheSameThingToEveryone` | markup with the parameter is **byte-identical** to markup without it — the page is not steerable from the query string at all |
| 4 | `RegisterConfirmation_RendersNoOffSiteLink_…` | as #1, for `EmailConfirmationLink` |
| 5 | `RegisterConfirmation_RendersNoConfirmationToken_…` | as #2, for `code=` / `confirmemail?userId=` |
| 6 | `Forgot_StillPublishesTheResetNotification_ButNavigatesWithNoQueryString` | **both halves at once** — the `ResetPasswordNotification` is still published with a `RequestUrl` containing `/account/reset-password` and `token=`, *and* the landing URL ends with the bare page URL and contains no `?` |
| 7-8 | `TheLinkCarryingQueryParameter_NoLongerExists` (×2, parameterised) | neither component declares a `ResetPasswordLink` / `EmailConfirmationLink` property at all |

Tests 1-6 prove the behaviour today. Tests 7-8 are the durable half: they are what stops the block
being re-added tomorrow, and they hold regardless of how the markup is later restructured.

Test 6 is the brief's "assert the flow still works" requirement, and it exists precisely because
*"the token is not in the URL"* would also pass if the flow had silently stopped sending anything —
the same defect wearing a fix's clothes.

### 4.3 Red before — and one false start, disclosed

**First run: all 7 tests failed, but 5 of them for the wrong reason.** They threw
`InvalidOperationException: Cannot provide a value for property 'Mediator' … There is no registered
service of type 'Mediator.IMediator'` — a missing test-harness registration, not the defect. **That
is not evidence**, and had I stopped there I would have reported a red-before that proved nothing.
Registering `IMediator` in the fixture fixed the harness.

**Second run — all 8 red, each on its own assertion.** The material captures:

Test 1 — the open redirect, rendered:

```
Expected LinkTargets(cut) {"https://evil.example/phish"} to not have any items matching
t.Contains("evil.example") because a query parameter must never become a link target …
but found {"https://evil.example/phish"}.
```

Test 2 — the account-takeover path, with the actual anchor the component emitted:

```html
<a blazor:onclick="2" type="button"
   href="https://localhost/account/reset-password?userId=17cb7855-2b4f-4adf-82e2-9a85a7ca1cf0&amp;token=Q2ZESjhEZWFtU3RQ"
   class="mud-button-root mud-button mud-button-filled …">
   <span class="mud-button-label"><p …>Go to Reset Password</p></span></a>
```

Test 5 — the registration twin:

```html
<a … href="https://localhost/account/confirmemail?userId=abc&amp;code=Q2ZESjhEZWFt" …>Confirm Account</a>
```

Test 6 — the leaking navigation, in full:

```
Expected navigation.Uri to end with "/account/forgotpasswordconfirmation" because the landing URL
must carry no query string at all, but
"http://localhost/account/forgotpasswordconfirmation?ResetPasswordLink=http%3A%2F%2Flocalhost%2F
 account%2Freset-password%3FuserId%3D17cb7855%26token%3DYS1yZWFsLXJlc2V0LXRva2Vu"
differs near "htt" (index 0).
```

Note what test 6's failure also proves: its earlier assertions — that the notification *was*
published with the real callback URL — **passed** against the old code. So the test discriminates
between the two halves rather than failing wholesale.

```
Failed!  - Failed: 8, Passed: 0, Skipped: 0, Total: 8
```

### 4.4 Green after

```
Passed!  - Failed: 0, Passed: 8, Skipped: 0, Total: 8
```

8 red → 8 green, with no test edited between the two runs except the harness registration
described above, which was made *before* the red run that constitutes the evidence.

---

## 5. Verification

### 5.1 Build and full suite

| | Baseline (§1) | After | Delta |
|---|---:|---:|---:|
| `Infrastructure.UnitTests` | 183 | 183 | 0 |
| `Application.UnitTests` | 356 (+12 skipped) | 356 (+12 skipped) | 0 |
| `Application.IntegrationTests` | 9 | 9 | 0 |
| `Server.UI.IntegrationTests` | 125 | **133** | **+8** |
| **Total passed** | **673** | **681** | **+8** |
| Skipped | 12 | 12 | 0 |
| Failed | 0 | 0 | 0 |

The entire delta is the 8 new tests. No existing test changed behaviour.

**Warnings: 10 before, 10 after, same locations.** Enumerated after a `--no-incremental` rebuild
and de-duplicated to distinct source positions: `AuditTrails.razor(100,72)`,
`Dashboard.razor(202,60)`, `DescriptionAttributeExtensions.cs` ×4, `MapsterConfiguration.cs` ×2,
`MudDateTimeField.razor(1,1)`, `TenantSelect.razor(13,44)`. Identical to the Pass 19 baseline list.
Zero new warnings. (A raw `--no-incremental` total reads 19 because projects report their warnings
twice in that mode; the distinct-location count is the meaningful figure.)

### 5.2 Live run

Instance started on `http://localhost:5199` (Development), driven with Playwright/Chromium.

**The real flow**, submitting the known address `administrator@localhost`:

```
LANDED URL   : http://localhost:5199/account/forgotpasswordconfirmation
QUERY STRING : (none)
BODY TEXT    : Check Your Inbox If an account with the provided email exists, a password reset
               link has been sent. Please check your inbox and spam folder.
ANCHORS      : []
TOKEN ON PAGE: no
```

Compare Pass 19's capture of the same interaction, which ended in
`?ResetPasswordLink=…%26token%3DQ2ZESjhEZWFtU3RQ…` with a working button.

**The hostile parameter**, on both pages:

```
OPEN REDIRECT (forgot)   : no off-site link
OPEN REDIRECT (register) : no off-site link
```

**The email still goes.** The Development mail sink rendered the message for that same request:

```
src/Server.UI/bin/Debug/net10.0/mail/20260902-184102-recovery-password-administrator_localhost-….html

http://localhost:5199/account/reset-password?userId=17cb7855-2b4f-4adf-82e2-9a85a7ca1cf0
  &token=Q2ZESjhEZWFtU3RQL25KTnVyT2g4U2JyWElmcjdacHdhVWUvUCtOeHM0REh0bXNQSm56bVJuaXpobGhy…
```

The link is in the mailbox and only in the mailbox — which is the whole point of the change.

### 5.3 §C.4 sweep — is there a third instance?

**No.** Sixteen `[SupplyParameterFromQuery]` properties remain across the identity pages. None
reaches an `href` or `src`. Every data-bound link target in the template now resolves to one of:
a compile-time `PageUrl` constant, `Assets[…]`, a navigation-menu model value, or a stored profile
picture URL — never a query-supplied value.

**One near-miss, examined and deliberately left alone.**
`src/Server.UI/Pages/Identity/Login/LoginWith2fa.razor:58`:

```razor
href="@($"{LoginWithRecoveryCode.PageUrl}?ReturnUrl={ReturnUrl}")"
```

`LoginWithRecoveryCode.PageUrl` is the constant `"/account/loginwithrecoverycode"`, so the href
always begins with that relative path — a query-supplied value cannot steer it off-site, and no
token is involved. It is **not** this defect. It is a minor unencoded interpolation (a `ReturnUrl`
containing `&` or `#` would corrupt the query string), which belongs to the `ReturnUrl` handling
work in the §8.3 plan of the Pass 19 report, not to this commit. Recorded here so the next reader
knows it was looked at and judged, not missed.

---

## 6. File map and diffstat

```
 src/Server.UI/Pages/Identity/Forgot/Forgot.razor                          |  9 +++++----
 src/Server.UI/Pages/Identity/Forgot/ForgotPasswordConfirmation.razor      | 20 +++++++-------------
 src/Server.UI/Pages/Identity/Register/Register.razor                      | 11 +++++++++--
 src/Server.UI/Pages/Identity/Register/RegisterConfirmation.razor          | 17 +++++------------
 .../Pages/Identity/Forgot/ForgotPasswordConfirmation.de-DE.resx           |  6 ------
 .../Pages/Identity/Forgot/ForgotPasswordConfirmation.en.resx              |  6 ------
 .../Pages/Identity/Forgot/ForgotPasswordConfirmation.resx                 |  6 ------
 .../Pages/Identity/Forgot/ForgotPasswordConfirmation.zh-CN.resx           |  6 ------
 .../Pages/Identity/Register/RegisterConfirmation.de-DE.resx               |  6 ------
 .../Pages/Identity/Register/RegisterConfirmation.en.resx                  |  6 ------
 .../Pages/Identity/Register/RegisterConfirmation.resx                     |  6 ------
 .../Pages/Identity/Register/RegisterConfirmation.zh-CN.resx               |  6 ------
 12 files changed, 27 insertions(+), 78 deletions(-)
```

Plus one new untracked file:

```
 tests/Server.UI.IntegrationTests/ResetLinkDisclosureComponentTests.cs     | 244 ++++++++++++++
```

**Net: 78 lines deleted, 27 added** — and of the 27, 19 are explanatory comments and 1 is the
`Register.razor` publish described in §3.4. The production-logic change is almost pure deletion.

---

## 7. Anomalies

1. **The first red run was invalid**, and is disclosed rather than quietly re-run: 5 of 7 tests
   failed on a missing `IMediator` registration in the fixture, not on the defect. Fixed, re-run,
   and only the second run is cited as evidence (§4.3).
2. **`Register.razor` gained one line** (§3.4). It is the only non-deletion in the commit. Without
   it the `RequireConfirmedAccount` branch would have lost its sole delivery mechanism.
3. **HTTP-level tests were not written**, deliberately (§4.1). At `prerender: false` they cannot
   observe component markup, so they would have been green against the broken code. The brief's
   assertion is implemented at the component level instead.
4. **Line endings.** `ForgotPasswordConfirmation.razor` was rewritten whole and initially came out
   LF where the repo uses CRLF. Normalised back to CRLF so the committed diff is content-only;
   verified afterwards that the file has no BOM either before or after, matching HEAD.
5. **Two pre-existing oddities were left untouched**, being outside this commit's one job: the
   `"Rest password email sent to…"` typo in `Forgot.razor:80`, and the address being logged on the
   success branch only (a log-based enumeration oracle). Both are Pass 21 work per the Pass 19
   plan.
6. **Scratch:** a Playwright install under the session scratchpad, deleted. The app was run on
   port 5199 and is confirmed stopped (port closed). One password-reset token was generated for
   `administrator@localhost` during the live check — unredeemed, single-use, time-limited, and mail
   delivery is the Sink in Development so nothing left the machine. Three log rows and one sink
   email file were produced under `src/Server.UI/bin/` (build output, not source).

---

## 8. Cherry-pick note for Yoab

**This commit contains exactly one change**, in 12 tracked files plus 1 new test file:

- `ForgotPasswordConfirmation.razor` — delete `ResetPasswordLink` and the block rendering it
- `Forgot.razor` — navigate to the bare page URL
- `RegisterConfirmation.razor` — delete `EmailConfirmationLink` and the block rendering it
- `Register.razor` — stop appending the link in both branches; publish the activation notification
  in the branch that previously relied on the on-screen button
- 8 `.resx` files — remove the four now-dead strings from every locale
- `tests/Server.UI.IntegrationTests/ResetLinkDisclosureComponentTests.cs` — 8 regression tests

Nothing else is touched. It can be cherry-picked as a unit.

**It must be applied separately to IMS, and to every other already-generated project.** A
`dotnet new` template has no update path to projects generated from it: fixing the template fixes
only projects generated *after* this commit. IMS was generated before it and therefore still
carries the live defect until this commit is applied there by hand.

**Two things to check in IMS before applying, in this order:**

1. **Is any IMS instance reachable from an untrusted network with `/account/forgot-password`
   enabled?** If yes, that instance is exposed to unauthenticated account takeover right now, and
   the patch goes to IMS *first* and the template second.
2. **Do not rely on `AllowSelfRegistration=false` as mitigation.** `SelfRegistrationMiddleware`
   blocks `/account/register` only; it does not touch the forgot-password route. The critical path
   remains fully exploitable with self-registration off. That setting reduces the *registration*
   twin's reach, nothing more.

If the four identity files have diverged in IMS, the load-bearing edits are small enough to apply
by hand: delete the two `[SupplyParameterFromQuery]` link properties and the two conditional blocks
that render them, and remove the two `["…Link"] = callbackUrl` dictionary entries from the
navigation calls. The regression tests port across unchanged.

---

## 9. Close

The defect Pass 19 reproduced is closed, at both instances, by deletion. Verified three ways: 8
regression tests that were red against the unfixed code and are green now; the full suite at
681 passed / 12 skipped / 0 failed with no new warnings; and a live run showing a bare landing URL,
no anchors, no token on the page, no off-site link from a hostile parameter — with the reset email
still rendering the full callback URL to the mailbox.
