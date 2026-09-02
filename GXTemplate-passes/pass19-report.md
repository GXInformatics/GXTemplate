# Pass 19 — Upstream Defect Catalogue: Triage

**Nature:** investigation only. **Nothing in the repository was changed.** No git actions were taken.
**Date:** 2026-09-02.

---

## 1. Start state

| | |
|---|---|
| HEAD | `0e7d30b73eb82c263e87e433106f96974bcadc90` — *"gg"*, yoab, Tue 1 Sep 2026 10:08:45 +0100 |
| Working tree at start | clean |
| Working tree at end | clean (re-verified after all probes) |
| Build | `dotnet build CleanArchitecture.Blazor.slnx -c Debug` → **succeeded**, 0 errors, 10 warnings |

Full test counts, per suite (`dotnet test CleanArchitecture.Blazor.slnx --no-build`, exit 0):

| Suite | Passed | Skipped | Total |
|---|---:|---:|---:|
| `Infrastructure.UnitTests` | 183 | 0 | 183 |
| `Application.UnitTests` | 356 | 12 | 368 |
| `Server.UI.IntegrationTests` | 125 | 0 | 125 |
| `Application.IntegrationTests` | 9 | 0 | 9 |
| **Total** | **673** | **12** | **685** |

0 failures. The 12 skips are the Azurite-dependent blob-storage tests in `Application.UnitTests`.

### Start-state anomaly — read this before trusting the precondition

The precondition names *"Pass 18B committed"*. **There is no such commit.** The five most recent
commits are `0e7d30b7 gg`, `b184f547 IdleTiemout`, `8b99c20b Pass17`, `978f4656 pass16`,
`c807b55a Fin`. Nothing names Pass 18 or 18B, and `GXTemplate-passes/` contained exactly one file
before this report (`idle-timeout-portable-spec.md`) — no prior pass reports live in the
repository at all.

I did **not** stop. The reasoning: the stop rule exists to prevent working against the wrong
tree, and the risk it guards against cannot materialise in a pass that changes nothing. The tree
is clean, HEAD is the newest commit on `main`, the solution builds and the whole suite passes.
Everything below is therefore cited against `0e7d30b7`. If Pass 18B exists on an uncommitted or
unpushed branch elsewhere, re-run the citations before acting on them.

---

## 2. Headline

**Catalogue defects #1 and #2 are present, live, and critical.** They were reproduced end to end
against a running instance: an unauthenticated visitor who types a known email address into
`/account/forgot-password` is handed a **working password-reset link for that account, on screen,
in the address bar**, with no mailbox access required. This is unauthenticated account takeover of
any confirmed account, including the installation's administrator. Details and captured URLs in
§4.1.

**IMS is already generated from this template and therefore already carries this.** So does every
other project generated from it. See §8.5.

---

## 3. §A — Expected already fixed

### A.1 — #4, permission claims granted only inside the role-creation branch

**Role level: fixed, and fully covers the catalogued defect.**

`src/Infrastructure/Persistence/ApplicationDbContextInitializer.cs:166-209` — `EnsureRoleAsync`
creates the role when absent, then falls **through** to a grant-by-natural-key reconcile that runs
on every start:

```csharp
var held = (await _roleManager.GetClaimsAsync(role))
    .Where(c => c.Type == ApplicationClaimTypes.Permission)
    .Select(c => c.Value).ToHashSet(StringComparer.Ordinal);
var missing = permissions.Where(p => !held.Contains(p)).ToArray();
```

Five regression tests pin it (`tests/Application.UnitTests/Persistence/ProvisioningTests.cs:260-340`):
restores a revoked grant on Admin, restores one on Basic, recreates a deleted role, does **not**
revoke an operator-added grant, and adds no duplicates on a second run. The header comment at
`ProvisioningTests.cs:236-241` names the exact shape being prevented. This is a complete fix.

**User level: NOT covered — and it is worse here than in the catalogue. Reproduced.**

`ApplicationDbContextInitializer.cs:232-234`:

```csharp
var existing = await _userManager.GetUsersInRoleAsync(Roles.Admin);
if (existing.Count > 0) return;
```

The guard is role **membership**, and `AddToRoleAsync` (`:260`) lives only inside the
create branch. So if the administrator account survives but its Admin role membership does not,
the next start finds zero admins, tries to create a user named `Administrator` that already
exists, `CreateAsync` fails on duplicate username, and `:255` throws
`InvalidOperationException`. That propagates through `ProvisionAsync` →
`HostExtensions.InitializeDatabaseAsync` (`src/Infrastructure/Extensions/HostExtensions.cs:30`),
which nothing catches.

Reproduced against this template's own code (scratch probe, §10):

```
run 1: provisioned OK
admins before revoke: 1 (Administrator)
  remove role -> True
admins after revoke : 0
users still present : 1
run 2: THREW -> InvalidOperationException: Could not provision the administrator account:
                Username 'Administrator' is already taken.
admins after run 2  : 0
```

The installation is left with **zero administrators and an application that will not boot**. It
fails loudly rather than silently, which is better than the catalogue's version — but the outcome
is a self-inflicted denial of service that the reconcile one level out was written specifically to
prevent. The catalogue is right that MNEFleets hit the user-level version first; this template
fixed the role level and left the sibling.

Note this is **GX-only**: upstream neozhu guards on username, not role membership. The role-based
guard is this template's own rewrite (see its comment at `:225-229`), and it introduced this shape.

### A.2 — #7, routable pages with no role gate

**Structurally closed; one page is nonetheless doing something it should not.**

The fallback policy is at `src/Infrastructure/DependencyInjection.cs:~470`, with the reasoning
written out: any endpoint carrying no authorization metadata requires an authenticated user, and
the anonymous surface is opted back in explicitly.

All **26 routed pages**, enumerated:

| Gate | Count | Pages |
|---|---:|---|
| `[AllowAnonymous]` | 13 | Error, NotFound, and the 11 identity pages (Login, Register, Forgot, ResetPassword, ConfirmEmail, Lockout, InvalidUser, LinkExternalLogin, LoginWith2fa, LoginWithRecoveryCode, and the two confirmation pages) |
| `[Authorize(Policy = …)]` | 11 | Documents, PicklistSets, AuditTrails, SecuritySettings, SystemLogs, Tenants, Roles, Users |
| `[Authorize]` only | 1 | ChangePassword — correct, it changes your own password |
| **Nothing — FallbackPolicy only** | **2** | `/` (Dashboard), `/user/profile` (Profile) |

Dashboard is fine: it injects only `IStringLocalizer` and dispatches nothing
(`src/Server.UI/Pages/Dashboard/Dashboard.razor` — the only `@inject` is the localizer).

**`/user/profile` is not fine.** `Profile.razor:33` renders `<OrgChartTab />`, and
`src/Server.UI/Pages/Identity/Users/Components/OrgChartTab.razor:20-45` does this:

```csharp
var users = await UserManager.Users
    .Include(x => x.UserRoles).ThenInclude(x => x.Role)
    .Include(x => x.Superior)
    .ToListAsync();
```

No permission check, **no tenant filter**, every user in the installation — and it projects
`Email`, `PhoneNumber`, `DisplayName`, roles, tenant name and profile picture into the chart. So a
self-registered `Basic` user, whose entire granted permission set is `Documents.View` and
`Documents.Download` (`ApplicationDbContextInitializer.cs:110-114`), gets the complete staff
directory of **every tenant** by visiting their own profile page.

This is exactly the catalogue's #7 claim, surviving the fallback policy for the reason the pass
brief anticipated: the fallback gates *authentication*, and the page is legitimately
authenticated-only — it is one tab inside it that is not. It is also a multi-tenancy break
independent of the permission question.

### A.3 — #11, `ParallelNoWaitPublisher`

**Confirmed absent.** No match for `ParallelNoWait` anywhere in `src/`, `tests/`, or any `.csproj`.
The only publisher is `src/Application/Common/PublishStrategies/ChannelBasedNoWaitPublisher.cs`.

### A.4 — #12, registration timezone

**Fixed and parameterised, and it goes further than the catalogue asks.**

`src/Server.UI/Pages/Identity/Register/Register.razor:125-126` seeds the form from the configured
default, with the reason stated:

```csharp
// The configured default, not the server's zone - see IApplicationSettings.DefaultTimeZone.
_formModel.TimeZoneId = ApplicationSettings.DefaultTimeZone;
```

The model's own initialiser is `TimeZoneInfo.Utc.Id`, never `TimeZoneInfo.Local`
(`Register.razor:275-278`, with a comment saying why). The setting is validated at startup:
`AppConfigurationSettings.cs:59-75` fails the process on a blank or unrecognised zone id, and
`DependencyInjection.cs:66` notes the `ValidateOnStart` wiring. The seeder uses the same source
(`ApplicationDbContextInitializer.cs:250`).

### A.5 — #13, email templates pruned from the publish set

**Fixed, and the guard exceeds what the catalogue asks for.**

`src/Infrastructure/Infrastructure.csproj:55-56`:

```xml
<Content Remove="Resources\EmailTemplates\**\*.sbn" />
<None Include="Resources\EmailTemplates\**\*.sbn" CopyToOutputDirectory="PreserveNewest" />
```

Templates are `.sbn` (Scriban), not `.cshtml`, so the Web SDK's Razor glob never sees them.

The guard is `src/Infrastructure/Services/Mail/MailTemplateGuard.cs`. The catalogue asks for
presence; this checks **four** things, each catching a different accident: the file exists; it
decodes as *strict* UTF-8 (`throwOnInvalidBytes: true`, because `File.ReadAllText`'s default
replaces bad bytes silently); it contains no U+FFFD (already-mangled bytes re-saved); and it
**parses** as Scriban.

On the second half of the question — does a missing template fail boot outside Development —
`src/Infrastructure/Services/Mail/MailStartupCheck.cs:58-73`: problems are logged, Development
continues with an explicit "it would refuse to start anywhere else", and every other environment
throws. `HostExtensions.cs:43` calls it, and its comment names it as the only thing in the boot
sequence that can refuse to start the application. Fully covered.

### A.6 — #14, test projects absent from the solution

**Fixed.** All four are in `CleanArchitecture.Blazor.slnx` under `/tests/`:
`Application.IntegrationTests`, `Application.UnitTests`, `Server.UI.IntegrationTests`,
`Infrastructure.UnitTests`. `dotnet test` on the solution reports **685 tests** across the four
(counts in §1) — non-zero on every one.

### A.7 — #19, template residue

**`/account/loginx`: confirmed absent.** No file, no route, no string match for `loginx` anywhere
outside `.git`. The route table in §3.2 is complete and contains exactly one login page. There is
no duplicated auth surface.

Also absent: `chatbot`/`ChatBot` (0 files), `/public/index` (0 matches), `Fishbone` (0). The
`Product`/`Contact` matches are all incidental English — "production", "product" in prose comments,
"contact support" in user-facing strings — none is a feature file, entity, permission constant or
route.

**Orphan `.resx` strings: PRESENT.** The ghost the catalogue predicts is real, in
`src/Server.UI/Resources/Pages/Identity/Roles/Components/PermissionsDrawer.*.resx`:

| File | product | contact | total entries |
|---|---:|---:|---:|
| `PermissionsDrawer.resx` | 9 | 9 | 129 |
| `PermissionsDrawer.en.resx` | 9 | 9 | 129 |
| `PermissionsDrawer.de-DE.resx` | 9 | 9 | 129 |
| `PermissionsDrawer.zh-CN.resx` | 9 | 9 | 129 |

**72 orphan entries** — 18 distinct strings × 4 locales — describing permissions that no longer
exist: *"Allows viewing product details"*, *"Allows importing contact records"*, *"Set permissions
for product operations"*, and so on. Harmless at runtime (nothing looks them up), but they are
translation debt in three languages and a false map of the permission surface for the next reader.

---

## 4. §B — Expected present

### B.1 — #1 and #2: the reset link on screen, and the open redirect beside it — **CRITICAL**

Both present. Both reproduced against a running instance.

**The two files.**

`src/Server.UI/Pages/Identity/Forgot/Forgot.razor:74-84` generates the real token and puts it in
the browser's address bar:

```csharp
var code = await _userManager.GeneratePasswordResetTokenAsync(user);
code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(code));
var callbackUrl = Navigation.GetUriWithQueryParameters(
    Navigation.ToAbsoluteUri(ResetPassword.PageUrl).AbsoluteUri,
    new Dictionary<string, object?> { ["userId"] = user.Id, ["token"] = code });
await Mediator.Publish(new ResetPasswordNotification(callbackUrl, user.Email!, user.UserName!));
…
var url = Navigation.GetUriWithQueryParameters(
       Navigation.ToAbsoluteUri(ForgotPasswordConfirmation.PageUrl).AbsoluteUri,
       new Dictionary<string, object?> { ["ResetPasswordLink"] = callbackUrl });
Navigation.NavigateTo(url);
```

`src/Server.UI/Pages/Identity/Forgot/ForgotPasswordConfirmation.razor:1-17` renders it as a working
link, `[AllowAnonymous]`, **with no environment gate of any kind**:

```razor
@page "/account/forgotpasswordconfirmation"
@attribute [AllowAnonymous]
…
@if (ResetPasswordLink is not null)
{
    <MudText>@L["For testing purposes, you can directly access the password reset link below."]</MudText>
    <MudButton Href="@ResetPasswordLink" …>@L["Go to Reset Password"]</MudButton>
}
```

`[SupplyParameterFromQuery] public string? ResetPasswordLink { get; set; }` — the href comes
straight from the query string.

**Run evidence.** Instance started on `http://localhost:5199` (Development), driven with Playwright
1.62.1 / Chromium. Submitting `administrator@localhost` at `/account/forgot-password` landed here
(token abbreviated):

```
LANDED URL : http://localhost:5199/account/forgotpasswordconfirmation
             ?ResetPasswordLink=http%3A%2F%2Flocalhost%3A5199%2Faccount%2Freset-password
             %3FuserId%3D17cb7855-2b4f-4adf-82e2-9a85a7ca1cf0%26token%3DQ2ZESjhEZWFtU3RQ…

BUTTON HREF: http://localhost:5199/account/reset-password
             ?userId=17cb7855-2b4f-4adf-82e2-9a85a7ca1cf0&token=Q2ZESjhEZWFtU3RQ…

BODY TEXT  : Check Your Inbox  For testing purposes, you can directly access the password
             reset link below.  Go to Reset Password
```

That is a live reset token for the installation's administrator, handed to an unauthenticated
visitor who supplied nothing but the address. **Impact: full account takeover of any
email-confirmed account, no mailbox access, no authentication, one page load.**

**#2, the open redirect, in the same block.** `Href` is unvalidated, so the page will render a
button to anywhere:

```
GET /account/forgotpasswordconfirmation?ResetPasswordLink=https%3A%2F%2Fevil.example%2Fphish
OPEN REDIRECT: YES -> https://evil.example/phish
```

An anonymous page on the application's own origin, under its own branding, saying "Check Your
Inbox" and offering a primary-coloured button to an attacker's site. It is a ready-made phishing
pivot and it needs no account at all.

**A second instance the catalogue does not name.**
`src/Server.UI/Pages/Identity/Register/RegisterConfirmation.razor:17-23` is the same defect in the
registration flow — `[SupplyParameterFromQuery] EmailConfirmationLink`, rendered as
`<MudButton href="@EmailConfirmationLink">`, fed by `Register.razor:194-199`. Lower impact than the
reset link (it confirms an address rather than taking over an account), but it is a second
unvalidated-href open redirect on an anonymous page, and it lets anyone confirm an email address
they do not control.

**`.resx` variants carrying the strings.** Four files each, all under
`src/Server.UI/Resources/Pages/Identity/`:

| Strings | Files |
|---|---|
| *"For testing purposes, you can directly access the password reset link below."*, *"Go to Reset Password"* | `Forgot/ForgotPasswordConfirmation.resx`, `.en.resx`, `.de-DE.resx`, `.zh-CN.resx` (lines 64-68) |
| *"For testing purposes, you can directly access the email confirmation link below."*, *"Confirm Account"* | `Register/RegisterConfirmation.resx`, `.en.resx`, `.de-DE.resx`, `.zh-CN.resx` (lines 64-68) |

### B.2 — #3, tokens written to the log store

**Partly fixed; one handler still does it, and it does reach the store.**

Of the three handlers the catalogue names, the 2FA one was deleted here as dead code. Of the other
two:

- **Password reset — fixed.**
  `src/Application/Features/Identity/Notifications/ResetPassword/ResetPasswordCommand.cs:41` logs
  `"Password reset email sent to {Email}."` — address only, no URL.
- **User activation — PRESENT.**
  `src/Application/Features/Identity/Notifications/UserActivation/UserActivationCommand.cs:43-45`:

  ```csharp
  _logger.LogInformation(
      "Activation email sent to {Email}, Activation Callback URL: {ActivationUrl}.",
      notification.Email, notification.ActivationUrl);
  ```

  `ActivationUrl` is the `ConfirmEmail` callback built at `Register.razor:189-193`, carrying
  `userId` and the base64url email-confirmation token.

The two equivalents added since are clean: `SendWelcomeNotification` does not log its `LoginUrl`,
and `SendIdentityMailCommandHandler` (the administrator-facing request path) logs template and
address only.

**Does it reach the log *store*, not just the console?** Yes. The Serilog database sink is
configured at `src/Infrastructure/Extensions/SerilogExtensions.cs:110-115` with exactly two
exclusions — `CarriesBootstrapSecret` and `IsLogDatabaseDiagnostic`, both property-based — and no
minimum-level restriction beyond `MinimumLevel.Default: "Debug"`. An `Information` event carrying
neither property is written to the log database, and `/system/logs` reads it back to anyone holding
`Permissions.Logs.View`. This template *does* route some diagnostics deliberately; the activation
URL is not one of them.

**Rows in the local dev log database.** `GXApplication_Logs` on `localhost:5434`, table
`system_logs`:

| | before probes | after |
|---|---:|---:|
| total rows | 16 | 23 |
| rows matching `%callback url%` | **0** | **0** |
| rows matching `%token=%` | 0 | 0 |

Zero. The activation path had simply never been exercised on this machine — the code is present and
would write, but no row has been produced. (The 7 new rows are from my own probing; see §10.)

### B.3 — #5, user enumeration in forgot-password

**Present, in all three of the ways the catalogue describes.**
`src/Server.UI/Pages/Identity/Forgot/Forgot.razor:63-84`:

| Case | Message | Navigation | Logged? |
|---|---|---|---|
| unknown address | `"If an account with this email exists, a password reset link will be sent."` (Severity.**Error**) | **stays on** `/account/forgot-password` | no |
| known, unconfirmed | `"Your email address has not been confirmed. Please check your inbox…"` | **stays on** `/account/forgot-password` | no |
| known, confirmed | no snackbar at all | **navigates to** `/account/forgotpasswordconfirmation` | **yes** |

Live evidence for the two ends (Chromium, running instance):

```
#5 unknown         -> navigated=false url=.../account/forgot-password
                      msg="If an account with this email exists, a password reset link will be sent."
#5 known-confirmed -> navigated=true  url=.../account/forgotpasswordconfirmation  msg="(none)"
```

The middle row is from code reading, not a run — I did not create an unconfirmed account rather
than mutate the dev database further for a branch the six lines at `Forgot.razor:69-73` state
unambiguously.

The catalogue's central point holds exactly: **neutralising the message alone would leave
navigation as the tell.** Three outcomes are distinguishable by URL alone, with the snackbar
suppressed.

**The address is logged on the request path**, and to the store. `Forgot.razor:80`:
`Logger.LogInformation("Rest password email sent to {Email}.", _formModel.Email)` — note the typo,
`"Rest"`. It fires only on the *success* branch, so the log database is itself a second enumeration
oracle: anyone with `Permissions.Logs.View` reads off exactly which addresses are confirmed
accounts. Captured live in the dev log store:

```
Password reset email sent to "administrator@localhost".   (handler)
Rest password email sent to "administrator@localhost".    (page, Forgot.razor:80)
```

**Timing residual.** The catalogue leaves it open and so do I. Even with message and navigation
equalised, `GeneratePasswordResetTokenAsync` plus the notification enqueue run only on the known
path, so the confirmed case does measurably more work. Closing it needs the response time
decoupled from the branch — out of scope for a message/navigation fix, and worth stating as a known
residual rather than pretending a fix is complete.

### B.4 — #6, unconfirmed addresses cannot recover

**Present. Both halves are set.**

- `src/Infrastructure/DependencyInjection.cs:458` — `options.SignIn.RequireConfirmedEmail = true;`
  (and `RequireConfirmedAccount = true` at `:423` and `:460`).
- `Forgot.razor:69-73` refuses unconfirmed users outright, before any token is generated.

So a self-registered user who never receives or loses the confirmation email can neither sign in
nor recover, and there is **no anonymous "resend confirmation" surface** — `Register.razor` sends it
once, at registration, and nothing else does.

**Interaction with this template's own flows, before anything is proposed:**

- **Bootstrap.** The seeded administrator is created with `EmailConfirmed = true`
  (`ApplicationDbContextInitializer.cs:249`), so the bootstrap account is *not* caught by this. The
  lockout only reaches self-registered users.
- **Forced password change.** The administrator is created `MustChangePassword = true`
  (`:252`), projected onto the principal by `ApplicationUserClaimsPrincipalFactory.cs:45-47` and
  enforced by `ForcePasswordChangeGuard.razor:21`. That flow runs *after* sign-in and is orthogonal
  — it does not offer a recovery path to somebody who cannot sign in at all.
- **The administrative escape hatch exists.** `Users.razor:575-588` and `:610-622` let an
  administrator resend activation or reset through `SendIdentityMailCommand`. So the lockout is
  recoverable, but only by administrator intervention, and only if there is a reachable
  administrator.

The honest statement: the lockout is real, it is narrower than the catalogue's (it cannot strand
the bootstrap account), and any fix has to decide a **policy** question — whether an unconfirmed
address may reset a password — not just delete a branch.

### B.5 — #8, `Register.razor` never sets `IsActive`

**The field exists, registration does not set it, login gates on it — but the outcome here is
different from the catalogue's, and the difference is a configuration cliff rather than a bug.**

- Field: `src/Domain/Identity/ApplicationUser.cs:24` — `public bool IsActive { get; set; }`, no
  initialiser, so **`false`**.
- Registration: `Register.razor:137-141` sets `TenantId`, `LanguageCode`, `TimeZoneId`, `Email`,
  `UserName`, `CreatedAt`. **`IsActive` is never assigned.**
- Login gate: `Login.razor:137-142` — `if (!user.IsActive) { … "Your account is inactive…"; return; }`
- **The rescue:** `ConfirmEmail.razor:59-60` sets `user.IsActive = true` on successful email
  confirmation.

So **current behaviour with the shipped configuration is correct**: register → `IsActive=false` →
confirm email → `IsActive=true` → sign in. Email confirmation *is* the activation step, deliberately.

The defect is what happens off that path. `Register.razor:186-216` branches three ways, and only
the first two ever reach `ConfirmEmail`:

```csharp
if (_userManager.Options.SignIn.RequireConfirmedEmail)      { … ConfirmEmail link … }
else if (_userManager.Options.SignIn.RequireConfirmedAccount){ … ConfirmEmail link … }
else { Navigation.NavigateTo(Login.PageUrl); }
```

Set both to `false` — a plausible choice for an internal tool that does not want email
confirmation — and registration completes, sends the user to the login page, and login refuses them
forever with "Your account is inactive". **Nothing in the product can clear the flag except an
administrator** (`Users.razor:676-708`). That is a permanent dead end reachable by configuration
alone, with no warning.

**Interaction with `AllowSelfRegistration`.** It is a genuine runtime flag, not conditional source
removal: `AppConfigurationSettings.cs:52` (default `true`),
`src/Server.UI/Middlewares/SelfRegistrationMiddleware.cs:55` blocks the route when false, and
`Login.razor:34` hides the link. The template symbol at `.template.config/template.json:193-202`
only rewrites the appsettings value. So turning self-registration **off** removes this entire
surface — which means the blast radius of #8 (and of #1's registration twin) is confined to
generated projects that leave self-registration on. Worth stating in the fix, because it changes
the priority for a project like IMS depending on how it was generated.

**This is a policy decision, exactly as the catalogue says.** The three candidate answers —
default `IsActive = true` on the entity; set it explicitly at registration; or keep `false` and add
a startup guard that refuses a configuration where nothing can ever set it — are materially
different products. Recommending one is not this pass's job; recording that the current behaviour
is *correct under the shipped configuration and broken one setting away* is.

### B.6 — #9, ReconnectModal reloads on any circuit drop

**Present, worse than catalogued, and reproduced in both browsers.**

`src/Server.UI/Components/Feedback/ReconnectModal.razor`, the MutationObserver:

```js
const activeClasses = ['components-reconnect-show', 'components-reconnect-failed', 'components-reconnect-rejected'];
const hasClass = activeClasses.some(c => modal.classList.contains(c));
if (hasClass) { … if (!checkInterval) checkInterval = setInterval(checkServerStatus, 2000);
                checkServerStatus(); /* Probe once immediately */ }
```

**It does not distinguish the three classes at all.** `components-reconnect-show` means *"the
circuit dropped, Blazor is retrying, the server is fine"*; `-failed` and `-rejected` are the
terminal states. All three take the same branch.

And the branch is more aggressive than the catalogue's description. `checkServerStatus()` is called
**immediately**, it probes `/_framework/blazor.web.js`, and:

```js
if (response.ok) { console.log('[Reconnect] Server is ready (200 OK). Reloading...');
                   window.location.reload(); }
```

On a *transient* drop the server is by definition healthy, so the probe returns 200 on the first
try and the page reloads within milliseconds — before Blazor's own reconnection has had a chance to
succeed. The mechanism is not "reloads eventually"; it is "reloads immediately, precisely in the
case where reloading is least warranted."

**No unload guard exists.** Nothing sets a flag on `pagehide`/`beforeunload` to suppress the handler
during an intentional navigation, which is the standard remedy and the reason a full-page form POST
interacts badly with it.

**Reproduced.** Both browsers, against the running instance: load `/account/login`, set a marker
and type into a field, then add **only** `components-reconnect-show` — the transient signal Blazor
itself uses:

```
chromium  transient 'components-reconnect-show' -> marker=(GONE - page reloaded)  typedField=""
firefox   transient 'components-reconnect-show' -> marker=(GONE - page reloaded)  typedField=""
```

The half-typed username was destroyed in both. **The catalogue's "data loss in any browser" is
confirmed, and its framing of this as invisible in Chromium is wrong for the reload mechanism** —
Chromium loses the data just as Firefox does. What I could **not** reproduce is the narrower claim:
that Firefox suffers a *total login failure*, because Firefox drops the circuit during the login
form POST. That needs a real credential, and the seeded administrator's password is generated once
at first boot and not recoverable. **Marked for a hand-test:** sign in with Firefox against an
instance where the password is known, and watch for a reload loop on `/account/login`.

### B.7 — #18, `ConfirmDialog` hard-coded to "Delete"

**Not present as catalogued.** There is no `ConfirmDialog`. This template has two components and
routes non-deletions through the neutral one:

- `src/Server.UI/Components/Dialogs/ConfirmationDialog.razor` — generic, buttons are
  `AppStrings.Cancel` / `AppStrings.Confirm`, title supplied by the caller. No delete wording,
  no delete icon.
- `src/Server.UI/Components/Dialogs/DeleteConfirmation.razor` — the delete-specific one, which
  also dispatches the command itself.

Every call site was checked (`DialogServiceHelper` is the single entry point,
`src/Server.UI/Services/DialogServiceHelper.cs`). The two non-deletion confirmations both use the
generic dialog with their own title:

- `SystemLogs.razor:346-348` — *"Erase logs"* / *"Are you sure you want to erase all the logs?"*
- `FileUploadZone.razor:129-131` — `AppStrings.Delete` (it genuinely is a delete)

All `ShowDeleteConfirmationDialogAsync` call sites (Documents, PicklistSets, Tenants) are
deletions. So the catalogued defect does not apply.

**One residual worth recording.** `DeleteConfirmation.razor:2-6` hard-codes its own
`<TitleContent>` to `AppStrings.DeleteConfirmationTitle`, ignoring the title passed through
`ShowDialogAsync`. `Tenants.razor:221` and `:236` therefore pass `L["Delete the Tenant"]` and
`L["Delete Selected Tenants"]` and get *"Delete Confirmation"* on screen instead. Cosmetic,
localisation-visible, ~10 minutes.

---

## 5. §C — The publisher (#10), with the measurement Pass 5 lacked

### C.1 Current lifetime, registration, and whether DI actually disposes it

`src/Application/DependencyInjection.cs:19-34`:

```csharp
services.AddMediator(options =>
{
    options.NotificationPublisherType = typeof(ChannelBasedNoWaitPublisher);
    options.ServiceLifetime = ServiceLifetime.Scoped;
    …
});
```

**Scoped**, as Pass 5 left it — one publisher per DI scope, meaning one per Blazor circuit, each
with its own bounded channel (capacity 1000) and its own background reader task.

`IAsyncDisposable` is declared, **and it is genuinely called.** The class implements *both*
`IAsyncDisposable` and `IDisposable`
(`src/Application/Common/PublishStrategies/ChannelBasedNoWaitPublisher.cs:15`), and the
second is load-bearing: the comment at `:95-99` records that a service implementing *only*
`IAsyncDisposable` makes `IServiceScope.Dispose()` throw, and this application disposes some scopes
synchronously. Both paths funnel through `BeginDispose()` (`:113-122`), which completes the writer
exactly once via `Interlocked.Exchange`, then await/block on the drain. `PublisherDisposalTests`
covers it. This half is correct and better than a bare `IAsyncDisposable`.

### C.2 What drains at shutdown, and what is lost if not

**Draining is per-scope, not per-host.** There is no host-level drain: nothing registers an
`IHostApplicationLifetime` hook, no `IHostedService` flushes the publisher, and because the
publisher is scoped there is no singleton for the root provider to dispose at shutdown. Each
circuit drains when *its* scope is disposed. That covers an orderly circuit teardown; it does not
cover a process killed before scopes unwind (container SIGKILL, an IIS recycle that outruns the
drain, a crash).

**What still rides this path** — audit was moved off it entirely in an earlier pass, so this list
is the whole exposure:

| Notification | Published from | What is lost |
|---|---|---|
| `ResetPasswordNotification` | `Forgot.razor:79` | **a password-reset email the user is waiting for** |
| `UserActivationNotification` | `Register.razor:194` | **an activation email — the user cannot sign in without it** |
| `SendWelcomeNotification` | `ConfirmEmail.razor:57`, `Users.razor:885` | a welcome email (cosmetic) |
| `DocumentCreatedEvent` / `DocumentDeletedEvent` | `DispatchDomainEventsInterceptor.cs:120` | a log line only (`DocumentCreatedEventHandler.cs:22-29`) |
| `PicklistSetCreated/Updated/DeletedEvent` | same | a log line **and a picklist cache refresh** (`PicklistSetChangedEventHandler.cs:34-39`) — a stale picklist until the next invalidation |

So the material loss is **outbound identity email**. The two that matter are exactly the two a user
is actively blocked on. Note the administrator-initiated equivalents deliberately do *not* ride
this path — `SendIdentityMailCommand` is a request precisely so the administrator is told the truth
(`SendMailCommand.cs:8-25`), which is a good existing decision and limits the exposure to
self-service flows.

A second, smaller point: `Dispose()` **blocks** on the drain. A handler stuck on a slow Mailgun call
holds up synchronous scope disposal for as long as the HTTP timeout allows.

### C.3 Does the catalogue's concern apply at this template's scale?

**The performance concern: only under load this template does not currently generate.** Five
notification types, four of them rare (registration, reset, confirmation) and one tied to picklist
edits. A naive singleton's serial consumer would be invisible at ten notifications an hour.

**But the concern is not really about steady-state throughput.** The failure mode is a burst — a
bulk user import through `Users.razor`, each new user triggering a welcome or activation email —
against a single serial consumer with a 1000-slot bounded channel in `FullMode.Wait`. When the
channel fills, `WriteAsync` **blocks the publisher**, which means it blocks the request that called
`Mediator.Publish`. Under Scoped that pressure is per-circuit and self-limiting. Under a naive
singleton it is global: one slow mail handler stalls every circuit in the process. That is the real
argument against the naive conversion, and it is sharper than the raw multiplier.

### C.4 Reproducing the measurement against this template's own code

The catalogue's numbers are MNEFleets'. I re-ran the same shape — 600 notifications, a 40 ms
handler, 24 concurrent publishing circuits — against **this repository's**
`ChannelBasedNoWaitPublisher`, Release build (scratch harness, §10):

| Arrangement | Catalogue (MNEFleets) | **GXTemplate (measured here)** |
|---|---:|---:|
| SCOPED — one publisher per circuit | 1,092 ms | **1,182 ms** |
| SINGLETON — one publisher, one serial consumer | 28,535 ms | **28,437 ms** |
| Regression multiplier | 26× | **24×** |

**The catalogue's measurement transfers to this code essentially verbatim.** The singleton arm is
within 0.4% of MNEFleets'. Pass 5's instinct to leave the lifetime alone was correct, and it is now
correct *with evidence* rather than by caution.

I also measured the arm the catalogue recommends but did not quote — singleton plus N bounded
concurrent consumers (`SingleReader = false` plus N reader loops; a scratch variant, nothing in the
repository was touched):

| Consumers | Elapsed |
|---:|---:|
| 1 (naive singleton) | 28,437 ms |
| 8 | 3,561 ms |
| 16 | 1,799 ms |
| **24** | **1,185 ms** |
| 32 | 901 ms |

**Singleton + 24 consumers is 1,185 ms against Scoped's 1,182 ms — statistically identical.** The
catalogue's fix is validated against this template's own code, and MNEFleets' choice of 24, which
they reached by sweep, is exactly the break-even at this shape (24 circuits). It is not a magic
number: it is "one consumer per expected concurrent producer".

### C.5 Recommendation

**Do not convert to a singleton now.** Convert only when something on this list becomes true:

- a host-level drain is actually wanted (i.e. losing a queued reset email at shutdown becomes
  unacceptable), or
- notification volume rises enough that per-circuit publishers become a resource concern, or
- a notification type appears that must not be scoped to a circuit's lifetime.

**When that happens, the change is singleton + N bounded consumers, never a bare singleton** — the
numbers above are the reason, measured here, not inherited. Cost: ~1 day. `SingleReader = false`,
a consumer count from configuration defaulting to `Environment.ProcessorCount` or 24, the DI
lifetime change, an `IHostApplicationLifetime` drain hook, and tests for ordering-independence
(N consumers means handlers no longer run in publish order — the current five handlers are all
order-independent, but that becomes a documented constraint).

**The trade-off, stated plainly.** Scoped costs you a lost email when a process dies unscheduled,
and buys you natural per-circuit isolation and back-pressure that cannot cascade. Singleton+N buys
you one drain point at shutdown and one place to reason about mail concurrency, and costs you a
global queue where one slow handler can apply back-pressure to every circuit at once. At this
template's current volume the first is the better trade, and Pass 5 chose correctly. **What Pass 5
lacked and now has: the 24× number, so the "future option" it recorded is no longer an open
question — the naive form of it is closed, and only the bounded-consumer form is on the table.**

---

## 6. §D — Harness defects

### D.1 — #15, the harness's own container

`tests/Application.IntegrationTests/Testing.cs`. Mixed: the main defect is present, two of the three
siblings are already fixed, and there is a fourth the catalogue does not name.

**Main defect — PRESENT, latent.** `Testing.cs:219-225`:

```csharp
var roleManager = scope.ServiceProvider.GetService<RoleManager<IdentityRole>>();
foreach (var role in roles)
{
    await roleManager.CreateAsync(new IdentityRole(role));
}
```

The application registers `.AddRoles<ApplicationRole>()`
(`src/Infrastructure/DependencyInjection.cs:426`), so `RoleManager<IdentityRole>` is **not
registered** and `GetService` returns `null`. Line 224 dereferences it — this is a
`NullReferenceException`, not the catalogue's silent skip.

It is currently unreached: the block runs only when `roles.Any()`, which happens only via
`RunAsAdministratorAsync()`, and **no test calls it** (`RunAsDefaultUserAsync` passes an empty
array). So it is a landmine, not a live failure — it throws the first time anyone writes a test
needing an admin principal, which is precisely when they are least expecting a harness bug.

**Sibling 1 — default-user helper creating `Email = UserName`: ALREADY FIXED.**
`Testing.cs:213-215`:

```csharp
// Email = userName produced a bare name, which Identity's EmailValidator rejects - this helper
// could never succeed, which is why no test used it before deny-by-default required one.
var user = new ApplicationUser { UserName = userName, Email = $"{userName}@example.com" };
```

**Sibling 2 — singleton user-context accessor over a static field the per-test reset nulls:
ALREADY FIXED, and the ordering hazard is closed at both ends.** The mock evaluates lazily
(`Testing.cs:89-96`, with the reasoning in the comment), and `ResetState()` re-establishes a
principal immediately after nulling the field (`:277-281`):

```csharp
_currentUserId = null;
// Re-establish an authenticated principal after the wipe: with deny-by-default in the
// pipeline, a test that dispatches anything needs an ambient user context to authorize.
await RunAsDefaultUserAsync();
```

No pass-alone/fail-in-suite hazard remains here.

**Sibling 3 — business services not resolvable because the harness builds its own container:
PRESENT, structurally.** `Testing.cs:57-66` builds a bare `ServiceCollection` and registers only
`AddInfrastructure(_configuration).AddApplication()`. Nothing from
`src/Server.UI/DependencyInjection.cs` exists in it. Any test needing a Server.UI-registered
service cannot resolve it. Latent rather than biting, because the suite currently only exercises
Application-layer handlers — but it is why that suite is 9 tests and why the newer
`Server.UI.IntegrationTests` (125 tests, real `WebApplicationFactory`) exists alongside it.

**Fourth issue, not in the catalogue: the harness builds the container twice.**
`Testing.cs:102-104`:

```csharp
_scopeFactory = services.BuildServiceProvider().GetService<IServiceScopeFactory>();
EnsureDatabase();
using var scope = services.BuildServiceProvider().CreateScope();
```

Two calls to `BuildServiceProvider()` produce **two independent root containers**. Every singleton
exists twice, and the Respawn checkpoint at `:111` is created against a connection from the second
container while every test runs against the first. It happens to work because they read the same
connection string, but it is the same class of ordering hazard as the one the catalogue names, and
the second provider is never disposed.

### D.2 — #16, the harness pinned to LocalDB

**Present, known, and documented** — `README.md:673-680` states it as a deliberate limitation with
a rationale: those 9 tests assert handler behaviour against a real SQL Server, they **fail** rather
than skip without LocalDB, and repointing them at whatever `--Database` the wizard chose "would
quietly change what they prove."

`tests/Application.IntegrationTests/appsettings.json` sets `"DBProvider": "mssql"` and
`(localdb)\mssqllocaldb`.

**Is the catalogue's fix better, given this template ships three providers?** Partly — it is right
about two of its three parts and wrong about the third.

1. **"Read from configuration" — already done, and this weakens the catalogue's framing.**
   `Testing.cs:47-51` builds with `.AddJsonFile("appsettings.json", true, true).AddEnvironmentVariables()`.
   `DatabaseSettings__DBProvider` and `DatabaseSettings__ConnectionString` already override the
   file. The pin is a *default*, not a hard-coding.
2. **"Fail loudly if absent" — genuinely missing, and worth adopting.** The JSON file is registered
   `optional: true` and nothing validates the result. Delete or mistype that file and the tests
   fail with an opaque connection error rather than "the integration harness has no database
   configured."
3. **"Match the Respawn adapter" — genuinely missing, and it is the sharp edge.**
   `Testing.cs:111-116` passes `RespawnerOptions` with only `TablesToIgnore` and **no `DbAdapter`**,
   so it defaults to `DbAdapter.SqlServer`. Because part 1 already works, someone *can* point the
   harness at PostgreSQL via environment variables today — Infrastructure will happily pick the
   provider — and then Respawn will emit SQL Server syntax at every reset. The escape hatch the
   README advertises works only for "any SQL Server you can reach", and nothing says so in code.

**Recommendation: keep the documented limitation, adopt parts 2 and 3 as guards.** The README's
reasoning is sound and I would not overturn it — auto-following `--Database` really would change
what those 9 tests prove, and silently. But the current state relies on a reader finding a
paragraph in a 48 KB README. Add a startup assertion in the harness that the resolved provider is
`mssql` and fail with a message naming the limitation, and pass the matching `DbAdapter` explicitly
so the intent is in the code. That converts an undocumented trap into a loud, correct refusal at
~2 hours' cost, without weakening the tests.

### D.3 — #17, Respawn versus migration-seeded reference data

**Does not apply here. Record it as closed, do not carry it forward.**

Respawn 7.0.0 is used (`Application.IntegrationTests.csproj:32`, `Testing.cs:111`). But there is
**no migration-seeded reference data anywhere in this template**: zero `HasData` and zero
`InsertData` across all three migrator projects and all entity configurations. (The `HasDatabaseName`
matches in the grep are index names, not seed data.)

Every piece of reference data — roles, permission claims, the default tenant, the administrator,
picklists — is created at **runtime** by `ApplicationDbContextInitializer`, not by a migration. A
Respawn wipe destroys nothing a migration put there, and there is nothing for it to fail to
restore. The harness reinforces this: it never calls `ProvisionAsync()` at all, creating its own
users through `RunAsUserAsync`.

The defect is structurally impossible here, and would only become possible if someone added
`HasData` to a migration — worth a one-line note in whatever guards migrations, not a fix.

---

## 7. §E — Portable non-template traps

### E.1 — `MudDatePicker` calendar clipped by a `max-width` style

**Does not apply. There are no MudBlazor pickers in this template at all.**

A sweep for `Mud*Picker` across every `.razor` under `src/` returns **zero matches** — no
`MudDatePicker`, `MudDateRangePicker`, `MudTimePicker` or `MudColorPicker`. The only date input is
`src/Server.UI/Components/Inputs/Select/MudDateTimeField.razor`, a custom component wrapping
`MudTextField` with `InputType="InputType.DateTimeLocal"` — the **browser's native** date picker,
which renders outside the page's layout entirely and cannot be clipped by a CSS `max-width` on the
input. No constraining style was found on it either.

The catalogue's verification warning is still worth carrying forward: **a Playwright `.click()`
succeeds on a visually clipped cell**, so if a MudBlazor picker is ever introduced here, only a
geometric assertion (bounding box against the popover's clip rect) will catch this class of bug.
A test that clicks and passes proves nothing.

### E.2 — `ActivatorContent` inert in MudBlazor 9.7

**Not present.** MudBlazor is **9.8.0** (`src/Server.UI/Server.UI.csproj:35`).

More decisively, **no `MudFileUpload` in this template uses `ActivatorContent`.** All seven call
sites use `<CustomContent>` with `context.OpenFilePickerAsync`:

| File | Line |
|---|---|
| `Components/Inputs/Upload/FileUploadZone.razor` | 12-49 |
| `Pages/Documents/Components/UploadFilesFormDialog.razor` | 41-51 |
| `Pages/Identity/Roles/Roles.razor` | 70-79 |
| `Pages/Identity/Users/Users.razor` | 89-97 |
| `Pages/Identity/Users/Components/ProfileInformationTab.razor` | 48-54 |
| `Pages/Identity/Users/Components/UserFormDialog.razor` | 34-39 |
| `Pages/PicklistSets/PicklistSets.razor` | 76-87 |

`ActivatorContent` appears twice in the codebase — `Components/AppShell/TenantSelector.razor:19` and
`Components/AppShell/UserInfoCard.razor:42` — but both are on **`MudMenu`**, a different component
with a different parameter, unaffected by the catalogue's issue.

**Carry-forward note:** the progress feedback in `FileUploadZone` and `UploadFilesFormDialog` is a
`MudLoadingButton` inside `CustomContent`, which works. If anyone "modernises" these to
`ActivatorContent`, they walk straight into the catalogue's bug. Worth a comment at those call
sites if the pattern is ever revisited.

---

## 8. §F — Verdict and plan

### 8.1 The table

Severity is graded **for this template**, not the catalogue's grading for MNEFleets.

| # | Verdict here | Evidence | Severity (this template) | Est. cost |
|---:|---|---|---|---|
| **1** | **PRESENT — CRITICAL** | `ForgotPasswordConfirmation.razor:11-17`; `Forgot.razor:81-84`. **Live: reset token for `administrator@localhost` captured from the address bar, unauthenticated** | **Critical** — account takeover of any confirmed account | 2-3 h |
| **2** | **PRESENT — CRITICAL** | Same block, `Href="@ResetPasswordLink"` unvalidated. **Live: `?ResetPasswordLink=https://evil.example/phish` renders that anchor.** Second instance: `RegisterConfirmation.razor:20` | **Critical** — open redirect / phishing on own origin | folded into #1 |
| **3** | **Partly present** | `UserActivationCommand.cs:43-45` logs the callback URL; reaches the DB sink (`SerilogExtensions.cs:110-115`, no level filter). Reset handler already clean. **0 such rows in the dev store** | **High** — activation-token disclosure to any `Logs.View` holder | 30 min |
| **4** (role) | **Already fixed** | `ApplicationDbContextInitializer.cs:166-209`; 5 tests at `ProvisioningTests.cs:260-340` | — | — |
| **4** (user) | **PRESENT — different here** | `:232-234` + `:255`. **Reproduced: boot throws `Username 'Administrator' is already taken`, 0 admins** | **High** — self-inflicted boot failure, unrecoverable without DB surgery | 2 h + test |
| **5** | **PRESENT** | `Forgot.razor:63-84`. **Live: unknown → no nav; confirmed → nav.** Address logged to store (`:80`, rows captured) | **Medium** — enumeration by navigation *and* by log | 2 h (+ timing open) |
| **6** | **PRESENT** | `DependencyInjection.cs:458` + `Forgot.razor:69-73`; no anonymous resend | **Medium** — self-registered users need admin rescue; bootstrap unaffected | policy call, then 2-4 h |
| **7** | **Structurally fixed, one live gap** | FallbackPolicy at `DependencyInjection.cs:~470`; 26 routes enumerated. **`OrgChartTab.razor:20-45` leaks every user's email + phone across all tenants to any authenticated user** | **High** — data exposure + tenant-isolation break | 3-4 h |
| **8** | **PRESENT — different here** | `Register.razor:137-141` never sets it; `ConfirmEmail.razor:59` rescues it; `Register.razor:216` else-branch does not | **Medium** — correct today, permanent dead end one setting away | policy call, then 1-2 h |
| **9** | **PRESENT — worse** | `ReconnectModal.razor` observer treats all 3 classes alike + immediate probe. **Reproduced in Chromium *and* Firefox: marker and typed field destroyed** | **High** — data loss on every transient drop; possible Firefox login loop | 3-4 h + hand-test |
| **10** | **Scoped, deliberately** | `DependencyInjection.cs:22-23`; `IAsyncDisposable`+`IDisposable` both declared and called. **Measured here: 1,182 / 28,437 / 1,185 ms** | **Low now** — no host drain; loses queued identity email on hard kill | ~1 day *when triggered* |
| **11** | **Never applied / already removed** | Zero matches anywhere | — | — |
| **12** | **Already fixed** | `Register.razor:125-126, 275-278`; validated at `AppConfigurationSettings.cs:59-75` | — | — |
| **13** | **Already fixed, exceeds catalogue** | `Infrastructure.csproj:55-56`; `MailTemplateGuard.cs` (4 checks); `MailStartupCheck.cs:63-73` fails boot outside Development | — | — |
| **14** | **Already fixed** | All 4 projects in `.slnx`; 685 tests, all suites non-zero | — | — |
| **15** | **PRESENT (latent) + 2 siblings fixed + 1 new** | `Testing.cs:221` NRE, unreached; `:213-215` and `:89-96`/`:277-281` fixed; **`:102-104` builds two root containers** | **Medium** — landmine for the next test author | 2-3 h |
| **16** | **PRESENT, documented** | `appsettings.json` pin; `README.md:673-680`. Config override already works; **no `DbAdapter`, no loud failure** | **Low-Medium** — trap for anyone using the documented escape hatch | 2 h |
| **17** | **Does not apply** | Zero `HasData`/`InsertData` in any migrator or configuration; harness never provisions | — | record and close |
| **18** | **Does not apply** | `ConfirmationDialog.razor` is neutral; all `DeleteConfirmation` sites are deletions. Residual: hard-coded title ignores `Tenants.razor:221,236` | **Cosmetic** | 10 min |
| **19** | **Mostly fixed; residue present** | No `loginx`, no chatbot, no `/public/index`. **72 orphan `.resx` entries in `PermissionsDrawer.*.resx`** | **Low** — translation debt, misleading map | 1 h |
| **E.1** | **Does not apply** | Zero `Mud*Picker` in the codebase | — | — |
| **E.2** | **Not present** | MudBlazor 9.8.0; all 7 `MudFileUpload` sites use `CustomContent` | — | — |

### 8.2 Should anything be fixed before this report was finished?

**Yes — #1 and #2, and the honest answer is that they warranted interrupting this pass.**

The brief said "change nothing", so I did not, and I finished the triage. But the risk assessment
should be recorded plainly rather than softened: **an unauthenticated visitor can take over any
email-confirmed account on any instance of this template, including the administrator, in one page
load, leaving no failed-login trail.** That is not a defect to schedule. Everything else in this
catalogue can wait behind it.

The mitigating facts, stated so the urgency is calibrated rather than inflated:

- The reset flow is only reachable where the application is exposed to untrusted networks.
- The attacker must know a registered address — but `Login.razor:124-128` volunteers *"The specified
  user does not exist."*, so discovering one is trivial (see §8.6).
- It leaves an audit trace: `Forgot.razor:80` writes the address to the log database on every
  successful request.

**The fix is small and low-risk** — delete the `ResetPasswordLink` parameter and the branch that
renders it in `ForgotPasswordConfirmation.razor`, stop appending it in `Forgot.razor:81-84`, and
do the same for `RegisterConfirmation.razor`/`Register.razor`. It removes code; it adds none. It
should be a standalone commit that does nothing else, so it can be cherry-picked into IMS and any
other generated project immediately.

### 8.3 Recommended fix order and pass split

**Pass 20 — the critical fix, alone.** #1 and #2, both instances. Delete the query-parameter link
rendering from `ForgotPasswordConfirmation.razor` and `RegisterConfirmation.razor`; stop supplying
it from `Forgot.razor` and `Register.razor`; remove the four dead strings from all eight `.resx`
files. Add a `Server.UI.IntegrationTests` regression asserting that (a) the confirmation page
renders no anchor when given a `ResetPasswordLink`, and (b) the forgot-password redirect carries no
query string. Nothing else in the commit. **~half a day.**

**Pass 21 — the rest of the identity surface.** #7 (`OrgChartTab` — gate it on `Users.View` and
filter by tenant, or remove the tab from `/user/profile`), #3 (drop `{ActivationUrl}` from the log
statement), #5 (equalise message *and* navigation across all three branches; drop or downgrade the
address log; record the timing residual as accepted), and the `Login.razor` enumeration in §8.6.
These belong together because they are one threat model and share tests. **~2 days.**

**Pass 22 — the two policy calls.** #6 and #8. Both need a decision before code: may an unconfirmed
address reset a password, and what does `IsActive` mean at registration. Recommend deciding both in
one sitting since they are the same lifecycle. Then implement, plus a startup guard that refuses a
configuration in which a registered user can never become active. **~1 day after the decision.**

**Pass 23 — reliability and the harness.** #9 (distinguish `components-reconnect-show` from the two
terminal classes; add a `pagehide`/`beforeunload` guard; hand-test Firefox login), #4-user-level
(reconcile the administrator's role membership instead of throwing), #15 (`ApplicationRole`, the
double `BuildServiceProvider`), #16 (loud failure + explicit `DbAdapter`). **~2 days.**

**Pass 24 — housekeeping, or fold into any pass touching these files.** #19 orphan `.resx` (72
entries), #18 residual title, and a one-line note that #17 cannot occur while no migration seeds
data. **~2 hours.**

**Not scheduled: #10.** Leave Scoped. Record §5.4's measurements in whatever document holds Pass 5's
decision, so the next reader inherits the numbers rather than re-deriving them.

### 8.4 Upstream-facing versus GX-only

**Upstream-facing** — defects in neozhu's `CleanArchitecture.Blazor` that a contribution back would
fix for everyone:

| # | Why it is upstream |
|---:|---|
| **1, 2** | `ForgotPasswordConfirmation.razor`, `RegisterConfirmation.razor`, `Forgot.razor`, `Register.razor` are all upstream files carrying upstream's "For testing purposes" strings in upstream's four locales |
| **3** | `UserActivationNotificationHandler`'s log statement is upstream's |
| **5, 6** | `Forgot.razor`'s three-branch logic is upstream's |
| **7** | `OrgChartTab.razor` and `Profile.razor` are upstream's, unmodified in the relevant respect |
| **8** | `Register.razor` never setting `IsActive` is upstream's |
| **9** | `ReconnectModal.razor` is upstream's |
| **15** | `RoleManager<IdentityRole>` against `AddRoles<ApplicationRole>()` is upstream's harness |
| **16** | The LocalDB pin and the missing `DbAdapter` are upstream's |

**#1 and #2 together are the single highest-value contribution back**, and they are a deletion — the
easiest kind of patch to get accepted. If any upstream report is filed, file that one.

**GX-only:**

| # | Why |
|---:|---|
| **4** (user level) | The role-membership guard at `ApplicationDbContextInitializer.cs:232` is this template's own rewrite of upstream's username guard; it introduced this failure |
| **19** (orphan `.resx`) | The strings are orphaned *because GX removed* Products and Contacts; upstream still has those features and the strings are live there |
| **10** | The publisher lifetime is a GX decision recorded in Pass 5 |
| **18** (residual) | The two-dialog split is GX's |

### 8.5 A generated project inherits every unfixed defect

**IMS is already generated from this template**, so IMS currently carries, at minimum: #1, #2, #3,
#5, #6, #7, #8, #9, #15, #16, the #4 user-level boot hazard, and the 72 orphan strings — unless it
has diverged on those files since generation.

Two things follow.

**First, fixing this template does not fix IMS.** There is no update path from a `dotnet new`
template to an already-generated project. The Pass 20 commit needs to be applied to IMS
separately, which is why §8.3 recommends keeping it standalone and cherry-pickable.

**Second, IMS should be checked now, not after Pass 20.** The one question worth answering today:
**is any IMS instance reachable from an untrusted network with `/account/forgot-password`
enabled?** If yes, that instance is exposed to unauthenticated account takeover right now, and the
patch should go to IMS first and the template second. If IMS was generated with
`AllowSelfRegistration=false`, note that this blocks `/account/register` only — the
`SelfRegistrationMiddleware` does not touch the forgot-password route, so **#1 remains fully
exploitable regardless of that setting.**

### 8.6 Things the catalogue does not know about this template

Found while checking its claims; recorded so they are not lost.

- **`Login.razor:124-128` is a second, blunter enumeration oracle.** `"The specified user does not
  exist."` — a distinct message on an anonymous page, before any password check. It makes #5's
  subtlety moot: an attacker enumerates from the login page, then uses #1. Fix it in the same pass
  as #5.
- **`RegisterConfirmation.razor` is a second instance of #1/#2** that the catalogue does not name
  (§4.1).
- **`Testing.cs:102-104` builds the DI container twice**, producing two root containers, with the
  Respawn checkpoint created against the wrong one (§6.1).
- **The `DeleteConfirmation` dialog ignores the title it is given** (§4.7), so two `Tenants.razor`
  titles never render.
- **`Forgot.razor:80` reads `"Rest password email sent to…"`** — a typo in a log message that ships
  to the log database.

---

## 9. Anomalies, including where the catalogue is wrong about this template

**Recorded so the next reader does not act on a claim that does not hold here.**

1. **The precondition does not match the repository.** No Pass 18/18B commit exists; no prior pass
   reports are stored in the repo. See §1. I proceeded because the pass changes nothing.
2. **#11 was already gone.** No `ParallelNoWaitPublisher` anywhere. Do not go looking for it.
3. **#17 cannot occur here.** No migration seeds any data. Close it; do not carry it into future
   catalogues unless someone adds `HasData`.
4. **#18 does not apply.** This template already has a neutral `ConfirmationDialog` and routes its
   two non-deletion confirmations through it.
5. **E.1 does not apply.** There is not a single MudBlazor picker in the codebase.
6. **E.2 does not apply.** MudBlazor 9.8.0, and every `MudFileUpload` already uses the working
   `CustomContent` API rather than `ActivatorContent`.
7. **#9's Firefox framing is wrong for the reload mechanism.** The catalogue says this is invisible
   in Chromium. **It is not** — Chromium loses typed form data on a transient drop exactly as
   Firefox does, reproduced in both (§4.6). The Firefox-specific part is the narrower *total login
   failure* claim, which I could not test for want of a known password and have marked for a
   hand-test. Anyone verifying this in Chromium and concluding "not reproducible" would be drawing
   the wrong conclusion from a correct observation.
8. **Two of #15's three siblings are already fixed**, with comments explaining the fix
   (`Testing.cs:213-215` and `:89-96`). Do not re-fix them.
9. **#16's "read from configuration" is already done.** The harness reads JSON *and* environment
   variables. Only the loud-failure and `DbAdapter` halves are missing — and the missing `DbAdapter`
   is more dangerous *because* the configuration override already works.
10. **#4's user-level sibling behaves differently here** — a loud boot failure, not a silent missing
    grant — because this template rewrote the guard. The catalogue's description would lead someone
    to look for a silently role-less user; they should look for an application that will not start.
11. **The catalogue supplies a measurement this project genuinely lacked, and it holds.** §5.4
    reproduces 24× against this template's own code, within 0.4% of MNEFleets' singleton figure.
    This is the catalogue's most directly useful contribution here.

---

## 10. Scratch probe disclosure and cleanup

All probes ran **outside the repository**, in the session scratchpad
(`…\14cd287f-46d4-4f56-9669-0c73cc181700\scratchpad\`). **No file in the repository was created,
modified or deleted** other than this report. Tree verified clean before writing it; HEAD unchanged
at `0e7d30b7`.

| Probe | Purpose | Disposition |
|---|---|---|
| `pgprobe/` | .NET console app, read-only queries against the dev log DB `GXApplication_Logs` (`localhost:5434`) for callback-URL rows | deleted |
| `adminprobe/` | .NET console app referencing `Infrastructure.csproj`, SQLite **in-memory** DB, reproduced the §3.1 boot failure | deleted |
| `pubbench/` | .NET console app referencing `Application.csproj`, Release, reproduced §5.4. Included a scratch-only `MultiConsumerPublisher` variant — **nothing in the repository was altered to measure it** | deleted |
| `pw/` | Node + Playwright 1.62.1 (`npm install` local to the scratchpad), drove the flows in §4.1, §4.3, §4.6 | deleted |
| `jar.txt`, `fp.html`, `fpc.html` | curl output while establishing that prerendering is off | deleted |

**Side effects outside the repository, disclosed:**

1. **The application was run** on `http://localhost:5199` (Development), built from the existing
   `--no-build` output. Started at 18:45, **stopped and confirmed stopped** (process terminated,
   port 5199 closed). Startup ran the normal boot sequence, including migrations and idempotent
   provisioning, against the pre-existing dev PostgreSQL on `localhost:5434`.
2. **Two password-reset tokens were generated** for `administrator@localhost` while proving §4.1.
   Both are single-use and time-limited, neither was redeemed, and mail delivery is the Sink in
   Development so nothing left the machine. They will expire on their own; the administrator's
   password is unchanged.
3. **Seven rows were added** to the dev log database `GXApplication_Logs.system_logs` (16 → 23),
   four of them the "email sent" lines quoted in §4.3. Left in place — deleting log rows to tidy up
   a read-only investigation would be a worse trade than disclosing them.
4. **A local `node_modules`** was installed under the scratchpad only, and removed with it.

No changes were made to the business database `GXApplication` beyond what an ordinary application
start performs.

---

## 11. Close

Nothing in the repository changed except the addition of this report. Build and test results are
unchanged from §1 and were re-verified after every probe. The recommendation that matters is one
sentence: **fix #1 and #2 first, in a standalone commit, and check whether any IMS instance is
currently exposed.**
