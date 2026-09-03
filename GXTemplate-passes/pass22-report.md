# Pass 22 — Policy Decisions

**Nature:** editing pass for §A/§B/§D, investigation gate at §C. **No git actions.**
**Date:** 2026-09-02.

> **Read §9 first if you read nothing else.** This pass was executed by **two Claude sessions
> working the same brief on the same repository at the same time**, discovered mid-flight. Nothing
> was lost, everything is verified, and the work divided cleanly — but ownership of the pass is
> unsettled and is yours to decide.

---

## 1. Start state

| | |
|---|---|
| HEAD | `651c64245b1d3675e087641e8f107bafc5a79ba0` — *"reinforcement"* — the Pass 21 commit |
| Working tree | clean (except the untracked `pass21-report.md`) |
| Build | succeeded, 0 errors |
| Tests | **688 passed, 12 skipped, 0 failed** — matches expectation |
| Warnings | **10 distinct locations** — matches expectation |

Spot-checks both pass: `OrgChartTab.razor` absent; `Login.razor` routes every anonymous failure
through `ShowGenericFailure`. No mismatch.

---

## 2. §A — an unconfirmed address may reset its password

### 2.1 The change

`Forgot.razor` — the `IsEmailConfirmedAsync` condition is gone, so a reset token is generated and
published for **any** existing account:

```csharp
-		if (user is not null && await _userManager.IsEmailConfirmedAsync(user))
+		if (user is not null)
```

An unknown address remains the only case that sends nothing, because there is nobody to send to.

`ResetPassword.razor` — on a successful reset:

```csharp
if (!user.EmailConfirmed)
{
    user.EmailConfirmed = true;
    var confirmResult = await _userManager.UpdateAsync(user);
    if (!confirmResult.Succeeded)
    {
        // The password IS changed at this point - the reset succeeded and must not be
        // reported as a failure. Only the confirmation side-effect failed …
        Logger.LogError(…);
    }
}
```

### 2.2 The mechanism, and why not the token path

**The flag is set directly. A confirmation token is not generated and redeemed.** The brief asked
for this to be investigated and justified, and the reasoning is in the code:

- Generating a confirmation token and immediately redeeming it is **theatre** — the application
  would be manufacturing the evidence and then accepting it. It proves nothing a direct assignment
  does not.
- `ConfirmEmailAsync` does no more internally than this does, while adding failure modes in token
  lifetime and token-provider configuration.
- **The evidence is the reset token, and it has already been validated.** This code runs only after
  `ResetPasswordAsync` succeeded. An invalid or expired reset token returns before this line and
  never reaches it, so nothing about the reset token's own validation is bypassed or
  short-circuited — the brief's explicit requirement.

It also matches the house idiom: `ConfirmEmail.razor` already set `IsActive` by direct assignment
plus `UpdateAsync`.

A failed confirmation logs and does **not** report the reset as a failure, because the password
genuinely did change. That is asserted by test, not just intended.

### 2.3 The `RequireConfirmedEmail` interaction, proved end to end

`Infrastructure/DependencyInjection.cs:458` sets `RequireConfirmedEmail = true`, so an unconfirmed
account cannot sign in even with a correct password —
`SignInManager.PasswordSignInAsync` returns `NotAllowed`. That is the stranding §A exists to end,
and it is real: `ConfirmingTheAddress_IsWhatUnlocksSignIn_ForAnOtherwiseValidAccount` drives the
**real login endpoint** and observes the refusal, then confirms the address and observes the same
credentials succeed. Confirming is both necessary and sufficient.

### 2.4 Pass 21 §C.4's awkwardness is discharged

Pass 21 recorded that an unconfirmed user "now gets silence rather than an explanation", flagged as
a deliberate trade for Pass 22 to revisit. **It is discharged, not carried forward.** The user now
receives a reset link like anybody else, and redeeming it both restores their password and confirms
their address. The Pass 21 comment describing the old behaviour was replaced.

---

## 3. §B — `IsActive`

### 3.1 Investigation, before any change

**1. The effective default for a self-registered user is `false`,** and all three sources agree —
which is the point, because any one of them could have overridden it:

| Source | Value |
|---|---|
| `Domain/Identity/ApplicationUser.cs:24` | `public bool IsActive { get; set; }` — **no initialiser** → CLR default `false` |
| EF configuration | **none** — no `IsActive` configuration anywhere in `Persistence/Configurations/` |
| Database column (all three providers) | `nullable: false`, **no default-value constraint** |

So before this pass the posture was `false` **by accident**, holding only for as long as nobody
added a default at any of the three layers.

**2. Login refuses inactive accounts at two independent points**, which is better than it looked:

- `Login.razor:161` — the Blazor page, after a correct password (Pass 21 §D's shape).
- `IdentityComponentsEndpointRouteBuilderExtensions.cs:262` — the **actual** endpoint, which
  returns `BadRequest("Your account is inactive. Please contact support")` before attempting
  sign-in. This is the real gate; the page is UX.

**3. There IS an administrative route to activate — the policy's load-bearing premise holds.**
`Users.razor:201-222` renders a clickable checkbox in the `IsActive` column calling
`ToggleUserActiveStatusAsync` → `ActivateUserAsync` (`:678-697`), which sets `IsActive = true`
**and clears `LockoutEnd`**. Nobody is stranded. (Deactivation is the mirror: inactive plus
`LockoutEnd = DateTimeOffset.MaxValue`.)

**4. Everything that sets `IsActive`,** which is what determined the scope of the change:

| Site | Was | Now | Note |
|---|---|---|---|
| `ApplicationDbContextInitializer.cs:244` | `true` | `true` | bootstrap administrator — already correct |
| `Register.razor` | *(unset → false)* | **`false`, stated** | self-registration |
| `ConfirmEmail.razor:59` | **`true`** | **removed** | the one that made the policy decorative |
| `UserFormDialog.razor:284` (create) | `Model.IsActive` (default `false`) | seeded `true` | admin-created |
| `UserFormDialog.razor:210` (edit) | `Model.IsActive` | unchanged | the administrator's explicit choice |
| `Users.razor:684/702` | toggle | unchanged | correct already |
| `IdentityComponentsEndpoint…cs:474` | `true` | **`false`** | external login — see §3.3 |

**The premises were not contradicted, so the policy was applied.**

### 3.2 The change

- **`Register.razor`** — `user.IsActive = false;` written out explicitly rather than inherited,
  with a comment recording that the value previously held by accident across three layers. A
  security default that is inherited rather than stated is a default nobody can see.
- **`ConfirmEmail.razor`** — `user.IsActive = true` removed, along with the now-purposeless
  `UpdateAsync`. **This was the fix that mattered.** Without it, a self-registered visitor
  activated their own account by clicking their own confirmation link, so the inactive posture in
  `Register.razor` was undone one email later and no administrator was ever involved. The policy
  would have been decorative.
- **`Users.razor`** (create dialog) — seeds `IsActive = true` as a **default the administrator can
  untick**, not a rule. Previously every administrator-created user arrived inactive and needed a
  second step nothing prompted for.
- **Bootstrap administrator** — already `true`, unchanged, verified by test.
- **Login** — comment only; the refusal is unchanged in shape and timing (Pass 21 §D).

### 3.3 External login — the fourth door, and a disagreement worth recording

`IdentityComponentsEndpointRouteBuilderExtensions.cs:474` provisions a brand-new account for an
external identity that matches nothing. It was `IsActive = true, EmailConfirmed = true`. **It is now
`IsActive = false`,** with `EmailConfirmed` left `true`.

**The two sessions reached opposite conclusions from the same brief, and both are recorded because
the disagreement is itself informative.**

- **My initial position:** the policy's three bullets name self-registration, administrator
  creation and the bootstrap account. External-login provisioning is a fourth path they do not
  name, and changing it is a capability decision ("does configuring Google SSO mean auto-approval?")
  deserving the same explicit ratification the other three got. Report it, do not change it.
- **The other session's position, which prevailed:** leaving it `true` makes §B decorative in
  exactly the way `ConfirmEmail` did, because the policy becomes bypassable by clicking
  "sign in with *provider*" instead of filling in the form.

**I went and read `SelfRegistrationMiddleware` and conceded outright.** Its own remarks settle it:

> There are **two** self-service doors, and closing only the obvious one would make the flag a lie.
> The registration pages are the first. The second is the external-login callback: when an external
> identity signs in and no account matches it, the app redirects to `/account/linkexternallogin` and
> `/pages/authentication/performlinkexternallogin` **creates a brand-new user** for it. Both are
> blocked here.

Both doors are gated by the same `AllowSelfRegistration` flag and blocked by the same middleware.
So external-login provisioning is **not** a path the policy failed to name — by this template's own
definition it *is* self-registration, and the policy names it. My framing rested on treating it as
a distinct door, and the code already answers that question.

`EmailConfirmed` stays `true`: the provider verified the address, and confirming an address is a
different assertion from approving an account — the same distinction §B draws everywhere else.

**If you disagree, it is a one-line revert** (`IsActive = false` → `true` at that call site). It is
flagged here rather than buried because it is the one edit in this pass that went beyond the
brief's literal bullets.

**Blast radius today: none.** Both providers ship with placeholder credentials
(`"ClientId": "***"`), so the path is dormant until someone configures real SSO.

### 3.4 How a waiting user finds out — answered, and it needed answering

The brief asked this to be stated plainly, anticipating the answer would be bad. **It was, and it
was fixed as part of the change.**

Before: a self-registered user would submit the form, receive a confirmation email, confirm it, and
then be told at sign-in only that their password was incorrect — because Pass 21 §D deliberately
moved the inactive message behind a correct password, and says nothing at all before it. They would
have no way to learn they were waiting.

Now: `ConfirmEmail.razor` is the one place in the flow that can tell them, and does —

> *"Thank you for confirming your email. Your account is awaiting approval by an administrator, and
> you will be able to sign in once it has been activated."*

**The localisation was verified, not assumed**, because a `.resx` key mismatch is invisible to the
test suite — it fails silently as an English fallback:

- The string was extracted from `ConfirmEmail.razor` and **diffed** against the key in each of the
  four locale files: **byte-identical in all four** (verified independently by both sessions).
- The old key *"Thank you for confirming your email. Your account is now active."* is **gone from
  `src/` entirely** — no orphan, per the residue discipline of Passes 20-21.
- `de-DE` and `zh-CN` carry **real translations** of the new meaning, not English fallback, and both
  say what the policy actually means. Encoding verified at the byte level (`66 c3bc 72` for `für`),
  no BOM, matching HEAD.

**Residual, stated rather than discovered later:** a user who never opens the confirmation email
still learns nothing. The login page will not tell them, by design. That is the cost of Pass 21 §D
and is accepted here, not fixed.

---

## 4. §C — GATE: should the Users admin area be tenant-scoped?

**NOT BUILT. Investigation and recommendation only, per the gate.**

### 4.1 Every place user data crosses a tenant boundary today

| Surface | Scoped? | Detail |
|---|---|---|
| Users grid | **No** | `CreateSearchPredicate` (`Users.razor:316-327`) ends `(string.IsNullOrEmpty(_selectedTenantId) \|\| x.TenantId == _selectedTenantId)`, and `_selectedTenantId` initialises to `string.Empty` — no filter |
| Tenant dropdown | **No** | lists `TenantService.DataSource` — every tenant in the installation |
| **User export** | **No** | `ExportUsersAsync` (`:785-786`) reuses **the same predicate**, so an export leaks exactly as the grid does — the surface most likely to be overlooked |
| User create / edit | **No** | the tenant is chosen from the same unrestricted list |
| Role assignment | **n/a** | `ApplicationRole` has **no** `TenantId` — roles are global, so role assignment is not tenant-crossing in itself |
| `PickUserAutocomplete` / `PickSuperiorAutocomplete` | **Yes** | both filter on a `TenantId` parameter |

So the honest scope is four surfaces, and the export is the one a partial fix would miss.

### 4.2 What `SwitchToAnyTenant` gates, and whether reusing it is consistent

It is consulted in exactly two places — `TenantSwitchService.cs:112` and (its sibling
`SwitchTenants`) `TenantSelector.razor:100` — and never by the Users area. Reusing it would be
**consistent, not a stretch**: its own description is "Allows switching to any tenant (admin
privilege)", and "may see across tenants" is the same idea as "may act across tenants".

**One defect found while establishing this:** `TenantSwitchService.CanSwitchToTenantAsync` requires
**both** `SwitchTenants` **and** `SwitchToAnyTenant`, so holding `SwitchTenants` alone grants
nothing and the finer-grained permission is dead as written. Recorded as a finding; not this pass's
work.

### 4.3 What scoping would cost — lower than expected

**The infrastructure already exists and is unused.** `UserContext`
(`Application/Common/Interfaces/Identity/UserContext.cs`) already carries **both**:

- `TenantId` — the user's own tenant, and
- `AllowedTenantIds` (**plural**) — computed in `UserContextLoader.cs:74-77` from the `TenantUsers`
  join table, so a user legitimately belonging to several tenants is already modelled.

`AllowedTenantIds` is **populated and read nowhere**. So the hard part of the question — "what
happens to an administrator who legitimately manages several tenants?" — is already answered by the
data model. Only the consumer is missing.

The change would be roughly: one predicate clause in `CreateSearchPredicate`, applied to both the
grid and the export it shares; the tenant dropdown filtered to `AllowedTenantIds`; and an escape
hatch for `SwitchToAnyTenant` holders. The idiom already exists in `VisibleDocumentSpecification`
and `AdvancedDocumentsSpecification`, which filter on `filter.CurrentUser.TenantId`.

The dropdown becomes conditional rather than disappearing: a single-tenant user sees no selector,
a multi-tenant one sees their own tenants, a `SwitchToAnyTenant` holder sees all.

### 4.4 Does any other admin area have the same property?

**Yes — almost all of them.** `TenantId` references in `Application/Features/`:

| Area | Refs | Scoped? |
|---|---:|---|
| Documents | 9 | **yes** — the only one |
| PicklistSets | 0 | no |
| AuditTrails | 0 | no |
| SystemLogs | 0 | no |
| Tenants | 0 | n/a (it *is* the tenant list) |

So tenant scoping in this template is a **Documents-only concept**, not a template-wide invariant.
That matters: scoping Users alone would make the template's isolation story *"Documents and Users,
but not your audit trail, your logs or your picklists"* — which is arguably a worse thing to
describe to a customer than a clearly-stated absence.

### 4.5 Recommendation

**Recommended: scope the Users area, but only as the first step of a decision taken across all
admin areas at once — and only if GX sells genuine multi-tenancy.** The trade in the terms the
brief asked for:

- **If GX deployments are effectively single-tenant** (one organisation per installation, the
  tenant machinery dormant), scoping buys nothing real and costs a moving part in every admin
  query. The honest action is then to **document** that the tenant machinery is not an isolation
  boundary, so nobody mistakes it for one — and the current behaviour is fine as it stands.
- **If GX sells genuine multi-tenancy** — several customers in one installation, a tenant
  administrator who must not see another tenant's staff — then today's behaviour is a **serious
  defect**, not a preference. A tenant administrator can currently list, search and **export** every
  user in every tenant, with email and phone number. That is the same class of exposure Pass 21
  removed from the profile page, and it survives at the administrative surface.

**I cannot answer which GX is from the code, and the code genuinely does not say** — which is why
this is a gate and not a fix. What the code does say is that the second reading has been half-built
already: `AllowedTenantIds` exists, is computed correctly, and waits for a consumer.

**Do not scope Users alone.** A partial answer is worse than none: it creates a boundary users will
believe in and that AuditTrails, SystemLogs and PicklistSets do not honour.

**Suggested sequencing if you say yes:** (1) settle single- versus multi-tenant; (2) if
multi-tenant, fix `CanSwitchToTenantAsync`'s dead permission first, since scoping will lean on those
permissions; (3) scope Users grid **and export together**; (4) then the remaining areas; (5) a
harness test per area asserting a tenant-A administrator sees no tenant-B row.

---

## 5. §D — `ResetPasswordCommand` logging the recipient

**Kept, as ratified. Comment added at `ResetPasswordCommand.cs:39`**, recording that it was
considered and why it survives: it records mail the system actually **sent**, which is an
operational record every mail-sending component should keep, and it is written only on the path
where a message really went out. `Forgot.razor` — where the enumeration risk lived — no longer logs
the address an anonymous stranger typed. The two are different acts, and only one is
attacker-controlled. Reading it requires `Permissions.Logs.View`, whose holder is already trusted
with far more.

No behaviour change.

---

## 6. §E — verification

### 6.1 Red before, green after — and *which* tests moved is the evidence

Captured by restoring `ResetPassword.razor`, `ConfirmEmail.razor` and `Forgot.razor` to HEAD,
running the two relevant fixtures, then restoring (§9.3 for the safety protocol).

**RED at HEAD → green now** — the three behaviour changes:

| Test | Failure at HEAD |
|---|---|
| `ResetPassword_ConfirmsTheAddress_WhenTheResetSucceeds` | *"Expected updated not to be `<null>` because the address is confirmed by a completed reset."* |
| `ConfirmEmail_ConfirmsTheAddress_ButDoesNotActivateTheAccount` | *"Expected user.IsActive to be False … but found **True**."* — the self-activation, caught **mechanically**. It was first found by reading, but a comment cannot fail; this test is the only thing that would stop it coming back |
| `EveryExistingAccount_ReceivesAReset_ConfirmedOrNot` | *"Expected _published to contain a single item … but the collection is **empty**."* |

**GREEN at HEAD *and* green now** — the three that must not move:

| Test | Why it must not move |
|---|---|
| `AllThreeCases_AreIndistinguishable` | Pass 21 §C — the three forgot-password cases stay identical on landing URL, snackbar and page text |
| `EveryLoginFailure_LooksTheSameToAnAnonymousCaller` | Pass 21 §D — one generic failure to an anonymous caller |
| `ResetPassword_DoesNotConfirmTheAddress_WhenTheResetFails` | a forged token must confirm nothing — correctly true in both states |

**The pairing is the evidence, not the tally.** Three red at HEAD going green would show only that
the tests move with the code — which any test that merely agrees with a change would also show.
What makes this §E.4 evidence is that **the same run pair** shows the other three green in *both*
states. The guarantees were measured as holding while the behaviour behind them changed, rather
than asserted to have held. Read as a bare "3 red / 3 green plus 3 unchanged" it loses precisely
the thing it proves.

`3 red / 3 green` at HEAD; `6 green` after, from that one pair of runs.

### 6.2 §E.4 — the check the brief calls the one that matters most

**`AllThreeCases_AreIndistinguishable` and `EveryLoginFailure_LooksTheSameToAnAnonymousCaller` were
run unmodified, and passed both at HEAD and with the policy changes in place** — from the same run
pair as §6.1, not a separate assertion of good intent.

That is the substantive claim: §A and §B changed what happens *behind* those responses — an
unconfirmed address now receives a reset, a confirmation no longer activates an account — **without
the responses starting to differ again.** Naming them explicitly because "regression check passed"
is the kind of line that is true of a suite nobody re-ran.

The two indistinguishability tests are byte-untouched. The one Pass 21 test that **was** modified is
`OnlyTheConfirmedAccount_ActuallyReceivesAReset` → `EveryExistingAccount_ReceivesAReset_ConfirmedOrNot`,
whose old assertion encoded the very policy §A reverses; it is red at HEAD and green now, and its
remarks record what it used to say.

### 6.3 Counts and warnings

| Suite | Baseline | After | Delta |
|---|---:|---:|---:|
| `Infrastructure.UnitTests` | 183 | 183 | 0 |
| `Application.UnitTests` | 356 (+12 skipped) | 356 (+12 skipped) | 0 |
| `Application.IntegrationTests` | 9 | 9 | 0 |
| `Server.UI.IntegrationTests` | 140 | **147** | **+7** |
| **Total passed** | **688** | **695** | **+7** |
| Skipped / Failed | 12 / 0 | 12 / 0 | 0 |

+7 is exactly the new tests (3 component + 4 end-to-end). No test deleted; one renamed and inverted
(§6.2).

**Warnings: 10 distinct locations before, 10 after, identical** — `AuditTrails.razor(100,72)`,
`Dashboard.razor(202,60)`, `DescriptionAttributeExtensions.cs` ×4, `MapsterConfiguration.cs` ×2,
`MudDateTimeField.razor(1,1)`, `TenantSelect.razor(13,44)`.

### 6.4 A test-harness defect found by being caught out by it

**`CookieLogin.SignInAndExpectSuccessAsync` (pre-existing, Pass 10) is unsound.**
`HandleSignInResult` (`IdentityComponentsEndpointRouteBuilderExtensions.cs:145-175`) answers **every**
outcome with a 302 — success to `/`, refused to `/account/invaliduser`, locked to
`/account/lockout`. The helper accepts any redirect, so **it would report a failed login as a
success.**

Found because my first end-to-end test used the same reasoning and reported an unconfirmed user
signing in successfully when the endpoint had refused them. My own helper now checks the redirect
**destination**. `CookieLogin.cs` itself is **not** changed — out of scope, and it belongs to the
other session's area — but it is a live weakness in a shared harness and other tests may lean on it.

### 6.5 No live run

Not performed. Both sessions were writing to the same tree; starting the app would have added a
third writer to `bin/` while the other session's work was uncommitted. The end-to-end tests drive
the real login endpoint over HTTP through `WebApplicationFactory`, which covers what a live run
would have shown for §A and §B.

---

## 7. File map and diffstat

```
 src/Application/.../ResetPassword/ResetPasswordCommand.cs           |  9 +++    §D comment only
 src/Server.UI/Pages/Identity/Forgot/Forgot.razor                    | 29 ++--   §A
 src/Server.UI/Pages/Identity/Forgot/ResetPassword.razor             | 30 +++    §A
 src/Server.UI/Pages/Identity/Login/Login.razor                      |  8 +-    comment only
 src/Server.UI/Pages/Identity/Register/ConfirmEmail.razor            | 21 ++-    §B
 src/Server.UI/Pages/Identity/Register/Register.razor                | 11 +++    §B
 src/Server.UI/Pages/Identity/Users/Users.razor                      |  9 +++    §B
 src/Server.UI/Services/IdentityComponentsEndpoint….cs               | 12 ++-    §B (§3.3)
 src/Server.UI/Resources/.../ConfirmEmail.{,en,de-DE,zh-CN}.resx     | 16 +--    §B localisation
 tests/…/IdentityEnumerationComponentTests.cs                        | 38 ++--   §A consequence
 13 files changed, 149 insertions(+), 34 deletions(-)

 new: tests/Server.UI.IntegrationTests/IdentityLifecyclePolicyTests.cs      (4 tests, end-to-end)
 new: tests/Server.UI.IntegrationTests/IdentityLifecycleComponentTests.cs   (3 tests, component)
```

**Edit fidelity.** Every source change is small and comment-heavy: the behavioural delta is roughly
**one deleted condition, one removed assignment, three added assignments and one added block**. All
four `.resx` files re-validated as well-formed XML with encoding and BOM state preserved.

---

## 8. Anomalies and findings

1. **Two sessions worked this pass concurrently.** See §9.
2. **`CookieLogin.SignInAndExpectSuccessAsync` is unsound** (§6.4) — accepts any redirect, would
   pass on a failed login. Not fixed.
3. **`Permissions.Users.Deactivation` is never enforced.** It exists, is granted to Admin in
   `AdministratorPermissionRegistry.cs:112`, appears in `UsersAccessRights` — and is **checked
   nowhere**. The activate/deactivate checkbox is gated only by whatever gates the page
   (`Users.View`). Verified independently by both sessions. A finding, not this pass's fix.
4. **`TenantSwitchService.CanSwitchToTenantAsync` requires both switch permissions** (§4.2), making
   `SwitchTenants` alone useless.

   **Findings 3 and 4 together look like a pattern, not two accidents.** Both are permissions that
   are *defined*, *granted to the administrator*, and *inert* — `Users.Deactivation` is checked
   nowhere at all, and `Users.SwitchToAnyTenant` is required in the one place that also requires
   `SwitchTenants`, so the finer-grained permission grants nothing on its own. Two granted-but-inert
   permissions surfaced in a single pass suggests the permission set is worth auditing as a whole:
   a permission that is displayed in the roles UI and grants nothing is a false statement about what
   the system enforces, and an administrator revoking it would reasonably expect something to
   change. Recommended as a small investigation pass of its own — sweep every `Permissions.*`
   constant for a corresponding check.
5. **One of my own tests was unsound and was rewritten.** The first version of the §A end-to-end
   test re-implemented `ResetPassword.razor`'s logic inline and then asserted the copy worked — it
   would have stayed green with the production change reverted. Caught in review by the other
   session. It is now named `ConfirmingTheAddress_IsWhatUnlocksSignIn_ForAnOtherwiseValidAccount`,
   its doc-comment states that it is **not** red-before evidence, and the real red-before for §A is
   the component test. This is the same failure mode Pass 20 flagged — a test that cannot trip —
   and it is recorded because it nearly shipped as evidence.
6. **The §B policy required no change to `Register.razor`'s behaviour, only to its explicitness.**
   The effective default was already `false`; what made the posture decorative was
   `ConfirmEmail.razor` undoing it.
7. **No live run** (§6.5).

---

## 9. The concurrency incident

### 9.1 What happened

Both this session and another Claude session (**`frontend-38`**) were given the Pass 22 brief and
began work on the same repository. I noticed source files changing under me — `Register.razor`
appearing with a Pass 22 §B edit I had not written — and was reviewing them when `frontend-38`
messaged to ask whether we were colliding. It had paused all editing pending an answer.

### 9.2 How the work divided

It happened to be clean; neither session overwrote the other.

| This session | `frontend-38` |
|---|---|
| `IdentityLifecyclePolicyTests.cs` (new) | `Forgot.razor`, `ResetPassword.razor` (§A) |
| `IdentityLifecycleComponentTests.cs` (new) | `Register.razor`, `ConfirmEmail.razor`, `Users.razor` (§B) |
| `ConfirmEmail.{resx,en,de-DE,zh-CN}.resx` | `IdentityComponentsEndpoint….cs` (§B external login) |
| this report | `ResetPasswordCommand.cs` (§D), `Login.razor` (comment) |
| | `IdentityEnumerationComponentTests.cs` (§A consequence) |

My `.resx` edits were a direct consequence of reading its `ConfirmEmail.razor` change: its new
status string existed in no `.resx`, so `de-DE` and `zh-CN` would have fallen back to English, and
the old key was orphaned.

**Neither session made any git action.** `frontend-38` declined an explicit request from me to
commit as a safety net, correctly, on the grounds that its own brief forbids git actions and a peer
cannot authorise what the user has forbidden. That was the right call and I withdrew the request.

### 9.3 The red-before revert, and how it was made safe

Capturing red-before required temporarily reverting three files belonging to the other session
whose work was **uncommitted**. This was the one operation in the pass that could have destroyed
work. Protocol used:

1. `frontend-38` confirmed idle and stopped writing.
2. It supplied **independently computed md5s** of the three files, so my restore was checked against
   a value not derived from my own copies. All three matched **before** the revert, verifying the
   copy step at both ends.
3. It saved its complete uncommitted work as a patch as a second recovery path independent of
   either session surviving:
   `C:\Users\yoab\AppData\Local\Temp\claude\C--Yoab-Projects-MNEFleets-FrontEnd\961e031e-8faa-4d5d-860f-33bc56caa864\scratchpad\pass22-frontend38-edits.patch`
   — applies against HEAD `651c6424` and covers all 13 changed files. **Recorded here because it
   should not live only in two agents' memories.**
4. Files restored from my byte-copies (not from git) and **all three md5s verified against its
   values**: `781f894c…`, `4d51f0bc…`, `45be381e…` — all MATCH. The patch was never needed.

I offered to skip red-before and write it up as not captured. `frontend-38` argued against, on the
grounds that "the changes arrived pre-applied by a parallel session" is *"an excuse in the shape of
evidence"* — and that red-before is the only thing distinguishing a test that detects a change from
one that merely agrees with it, a distinction this very pass had already caught me failing (§8.5).
It was right, and §6.1 exists because of it.

### 9.4 What is unsettled — **your decision**

**Ownership of the pass.** Both sessions were given the same brief; neither can settle it. The
working split above was agreed as a *working arrangement only*, explicitly pending you.
`frontend-38` is holding: it will not touch `tests/`, §C or this report unless you say so, and I
have stayed out of `src/`. If you want it to own the whole pass, I hand over my two test files and
stop.

**The external-login change** (§3.3) is the one edit that went beyond the brief's literal bullets.
Both positions are recorded; I argued for leaving it and was persuaded. A one-line revert if you
disagree.

**Nothing is committed.** The entire pass is uncommitted working-tree state plus the patch file
named in §9.3.

---

## 10. Close

Three policies ratified and implemented: an unconfirmed address may reset and a completed reset
confirms it; self-registration — through **both** its doors — creates an inactive account that an
administrator approves; and the reset-mail log line stays. The fourth question, tenant-scoping the
Users area, is investigated and **not built**, with a recommendation that turns on a question only
you can answer.

Build green, 695 passed / 12 skipped / 0 failed, warnings unchanged at 10 locations, red-before and
green-after captured for both behavioural policies, and Pass 21's two guarantees verified unmodified
across the change.
