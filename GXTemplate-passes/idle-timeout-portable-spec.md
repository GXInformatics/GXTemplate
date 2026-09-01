# Idle Timeout & Auto-Logout — Portable Implementation Specification

A complete, self-contained description of the idle-timeout feature as it exists in a
.NET 9 / Blazor Server / MudBlazor / ASP.NET Core Identity application built on a Clean
Architecture layering (`Domain`, `Application`, `Infrastructure`, `Server.UI`, plus
per-provider `Migrators` projects).

This document is written for someone rebuilding the feature in a repository that cannot see
the original. Every code block below is copied verbatim from the source files unless the
text at that point says otherwise. Where a file is only partly reproduced, the omission is
stated explicitly at that spot.

**Namespace note.** The source uses the root namespace `CleanArchitecture.Blazor.*`
throughout. Substitute your own root namespace wherever it appears; nothing else about the
namespaces is load-bearing.

**Comment note.** Several source comments refer to internal revision numbers ("Pass 16A",
"Pass 18"). They are reproduced verbatim because the instruction is to copy, not
reconstruct. They mean nothing outside the original repository — delete or reword them when
you paste the code in. Their factual content is restated in §6 of this document in a form
that stands alone.

---

## Table of contents

1. [Overview](#1-overview)
2. [Every file, in full](#2-every-file-in-full)
3. [Configuration](#3-configuration)
4. [The three policy levels](#4-the-three-policy-levels)
5. [UI behaviour](#5-ui-behaviour)
6. [Decisions worth carrying](#6-decisions-worth-carrying)
7. [Porting notes](#7-porting-notes)
8. [Corrections to the claims supplied with this brief](#8-corrections)

---

## 1. Overview

The feature signs a user out after a configurable period of inactivity, having first warned
them with a modal countdown that offers to keep the session alive. An administrator sets the
policy on a screen; a user may narrow it — never widen it — on their profile; a deployment
sets the bounds both are held to, in configuration, validated at startup.

It rests on one principle, and every design decision in it follows from that principle:

> **Client-side detection is the user experience. Server-side principal validation is the
> enforcement. Both read the same effective policy.**

The browser half (`gxIdleTimeout.js` plus `IdleTimeoutMonitor.razor`) watches input events,
shares activity across tabs through `localStorage`, opens a warning dialog, counts down
against an absolute deadline, and drives the sign-out. None of it is trusted. A JavaScript
timer can be paused on a breakpoint, disabled, or simply stop when the Blazor circuit drops —
and while the authentication cookie remains valid the user remains authenticated, however
convincing the modal covering their screen.

The server half (`IdleSessionEnforcer`, invoked from the cookie handler's
`OnValidatePrincipal`) is what actually ends the session. It runs on **every authenticated
HTTP request**, reads the policy in force **at that moment**, compares it against a
last-activity stamp carried in the authentication ticket's own properties, and rejects the
principal when the window has elapsed. It would end the session whether or not the JavaScript
ever ran.

Both halves resolve the effective policy through one interface,
`IIdleTimeoutPolicyProvider`, so the countdown the user sees and the deadline the server
enforces cannot drift apart. Because the server reads the *current* policy rather than one
baked into the cookie at sign-in, an administrator shortening the window takes effect on
sessions that are already open — which is the entire reason for putting the setting on a
screen rather than in a config file.

A third piece exists only because of Blazor Server: a keep-alive endpoint at
`/account/keep-alive`. A user working inside one long-lived SignalR circuit makes almost no
HTTP requests, so a sliding authentication cookie can expire underneath somebody who has been
working continuously for hours. The browser pings that endpoint while the user is active; it
is the only path the enforcer treats as evidence of presence.

### The pieces at a glance

| Concern | Mechanism |
|---|---|
| Detect inactivity, warn, count down | `wwwroot/js/gxIdleTimeout.js` + `IdleTimeoutMonitor.razor` |
| End the session gracefully | the application's existing sign-out endpoint |
| End it even if the circuit is dead | the JS deadline is absolute, and fires without .NET |
| Guarantee it ends regardless of the browser | `IdleSessionEnforcer`, in `OnValidatePrincipal` |
| Absolute upper bound on any session | cookie `ExpireTimeSpan` = max window + countdown + grace |
| Keep a live circuit's cookie alive | `POST /account/keep-alive` |
| Resolve the effective policy | `IIdleTimeoutPolicyProvider` (cached, invalidated on save) |

---

## 2. Every file, in full

### 2.0 File inventory

| # | Path | New or modified | What it is |
|---|---|---|---|
| 2.1 | `src/Server.UI/wwwroot/js/gxIdleTimeout.js` | **new** | Idle detection, cross-tab coordination, countdown deadline, keep-alive ping |
| 2.2 | `src/Server.UI/Components/Security/IdleTimeoutMonitor.razor` | **new** | The warning dialog and the circuit-side bridge to the JS module |
| 2.3 | `src/Application/Common/Interfaces/IIdleTimeoutPolicyProvider.cs` | **new** | Policy records + the provider interface |
| 2.4 | `src/Application/Common/Interfaces/IIdleTimeoutSettings.cs` | **new** | The deployment bounds interface |
| 2.5 | `src/Infrastructure/Configurations/IdleTimeoutSettings.cs` | **new** | Options class, cookie-lifetime arithmetic, startup validation |
| 2.6 | `src/Infrastructure/Services/Security/IdleTimeoutPolicyProvider.cs` | **new** | Caching provider: administered policy + per-user narrowing |
| 2.7 | `src/Infrastructure/Services/Security/IdleSessionEnforcer.cs` | **new** | Route constants + the per-request enforcement |
| 2.8 | `src/Domain/Entities/SecurityPolicy.cs` | **new** | The single-row policy entity |
| 2.9 | `src/Infrastructure/Persistence/Configurations/SecurityPolicyConfiguration.cs` | **new** | EF configuration (explicit table name) |
| 2.10 | `src/Domain/Identity/ApplicationUser.cs` | modified | `int? IdleTimeoutMinutes` added |
| 2.11 | `src/Infrastructure/Persistence/ApplicationDbContext.cs`, `src/Application/Common/Interfaces/IApplicationDbContext.cs` | modified | `DbSet<SecurityPolicy> SecurityPolicies` added |
| 2.12 | `src/Application/Features/SecuritySettings/**` | **new** | Query, command, validator, permissions |
| 2.13 | `src/Server.UI/Pages/SystemManagement/SecuritySettings.razor` | **new** | Administrator screen |
| 2.14 | `src/Server.UI/Pages/Identity/Users/Components/SecurityTab.razor` | **new** | Per-user preference screen |
| 2.15 | `src/Server.UI/Services/IdentityComponentsEndpointRouteBuilderExtensions.cs` | modified | Keep-alive endpoint added; origin helper and logout endpoint pre-existed |
| 2.16 | `src/Server.UI/Middlewares/SecuritySettingsPageMiddleware.cs` | **new** | 404s the admin route when the feature is off |
| 2.17 | `src/Infrastructure/DependencyInjection.cs` | modified | Options binding, service registration, cookie configuration |
| 2.18 | `src/Server.UI/DependencyInjection.cs` | modified | One `UseMiddleware` line |
| 2.19 | `src/Migrators/*/Migrations/*_InitialCreate.cs` | modified | `SecurityPolicies` table + `AspNetUsers.IdleTimeoutMinutes` column |
| 2.20 | `src/Application/Features/SecuritySettings/Security/SecuritySettingsPermissions.cs`, `src/Application/Common/Security/AdministratorPermissionRegistry.cs`, `src/Infrastructure/Persistence/ApplicationDbContextInitializer.cs` | new + modified | Permission constants and their route to the administrator grant |
| 2.21 | `src/Server.UI/Services/Navigation/MenuService.cs` | modified | Menu entry + removal when disabled |
| 2.22 | `src/Server.UI/Pages/Identity/Users/Profile.razor` | modified | Conditional Security tab panel |
| 2.23 | `src/Server.UI/Pages/Identity/Login/Login.razor` | modified | `reason=idle` informational alert |
| 2.24 | `src/Server.UI/Layouts/AppLayout.razor` | modified | Renders the monitor once, inside `<Authorized>` |
| 2.25 | `src/Application/Common/Constants/AppStrings.cs` | modified | Localised strings |
| 2.26 | `src/Server.UI/appsettings.json` | modified | The `SecuritySettings:IdleTimeout` block |
| 2.27 | `src/Infrastructure/Services/InMemoryTicketStore.cs` | pre-existing | Server-side ticket store; relevant context, not part of the feature |
| 2.28 | tests (five files) | **new** | See §2.28 |

---

### 2.1 `src/Server.UI/wwwroot/js/gxIdleTimeout.js` — **new**

The whole file. This is the only JavaScript the feature adds.

Note the location: `wwwroot/js/`, **not** collocated beside the component as
`IdleTimeoutMonitor.razor.js`. Collocated scripts are auto-served only for Razor class
libraries; in an application project a `.razor.js` file is not served and the dynamic
`import` fails at runtime with a 404 that surfaces only in the browser console.

```javascript
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Idle detection, the warning countdown, and the hard deadline.
//
// This module is the USER EXPERIENCE half of the idle timeout. It is not the enforcement: the
// server ends sessions in IdleSessionEnforcer, on every authenticated request, and would do so
// whether or not this file ever ran. Nothing here is trusted.
//
// It never calls .NET per input event - it keeps timestamps locally and invokes .NET only on state
// transitions (warning opens, countdown ticks, activity resumes elsewhere), so an active user costs
// no SignalR traffic at all.

const STORAGE_KEY = 'gx:idle:lastActivity';
const SIGNOUT_KEY = 'gx:idle:signedOut';
const TAB_ID = newTabId();
const ACTIVITY_EVENTS = ['mousedown', 'mousemove', 'keydown', 'wheel', 'touchstart', 'scroll'];
const TICK_MS = 1000;
const WRITE_THROTTLE_MS = 2000;

let dotNet = null;
let idleMs = 0, countdownMs = 0, keepAliveMs = 0;
let keepAliveUrl = '', logoutUrl = '', loginUrl = '', antiforgeryToken = null;
let lastLocalActivity = Date.now();
let lastWrite = 0, lastKeepAlive = Date.now();
let countdownDeadline = null;   // absolute ms, set when the warning opens; null when it is closed
let tickHandle = null;
let leaving = false;            // makes navigation idempotent when two paths fire at once

function newTabId() {
    // crypto.randomUUID needs a secure context. The app is HTTPS-only, but a dev proxy on http
    // should degrade rather than throw and take the whole module with it.
    try { return crypto.randomUUID(); } catch { return `${Date.now()}-${Math.random()}`; }
}

export function initialize(dotNetRef, options) {
    dotNet = dotNetRef;
    idleMs = options.idleMs;
    countdownMs = options.countdownMs;
    keepAliveMs = options.keepAliveMs;          // 0 disables the ping
    keepAliveUrl = options.keepAliveUrl;
    logoutUrl = options.logoutUrl;
    loginUrl = options.loginUrl;
    antiforgeryToken = options.antiforgeryToken ?? null;

    // A sign-out record left by the PREVIOUS session would bounce this freshly signed-in tab
    // straight back out, and would keep doing it. Clearing it here is what makes signing in again
    // after an idle logout work at all.
    try { localStorage.removeItem(SIGNOUT_KEY); } catch { }

    recordActivity(true);
    ACTIVITY_EVENTS.forEach(e =>
        window.addEventListener(e, onActivity, { passive: true, capture: true }));
    document.addEventListener('visibilitychange', onVisibility);
    window.addEventListener('storage', onStorage);
    window.addEventListener('focus', verifySession);
    tickHandle = setInterval(tick, TICK_MS);
}

// Another tab ended the session. Leave now rather than on this tab's next tick - the point of the
// broadcast is that a window which still looks signed in is never left open.
function onStorage(e) {
    if (e.key !== SIGNOUT_KEY || !e.newValue || leaving) return;
    leaveTo(loginUrl);
}

// On regaining focus, ask the server whether the session still exists.
//
// This is the robust half of cross-tab sign-out: it depends on no message being received, so it
// covers a tab that was throttled or asleep through the whole countdown - and it covers sign-out
// from ANY cause, not just idle. An explicit logout elsewhere, an administrator disabling the
// account, or the cookie simply expiring all surface here as a 401.
async function verifySession() {
    if (leaving || document.hidden) return;
    try {
        const res = await ping();
        if (res.status === 401 || res.status === 403) leaveTo(loginUrl);
        else lastKeepAlive = Date.now();
    } catch {
        // Offline. The cookie remains the authority; do not sign anybody out over a failed fetch.
    }
}

function ping() {
    const headers = antiforgeryToken ? { 'RequestVerificationToken': antiforgeryToken } : {};
    return fetch(keepAliveUrl, { method: 'POST', credentials: 'same-origin', headers });
}

// Announce the sign-out to every other tab, then end this one.
async function signOutAllTabs(reason) {
    if (leaving) return;
    try {
        localStorage.setItem(SIGNOUT_KEY, JSON.stringify({ t: Date.now(), tab: TAB_ID, reason }));
    } catch { }

    // The template's sign-out endpoint is a POST that redirects. Posting it from here (rather than
    // navigating to it) keeps a single sign-out endpoint - there is no second one to drift - and
    // lets this tab choose where to land afterwards.
    leaving = true;
    clearInterval(tickHandle);
    try {
        const body = new FormData();
        body.append('returnUrl', loginUrl);
        if (antiforgeryToken) body.append('__RequestVerificationToken', antiforgeryToken);
        await fetch(logoutUrl, { method: 'POST', credentials: 'same-origin', body });
    } catch { }
    window.location.assign(loginUrl);
}

function leaveTo(url) {
    if (leaving) return;
    leaving = true;
    clearInterval(tickHandle);
    window.location.assign(url);
}

function onActivity() {
    // While the countdown is showing, activity in THIS tab is deliberately ignored: a stray mouse
    // movement must not silently extend a session that has already announced it is ending.
    // Dismissal here requires the explicit button. Activity in OTHER tabs still cancels it, through
    // the shared timestamp below - a user demonstrably working elsewhere is not idle.
    if (countdownDeadline !== null) return;
    recordActivity(false);
}

function onVisibility() {
    if (!document.hidden && countdownDeadline === null) recordActivity(false);
}

function recordActivity(force) {
    const now = Date.now();
    lastLocalActivity = now;
    if (force || now - lastWrite > WRITE_THROTTLE_MS) {
        lastWrite = now;
        try { localStorage.setItem(STORAGE_KEY, JSON.stringify({ t: now, tab: TAB_ID })); } catch { }
    }
}

// The most recent activity in ANY tab. During a countdown this tab's own writes are excluded, which
// is what makes "activity elsewhere cancels, activity here does not" fall out of one comparison.
function lastActivityAcrossTabs() {
    let newest = countdownDeadline === null ? lastLocalActivity : 0;
    try {
        const raw = localStorage.getItem(STORAGE_KEY);
        if (raw) {
            const rec = JSON.parse(raw);
            if (rec.tab !== TAB_ID || countdownDeadline === null) newest = Math.max(newest, rec.t);
        }
    } catch { }
    return newest;
}

async function tick() {
    if (leaving) return;

    const now = Date.now();
    const idleFor = now - lastActivityAcrossTabs();

    if (countdownDeadline !== null) {
        if (idleFor < idleMs) {                     // another tab is active - stand down
            countdownDeadline = null;
            await invoke('OnActivityResumed');
            return;
        }

        const remaining = Math.max(0, countdownDeadline - now);
        await invoke('OnCountdownTick', Math.ceil(remaining / 1000));

        if (remaining <= 0) {
            // The hard backstop. Deliberately here and not in .NET: it must still fire when the
            // circuit is dead, which is exactly when the dialog has stopped updating.
            await signOutAllTabs('idle');
        }
        return;
    }

    if (idleFor >= idleMs) {
        countdownDeadline = now + countdownMs;
        await invoke('OnIdleWarning', Math.ceil(countdownMs / 1000));
        return;
    }

    if (keepAliveMs > 0 && now - lastKeepAlive >= keepAliveMs) {
        lastKeepAlive = now;
        // Renews the sliding authentication cookie. Inside one long-lived Blazor circuit the browser
        // makes almost no HTTP requests, so without this ping an actively working user's cookie
        // expires underneath them and the next real request - a download, a refresh - lands on the
        // login page mid-task.
        try {
            const res = await ping();
            if (res.status === 401 || res.status === 403) leaveTo(loginUrl);
        } catch { }
    }
}

// The circuit can be gone by the time a tick fires, which is not an error - the JS deadline is
// designed to outlive it. Swallow the interop failure and let the deadline do its work.
async function invoke(method, ...args) {
    if (!dotNet) return;
    try { await dotNet.invokeMethodAsync(method, ...args); } catch { }
}

// Called from .NET when the user clicks Stay Logged In.
export async function extend() {
    countdownDeadline = null;
    recordActivity(true);
    lastKeepAlive = Date.now();
    try {
        const res = await ping();
        // The server has already ended it. Do not resurrect a session client-side by closing the
        // dialog and carrying on: the cookie is the authority and it says no.
        if (res.status === 401 || res.status === 403) leaveTo(loginUrl);
    } catch { }
}

// Called from .NET when the user signs out explicitly, so other tabs follow at once rather than on
// their next tick.
export function signOut() { return signOutAllTabs('explicit'); }

// Public hook for long-running UI work that produces no input events - a report export, a bulk
// import - so a user watching a progress bar is not treated as absent.
export function touch() { recordActivity(true); }

export function dispose() {
    clearInterval(tickHandle);
    ACTIVITY_EVENTS.forEach(e => window.removeEventListener(e, onActivity, { capture: true }));
    document.removeEventListener('visibilitychange', onVisibility);
    window.removeEventListener('storage', onStorage);
    window.removeEventListener('focus', verifySession);
    dotNet = null;
}
```

**Exported surface**, since the .NET side depends on the exact names:

| Export | Called by | Purpose |
|---|---|---|
| `initialize(dotNetRef, options)` | `IdleTimeoutMonitor.OnAfterRenderAsync` | Starts listeners and the 1-second tick |
| `extend()` | Stay Logged In handler | Cancels the countdown, re-pings, navigates on 401/403 |
| `signOut()` | Sign Out Now handler | Broadcasts, POSTs logout, navigates to login |
| `touch()` | Public hook for long-running UI work | Records activity with no input event |
| `dispose()` | `IAsyncDisposable` | Removes every listener and clears the interval |

**Inbound `[JSInvokable]` names** the module calls on the .NET reference:
`OnIdleWarning(seconds)`, `OnCountdownTick(seconds)`, `OnActivityResumed()`.

**`localStorage` keys**: `gx:idle:lastActivity` (shared activity timestamp, written at most
every 2 seconds) and `gx:idle:signedOut` (the cross-tab sign-out broadcast, **cleared on every
`initialize`** — see §6 for why that housekeeping is what makes signing in again after an idle
logout work at all). Every access to both is wrapped in `try/catch`, because a browser in
private mode or with site data blocked throws on access.

---

### 2.2 `src/Server.UI/Components/Security/IdleTimeoutMonitor.razor` — **new**

The whole file. It renders nothing else — the `<MudDialog>` is the entire markup, and it is
displayed by MudBlazor's `MudDialogProvider`, which the application layout already hosts.

```razor
@using CleanArchitecture.Blazor.Application.Common.Constants
@using CleanArchitecture.Blazor.Application.Common.Interfaces
@using CleanArchitecture.Blazor.Infrastructure.Services.Security
@using Microsoft.AspNetCore.Components.Authorization
@implements IAsyncDisposable
@inject IJSRuntime JS
@inject IIdleTimeoutPolicyProvider PolicyProvider
@inject ILogger<IdleTimeoutMonitor> Logger

@* The dialog stays in the render tree and is closed by BINDING Visible, never by being removed.
   MudBlazor shows an inline dialog through MudDialogProvider, and dropping the <MudDialog> element
   behind an @if does NOT tell the provider to close it - the provider goes on rendering a dialog the
   component no longer knows about. With BackdropClick and CloseOnEscapeKey both false (deliberate,
   below) the result is undismissable and its overlay swallows every click on the page.

   That was Pass 18's defect: it left the page frozen on EVERY close path - the Stay Logged In
   button, and the silent close when another tab reports activity - regardless of what the
   keep-alive returned.

   @bind-Visible rather than a one-way Visible: it keeps the flag and the dialog's own state from
   ever disagreeing. The dialog cannot close itself here, but if a future option let it, the
   component would hear about it rather than holding a flag that had quietly become a lie. *@
<MudDialog @bind-Visible="_warningOpen" Options="_dialogOptions" aria-live="assertive" role="alertdialog">
    <TitleContent>
        <MudText Typo="Typo.h6">
            <MudIcon Icon="@Icons.Material.Filled.Timer" Class="mb-n1 mr-2" />
            @AppStrings.SessionExpiringTitle
        </MudText>
    </TitleContent>
    <DialogContent>
        <MudText Class="mb-4">@AppStrings.SessionExpiringMessage</MudText>

        <MudText Typo="Typo.h3" Align="Align.Center" aria-live="assertive">
            @_secondsRemaining
        </MudText>
        <MudText Typo="Typo.caption" Align="Align.Center" Class="d-block mb-2">
            @AppStrings.Seconds
        </MudText>

        <MudProgressLinear Color="Color.Warning" Value="@_countdownPercent" Class="mt-2" />
    </DialogContent>
    <DialogActions>
        <MudButton OnClick="SignOutNowAsync">@AppStrings.SignOutNow</MudButton>
        <MudButton Color="Color.Primary"
                   Variant="Variant.Filled"
                   autofocus
                   OnClick="StayLoggedInAsync">
            @AppStrings.StayLoggedIn
        </MudButton>
    </DialogActions>
</MudDialog>

@code {
    // The circuit half of the idle timeout: the dialog, the countdown it displays, and the two
    // buttons. Rendered ONCE, from AppLayout's <Authorized> branch - never per page, or a navigation
    // would leave a second set of listeners running against the same localStorage keys.
    //
    // It owns none of the timing. gxIdleTimeout.js holds the deadline and calls in on transitions,
    // so a dropped circuit stops the dialog updating but does not stop the session ending. That
    // split is the whole design: this is user experience, IdleSessionEnforcer is enforcement.

    [CascadingParameter] private Task<AuthenticationState> AuthenticationStateTask { get; set; } = null!;

    private static readonly DialogOptions _dialogOptions = new()
    {
        // Dismissal is explicit only. A backdrop click or an Escape keypress is exactly the sort of
        // stray input that must NOT be read as "I am still here".
        BackdropClick = false,
        CloseOnEscapeKey = false,
        MaxWidth = MaxWidth.ExtraSmall,
        FullWidth = true
    };

    private IJSObjectReference? _module;
    private DotNetObjectReference<IdleTimeoutMonitor>? _self;
    private bool _warningOpen;
    private int _secondsRemaining;
    private int _countdownSeconds = 1;

    private double _countdownPercent =>
        _countdownSeconds <= 0 ? 0 : 100d * _secondsRemaining / _countdownSeconds;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _module is not null)
        {
            return;
        }

        // Belt and braces with the <Authorized> branch this sits in: an anonymous visitor must have
        // no timers, no listeners and no module fetched at all.
        var state = await AuthenticationStateTask;
        if (state.User.Identity?.IsAuthenticated != true)
        {
            return;
        }

        var policy = await PolicyProvider.GetEffectiveAsync(state.User);
        if (!policy.Enabled)
        {
            return;
        }

        _countdownSeconds = policy.CountdownSeconds;
        _self = DotNetObjectReference.Create(this);
        _module = await JS.InvokeAsync<IJSObjectReference>("import", "./js/gxIdleTimeout.js");

        await _module.InvokeVoidAsync("initialize", _self, new
        {
            idleMs = policy.IdleMinutes * 60_000,
            countdownMs = policy.CountdownSeconds * 1_000,
            // Half the idle window: frequent enough that the sliding cookie always renews well
            // before it could lapse, rare enough to be nothing on the wire. Zero disables the ping.
            keepAliveMs = PolicyProvider.Enabled && KeepAliveEnabled
                ? Math.Max(policy.IdleMinutes * 60_000 / 2, 15_000)
                : 0,
            keepAliveUrl = IdleTimeoutRoutes.KeepAlive,
            logoutUrl = IdentityComponentsEndpointRouteBuilderExtensions.Logout,
            loginUrl = IdleTimeoutRoutes.LoginAfterIdle
        });
    }

    [Parameter] public bool KeepAliveEnabled { get; set; } = true;

    [JSInvokable]
    public Task OnIdleWarning(int seconds)
    {
        _secondsRemaining = seconds;
        _warningOpen = true;
        return InvokeAsync(StateHasChanged);
    }

    [JSInvokable]
    public Task OnCountdownTick(int seconds)
    {
        _secondsRemaining = seconds;
        return InvokeAsync(StateHasChanged);
    }

    /// <summary>Another tab is demonstrably in use, so this one stands down without a word.</summary>
    [JSInvokable]
    public Task OnActivityResumed() => CloseWarningAsync();

    /// <summary>
    /// Closes the warning, and is the ONLY way this component closes it.
    /// </summary>
    /// <remarks>
    /// Closing means setting the bound flag, never removing the dialog from the render tree - see the
    /// markup above for why. Every close path goes through here so there is one thing to get right.
    /// </remarks>
    private Task CloseWarningAsync()
    {
        _warningOpen = false;
        return InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Stay Logged In: close first, then tell the module - never the other way round.
    /// </summary>
    /// <remarks>
    /// <b>Close before calling out, and do not await the call.</b> The alternative - await the module
    /// and close afterwards - makes the dialog's fate depend on a network round trip that can hang
    /// indefinitely, and this dialog is configured to be undismissable by backdrop or Escape, so a
    /// handler that fails to reach its closing line leaves the page unusable. Pass 18 measured the
    /// dialog staying open on a hanging call, on a throwing call, and on a perfectly successful one.
    /// A timeout would bound the hang but would still leave the dialog open for the length of it, for
    /// no benefit: nothing in the response changes what this method does.
    /// <para>
    /// Nothing is lost by not awaiting. <c>extend()</c> owns the session decision entirely - it
    /// re-pings, and on 401/403 it navigates to the login page itself. The .NET side has no decision
    /// to make on the result, which is why fire-and-forget is the whole answer here rather than a
    /// shortcut: a dead session still ends, because the module ends it.
    /// </para>
    /// <para>
    /// The continuation swallows nothing silently but must not rethrow. An exception from a JS call
    /// awaited in a click handler propagates into the circuit and tears it down - an independent way
    /// to freeze the page, which Pass 18 also measured. A failed keep-alive is a session question,
    /// not a reason to destroy the page the user is looking at.
    /// </para>
    /// </remarks>
    private async Task StayLoggedInAsync()
    {
        await CloseWarningAsync();

        if (_module is null)
        {
            return;
        }

        _ = InvokeModuleSafelyAsync("extend");
    }

    private async Task SignOutNowAsync()
    {
        // Same shape and the same reasoning: signOut() navigates away, so there is nothing to wait
        // for, and a failure to reach the module must not leave the user staring at a live dialog.
        await CloseWarningAsync();

        if (_module is null)
        {
            return;
        }

        _ = InvokeModuleSafelyAsync("signOut");
    }

    /// <summary>
    /// Invokes a module function without letting its failure reach the circuit.
    /// </summary>
    private async Task InvokeModuleSafelyAsync(string function)
    {
        try
        {
            await _module!.InvokeVoidAsync(function);
        }
        catch (JSDisconnectedException)
        {
            // The circuit is already gone. Ordinary on sign-out; nothing to report.
        }
        catch (Exception ex)
        {
            // Logged rather than swallowed, and deliberately not rethrown: by the time this runs the
            // dialog is already closed and the user has their page back, which is the outcome that
            // matters. The session itself is still governed server-side by IdleSessionEnforcer.
            Logger.LogWarning(ex, "The idle-timeout module's {Function} call failed.", function);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try
            {
                await _module.InvokeVoidAsync("dispose");
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // The circuit is already gone - which is the ordinary case on sign-out. There is
                // nothing to clean up in a browser that has navigated away.
            }
        }

        _self?.Dispose();
    }
}
```

**What it renders, and what renders it.** The component's own markup is one `<MudDialog>`
bound to `_warningOpen`. It relies on `MudDialogProvider` and `MudPopoverProvider` being
present in the layout above it (MudBlazor's standard requirement — the application's
`MainLayout` already hosts them). The component itself renders no dialog chrome; the provider
does. That fact is the root of the defect described in §6 and of the testing rule in §2.28.

**Ambient injections.** `Snackbar`, `Mediator`, `Navigation`, `JS` and others are injected
application-wide from `src/Server.UI/_Imports.razor`, which contains (excerpt — the file also
carries ~60 `@using` lines, omitted here):

```razor
@inject IUserProfileState UserProfileState
@inject IApplicationSettings ApplicationSettings
@inject ISnackbar Snackbar

@inject IAuthorizationService AuthService 
@inject IValidationService Validator
@inject IJSRuntime JS
@inject IMediator Mediator
@inject NavigationManager Navigation
@inject DialogServiceHelper DialogServiceHelper
@inject IAppCache AppCache
@inject IPermissionService PermissionService
@inject IObjectMapper ObjectMapper
@inject TypeAdapterConfig TypeAdapterConfig
```

`IdleTimeoutMonitor.razor` re-declares `@inject IJSRuntime JS` locally anyway, so it does not
depend on that idiom. `SecurityTab.razor` and `SecuritySettings.razor` **do** depend on it —
they use `Snackbar` and `Mediator` without injecting them. In a target repository without an
`_Imports`-level injection convention, add `@inject ISnackbar Snackbar` and
`@inject IMediator Mediator` to those two files.

---

### 2.3 `src/Application/Common/Interfaces/IIdleTimeoutPolicyProvider.cs` — **new**

```csharp
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Claims;

namespace CleanArchitecture.Blazor.Application.Common.Interfaces;

/// <summary>The idle policy in force for one session.</summary>
/// <param name="Enabled">False when the feature is off; the other values are then meaningless.</param>
/// <param name="IdleMinutes">How long the session may sit idle before the warning opens.</param>
/// <param name="CountdownSeconds">How long the warning counts down before signing the user out.</param>
public readonly record struct IdleTimeoutPolicy(bool Enabled, int IdleMinutes, int CountdownSeconds)
{
    /// <summary>Total time from last activity to sign-out.</summary>
    public TimeSpan TotalWindow =>
        TimeSpan.FromMinutes(IdleMinutes).Add(TimeSpan.FromSeconds(CountdownSeconds));

    /// <summary>The policy for a deployment with the feature turned off.</summary>
    public static readonly IdleTimeoutPolicy Disabled = new(false, 0, 0);
}

/// <summary>The policy an administrator has set, before any per-user tightening.</summary>
public readonly record struct AdministeredIdleTimeoutPolicy(int IdleMinutes, int CountdownSeconds);

/// <summary>
/// Reads the effective idle policy. The single source both the browser countdown and the server-side
/// principal check are driven from.
/// </summary>
/// <remarks>
/// <b>Read on every authenticated HTTP request</b>, by the cookie handler's principal validation, so
/// implementations must cache the administered policy and invalidate on save rather than querying
/// per request.
/// <para>
/// Reading the CURRENT policy on each request - rather than baking it into the cookie at sign-in -
/// is what makes the setting administrable: shortening the window takes effect on sessions already
/// in progress, which is the entire point of putting it on a screen.
/// </para>
/// </remarks>
public interface IIdleTimeoutPolicyProvider
{
    /// <summary>Whether the feature is switched on for this deployment at all.</summary>
    bool Enabled { get; }

    /// <summary>
    /// The administered policy, clamped into the configured bounds. Cached; safe to call per request.
    /// </summary>
    Task<AdministeredIdleTimeoutPolicy> GetAdministeredAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The administered policy narrowed by this user's own preference.
    /// </summary>
    /// <remarks>
    /// A user may only <b>shorten</b> their window, never lengthen it. An idle timeout is a control
    /// against unattended workstations; if a user could raise their own, the first person who finds
    /// it inconvenient sets it to eight hours and the control is gone - the same reasoning that keeps
    /// password policy out of a user profile. Tightening is both safe and genuinely useful: someone
    /// on a shared shop-floor terminal can choose five minutes.
    /// <para>
    /// The narrowing is applied HERE, at read time, and not only in the screen's validator - so a
    /// value forced into the database by other means is still clamped before it reaches enforcement.
    /// </para>
    /// </remarks>
    Task<IdleTimeoutPolicy> GetEffectiveAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default);

    /// <summary>Drops the cached administered policy. Call immediately after saving one.</summary>
    void Invalidate();

    /// <summary>Drops one user's cached preference. Call immediately after saving one.</summary>
    void InvalidateUser(string userId);
}
```

---

### 2.4 `src/Application/Common/Interfaces/IIdleTimeoutSettings.cs` — **new**

```csharp
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace CleanArchitecture.Blazor.Application.Common.Interfaces;

/// <summary>
/// The deployment's idle-timeout <b>bounds</b>, and the values a fresh database is seeded with.
/// </summary>
/// <remarks>
/// This is deliberately not the effective policy. Configuration supplies only what an administrator
/// may not exceed; the policy in force is administered at runtime and read through
/// <see cref="IIdleTimeoutPolicyProvider"/>.
/// <para>
/// The split matters for one value in particular. <see cref="MaxIdleTimeoutMinutes"/> alone decides
/// the authentication cookie's absolute lifetime, which is fixed when the cookie is issued and
/// cannot be shortened retroactively - so it has to be a deployment decision, not an administrator
/// one. Every other bound exists to keep an administrator from configuring a policy the cookie
/// cannot honour.
/// </para>
/// </remarks>
public interface IIdleTimeoutSettings
{
    /// <summary>
    /// When false the feature is inert end to end: no JS module is fetched, no principal check runs,
    /// and neither settings screen is reachable.
    /// </summary>
    bool Enabled { get; set; }

    /// <summary>Idle window seeded into a fresh database, in minutes.</summary>
    int DefaultIdleTimeoutMinutes { get; set; }

    /// <summary>Warning countdown seeded into a fresh database, in seconds.</summary>
    int DefaultCountdownSeconds { get; set; }

    /// <summary>The shortest idle window any policy - administered or per-user - may specify.</summary>
    int MinIdleTimeoutMinutes { get; set; }

    /// <summary>
    /// The longest idle window any policy may specify, and the only value that sizes the
    /// authentication cookie.
    /// </summary>
    int MaxIdleTimeoutMinutes { get; set; }

    /// <summary>
    /// Whether a user may shorten their own idle window. Never lengthen it - see
    /// <see cref="IIdleTimeoutPolicyProvider"/>.
    /// </summary>
    bool AllowUserOverride { get; set; }

    /// <summary>
    /// Whether the browser pings the keep-alive endpoint while the user is active. Off makes the
    /// sliding cookie unable to renew inside a long-lived Blazor circuit; see the endpoint's remarks.
    /// </summary>
    bool KeepAlivePingEnabled { get; set; }

    /// <summary>
    /// Slack added to the cookie's lifetime on top of the maximum window and the countdown, so that
    /// the cookie never expires marginally before the enforcement that is meant to end the session.
    /// </summary>
    int CookieGraceMinutes { get; set; }
}
```

---

### 2.5 `src/Infrastructure/Configurations/IdleTimeoutSettings.cs` — **new**

The options class. It implements both `IIdleTimeoutSettings` (so the Application layer can
see the bounds without referencing Infrastructure) and `IValidatableObject` (so the bad
combinations fail the process at startup).

```csharp
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel.DataAnnotations;
using CleanArchitecture.Blazor.Application.Common.Interfaces;

namespace CleanArchitecture.Blazor.Infrastructure.Configurations;

/// <summary>
/// Bounds and bootstrap defaults for the idle timeout, bound from
/// <c>SecuritySettings:IdleTimeout</c>.
/// </summary>
/// <remarks>
/// Validated with <c>ValidateDataAnnotations().ValidateOnStart()</c>, so a deployment that
/// configures a policy the cookie cannot honour fails the process at startup naming the value,
/// rather than producing sessions that end at a time nobody intended.
/// </remarks>
public class IdleTimeoutSettings : IIdleTimeoutSettings, IValidatableObject
{
    /// <summary>The configuration section, as a path: <c>SecuritySettings:IdleTimeout</c>.</summary>
    public const string Key = "SecuritySettings:IdleTimeout";

    /// <summary>The hard ceiling on <see cref="MaxIdleTimeoutMinutes"/> - eight hours.</summary>
    /// <remarks>
    /// A ceiling on the ceiling: past this the cookie outlives a working day and the control stops
    /// being an idle timeout at all. A deployment that genuinely wants a longer session should turn
    /// the feature off deliberately rather than configure it into irrelevance.
    /// </remarks>
    public const int AbsoluteMaxIdleTimeoutMinutes = 480;

    /// <summary>The narrowest and widest countdown the warning dialog is usable at.</summary>
    public const int MinCountdownSeconds = 10;

    /// <summary>See <see cref="MinCountdownSeconds"/>.</summary>
    public const int MaxCountdownSeconds = 600;

    /// <inheritdoc />
    public bool Enabled { get; set; } = true;

    /// <inheritdoc />
    public int DefaultIdleTimeoutMinutes { get; set; } = 15;

    /// <inheritdoc />
    public int DefaultCountdownSeconds { get; set; } = 60;

    /// <inheritdoc />
    public int MinIdleTimeoutMinutes { get; set; } = 1;

    /// <inheritdoc />
    public int MaxIdleTimeoutMinutes { get; set; } = 120;

    /// <inheritdoc />
    public bool AllowUserOverride { get; set; } = true;

    /// <inheritdoc />
    public bool KeepAlivePingEnabled { get; set; } = true;

    /// <inheritdoc />
    public int CookieGraceMinutes { get; set; } = 2;

    /// <summary>
    /// The authentication cookie's absolute lifetime: the widest window an administrator could set,
    /// plus the countdown that follows it, plus grace.
    /// </summary>
    /// <remarks>
    /// Sized from the MAXIMUM rather than from the current policy because the cookie is issued once,
    /// at sign-in, and cannot be shortened afterwards. Tightening the policy is enforced instead by
    /// the principal check on each request, which reads the policy in force at that moment; the
    /// cookie's own expiry is only the outer bound.
    /// </remarks>
    public TimeSpan CookieLifetime => TimeSpan
        .FromMinutes(MaxIdleTimeoutMinutes + CookieGraceMinutes)
        .Add(TimeSpan.FromSeconds(DefaultCountdownSeconds));

    /// <summary>
    /// The cookie lifetime used when the feature is off: a plain fixed session, unrelated to any
    /// idle policy.
    /// </summary>
    public static readonly TimeSpan DisabledCookieLifetime = TimeSpan.FromHours(8);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        // Nothing below is enforced when the feature is off - the values are inert, and failing a
        // start over a setting that does nothing would be noise.
        if (!Enabled)
        {
            yield break;
        }

        if (MinIdleTimeoutMinutes < 1)
        {
            yield return new ValidationResult(
                $"{nameof(MinIdleTimeoutMinutes)} must be at least 1; found {MinIdleTimeoutMinutes}.",
                [nameof(MinIdleTimeoutMinutes)]);
        }

        if (MaxIdleTimeoutMinutes > AbsoluteMaxIdleTimeoutMinutes)
        {
            yield return new ValidationResult(
                $"{nameof(MaxIdleTimeoutMinutes)} must not exceed {AbsoluteMaxIdleTimeoutMinutes} " +
                $"(eight hours); found {MaxIdleTimeoutMinutes}.",
                [nameof(MaxIdleTimeoutMinutes)]);
        }

        if (MaxIdleTimeoutMinutes <= MinIdleTimeoutMinutes)
        {
            yield return new ValidationResult(
                $"{nameof(MaxIdleTimeoutMinutes)} ({MaxIdleTimeoutMinutes}) must be greater than " +
                $"{nameof(MinIdleTimeoutMinutes)} ({MinIdleTimeoutMinutes}).",
                [nameof(MaxIdleTimeoutMinutes)]);
        }

        if (DefaultIdleTimeoutMinutes < MinIdleTimeoutMinutes ||
            DefaultIdleTimeoutMinutes > MaxIdleTimeoutMinutes)
        {
            yield return new ValidationResult(
                $"{nameof(DefaultIdleTimeoutMinutes)} ({DefaultIdleTimeoutMinutes}) must lie within " +
                $"[{MinIdleTimeoutMinutes}, {MaxIdleTimeoutMinutes}].",
                [nameof(DefaultIdleTimeoutMinutes)]);
        }

        if (DefaultCountdownSeconds < MinCountdownSeconds || DefaultCountdownSeconds > MaxCountdownSeconds)
        {
            yield return new ValidationResult(
                $"{nameof(DefaultCountdownSeconds)} must lie within " +
                $"[{MinCountdownSeconds}, {MaxCountdownSeconds}]; found {DefaultCountdownSeconds}.",
                [nameof(DefaultCountdownSeconds)]);
        }

        // The countdown may equal the shortest window but never exceed it. Exceeding means the
        // warning would have to open before the user had finished going idle, which is incoherent -
        // and at the tightest permitted policy it is the shortest window that binds, not the
        // administered one.
        var shortestWindowSeconds = MinIdleTimeoutMinutes * 60;
        if (DefaultCountdownSeconds > shortestWindowSeconds)
        {
            yield return new ValidationResult(
                $"{nameof(DefaultCountdownSeconds)} ({DefaultCountdownSeconds}s) exceeds the shortest " +
                $"idle window {nameof(MinIdleTimeoutMinutes)} allows ({MinIdleTimeoutMinutes}m = " +
                $"{shortestWindowSeconds}s). Shorten the countdown or raise the minimum window.",
                [nameof(DefaultCountdownSeconds)]);
        }

        if (CookieGraceMinutes < 1)
        {
            yield return new ValidationResult(
                $"{nameof(CookieGraceMinutes)} must be at least 1; found {CookieGraceMinutes}.",
                [nameof(CookieGraceMinutes)]);
        }
    }
}
```

---

### 2.6 `src/Infrastructure/Services/Security/IdleTimeoutPolicyProvider.cs` — **new**

```csharp
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Claims;
using CleanArchitecture.Blazor.Domain.Identity;
using CleanArchitecture.Blazor.Infrastructure.Configurations;
using ZiggyCreatures.Caching.Fusion;

namespace CleanArchitecture.Blazor.Infrastructure.Services.Security;

/// <summary>
/// Reads the administered idle policy, caches it, and narrows it by the signed-in user's own
/// preference.
/// </summary>
/// <remarks>
/// <b>This runs on every authenticated HTTP request</b> - the cookie handler's principal validation
/// calls it - so both reads are behind a cache with explicit invalidation, not a short TTL. A stale
/// policy would mean an administrator's change not taking effect, which is precisely the behaviour
/// putting the setting on a screen was meant to provide; each save invalidates rather than waiting
/// for an expiry.
/// <para>
/// <b>Why the user preference is read from the database rather than carried as a claim.</b> A claim
/// would be free to read, and it is how <c>MustChangePassword</c> is done - but a claim only changes
/// when the authentication cookie is reissued, and reissuing it means
/// <c>SignInManager.RefreshSignInAsync</c>, which cannot run inside a Blazor circuit: the response
/// has already started and the cookie cannot be written. A user changing their own timeout on a
/// Blazor page would therefore see it take effect at their NEXT sign-in, which is not what the
/// screen says it does. A per-user cache entry, invalidated on save, is correct on the very next
/// request and costs a dictionary lookup.
/// </para>
/// </remarks>
public sealed class IdleTimeoutPolicyProvider : IIdleTimeoutPolicyProvider
{
    /// <summary>
    /// One key, because there is one row. A multi-tenant deployment keys this by tenant and changes
    /// nothing else - which is why every reader goes through this type rather than querying the
    /// table.
    /// </summary>
    public const string CacheKey = "security-policy:idle-timeout";

    /// <summary>Per-user preference cache key.</summary>
    public static string UserCacheKey(string userId) => $"security-policy:idle-timeout:user:{userId}";

    /// <summary>
    /// Long, and deliberately so: both caches are invalidated on save, so the duration is a backstop
    /// against a missed invalidation rather than the mechanism by which changes propagate.
    /// </summary>
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(12);

    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
    private readonly IFusionCache _cache;
    private readonly IIdleTimeoutSettings _settings;
    private readonly ILogger<IdleTimeoutPolicyProvider> _logger;

    public IdleTimeoutPolicyProvider(
        IDbContextFactory<ApplicationDbContext> dbContextFactory,
        IFusionCache cache,
        IIdleTimeoutSettings settings,
        ILogger<IdleTimeoutPolicyProvider> logger)
    {
        _dbContextFactory = dbContextFactory;
        _cache = cache;
        _settings = settings;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool Enabled => _settings.Enabled;

    /// <inheritdoc />
    public async Task<AdministeredIdleTimeoutPolicy> GetAdministeredAsync(
        CancellationToken cancellationToken = default)
    {
        if (!_settings.Enabled)
        {
            return new AdministeredIdleTimeoutPolicy(
                _settings.DefaultIdleTimeoutMinutes, _settings.DefaultCountdownSeconds);
        }

        var stored = await _cache.GetOrSetAsync(
            CacheKey,
            async ct => await LoadAdministeredAsync(ct),
            options => options.SetDuration(CacheDuration),
            cancellationToken).ConfigureAwait(false);

        // Clamped on the way OUT, not only on the way in. A row written before the bounds were
        // tightened - or edited around the screen - is still held to the deployment's limits, and
        // the authentication cookie was sized from those limits.
        return new AdministeredIdleTimeoutPolicy(
            ClampIdleMinutes(stored.IdleMinutes),
            ClampCountdown(stored.CountdownSeconds));
    }

    /// <inheritdoc />
    public async Task<IdleTimeoutPolicy> GetEffectiveAsync(
        ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        if (!_settings.Enabled)
        {
            return IdleTimeoutPolicy.Disabled;
        }

        var administered = await GetAdministeredAsync(cancellationToken).ConfigureAwait(false);
        var preference = await ReadPreferenceAsync(user, cancellationToken).ConfigureAwait(false);

        // min(), never max(): a user preference may only tighten. Clamped afterwards so a value
        // forced into the database below the floor still lands on the floor.
        var idleMinutes = preference is { } chosen
            ? Math.Min(chosen, administered.IdleMinutes)
            : administered.IdleMinutes;

        return new IdleTimeoutPolicy(
            Enabled: true,
            IdleMinutes: ClampIdleMinutes(idleMinutes),
            // Not user-adjustable: the countdown is how long the warning is shown, not how long a
            // session may sit idle. It is a warning, not a policy.
            CountdownSeconds: administered.CountdownSeconds);
    }

    /// <inheritdoc />
    public void Invalidate() => _cache.Remove(CacheKey);

    /// <inheritdoc />
    public void InvalidateUser(string userId) => _cache.Remove(UserCacheKey(userId));

    /// <summary>
    /// Reads the single policy row, seeding it from configuration the first time.
    /// </summary>
    /// <remarks>
    /// Seeding lazily here rather than in the database initializer keeps the feature working on a
    /// database provisioned before it existed, with no data migration - the first read after the
    /// upgrade writes the row.
    /// </remarks>
    private async Task<AdministeredIdleTimeoutPolicy> LoadAdministeredAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var row = await db.SecurityPolicies
            .OrderBy(p => p.Id)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (row is not null)
        {
            return new AdministeredIdleTimeoutPolicy(row.IdleTimeoutMinutes, row.CountdownSeconds);
        }

        var seeded = new SecurityPolicy
        {
            IdleTimeoutMinutes = _settings.DefaultIdleTimeoutMinutes,
            CountdownSeconds = _settings.DefaultCountdownSeconds
        };

        db.SecurityPolicies.Add(seeded);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Seeded the security policy from configuration: idle {Idle}m, countdown {Countdown}s.",
            seeded.IdleTimeoutMinutes, seeded.CountdownSeconds);

        return new AdministeredIdleTimeoutPolicy(seeded.IdleTimeoutMinutes, seeded.CountdownSeconds);
    }

    /// <summary>
    /// The user's chosen window, or null when they have not chosen one - or when the deployment has
    /// switched the choice off, in which case an existing preference is ignored rather than honoured.
    /// </summary>
    private async Task<int?> ReadPreferenceAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (!_settings.AllowUserOverride)
        {
            return null;
        }

        var userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            return null;
        }

        // Nullable<int> is not cacheable through FusionCache's generic path as cleanly as a sentinel,
        // so "no preference" is cached as 0 and mapped back here. Caching the absence matters: most
        // users never set one, and without it every request would be a database read for a null.
        var cached = await _cache.GetOrSetAsync(
            UserCacheKey(userId),
            async ct =>
            {
                await using var db = await _dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

                return await db.Users
                    .Where(u => u.Id == userId)
                    .Select(u => u.IdleTimeoutMinutes ?? 0)
                    .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            },
            options => options.SetDuration(CacheDuration),
            cancellationToken).ConfigureAwait(false);

        return cached > 0 ? cached : null;
    }

    private int ClampIdleMinutes(int minutes) =>
        Math.Clamp(minutes, _settings.MinIdleTimeoutMinutes, _settings.MaxIdleTimeoutMinutes);

    private int ClampCountdown(int seconds) => Math.Clamp(
        seconds, IdleTimeoutSettings.MinCountdownSeconds, IdleTimeoutSettings.MaxCountdownSeconds);
}
```

**Cache dependency.** The provider takes `ZiggyCreatures.Caching.Fusion.IFusionCache`. Any
cache with get-or-set-with-factory and explicit remove will do; the two behaviours that
matter are (a) the factory runs once per miss and (b) `Remove` takes effect immediately.
A plain `IMemoryCache` is a drop-in substitute. If you swap it, keep the `SetDuration(12h)`
equivalent — it is a backstop, not the propagation mechanism (§6).

**`_settings.Enabled == false` short-circuits.** `GetAdministeredAsync` returns the
configured defaults without touching the database, and `GetEffectiveAsync` returns
`IdleTimeoutPolicy.Disabled`. Nothing seeds a row while the feature is off.

---

### 2.7 `src/Infrastructure/Services/Security/IdleSessionEnforcer.cs` — **new**

Contains two public types: the route constants (`IdleTimeoutRoutes`) and the enforcement
(`IdleSessionEnforcer`). They live in one file in the source; splitting them is harmless.

```csharp
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;

namespace CleanArchitecture.Blazor.Infrastructure.Services.Security;

/// <summary>Routes the idle-timeout feature owns, named once so two layers cannot disagree.</summary>
public static class IdleTimeoutRoutes
{
    /// <summary>
    /// The keep-alive ping. Mapped by the UI, recognised here - <see cref="IdleSessionEnforcer"/>
    /// treats a request to this path as the user being active, and it is the only path that renews
    /// the idle window.
    /// </summary>
    public const string KeepAlive = "/account/keep-alive";

    /// <summary>Where a browser is sent after an idle sign-out, so the login page can explain.</summary>
    public const string LoginAfterIdle = "/account/login?reason=idle";
}

/// <summary>
/// The server-side half of the idle timeout: the part that is enforcement rather than user
/// experience.
/// </summary>
/// <remarks>
/// A JavaScript timer is not security. It can be disabled, paused on a breakpoint, or stopped when
/// the Blazor circuit drops; while the authentication cookie is still valid the user is still
/// authenticated, and a modal that covers the UI has signed nobody out. This type is what actually
/// ends the session, and it runs inside the cookie handler's principal validation - on every
/// authenticated HTTP request.
/// <para>
/// It reads the policy in force <b>at that moment</b> rather than one baked into the cookie at
/// sign-in, so an administrator tightening the window takes effect on sessions already open. The
/// cookie's own <c>ExpireTimeSpan</c> is only the outer bound, sized from the widest window any
/// policy could reach because a cookie cannot be shortened after it is issued.
/// </para>
/// </remarks>
public sealed class IdleSessionEnforcer
{
    /// <summary>
    /// Where the last-activity stamp lives: the ticket's own properties.
    /// </summary>
    /// <remarks>
    /// In the ticket rather than in a table, so the check costs no database round-trip per request.
    /// This deployment stores tickets server-side (<c>MemoryCacheTicketStore</c>), so the value never
    /// travels to the browser and cannot be tampered with there; with a cookie-borne ticket it would
    /// still be inside the protected payload.
    /// </remarks>
    public const string LastActivityKey = "gx:idle:lastActivity";

    private readonly IIdleTimeoutPolicyProvider _policy;
    private readonly ILogger<IdleSessionEnforcer> _logger;

    public IdleSessionEnforcer(IIdleTimeoutPolicyProvider policy, ILogger<IdleSessionEnforcer> logger)
    {
        _policy = policy;
        _logger = logger;
    }

    /// <summary>
    /// Decides whether the session may continue, and stamps activity when the request is a
    /// keep-alive ping.
    /// </summary>
    /// <returns>False when the session has been idle past its effective window.</returns>
    public async Task<bool> IsStillValidAsync(CookieValidatePrincipalContext context)
    {
        if (!_policy.Enabled || context.Principal?.Identity?.IsAuthenticated != true)
        {
            return true;
        }

        var policy = await _policy.GetEffectiveAsync(context.Principal, context.HttpContext.RequestAborted)
            .ConfigureAwait(false);

        if (!policy.Enabled)
        {
            return true;
        }

        var now = DateTimeOffset.UtcNow;
        var lastActivity = ReadLastActivity(context) ?? context.Properties.IssuedUtc ?? now;

        if (now - lastActivity > policy.TotalWindow)
        {
            _logger.LogInformation(
                "Signing {User} out: idle for {IdleMinutes:F1} minutes, past the effective window of " +
                "{Window} (idle {Policy}m + countdown {Countdown}s).",
                context.Principal.Identity?.Name,
                (now - lastActivity).TotalMinutes,
                policy.TotalWindow,
                policy.IdleMinutes,
                policy.CountdownSeconds);

            return false;
        }

        // Only the keep-alive ping renews the window. Every other authenticated request - a static
        // asset, a framework callback, the browser reconnecting a circuit - must NOT count as the
        // user being present, or an unattended workstation would keep itself signed in.
        if (IsKeepAlive(context.HttpContext.Request))
        {
            context.Properties.Items[LastActivityKey] =
                now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);

            // Renews the stored ticket in place. Cheaper than re-issuing through SignInAsync, which
            // with a server-side ticket store would rotate the session key on every ping.
            context.ShouldRenew = true;
        }

        return true;
    }

    private static bool IsKeepAlive(HttpRequest request) =>
        request.Path.Equals(IdleTimeoutRoutes.KeepAlive, StringComparison.OrdinalIgnoreCase);

    private static DateTimeOffset? ReadLastActivity(CookieValidatePrincipalContext context)
    {
        if (!context.Properties.Items.TryGetValue(LastActivityKey, out var raw) ||
            !long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epochMs))
        {
            // Absent on a freshly issued ticket, which is the common case for a first request after
            // sign-in. The caller falls back to the ticket's issue time, so a session is never
            // treated as having been idle since the epoch.
            return null;
        }

        return DateTimeOffset.FromUnixTimeMilliseconds(epochMs);
    }
}
```

**The three behaviours to reproduce exactly:**

1. The idle comparison is `now - lastActivity > policy.TotalWindow`, where `TotalWindow` is
   **idle + countdown**, not idle alone. While the warning counts down the user can still
   click *Stay Logged In*, so the server must not have ended the session underneath the
   dialog it is showing.
2. A missing last-activity stamp falls back to `context.Properties.IssuedUtc`, never to
   `default(DateTimeOffset)`. A freshly issued ticket carries no stamp; treating "absent" as
   the epoch signs every user out on their first request after signing in.
3. Only `IdleTimeoutRoutes.KeepAlive` stamps activity and sets `ShouldRenew`. Every other
   authenticated request — a static asset, a framework callback, a circuit reconnect — must
   **not** count, or an unattended workstation keeps itself signed in through whatever its
   browser happens to fetch.

---

### 2.8 `src/Domain/Entities/SecurityPolicy.cs` — **new**

```csharp
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using CleanArchitecture.Blazor.Domain.Common.Entities;

namespace CleanArchitecture.Blazor.Domain.Entities;

/// <summary>
/// The security policy an administrator has set for this installation - today, the idle timeout.
/// </summary>
/// <remarks>
/// <b>One row.</b> The provider reads the first row and seeds one from configuration when the table
/// is empty, so a fresh database needs no seeding step of its own. Adding a tenant column later is a
/// migration plus a cache key, not a redesign - which is why the reader goes through
/// <c>IIdleTimeoutPolicyProvider</c> rather than querying the table at its call sites.
/// <para>
/// <b>Audited.</b> Changing how long a session may sit unattended is a security event, so the entity
/// carries <see cref="IAuditable"/> and its before/after values land in AuditTrails in the same
/// transaction as the change.
/// </para>
/// <para>
/// <b>A template table, not a business model.</b> It derives from <see cref="BaseAuditableEntity"/>
/// and is therefore an <see cref="IBusinessEntity"/> like anything a project writes - so, like
/// Documents and PicklistSets, its configuration names its table explicitly to keep it out of the
/// <c>core</c> schema. See <c>SecurityPolicyConfiguration</c>.
/// </para>
/// </remarks>
public class SecurityPolicy : BaseAuditableEntity, IAuditable
{
    /// <summary>Minutes a session may sit idle before the warning countdown opens.</summary>
    public int IdleTimeoutMinutes { get; set; }

    /// <summary>Seconds the warning counts down before the session ends.</summary>
    public int CountdownSeconds { get; set; }
}
```

`BaseAuditableEntity` is the template's own base (int `Id`, `CreatedAt`, `CreatedById`,
`LastModifiedAt`, `LastModifiedById`, and a `DomainEvents` collection). `IAuditable` is the
marker an EF save-changes interceptor looks for to write before/after values into an
`AuditTrails` table inside the same transaction. Substitute your repository's equivalents; if
you have no audit mechanism, drop `IAuditable` and note that policy changes then go
unrecorded.

**The per-user preference is deliberately *not* audited.** It lives on `ApplicationUser`, and
Identity entities are outside the audit trail in this design. Auditing it would mean auditing
Identity, which is a larger decision than this feature should make on its own.

---

### 2.9 `src/Infrastructure/Persistence/Configurations/SecurityPolicyConfiguration.cs` — **new**

```csharp
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CleanArchitecture.Blazor.Infrastructure.Persistence.Configurations;

public class SecurityPolicyConfiguration : IEntityTypeConfiguration<SecurityPolicy>
{
    public void Configure(EntityTypeBuilder<SecurityPolicy> builder)
    {
        // Named explicitly so the GX naming convention yields. SecurityPolicy derives from
        // BaseAuditableEntity and is therefore an IBusinessEntity, but it is one of the TEMPLATE's
        // tables rather than this business's: it stays "SecurityPolicies" in the default schema
        // instead of becoming core."TBL_SECURITY_POLICY", for the same reason Documents does - the
        // core schema is where a project's own models live, and a template upgrade must never hand
        // an existing project a rename migration. TemplateTablesStayOutOfCoreTests pins this.
        builder.ToTable("SecurityPolicies");

        builder.Ignore(e => e.DomainEvents);
    }
}
```

**Why the explicit `ToTable`.** This repository applies a naming convention that renames
every `IBusinessEntity` into a `core` schema as `TBL_UPPER_SNAKE`. `SecurityPolicy` derives
from `BaseAuditableEntity` and would be swept up by it, so the configuration names the table
explicitly to opt out — the `core` schema is where a *project's* models live, and a template
upgrade must never hand an existing project a rename migration.

If your target repository has no such convention, this configuration reduces to
`builder.Ignore(e => e.DomainEvents);` (or nothing at all, if your base entity does not carry
domain events).

---

### 2.10 `src/Domain/Identity/ApplicationUser.cs` — **modified**

One property added at the end of the class. Surrounding context, verbatim:

```csharp
    public bool MustChangePassword { get; set; }

    /// <summary>
    /// This user's own idle window, in minutes, or <c>null</c> to follow the administered policy.
    /// </summary>
    /// <remarks>
    /// May only ever SHORTEN the administered window - the effective value is the smaller of the two,
    /// applied at read time by <c>IIdleTimeoutPolicyProvider</c> so that a value forced in by other
    /// means is still clamped. Lengthening is refused because an idle timeout is a control against
    /// unattended workstations: if a user could raise their own, the first person to find it
    /// inconvenient would set it to eight hours and the control would be gone.
    /// <para>
    /// Projected onto the principal as a claim by <c>ApplicationUserClaimsPrincipalFactory</c>, so
    /// that the per-request principal check costs no database round-trip. Changing it therefore has
    /// to refresh the sign-in, exactly as the change-password flow does for MustChangePassword.
    /// </para>
    /// </remarks>
    public int? IdleTimeoutMinutes { get; set; }
}
```

> **The second `<para>` of that comment is wrong and must not be carried over.**
> `ApplicationUserClaimsPrincipalFactory.GenerateClaimsAsync` adds only the
> `MustChangePassword` claim; `IdleTimeoutMinutes` is never projected, and nothing refreshes
> the sign-in when it changes. The provider reads it from the database behind a per-user
> cache, which is deliberate and correct — see §6, *The per-user preference is read from the
> database, not a claim*. The comment is stale documentation of an approach that was
> abandoned. Reproduce the property; drop or rewrite that paragraph.

Before the change the class ended at `MustChangePassword`. Nothing else in
`ApplicationUser.cs` was touched.

---

### 2.11 DbSet registration — **modified** (two files)

`src/Application/Common/Interfaces/IApplicationDbContext.cs`, in context:

```csharp
    DbSet<Document> Documents { get; set; }
    DbSet<PicklistSet> PicklistSets { get; set; }

    /// <summary>The administered security policy - one row. See <c>SecurityPolicy</c>.</summary>
    DbSet<SecurityPolicy> SecurityPolicies { get; set; }
    DbSet<Tenant> Tenants { get; set; }
    DbSet<TenantUser> TenantUsers { get; set; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
```

`src/Infrastructure/Persistence/ApplicationDbContext.cs`, in context:

```csharp
    public DbSet<Document> Documents { get; set; }

    public DbSet<PicklistSet> PicklistSets { get; set; }
    public DbSet<SecurityPolicy> SecurityPolicies { get; set; }
    public DbSet<DataProtectionKey> DataProtectionKeys { get; set; }
```

The `SecurityPolicies` line is the only addition in each file.

---

### 2.12 The `SecuritySettings` feature — **all new**

Four files under `src/Application/Features/SecuritySettings/`.

The mediator here is [Mediator](https://github.com/martinothamar/Mediator) (source-generated),
hence `ValueTask<T> Handle(...)` rather than MediatR's `Task<T>`. `Result<T>` is the
template's own result type with `SuccessAsync` / `Match`. `[RequestAuthorize]` is a
deny-by-default pipeline behaviour that checks the named policy before a handler runs —
substitute your own request-authorization mechanism, but keep the property that **both the
read and the write are authorized at the request level**, not only by the page attribute.

#### 2.12a `Queries/GetSecurityPolicyQuery.cs`

```csharp
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace CleanArchitecture.Blazor.Application.Features.SecuritySettings.Queries;

/// <summary>
/// The administered idle policy, together with the bounds the screen must hold the administrator to.
/// </summary>
/// <remarks>
/// The bounds travel with the policy deliberately. The screen has to show them ("Between 1 and 120
/// minutes") and enforce them, and a screen that fetched them separately - or hard-coded them -
/// would be a second place for the deployment's limits to live.
/// </remarks>
public class SecurityPolicyDto
{
    public int IdleTimeoutMinutes { get; set; }
    public int CountdownSeconds { get; set; }

    /// <summary>False when the deployment has switched the feature off entirely.</summary>
    public bool Enabled { get; set; }

    public int MinIdleTimeoutMinutes { get; set; }
    public int MaxIdleTimeoutMinutes { get; set; }

    /// <summary>Whether users may shorten their own window; drives the profile screen's visibility.</summary>
    public bool AllowUserOverride { get; set; }
}

[RequestAuthorize(Policy = Permissions.SecuritySettings.View)]
public class GetSecurityPolicyQuery : IRequest<Result<SecurityPolicyDto>>;

public class GetSecurityPolicyQueryHandler : IRequestHandler<GetSecurityPolicyQuery, Result<SecurityPolicyDto>>
{
    private readonly IIdleTimeoutPolicyProvider _provider;
    private readonly IIdleTimeoutSettings _settings;

    public GetSecurityPolicyQueryHandler(
        IIdleTimeoutPolicyProvider provider, IIdleTimeoutSettings settings)
    {
        _provider = provider;
        _settings = settings;
    }

    public async ValueTask<Result<SecurityPolicyDto>> Handle(
        GetSecurityPolicyQuery request, CancellationToken cancellationToken)
    {
        // Through the provider rather than the table: it is the thing that seeds the first row and
        // clamps a stored value into the current bounds, so the screen shows what enforcement will
        // actually use rather than what happens to be persisted.
        var administered = await _provider.GetAdministeredAsync(cancellationToken);

        return await Result<SecurityPolicyDto>.SuccessAsync(new SecurityPolicyDto
        {
            IdleTimeoutMinutes = administered.IdleMinutes,
            CountdownSeconds = administered.CountdownSeconds,
            Enabled = _settings.Enabled,
            MinIdleTimeoutMinutes = _settings.MinIdleTimeoutMinutes,
            MaxIdleTimeoutMinutes = _settings.MaxIdleTimeoutMinutes,
            AllowUserOverride = _settings.AllowUserOverride
        });
    }
}
```

#### 2.12b `Commands/UpdateSecurityPolicyCommand.cs`

```csharp
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace CleanArchitecture.Blazor.Application.Features.SecuritySettings.Commands;

/// <summary>
/// Saves the installation's idle policy.
/// </summary>
/// <remarks>
/// Its own permission rather than a general administration right: how long a session may sit
/// unattended is a security control, and the people who should hold it are not necessarily the
/// people who administer users or picklists.
/// </remarks>
[RequestAuthorize(Policy = Permissions.SecuritySettings.Edit)]
public class UpdateSecurityPolicyCommand : IRequest<Result<int>>
{
    [Description("Idle timeout (minutes)")] public int IdleTimeoutMinutes { get; set; }
    [Description("Countdown (seconds)")] public int CountdownSeconds { get; set; }
}

public class UpdateSecurityPolicyCommandHandler : IRequestHandler<UpdateSecurityPolicyCommand, Result<int>>
{
    private readonly IApplicationDbContextFactory _dbContextFactory;
    private readonly IIdleTimeoutPolicyProvider _provider;

    public UpdateSecurityPolicyCommandHandler(
        IApplicationDbContextFactory dbContextFactory, IIdleTimeoutPolicyProvider provider)
    {
        _dbContextFactory = dbContextFactory;
        _provider = provider;
    }

    public async ValueTask<Result<int>> Handle(
        UpdateSecurityPolicyCommand request, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateAsync(cancellationToken);

        var policy = await db.SecurityPolicies
            .OrderBy(p => p.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (policy is null)
        {
            // Only reachable if nothing has read the policy yet on a fresh database - the provider
            // seeds the row on first read. Creating it here keeps the save from depending on that
            // ordering.
            policy = new SecurityPolicy();
            db.SecurityPolicies.Add(policy);
        }

        policy.IdleTimeoutMinutes = request.IdleTimeoutMinutes;
        policy.CountdownSeconds = request.CountdownSeconds;

        // SecurityPolicy is IAuditable, so the before/after values land in AuditTrails inside this
        // same transaction - a policy change is a security event and is recorded as one.
        await db.SaveChangesAsync(cancellationToken);

        // Immediately, not on a TTL. The cached policy is read on every authenticated request by the
        // principal check; leaving a stale one in place would mean the change not reaching sessions
        // already open, which is the one behaviour putting this on a screen was meant to provide.
        _provider.Invalidate();

        return await Result<int>.SuccessAsync(policy.Id);
    }
}
```

#### 2.12c `Commands/UpdateSecurityPolicyCommandValidator.cs`

```csharp
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace CleanArchitecture.Blazor.Application.Features.SecuritySettings.Commands;

/// <summary>
/// Holds an administrator to the deployment's bounds.
/// </summary>
/// <remarks>
/// The bounds come from configuration rather than from constants here, because the authentication
/// cookie's lifetime is derived from the same values: a policy outside them would be one the cookie
/// cannot honour, producing sessions that end at a time nobody chose. The provider clamps on read as
/// well - this validator is what makes the refusal visible instead of silent.
/// </remarks>
public class UpdateSecurityPolicyCommandValidator : AbstractValidator<UpdateSecurityPolicyCommand>
{
    public UpdateSecurityPolicyCommandValidator(IIdleTimeoutSettings settings)
    {
        RuleFor(v => v.IdleTimeoutMinutes)
            .InclusiveBetween(settings.MinIdleTimeoutMinutes, settings.MaxIdleTimeoutMinutes)
            .WithMessage(_ =>
                $"The idle timeout must be between {settings.MinIdleTimeoutMinutes} and " +
                $"{settings.MaxIdleTimeoutMinutes} minutes.");

        RuleFor(v => v.CountdownSeconds)
            .InclusiveBetween(10, 600)
            .WithMessage("The countdown must be between 10 and 600 seconds.");

        // The warning cannot be longer than the wait that precedes it: the countdown opens AFTER the
        // idle window elapses, so a countdown longer than the window means most of a session's
        // "idle" time is spent showing a dialog.
        RuleFor(v => v)
            .Must(v => v.CountdownSeconds <= v.IdleTimeoutMinutes * 60)
            .WithMessage(v =>
                $"The countdown ({v.CountdownSeconds}s) cannot exceed the idle timeout " +
                $"({v.IdleTimeoutMinutes}m = {v.IdleTimeoutMinutes * 60}s).")
            .OverridePropertyName(nameof(UpdateSecurityPolicyCommand.CountdownSeconds));
    }
}
```

#### 2.12d `Security/SecuritySettingsPermissions.cs`

Note the namespace: it declares into `CleanArchitecture.Blazor.Application.Common.Security`
rather than the feature namespace, so that `Permissions.SecuritySettings.*` sits alongside
every other permission group as a partial class.

```csharp
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace CleanArchitecture.Blazor.Application.Common.Security;

public static partial class Permissions
{
    [DisplayName("Security Settings Permissions")]
    [Description("Set permissions for the installation's security policy")]
    public static class SecuritySettings
    {
        [Description("Allows viewing the security policy")]
        public const string View = "Permissions.SecuritySettings.View";

        // Deliberately its own permission rather than a general administration right: changing how
        // long a session may sit unattended is a security control, and the set of people who should
        // hold it is not the same as the set who administer users or picklists.
        [Description("Allows changing the security policy, including the idle timeout")]
        public const string Edit = "Permissions.SecuritySettings.Edit";
    }
}

public class SecuritySettingsAccessRights
{
    public bool View { get; set; }
    public bool Edit { get; set; }
}
```

**There is no command for the per-user preference.** `SecurityTab.razor` writes it directly
through `UserManager<ApplicationUser>.UpdateAsync`, the same way the rest of the profile
screen edits the user record. If your repository routes all writes through the mediator,
adding a `SetMyIdleTimeoutCommand` is a straightforward change — but it must then perform the
same two steps the screen does: clamp against `[MinIdleTimeoutMinutes, administered]`, and
call `IIdleTimeoutPolicyProvider.InvalidateUser(userId)` immediately after saving.

**Duplicated literals to be aware of.** The validator hard-codes `InclusiveBetween(10, 600)`
for the countdown, while `IdleTimeoutSettings.MinCountdownSeconds` / `MaxCountdownSeconds`
declare the same numbers in the Infrastructure layer. They are duplicated because the
Application layer does not reference Infrastructure. If you move the constants somewhere both
layers can see (the `IIdleTimeoutSettings` interface, for instance), reference them in both
places rather than leaving two copies.

---

### 2.13 `src/Server.UI/Pages/SystemManagement/SecuritySettings.razor` — **new**

The administrator screen, in full.

```razor
@page "/system/security-settings"
@using CleanArchitecture.Blazor.Application.Features.SecuritySettings.Commands
@using CleanArchitecture.Blazor.Application.Features.SecuritySettings.Queries

@attribute [Authorize(Policy = Permissions.SecuritySettings.View)]
@inject IStringLocalizer<SecuritySettings> L
<PageTitle>@AppStrings.SecuritySettings</PageTitle>

<MudContainer MaxWidth="MaxWidth.Small" Class="pa-0">
    <MudCard Elevation="2">
        <MudCardHeader>
            <CardHeaderContent>
                <MudStack Row AlignItems="AlignItems.Center" Spacing="2">
                    <MudIcon Icon="@Icons.Material.Filled.Timer" Size="Size.Large" />
                    <MudText Typo="Typo.h5">@AppStrings.IdleTimeout</MudText>
                </MudStack>
            </CardHeaderContent>
        </MudCardHeader>

        <MudCardContent>
            @if (_model is null)
            {
                <MudProgressCircular Indeterminate="true" />
            }
            else if (!_model.Enabled)
            {
                <MudAlert Severity="Severity.Info">
                    @L["The idle timeout is switched off for this deployment. Enable it in configuration (SecuritySettings:IdleTimeout:Enabled) to administer a policy here."]
                </MudAlert>
            }
            else
            {
                <MudForm @ref="_form" Model="_model">
                    @* State the effective value plainly. A countdown that appears unexpectedly is
                       the single most common support call this feature generates, and an
                       administrator who cannot see what they have set cannot answer it. *@
                    <MudAlert Severity="Severity.Normal" Dense="true" Class="mb-4">
                        @string.Format(AppStrings.IdleTimeoutEffectiveFormat, _model.IdleTimeoutMinutes)
                    </MudAlert>

                    <MudNumericField T="int"
                                     @bind-Value="_model.IdleTimeoutMinutes"
                                     Label="@AppStrings.IdleTimeoutMinutesLabel"
                                     HelperText="@string.Format(AppStrings.IdleTimeoutBoundsFormat, _model.MinIdleTimeoutMinutes, _model.MaxIdleTimeoutMinutes)"
                                     Min="_model.MinIdleTimeoutMinutes"
                                     Max="_model.MaxIdleTimeoutMinutes"
                                     Immediate="true"
                                     Class="mb-4" />

                    <MudNumericField T="int"
                                     @bind-Value="_model.CountdownSeconds"
                                     Label="@AppStrings.CountdownSecondsLabel"
                                     Min="10"
                                     Max="600"
                                     Immediate="true"
                                     Class="mb-4" />

                    <MudAlert Severity="Severity.Warning" Dense="true">
                        @AppStrings.IdleTimeoutAffectsLiveSessions
                    </MudAlert>
                </MudForm>
            }
        </MudCardContent>

        <MudCardActions>
            <AuthorizeView Policy="@Permissions.SecuritySettings.Edit">
                <Authorized>
                    <MudButton Variant="Variant.Filled"
                               Color="Color.Primary"
                               Disabled="@(_saving || _model is null || !_model.Enabled)"
                               OnClick="SaveAsync">
                        @if (_saving)
                        {
                            <MudProgressCircular Size="Size.Small" Indeterminate="true" Class="mr-2" />
                        }
                        @AppStrings.Save
                    </MudButton>
                </Authorized>
            </AuthorizeView>
        </MudCardActions>
    </MudCard>
</MudContainer>

@code {
    // Read and write both go through the mediator, so the deny-by-default request authorization
    // covers them - View to load, Edit to save - rather than the page attribute being the only
    // check. The AuthorizeView above hides the button; the command attribute is what enforces it.

    private SecurityPolicyDto? _model;
    private MudForm? _form;
    private bool _saving;

    protected override async Task OnInitializedAsync()
    {
        var result = await Mediator.Send(new GetSecurityPolicyQuery());
        result.Match(
            dto => _model = dto,
            errors => Snackbar.Add(errors, Severity.Error));
    }

    private async Task SaveAsync()
    {
        if (_model is null) return;

        _saving = true;
        try
        {
            var result = await Mediator.Send(new UpdateSecurityPolicyCommand
            {
                IdleTimeoutMinutes = _model.IdleTimeoutMinutes,
                CountdownSeconds = _model.CountdownSeconds
            });

            result.Match(
                _ =>
                {
                    Snackbar.Add(AppStrings.SaveSuccess, Severity.Info);
                    // Say it out loud on the save, not only in the static warning above: an
                    // administrator tightening the window is about to sign people out.
                    Snackbar.Add(AppStrings.IdleTimeoutAffectsLiveSessions, Severity.Warning);
                },
                errors => Snackbar.Add(errors, Severity.Error));
        }
        finally
        {
            _saving = false;
        }
    }
}
```

Three things on this screen are deliberate:

- **Both the route attribute and the request authorization apply.** `@attribute [Authorize(Policy = ...View)]`
  guards the page; `[RequestAuthorize]` on the query and command is what actually enforces
  read and write. The `<AuthorizeView Policy="...Edit">` around the Save button only hides
  the control.
- **The bounds travel with the policy** in `SecurityPolicyDto`, so the numeric field's `Min`
  and `Max` and its helper text come from the deployment's configuration rather than being
  hard-coded on the screen.
- **The `!_model.Enabled` branch is unreachable in normal operation**, because
  `SecuritySettingsPageMiddleware` 404s the route when the feature is off. It is a
  belt-and-braces fallback for a host that has not registered the middleware.

---

### 2.14 `src/Server.UI/Pages/Identity/Users/Components/SecurityTab.razor` — **new**

The per-user preference screen, in full.

```razor
@using CleanArchitecture.Blazor.Application.Common.Interfaces
@using CleanArchitecture.Blazor.Domain.Identity
@inherits OwningComponentBase
@inject IStringLocalizer<CleanArchitecture.Blazor.Server.UI.Pages.Identity.Users.Profile> L
@inject IIdleTimeoutSettings IdleTimeoutSettings
@inject IIdleTimeoutPolicyProvider PolicyProvider

@if (!IdleTimeoutSettings.Enabled || !IdleTimeoutSettings.AllowUserOverride)
{
    @* Absent, not disabled. A greyed-out control invites a support call asking how to enable it;
       a deployment that has switched user overrides off has decided this is not the user's to set. *@
}
else if (_loaded)
{
    <MudGrid Class="pa-4" Justify="Justify.Center">
        <MudItem xs="12" sm="8" md="6">
            <MudText Typo="Typo.h6" GutterBottom="true">@AppStrings.IdleTimeout</MudText>

            @* Stated plainly, because a countdown that appears unexpectedly is the most common
               support call this feature generates. *@
            <MudAlert Severity="Severity.Normal" Dense="true" Class="mb-4">
                @string.Format(AppStrings.IdleTimeoutEffectiveFormat, _effectiveMinutes)
            </MudAlert>

            <MudSwitch T="bool"
                       Value="_useOwn"
                       ValueChanged="OnUseOwnChanged"
                       Color="Color.Primary"
                       Label="@AppStrings.SignMeOutSooner" />

            @if (_useOwn)
            {
                <MudNumericField T="int"
                                 @bind-Value="_chosenMinutes"
                                 Label="@AppStrings.IdleTimeoutMinutesLabel"
                                 HelperText="@string.Format(AppStrings.IdleTimeoutBoundsFormat, IdleTimeoutSettings.MinIdleTimeoutMinutes, _administeredMinutes)"
                                 Min="IdleTimeoutSettings.MinIdleTimeoutMinutes"
                                 Max="_administeredMinutes"
                                 Immediate="true"
                                 Class="mt-4" />
            }

            <MudStack Row Class="mt-6" Spacing="2">
                <MudButton Variant="Variant.Filled" Color="Color.Primary"
                           Disabled="_saving" OnClick="SaveAsync">
                    @AppStrings.Save
                </MudButton>
                @if (_useOwn)
                {
                    <MudButton Variant="Variant.Text" Disabled="_saving" OnClick="ResetAsync">
                        @AppStrings.UseOrganisationDefault
                    </MudButton>
                }
            </MudStack>
        </MudItem>
    </MudGrid>
}

@code {
    // "Sign me out sooner than the organisation default" - and only sooner.
    //
    // The upper bound on this field is the ADMINISTERED window, not the deployment maximum, so the
    // control cannot express a longer session at all. That is the point: an idle timeout guards an
    // unattended workstation, and a user who could raise their own would simply raise it. Shortening
    // is both safe and useful - someone on a shared shop-floor terminal can choose five minutes.
    //
    // The bound here is a courtesy, not the enforcement: IdleTimeoutPolicyProvider takes the minimum
    // of the two at read time, so a value put into the database by other means is still narrowed.

    [CascadingParameter] private Task<AuthenticationState> AuthenticationStateTask { get; set; } = null!;

    private UserManager<ApplicationUser> _userManager = null!;
    private string? _userId;
    private bool _loaded;
    private bool _saving;
    private bool _useOwn;
    private int _chosenMinutes;
    private int _administeredMinutes;
    private int _effectiveMinutes;

    protected override async Task OnInitializedAsync()
    {
        if (!IdleTimeoutSettings.Enabled || !IdleTimeoutSettings.AllowUserOverride)
        {
            return;
        }

        _userManager = ScopedServices.GetRequiredService<UserManager<ApplicationUser>>();

        var state = await AuthenticationStateTask;
        _userId = _userManager.GetUserId(state.User);

        var administered = await PolicyProvider.GetAdministeredAsync();
        _administeredMinutes = administered.IdleMinutes;

        var user = _userId is null ? null : await _userManager.FindByIdAsync(_userId);
        _useOwn = user?.IdleTimeoutMinutes is not null;
        _chosenMinutes = user?.IdleTimeoutMinutes ?? _administeredMinutes;

        _effectiveMinutes = (await PolicyProvider.GetEffectiveAsync(state.User)).IdleMinutes;
        _loaded = true;
    }

    private void OnUseOwnChanged(bool value)
    {
        _useOwn = value;
        if (!value)
        {
            _chosenMinutes = _administeredMinutes;
        }
    }

    private Task SaveAsync() => PersistAsync(_useOwn ? _chosenMinutes : null);

    private Task ResetAsync()
    {
        _useOwn = false;
        _chosenMinutes = _administeredMinutes;
        return PersistAsync(null);
    }

    private async Task PersistAsync(int? minutes)
    {
        if (_userId is null) return;

        if (minutes is { } chosen &&
            (chosen < IdleTimeoutSettings.MinIdleTimeoutMinutes || chosen > _administeredMinutes))
        {
            Snackbar.Add(
                string.Format(AppStrings.IdleTimeoutBoundsFormat,
                    IdleTimeoutSettings.MinIdleTimeoutMinutes, _administeredMinutes),
                Severity.Error);
            return;
        }

        _saving = true;
        try
        {
            var user = await _userManager.FindByIdAsync(_userId);
            if (user is null) return;

            user.IdleTimeoutMinutes = minutes;
            var result = await _userManager.UpdateAsync(user);

            if (!result.Succeeded)
            {
                Snackbar.Add(string.Join("; ", result.Errors.Select(e => e.Description)), Severity.Error);
                return;
            }

            // Immediately, so the change is live on this user's very next request rather than at
            // their next sign-in. This is the reason the preference is read from the database and
            // not carried as a claim - see IdleTimeoutPolicyProvider's remarks.
            PolicyProvider.InvalidateUser(_userId);

            var state = await AuthenticationStateTask;
            _effectiveMinutes = (await PolicyProvider.GetEffectiveAsync(state.User)).IdleMinutes;

            Snackbar.Add(AppStrings.SaveSuccess, Severity.Info);
        }
        finally
        {
            _saving = false;
        }
    }
}
```

`@inherits OwningComponentBase` gives the component its own DI scope, from which it resolves
`UserManager<ApplicationUser>` — a scoped service that must not be captured from the
circuit's long-lived scope.

The numeric field's upper bound is `_administeredMinutes`, **not** the deployment maximum, so
the control cannot express a longer session at all. That bound is a courtesy: the enforcement
is `Math.Min` at read time in the provider.

---

### 2.15 `src/Server.UI/Services/IdentityComponentsEndpointRouteBuilderExtensions.cs` — **modified**

This file is ~1,000 lines and maps the whole Identity endpoint surface (login, external
login, passkeys, 2FA, logout, refresh-signin, …). **Only the keep-alive endpoint is new to
this feature.** The origin helper and the logout endpoint reproduced below already existed and
are included because the feature depends on them.

The relevant excerpts follow, each with enough surrounding context to place it. Everything
else in the file is unchanged and is not reproduced.

**The class header and route constants** (lines ~23–50, verbatim; the file declares several
more constants after `Login` which are omitted):

```csharp
internal static class IdentityComponentsEndpointRouteBuilderExtensions
{
    /// <summary>
    /// The endpoint URL for performing external login operations.
    /// </summary>
    public static readonly string PerformExternalLogin = "/pages/authentication/performexternallogin";
    
    /// <summary>
    /// The endpoint URL for handling external login callbacks.
    /// </summary>
    public static readonly string ExternalLogin = "/pages/authentication/externallogin";
    
    /// <summary>
    /// The endpoint URL for user logout operations.
    /// </summary>
    public static readonly string Logout = "/pages/authentication/logout";
```

`IdleTimeoutMonitor` passes `IdentityComponentsEndpointRouteBuilderExtensions.Logout` into
the JS module as `logoutUrl`, so the module posts to the application's single existing
sign-out endpoint rather than introducing a second one.

**The entry point and route group** (verbatim excerpt):

```csharp
    public static IEndpointConventionBuilder MapAdditionalIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var loggerFactory = endpoints.ServiceProvider.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger("IEndpointConventionBuilder");

        ArgumentNullException.ThrowIfNull(endpoints);

        var accountGroup = endpoints.MapGroup("/pages/authentication");
```

**The origin-check helper** (pre-existing, verbatim):

```csharp
    /// <summary>
    /// Validates that the request originates from the same domain to prevent CSRF attacks.
    /// </summary>
    /// <param name="context">The HTTP context of the request.</param>
    /// <param name="logger">Logger for security warnings.</param>
    /// <returns>True if the origin is valid, false otherwise.</returns>
    private static bool ValidateRequestOrigin(HttpContext context, ILogger logger)
    {
        var referer = context.Request.Headers.Referer.ToString();
        var host = context.Request.Host.ToString();
        var scheme = context.Request.Scheme;
        var expectedOrigin = $"{scheme}://{host}";

        if (string.IsNullOrEmpty(referer) || !referer.StartsWith(expectedOrigin, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogError("Request from unauthorized origin. ");
            return false;
        }
        return true;
    }
```

A legitimate same-origin `fetch` sends `Referer: https://host/whatever-page`, which passes.
A cross-origin POST and a request with no `Referer` at all are both refused. The same helper
already guarded the login and external-login endpoints; the keep-alive reuses it rather than
inventing a second rule.

**The logout endpoint** (pre-existing, verbatim) — the JS module POSTs to this:

```csharp
        // Configure user logout endpoint
        accountGroup.MapPost("/logout", async (
            ClaimsPrincipal user,
            SignInManager<ApplicationUser> signInManager,
            [FromForm] string? returnUrl = null) =>
        {
            // Sign out the current user and clear authentication cookies
            await signInManager.SignOutAsync().ConfigureAwait(false);
            logger.LogInformation("{UserName} has logged out.", user.Identity?.Name);

            // The sign-out has already happened by this point, so nothing about the return URL is a
            // reason to fail the request. A missing one falls back to the login page; a non-local
            // one is DISCARDED rather than followed, because honouring it would turn an
            // unauthenticated-by-then endpoint into an open redirect.
            return TypedResults.LocalRedirect(ResolveLocalReturnUrl(returnUrl, RedirectUrls.Login));
        }).RequireAuthorization().DisableAntiforgery();
```

**The keep-alive endpoint — new, and the whole of it, verbatim including its comment block.
This is the piece to read most carefully:**

```csharp
        // Answers with STATUS CODES, not redirects, and is the one endpoint here that does.
        //
        // Everything else on this surface is browser-facing, so a challenge that redirects to the
        // login page is right for it. This one is machine-facing - no human ever navigates to it -
        // and the redirect was actively harmful: the browser's fetch() follows redirects by default,
        // so an expired session answered 302 -> /account/login -> 200, and every place the client
        // checks for a dead session saw success. The focus re-verification, the ping's own check and
        // the "do not resurrect a dead session" guard behind Stay Logged In were all inert (Pass
        // 16A, Finding 1).
        //
        // Hence AllowAnonymous plus an explicit check, rather than RequireAuthorization: the
        // fallback policy's challenge is what produces the redirect, and it fires before any handler
        // runs, so this cannot be fixed from inside the handler while the policy still applies. The
        // authorization is not lost, it is stated here - and this endpoint has nothing to protect in
        // any case, since it returns a bare status and the last-activity stamp is written by
        // IdleSessionEnforcer during AUTHENTICATION, for an authenticated principal only, whatever
        // this handler decides. An anonymous caller learns only that they are not signed in.
        //
        // Deliberately NOT done by touching the cookie handler's OnRedirectToLogin: that event is
        // shared by every page and endpoint in the application, and Pass 4B-H is the standing
        // lesson in what a blanket change to authentication responses costs.
        //
        // The JSON bodies are not decoration either. UseStatusCodePagesWithReExecute("/not-found")
        // rewrites any 400-599 response that has no body and no content type, so a bare
        // Results.Unauthorized() would come back as the not-found page. Giving the response a
        // content type keeps it the terse machine answer the client is reading.
        //
        // The rest is unchanged: it exists to make an AUTHENTICATED HTTP REQUEST and nothing else. A
        // Blazor Server user working inside one long-lived SignalR circuit makes almost none, so the
        // sliding authentication cookie never renews and expires underneath somebody who has been
        // working for hours; the first real request afterwards - a download, a refresh, an export -
        // bounces them to the login page mid-task. This ping is what keeps the sliding window
        // moving, which is also why "Stay Logged In" calls it rather than only resetting a timer in
        // the browser.
        //
        // It is load-bearing in a second way: IdleSessionEnforcer treats a request to THIS path, and
        // only this path, as the user being present, and stamps the ticket's last-activity from it.
        // Every other authenticated request deliberately does not count - otherwise an unattended
        // workstation would keep renewing its own session.
        //
        // Mapped outside accountGroup because the enforcer matches an absolute path that
        // Infrastructure names (IdleTimeoutRoutes.KeepAlive), and a route group prefix would put the
        // two out of step.
        //
        // Origin-checked rather than antiforgery-tokenised, matching the login endpoint. It is not
        // exempt from CSRF thinking just because it returns nothing: this application sets
        // SameSite=None on the authentication cookie, so a cross-site POST would carry it, and an
        // unchecked keep-alive would let any page the user happens to have open hold their session
        // open indefinitely - defeating precisely the control this endpoint serves.
        endpoints.MapPost(IdleTimeoutRoutes.KeepAlive, (HttpContext context) =>
            {
                if (context.User.Identity?.IsAuthenticated != true)
                {
                    return Results.Json(new { signedOut = true }, statusCode: StatusCodes.Status401Unauthorized);
                }

                return ValidateRequestOrigin(context, logger)
                    ? Results.NoContent()
                    : Results.Json(new { forbidden = true }, statusCode: StatusCodes.Status403Forbidden);
            })
            .AllowAnonymous() // see above: the check is stated in the handler so the answer can be a status code
            .DisableAntiforgery();



```

**Reproduce these five properties exactly:**

| Property | Why |
|---|---|
| `MapPost` at the **absolute** path `/account/keep-alive`, outside any route group | `IdleSessionEnforcer` matches an absolute path from a shared constant; a group prefix puts the two out of step, and the mismatch is silent — pings return 204 and no session ever renews |
| `.AllowAnonymous()` with the authentication check written into the handler | A fallback authorization policy's challenge fires *before* any handler and redirects; `fetch` follows redirects, so an expired session reads as `200` and every client-side dead-session check becomes inert |
| `401` for unauthenticated, `403` for origin-refused, `204` for success | Status codes the client can branch on. Note the order: the authentication check runs first, so an anonymous cross-origin caller gets `401`, not `403` |
| **JSON bodies on both error responses** | `UseStatusCodePagesWithReExecute("/not-found")` rewrites any 400–599 response that has no body and no content type. A bare `Results.Unauthorized()` comes back as the not-found page. `204` needs no body — it is outside that range |
| `.DisableAntiforgery()` **plus** the explicit origin check | Not exempt from CSRF thinking. The cookie is `SameSite=None`, so a cross-site POST carries it, and an unchecked keep-alive lets any page the user has open hold their session open indefinitely — defeating precisely the control the endpoint serves |

**Do not** implement the status-code behaviour by teaching the cookie handler's
`OnRedirectToLogin` about this path. That event is shared by every page and endpoint in the
application; a blanket change to authentication responses is far more dangerous than the
problem it solves.

**One caveat the source does not state.** `ValidateRequestOrigin` refuses a request with no
`Referer`, and the JS module treats `403` exactly like `401` — it navigates to the login
page. A browser or extension configured with a strict referrer policy that suppresses
`Referer` on same-origin POSTs will therefore produce spurious sign-outs. The default
`strict-origin-when-cross-origin` policy sends the full URL same-origin, so this does not
arise in a default browser, but it is worth knowing if your target repository sets a
`Referrer-Policy` header.

---

### 2.16 `src/Server.UI/Middlewares/SecuritySettingsPageMiddleware.cs` — **new**

```csharp
using CleanArchitecture.Blazor.Application.Common.Interfaces;

namespace CleanArchitecture.Blazor.Server.UI.Middlewares;

/// <summary>
/// Closes the security-settings screen when the idle timeout is switched off in configuration.
/// </summary>
/// <remarks>
/// The same shape as <see cref="SelfRegistrationMiddleware"/>, and for the same reason: a runtime
/// flag rather than conditional source removal, so a generated project can turn the feature on or
/// off without regenerating from the template.
/// <para>
/// The response is <b>404, not 403</b>, exactly as the self-registration surface answers: with the
/// idle timeout disabled the screen does not exist, and saying "forbidden" would confirm it is
/// there. It is also not an authorization failure - a user holding
/// <c>Permissions.SecuritySettings.Edit</c> is not being refused, there is simply nothing to edit.
/// </para>
/// <para>
/// This is one of the two surfaces the feature owns. The other, the Security tab on the profile
/// page, is a component rather than a route, so <c>Profile.razor</c> omits the tab panel itself -
/// there is no route to close. Both must agree, or "Enabled: false makes the feature inert" is only
/// half true (Pass 16A, Finding 3, which found this screen answering 200 and the profile showing an
/// empty tab).
/// </para>
/// </remarks>
public class SecuritySettingsPageMiddleware
{
    /// <summary>The route the security-settings page is served at.</summary>
    public const string SecuritySettingsPath = "/system/security-settings";

    private readonly RequestDelegate _next;

    public SecuritySettingsPageMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, IIdleTimeoutSettings idleTimeoutSettings)
    {
        if (ShouldBlock(context.Request.Path, idleTimeoutSettings.Enabled))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await _next(context);
    }

    /// <summary>
    /// The decision, separated from the pipeline so it can be tested directly against a path rather
    /// than only through a running host.
    /// </summary>
    public static bool ShouldBlock(PathString path, bool idleTimeoutEnabled)
    {
        if (idleTimeoutEnabled) return false;
        if (!path.HasValue) return false;

        var value = path.Value!;

        // Trailing segments and trailing slashes must not be a way around the block.
        return value.Equals(SecuritySettingsPath, StringComparison.OrdinalIgnoreCase)
               || value.StartsWith(SecuritySettingsPath + "/", StringComparison.OrdinalIgnoreCase);
    }
}
```

---

### 2.17 `src/Infrastructure/DependencyInjection.cs` — **modified**

Three separate edits in a large file. Each is reproduced with its surrounding context.

#### 2.17a Options binding (in `AddSettings`, after the pre-existing `StorageSettings` block)

```csharp
        // StorageSettings follows the DatabaseSettings idiom exactly: IValidatableObject +
        // ValidateDataAnnotations().ValidateOnStart(), so an unsupported provider - or an azureblob
        // provider with no connection string - is a startup failure naming the offending value,
        // not a surprise on the first upload.
        services.AddOptions<StorageSettings>()
            .Bind(configuration.GetSection(StorageSettings.Key))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton(s => s.GetRequiredService<IOptions<StorageSettings>>().Value);

        // IdleTimeoutSettings follows the same idiom, and the startup failure earns its keep here:
        // these values size the authentication cookie, so a deployment that configures a countdown
        // longer than the shortest window it permits would produce sessions ending at a time nobody
        // chose. Note this section is nested - "SecuritySettings:IdleTimeout" - so GetSection takes
        // the path, not a top-level name.
        services.AddOptions<IdleTimeoutSettings>()
            .Bind(configuration.GetSection(IdleTimeoutSettings.Key))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton(s => s.GetRequiredService<IOptions<IdleTimeoutSettings>>().Value)
            .AddSingleton<IIdleTimeoutSettings>(s => s.GetRequiredService<IOptions<IdleTimeoutSettings>>().Value);

        return services;
```

The `IdleTimeoutSettings` block is the addition. Both the concrete type and the interface are
registered as singletons resolving to the same options instance — the concrete type for the
wiring tests and anything inside Infrastructure, the interface for the Application layer and
the Razor components.

#### 2.17b Service registration (in `AddDatabaseServices`)

```csharp
        services.AddScoped<IApplicationDbContextFactory, ApplicationDbContextFactory>();

        // The idle-timeout pair. Scoped because the provider resolves a scoped context factory; the
        // enforcer is resolved from HttpContext.RequestServices inside the cookie event, which is
        // the request scope.
        services.AddScoped<IIdleTimeoutPolicyProvider, IdleTimeoutPolicyProvider>();
        services.AddScoped<IdleSessionEnforcer>();
        services.AddScoped<ApplicationDbContextInitializer>();
```

The two `Idle*` lines and their comment are the addition.

#### 2.17c Cookie configuration — **the highest-risk part to reproduce**

The full `ConfigureApplicationCookie` block, verbatim, with the tail of the preceding
authentication builder for context. `LOGIN_PATH` is a private const on the same class:
`private const string LOGIN_PATH = "/account/login";`

```csharp
            .AddIdentityCookies(options => { });

        services.ConfigureApplicationCookie(options =>
        {
            var idle = configuration.GetSection(IdleTimeoutSettings.Key).Get<IdleTimeoutSettings>()
                       ?? new IdleTimeoutSettings();

            // Sized from the WIDEST window any policy may reach, not from the policy in force. A
            // cookie is issued once and cannot be shortened afterwards, so deriving its lifetime
            // from a runtime-administered value would mean an administrator's change never reaching
            // sessions already open. Tightening is enforced per request by IdleSessionEnforcer
            // instead; this is only the outer bound.
            options.ExpireTimeSpan = idle.Enabled
                ? idle.CookieLifetime
                : IdleTimeoutSettings.DisabledCookieLifetime;

            options.SlidingExpiration = true;
            options.SessionStore = new MemoryCacheTicketStore();
            options.LoginPath = LOGIN_PATH;
            options.Cookie.SameSite = SameSiteMode.None;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;

            if (!idle.Enabled)
            {
                return;
            }

            // CHAINED, never replaced. At this point OnValidatePrincipal is Identity's security-stamp
            // validator, installed by AddIdentityCookies above - the thing that makes "changing a
            // user's roles or password signs their existing sessions out" true. Assigning over it
            // would delete that guarantee silently: every escalation guard in the application would
            // still compile, still pass its own tests, and no longer end a session whose permissions
            // had been revoked. IdleTimeoutWiringTests asserts the chain survives.
            var securityStampValidation = options.Events.OnValidatePrincipal;

            options.Events.OnValidatePrincipal = async context =>
            {
                await securityStampValidation(context).ConfigureAwait(false);

                // The stamp validator rejects by nulling the principal. Nothing left to enforce.
                if (context.Principal is null)
                {
                    return;
                }

                var enforcer = context.HttpContext.RequestServices
                    .GetRequiredService<IdleSessionEnforcer>();

                if (await enforcer.IsStillValidAsync(context).ConfigureAwait(false))
                {
                    return;
                }

                context.RejectPrincipal();
                await context.HttpContext
                    .SignOutAsync(IdentityConstants.ApplicationScheme)
                    .ConfigureAwait(false);
            };
        });
        services.AddDataProtection().PersistKeysToDbContext<ApplicationDbContext>();
```

Before this feature the same block existed without the `idle` local, with a fixed
`ExpireTimeSpan`, and with no `OnValidatePrincipal` assignment at all — `AddIdentityCookies`
installed the stamp validator and nothing touched it.

**Five things here that must be reproduced exactly:**

1. **Capture the existing delegate, then chain.**
   `var securityStampValidation = options.Events.OnValidatePrincipal;` followed by
   `await securityStampValidation(context)` inside the new handler. Writing
   `options.Events.OnValidatePrincipal = async ctx => { ... }` without the capture silently
   deletes ASP.NET Identity's security-stamp validation. See §6.
2. **The `context.Principal is null` guard after the stamp validator.** The stamp validator
   rejects by nulling the principal. Running the idle check afterwards on a null principal is
   at best pointless and at worst a null-reference inside the authentication handler.
3. **`RejectPrincipal()` *and* `SignOutAsync`.** `RejectPrincipal` ends the request's
   identity; `SignOutAsync` deletes the cookie and, with a server-side ticket store, removes
   the stored ticket. Without the second the browser goes on presenting a cookie that will be
   rejected on every subsequent request.
4. **The `if (!idle.Enabled) return;` early exit sits *after* the cookie properties are set.**
   Even with the feature off the cookie still needs its `ExpireTimeSpan`, `LoginPath`,
   `SameSite`, `SecurePolicy` and `SessionStore`. Only the event chaining is skipped.
5. **The settings are read from `IConfiguration`, not from DI.**
   `ConfigureApplicationCookie` runs during service registration, before a service provider
   exists. The `?? new IdleTimeoutSettings()` fallback means a missing configuration section
   yields the shipped defaults rather than a null-reference at startup.

**`SameSite=None` is this application's pre-existing choice, not something the feature
introduced** — it is why the keep-alive endpoint has to be origin-checked. If your target
repository uses `SameSite=Lax` or `Strict`, keep the origin check anyway: it costs nothing and
the endpoint's whole purpose is to keep a session alive.

**The server-side ticket store matters for one detail.** `options.SessionStore` means the
authentication ticket — including the last-activity stamp `IdleSessionEnforcer` writes into
`context.Properties.Items` — is held server-side and never travels to the browser. With a
cookie-borne ticket the stamp would still be inside the protected payload and therefore not
tamperable, so the feature works either way; the store is an existing property of this
application, reproduced here as context rather than as a requirement. Its file is at §2.27.

---

### 2.18 `src/Server.UI/DependencyInjection.cs` — **modified**

One line added to the request pipeline, in `ConfigureServer`. Verbatim, with the surrounding
pipeline for placement:

```csharp
        // Single global exception handler registration to activate IExceptionHandler (GlobalExceptionHandler) + ProblemDetails pipeline.
        app.UseExceptionHandler();
        app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
        app.UseForwardedHeaders();
        // Liveness must answer before a user has authenticated, so it opts out of the fallback policy.
        app.MapHealthChecks("/health").AllowAnonymous();
        //app.UseDataProtectionKeyCheck();
        app.UseAuthentication();
        app.UseAuthorization();
        // After authentication, so context.User carries the MustChangePassword claim; before the
        // endpoints, so a flagged user cannot reach one. Only the HTTP half - in-circuit navigation
        // is guarded by ForcePasswordChangeGuard inside AppLayout.
        app.UseMiddleware<ForcePasswordChangeMiddleware>();
        // Before the endpoints, so a disabled registration surface is unreachable by direct URL as
        // well as by the (hidden) link on the login page.
        app.UseMiddleware<SelfRegistrationMiddleware>();
        // Same reasoning, same shape: with the idle timeout switched off its settings screen does not
        // exist, so the route answers 404 rather than rendering a page explaining that the feature is
        // off. The profile page's Security tab is omitted at the component level to match.
        app.UseMiddleware<SecuritySettingsPageMiddleware>();
        app.UseAntiforgery();
        app.UseHttpsRedirection();
```

The `SecuritySettingsPageMiddleware` line and its comment are the addition. Placement is
after `UseAuthorization()` and before the endpoints.

Note `app.UseStatusCodePagesWithReExecute("/not-found", ...)` near the top — this is the
middleware that makes the keep-alive endpoint's JSON bodies necessary.


---

### 2.19 Migrations — **modified**

The source repository is a project template, so it ships a single regenerated
`InitialCreate` migration per provider rather than an incremental one. **In a live project
that is exactly what you must not do** — see §7. What follows is the *content* your additive
migration has to produce: one new table and one new nullable column.

Three provider projects each carry the same two changes:

- `src/Migrators/Migrators.MSSQL/Migrations/20260831123525_InitialCreate.cs`
- `src/Migrators/Migrators.PostgreSQL/Migrations/20260831123517_InitialCreate.cs`
- `src/Migrators/Migrators.SqLite/Migrations/20260831123533_InitialCreate.cs`

#### The new table

SQL Server, verbatim from the migration:

```csharp
            migrationBuilder.CreateTable(
                name: "SecurityPolicies",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    IdleTimeoutMinutes = table.Column<int>(type: "int", nullable: false),
                    CountdownSeconds = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedById = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    LastModifiedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastModifiedById = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecurityPolicies", x => x.Id);
                });
```

PostgreSQL, verbatim:

```csharp
            migrationBuilder.CreateTable(
                name: "SecurityPolicies",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    IdleTimeoutMinutes = table.Column<int>(type: "integer", nullable: false),
                    CountdownSeconds = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedById = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    LastModifiedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastModifiedById = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecurityPolicies", x => x.Id);
                });
```

SQLite, verbatim:

```csharp
            migrationBuilder.CreateTable(
                name: "SecurityPolicies",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    IdleTimeoutMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    CountdownSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedById = table.Column<string>(type: "TEXT", maxLength: 450, nullable: true),
                    LastModifiedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastModifiedById = table.Column<string>(type: "TEXT", maxLength: 450, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecurityPolicies", x => x.Id);
                });
```

The `Down` method in each drops it: `migrationBuilder.DropTable(name: "SecurityPolicies");`

#### The new column on `AspNetUsers`

Inside the `AspNetUsers` `CreateTable`, verbatim from the SQL Server migration, with the
neighbouring columns for placement:

```csharp
                    MustChangePassword = table.Column<bool>(type: "bit", nullable: false),
                    IdleTimeoutMinutes = table.Column<int>(type: "int", nullable: true),
                    UserName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
```

PostgreSQL uses `type: "integer", nullable: true`; SQLite uses `type: "INTEGER", nullable: true`.

**Nullable is load-bearing.** `null` means "follow the administered policy". A non-nullable
column with a default would make every existing user look as though they had chosen a
preference.

#### What an additive migration looks like

For a live database, the equivalent hand-written migration is:

```csharp
public partial class AddIdleTimeout : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "SecurityPolicies",
            columns: table => new
            {
                // ...as above, for your provider...
            },
            constraints: table => { table.PrimaryKey("PK_SecurityPolicies", x => x.Id); });

        migrationBuilder.AddColumn<int>(
            name: "IdleTimeoutMinutes",
            table: "AspNetUsers",
            type: "int",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "SecurityPolicies");
        migrationBuilder.DropColumn(name: "IdleTimeoutMinutes", table: "AspNetUsers");
    }
}
```

*(That block is composed for this document, not copied from the repository — the repository
has no additive migration, only the regenerated initial one.)*

**No data migration is needed.** The `SecurityPolicies` table starts empty and is seeded
lazily on first read; see §6.

---

### 2.20 Permissions and their route to the administrator grant — new + modified

Three files are involved. The constants are in §2.12d above.

#### 2.20a The registry — `src/Application/Common/Security/AdministratorPermissionRegistry.cs` — **modified**

This file is ~230 lines and enumerates every permission in the application. **Only two lines
are new to this feature.** The relevant excerpt, verbatim, with its neighbours for placement:

```csharp
        Permissions.Roles.ManageClaimsInRole,
        Permissions.Roles.ViewClaimsInRole,

        Permissions.SecuritySettings.View,
        Permissions.SecuritySettings.Edit,

        Permissions.Tenants.View,
```

The rest of the file is unchanged and is not reproduced. What matters for a port is the
*shape*, which is worth adopting whether or not you copy the file:

- `DiscoverAllPermissions()` reflects over the public static string fields of every type
  nested under `Permissions`.
- `Validate(all, granted, excluded)` throws unless every discovered constant appears in
  **exactly one** of two explicit lists — `Granted` and `Excluded` — and every listed name is
  a real constant. It fails in four directions: unlisted, listed in both, listed but no longer
  declared, and listed twice.
- `AssertNoDivergence()` applies that to the real lists and is called at startup.

The point is that adding a permission constant does not silently grant it. Somebody has to
decide, and until they do, startup fails naming the constant. If your target repository
grants the administrator every permission by reflection, this feature works without the
registry — but then the two new constants are granted with no decision recorded.

#### 2.20b The seeder — `src/Infrastructure/Persistence/ApplicationDbContextInitializer.cs` — pre-existing shape, **critical to the port**

Verbatim excerpt — the reconcile logic that delivers **new** permissions to **existing**
databases. This is the single most important thing to get right when porting into a deployed
application:

```csharp
    /// <summary>
    /// Brings the roles and their permission grants up to date, one grant at a time.
    /// </summary>
    /// <remarks>
    /// The obvious shape - <c>if (await _roleManager.RoleExistsAsync(Roles.Admin)) return;</c> - is
    /// wrong, and wrong silently. It makes provisioning idempotent per RUN rather than per ITEM, so
    /// a permission added to <see cref="AdministratorPermissionRegistry"/> in a later release never
    /// reaches any database that was provisioned before it: the role already exists, so nothing
    /// runs. Nothing fails either. <c>AssertNoDivergence</c> does not catch it, because it compares
    /// the registry to the permission CONSTANTS, not to what a given database actually holds.
    /// <para>
    /// So each role is reconciled by name and each grant by its natural key (role + permission
    /// value). Two consequences are deliberate:
    /// </para>
    /// <para>
    /// <b>Grant-only, never revoke.</b> Claims this method does not know about are left alone, so a
    /// permission an operator granted at runtime survives the next restart instead of being tidied
    /// away by a deployment. The reconcile therefore restores what is missing; it does not enforce
    /// an exact set.
    /// </para>
    /// <para>
    /// <b>Logs on insert, not on run.</b> A start that changes nothing says nothing, so a line in
    /// the log means a grant genuinely appeared - which is the only way to tell a no-op restart from
    /// one that repaired a database.
    /// </para>
    /// </remarks>
    private async Task EnsureRolesAsync()
    {
        await EnsureRoleAsync(Roles.Admin,
            "Full access to every feature and every setting.",
            // The administrator grant is an explicit list checked against the Permissions constants
            // at startup, not a reflection sweep - see AdministratorPermissionRegistry for why.
            AdministratorPermissionRegistry.Granted);

        await EnsureRoleAsync(Roles.Basic,
            "Ordinary member: can see and download documents.",
            BasicPermissions);
    }

    /// <summary>
    /// Creates <paramref name="roleName"/> if it is absent, then grants any of
    /// <paramref name="permissions"/> it does not already hold.
    /// </summary>
    private async Task EnsureRoleAsync(string roleName, string description, IEnumerable<string> permissions)
    {
        var role = await _roleManager.FindByNameAsync(roleName);

        if (role is null)
        {
            role = new ApplicationRole(roleName)
            {
                Description = description,
                CreatedAt = DateTime.UtcNow
            };

            var created = await _roleManager.CreateAsync(role);
            if (!created.Succeeded)
            {
                // Returning rather than throwing: a role that cannot be created is a broken
                // installation, but failing the start here would take down an application whose
                // other roles are fine. The log names the role and the reason.
                _logger.LogError("Could not provision the {Role} role: {Errors}",
                    roleName, string.Join("; ", created.Errors.Select(e => e.Description)));
                return;
            }

            _logger.LogInformation("Provisioned the {Role} role.", roleName);
        }

        var held = (await _roleManager.GetClaimsAsync(role))
            .Where(c => c.Type == ApplicationClaimTypes.Permission)
            .Select(c => c.Value)
            .ToHashSet(StringComparer.Ordinal);

        var missing = permissions.Where(p => !held.Contains(p)).ToArray();

        foreach (var permission in missing)
        {
            await _roleManager.AddClaimAsync(
                role, new Claim(ApplicationClaimTypes.Permission, permission));
        }

        if (missing.Length > 0)
        {
            _logger.LogInformation("Granted {Count} permission(s) to the {Role} role: {Permissions}",
                missing.Length, roleName, string.Join(", ", missing));
        }
    }
```

**If your seeder is guarded by "has anything been seeded yet?", the two new permissions will
never reach any existing database, the administrator will never hold them, and both screens
will be unreachable after deployment with no error anywhere.** Fix the seeder before shipping
the feature, or grant the claims by hand.

#### 2.20c How the permission becomes an authorization policy

The constants are turned into policies by a reflection loop in `AddAuthorization`, verbatim:

```csharp
                // Here I stored necessary permissions/roles in a constant
                foreach (var prop in typeof(Permissions).GetNestedTypes().SelectMany(c =>
                             c.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)))
                {
                    var propertyValue = prop.GetValue(null);
                    if (propertyValue is not null)
                        options.AddPolicy((string)propertyValue,
                            policy => policy.RequireClaim(ApplicationClaimTypes.Permission, (string)propertyValue));
                }
```

That loop is pre-existing and needs no change: declaring `Permissions.SecuritySettings.View`
and `.Edit` is enough for `[Authorize(Policy = ...)]` and `[RequestAuthorize(Policy = ...)]`
to resolve.

---

### 2.21 `src/Server.UI/Services/Navigation/MenuService.cs` — **modified**

Two edits: a constructor that removes the entry when the feature is off, and the entry
itself. The file is ~300 lines of menu declaration; only these two regions changed.

The constructor, verbatim (the class had no constructor before):

```csharp
public class MenuService : IMenuService
{
    /// <summary>
    /// Builds the menu, dropping any surface that belongs to a switched-off feature.
    /// </summary>
    /// <remarks>
    /// The security-settings route answers 404 when the idle timeout is disabled
    /// (<see cref="SecuritySettingsPageMiddleware"/>), so leaving its entry in the menu would offer a
    /// link straight to a 404. Removed here, once, rather than omitted from the declaration below, so
    /// that the menu stays one readable list.
    /// </remarks>
    public MenuService(IIdleTimeoutSettings idleTimeoutSettings)
    {
        if (idleTimeoutSettings.Enabled) return;

        foreach (var section in _features)
        {
            foreach (var item in section.SectionItems ?? [])
            {
                var children = item.MenuItems;
                if (children is null) continue;

                for (var i = children.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(children[i].Href,
                            SecuritySettingsPageMiddleware.SecuritySettingsPath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        children.RemoveAt(i);
                    }
                }
            }
        }
    }
```

The menu entry itself, verbatim, inside the "System" section with its neighbours for
placement:

```csharp
                new()
                {
                    IsParent = true,
                    Title = "System",
                    Icon = Icons.Material.Filled.Devices,
                    MenuItems = new List<MenuSectionSubItemModel>
                    {
                        new()
                        {
                            Title = "Picklist",
                            Href = "/system/picklistset",
                            PageStatus = PageStatus.Completed
                        },
                        new()
                        {
                            Title = "Security Settings",
                            Href = "/system/security-settings",
                            PageStatus = PageStatus.Completed
                        },
                        new()
                        {
                            Title = "Audit Trails",
                            Href = "/system/audittrails",
                            PageStatus = PageStatus.Completed
                        },
```

The `"Security Settings"` entry is the addition. The file also needs
`using CleanArchitecture.Blazor.Application.Common.Interfaces;` and
`using CleanArchitecture.Blazor.Server.UI.Middlewares;` added at the top.

**`MenuService` must be registered with a lifetime that lets it take a constructor
dependency.** It already was in this repository; a `new MenuService()` anywhere would now
fail to compile, which is the intended prompt.

---

### 2.22 `src/Server.UI/Pages/Identity/Users/Profile.razor` — **modified**

The whole file is short, so here it is in full:

```razor
@page "/user/profile"

@using CleanArchitecture.Blazor.Server.UI.Pages.Identity.Users.Components
@using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity
@using CleanArchitecture.Blazor.Application.Common.Interfaces
@inject IStringLocalizer<Profile> L
@inject IIdleTimeoutSettings IdleTimeoutSettings


<PageTitle>@ApplicationSettings.AppName - @L["Profile"]</PageTitle>


<MudTabs Outlined="true" Position="Position.Top" Rounded="true" Border="true"
         ApplyEffectsToContainer="true" Class="mt-8" TabPanelsClass="py-5">
    <MudTabPanel Text="@L["Profile"]">
        <ProfileInformationTab />
    </MudTabPanel>
    <MudTabPanel Text="@L["Change Password"]">
        <ChangePasswordTab />
    </MudTabPanel>
    @* Absent, not disabled, and absent for the same two reasons SecurityTab itself checks: with the
       feature off there is nothing to configure, and with user overrides off this is not the user's
       to set. An empty tab is worse than no tab - it invites a support call asking what belongs in
       it. The route for the ADMIN screen is closed by SecuritySettingsPageMiddleware; this one is a
       component, so it is omitted here. *@
    @if (IdleTimeoutSettings.Enabled && IdleTimeoutSettings.AllowUserOverride)
    {
        <MudTabPanel Text="@L["Security"]">
            <SecurityTab />
        </MudTabPanel>
    }
    <MudTabPanel Text="@L["Org Chart"]">
        <OrgChartTab />
    </MudTabPanel>


</MudTabs>


@code {
   
}
```

The additions are: the `@using ...Common.Interfaces` line, the
`@inject IIdleTimeoutSettings IdleTimeoutSettings` line, and the commented `@if` block with
the Security panel. Before the change the file went straight from the Change Password panel
to the Org Chart panel.

Note that the guard is duplicated — `Profile.razor` checks it *and* `SecurityTab.razor`
checks the same two flags in its own markup and `OnInitializedAsync`. That is deliberate: the
component must be safe to render from anywhere, and the page must not declare a panel that
would render nothing.

---

### 2.23 `src/Server.UI/Pages/Identity/Login/Login.razor` — **modified**

Two additions: a query-string parameter and an informational alert. Verbatim excerpts.

The alert, at the top of the form:

```razor
	<EditForm Model="_formModel" OnValidSubmit="HandleSubmitAsync" FormName="login">
		<DataAnnotationsValidator />
		<MudStack Spacing="2">
			<MudText Typo="Typo.h4" GutterBottom="true">@L["Sign In"]</MudText>

			@* Informational, not an error: the user did nothing wrong, and a red panel here is the
			   single most common support call this feature generates. *@
			@if (string.Equals(Reason, "idle", StringComparison.OrdinalIgnoreCase))
			{
				<MudAlert Severity="Severity.Info" Dense="true" Class="mb-2">
					@AppStrings.SignedOutForInactivity
				</MudAlert>
			}
			<MudText>
```

The parameter, in the `@code` block:

```csharp
	public const string PageUrl = "/account/login";
	private string? _errorMessage;
	[SupplyParameterFromQuery] public string? ReturnUrl { get; set; }

	/// <summary>Why the browser arrived here. "idle" is set by the idle-timeout sign-out.</summary>
	[SupplyParameterFromQuery] public string? Reason { get; set; }
	private string _pageTitle = "Sign In";
```

The `Reason` property and its comment are the addition; `ReturnUrl` pre-existed.

The page route is `@page "/account/login"` with `@attribute [AllowAnonymous]`. The JS module
navigates to `IdleTimeoutRoutes.LoginAfterIdle`, which is the constant
`"/account/login?reason=idle"` — so the two must agree on both the path and the query key.
Nothing else in `Login.razor` changed.

---

### 2.24 `src/Server.UI/Layouts/AppLayout.razor` — **modified**

Verbatim excerpt showing where the monitor is rendered:

```razor
@using CleanArchitecture.Blazor.Application.Common.Interfaces
@using CleanArchitecture.Blazor.Server.UI.Components.Security
@inject LayoutService LayoutService
@inject IIdleTimeoutSettings IdleTimeoutSettings
@implements IDisposable
<MudLayout>
    <AuthorizeView>
        <NotAuthorized>
            <RedirectToLogin/>
        </NotAuthorized>
        <Authorized>
```

and, at the end of the `<Authorized>` branch:

```razor
            <UserLoginState />
            <ForcePasswordChangeGuard />

            @* Once, here - not per page. A second instance would mean two sets of activity
               listeners writing the same localStorage keys and two dialogs racing to sign out. *@
            <IdleTimeoutMonitor KeepAliveEnabled="@IdleTimeoutSettings.KeepAlivePingEnabled" />
        </Authorized>
    </AuthorizeView>
</MudLayout>
```

The two `@using` lines, the `@inject`, and the `<IdleTimeoutMonitor .../>` element are the
additions.

**Three placement requirements:**

1. **Inside `<Authorized>`.** An anonymous visitor must fetch no module, start no timers and
   register no listeners.
2. **Once, in the layout — never per page.** A second instance means two sets of activity
   listeners writing the same `localStorage` keys and two dialogs racing to sign out.
3. **Under a layout that hosts `MudDialogProvider` and `MudPopoverProvider`.** In this
   repository `AppLayout` has `@layout MainLayout`, and `MainLayout` hosts the providers.
   Without them the dialog renders nothing at all.

---

### 2.25 `src/Application/Common/Constants/AppStrings.cs` — **modified**

A block of static properties added. Verbatim, with the preceding block for placement:

```csharp
    // Success/Failure messages
    public static string SaveSuccess => Localize("Save successfully");
    public static string CreateSuccess => Localize("Create successfully");
    public static string DeleteSuccess => Localize("Delete successfully");
    public static string UploadSuccess => Localize("Upload successfully");
    public static string ExportFail => Localize("Export failed");
    public static string ImportFail => Localize("Import failed");

    // Idle timeout. Localize() falls back to the key, so these read correctly in English before any
    // translator has seen them; add the entries to the .resx files to translate.
    public static string SessionExpiringTitle => Localize("Session about to expire");
    public static string SessionExpiringMessage =>
        Localize("You have been inactive for a while. You will be signed out automatically unless you choose to stay.");
    public static string Seconds => Localize("seconds");
    public static string StayLoggedIn => Localize("Stay Logged In");
    public static string SignOutNow => Localize("Sign Out Now");
    public static string SignedOutForInactivity =>
        Localize("You were signed out after a period of inactivity.");

    public static string SecuritySettings => Localize("Security Settings");
    public static string IdleTimeout => Localize("Idle timeout");
    public static string IdleTimeoutMinutesLabel => Localize("Sign out after (minutes of inactivity)");
    public static string CountdownSecondsLabel => Localize("Warning countdown (seconds)");
    public static string IdleTimeoutEffectiveFormat =>
        Localize("You will be signed out after {0} minutes of inactivity.");
    public static string IdleTimeoutAffectsLiveSessions =>
        Localize("This takes effect on sessions that are already open, not only on new sign-ins. A user whose window becomes shorter than their current idle time is signed out on their next request.");
    public static string SignMeOutSooner => Localize("Sign me out sooner than the organisation default");
    public static string UseOrganisationDefault => Localize("Use the organisation default");
    public static string IdleTimeoutBoundsFormat => Localize("Between {0} and {1} minutes.");
```

Everything from the `// Idle timeout.` comment onwards is new. `Localize` is a private helper
on the same class that looks the key up in a `ResourceManager` for the current UI culture and
**falls back to the key itself** when there is no entry — which is why these read correctly in
English with no `.resx` work. The full `Localize` body is not reproduced; only that fallback
behaviour matters for a port.

---

### 2.26 `src/Server.UI/appsettings.json` — **modified**

The block added, verbatim (the file uses JSONC-style comments, which .NET configuration
accepts):

```jsonc
  "SecuritySettings": {
    "IdleTimeout": {
      // False switches the feature off end to end - no JS module is fetched, no principal check
      // runs, neither settings screen appears, and the cookie falls back to a fixed 8-hour lifetime.
      "Enabled": true,
      "DefaultIdleTimeoutMinutes": 15,
      "DefaultCountdownSeconds": 60,
      "MinIdleTimeoutMinutes": 1,
      "MaxIdleTimeoutMinutes": 120,
      // Whether users may shorten their own window. They can never lengthen it.
      "AllowUserOverride": true,
      // The browser pings an authenticated endpoint while the user is active, so the sliding cookie
      // renews. Turning this off inside a Blazor Server app means an actively working user's cookie
      // can expire underneath them - see the README.
      "KeepAlivePingEnabled": true,
      "CookieGraceMinutes": 2
    }
  },
```

There is exactly one `appsettings.json` in this repository — no `appsettings.Development.json`
and no per-environment override — so a generated project receives precisely the block above.

---

### 2.27 `src/Infrastructure/Services/InMemoryTicketStore.cs` — pre-existing, context only

The server-side ticket store assigned to `options.SessionStore`. It is **not** part of this
feature and a target repository that does not use one needs nothing here — the feature works
with a cookie-borne ticket. It is reproduced because `IdleSessionEnforcer` writes the
last-activity stamp into the ticket properties, and how those properties are persisted is
worth understanding.

```csharp
﻿using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using ZiggyCreatures.Caching.Fusion;

namespace CleanArchitecture.Blazor.Infrastructure.Services
{
    /// <summary>
    /// ITicketStore implementation backed by FusionCache (no DI).
    /// - Process-wide singleton FusionCache via Lazy<T>.
    /// - Per-entry TTL aligned with AuthenticationTicket.ExpiresUtc to avoid "stale-but-valid" drift.
    /// - Resilience enabled (fail-safe / jitter / lock) with conservative values for auth sensitivity.
    /// </summary>
    public sealed class MemoryCacheTicketStore : ITicketStore
    {
        private const string KeyPrefix = "AuthSessionStore-";

        // Default entry options used when the ticket has no ExpiresUtc.
        // For authentication scenarios we keep fail-safe short to reduce security risk.
        private static readonly FusionCacheEntryOptions DefaultEntryOptions = new()
        {
            // Fallback absolute TTL if the ticket doesn't carry an explicit expiry.
            Duration = TimeSpan.FromDays(7),

            // —— Resilience: fail-safe & timeouts ——
            // Fail-safe is OFF for authentication tickets. It exists to serve a logically expired
            // value rather than fail, which is the wrong trade here: the entry's Duration IS the
            // session lifetime, so serving past it keeps an expired session usable - previously for
            // up to FailSafeMaxDuration. RetrieveAsync uses GetOrDefaultAsync, which with fail-safe
            // on can return exactly such a stale ticket.
            IsFailSafeEnabled = false,

            // Factory timeouts mostly affect GetOrSet (not used here), but are kept for consistency.
            FactorySoftTimeout = TimeSpan.FromMilliseconds(250),
            FactoryHardTimeout = TimeSpan.FromMilliseconds(1000),

            // —— Anti-stampede ——
            // Spread expirations to mitigate thundering herds; short lock to avoid long waits.
            JitterMaxDuration = TimeSpan.FromSeconds(30),
            LockTimeout = TimeSpan.FromSeconds(1)
        };

        // Process-wide FusionCache singleton. Configure() may adjust options prior to first access.
        private static readonly Lazy<IFusionCache> LazyCache = new(() =>
        {
            var options = new FusionCacheOptions
            {
                CacheName = "AuthSessionStore",
                DefaultEntryOptions = DefaultEntryOptions
            };

            _configureAction?.Invoke(options);

            var cache = new FusionCache(options);

            // OPTIONAL (no DI):
            // If you want 2nd-level cache + backplane, wire them here:
            // cache.SetSerializer(new ZiggyCreatures.FusionCache.Serialization.SystemTextJson
            //     .FusionCacheSystemTextJsonSerializer());
            // cache.SetDistributedCache(yourDistributedCacheInstance /* IDistributedCache */);
            // cache.SetBackplane(yourBackplaneInstance /* e.g., RedisBackplane */);

            return cache;
        }, isThreadSafe: true);

        private static Action<FusionCacheOptions>? _configureAction;

        /// <summary>
        /// Allows customizing FusionCacheOptions BEFORE the first use.
        /// Throws if the cache was already created.
        /// </summary>
        public static void Configure(Action<FusionCacheOptions> configure)
        {
            if (configure is null) throw new ArgumentNullException(nameof(configure));
            if (LazyCache.IsValueCreated)
                throw new InvalidOperationException("FusionCache already initialized. Configure must be called before first use.");
            _configureAction = configure;
        }

        private static IFusionCache Cache => LazyCache.Value;

        public async Task<string> StoreAsync(AuthenticationTicket ticket)
        {
            if (ticket is null) throw new ArgumentNullException(nameof(ticket));

            var key = KeyPrefix + Guid.NewGuid().ToString("N");
            await RenewAsync(key, ticket).ConfigureAwait(false);
            return key;
        }

        public async Task RenewAsync(string key, AuthenticationTicket ticket)
        {
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("UserContextKey is required.", nameof(key));
            if (ticket is null) throw new ArgumentNullException(nameof(ticket));

            // Align cache TTL with ticket expiry when available.
            var options = BuildPerEntryOptions(ticket);

            // If the ticket is already expired (or nearly), do not cache it.
            if (options is null)
            {
                // No ExpiresUtc → fall back to defaults.
                await Cache.SetAsync(key, ticket, DefaultEntryOptions).ConfigureAwait(false);
                return;
            }

            if (options.Duration <= TimeSpan.Zero)
            {
                // Ticket considered expired; ensure removal to avoid serving stale auth data.
                await Cache.RemoveAsync(key).ConfigureAwait(false);
                return;
            }

            await Cache.SetAsync(key, ticket, options).ConfigureAwait(false);
        }

        public async Task<AuthenticationTicket?> RetrieveAsync(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;

            // No factory: return null when missing. Fail-safe is disabled for this cache, so an
            // entry past its Duration is gone rather than served as a still-valid session.
            return await Cache.GetOrDefaultAsync<AuthenticationTicket>(key).ConfigureAwait(false);
        }

        public async Task RemoveAsync(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            await Cache.RemoveAsync(key).ConfigureAwait(false);
        }

        /// <summary>
        /// Builds per-entry options from AuthenticationTicket so that cache TTL equals the ticket lifetime.
        /// Returns:
        /// - FusionCacheEntryOptions with Duration if ExpiresUtc is present and in the future,
        /// - null if the ticket has no ExpiresUtc (caller uses DefaultEntryOptions),
        /// - options with small positive Duration if extremely close to expiry (optional),
        /// - Duration <= 0 means "expired": caller should not cache and may remove the key.
        /// </summary>
        private static FusionCacheEntryOptions? BuildPerEntryOptions(AuthenticationTicket ticket)
        {
            var expiresUtc = ticket.Properties.ExpiresUtc;
            if (expiresUtc is null)
                return null;

            var now = DateTimeOffset.UtcNow;
            var ttl = expiresUtc.Value - now;

            // If already expired, caller will remove and not cache.
            // If extremely small but positive, you may choose to set a tiny TTL to smooth boundary conditions.
            if (ttl > TimeSpan.Zero && ttl < TimeSpan.FromSeconds(5))
                ttl = TimeSpan.FromSeconds(5);

            return new FusionCacheEntryOptions
            {
                Duration = ttl,

                // Off, for the same reason as DefaultEntryOptions: Duration here is the ticket's own
                // remaining lifetime, and nothing should outlive it.
                IsFailSafeEnabled = false,

                // We do not typically need eager refresh for tickets; renew happens via app logic.
                // EagerRefreshThreshold = ...

                // Anti-stampede for concurrent renewals.
                JitterMaxDuration = TimeSpan.FromSeconds(30),
                LockTimeout = TimeSpan.FromSeconds(1)
            };
        }
    }
}
```

The relevant interaction: `ShouldRenew = true` causes the cookie handler to call
`ITicketStore.RenewAsync`, which is what persists the mutated `Properties.Items` — including
the last-activity stamp. Without `ShouldRenew` the mutation is discarded. See §6.

---

### 2.28 The tests

Five files. Test frameworks differ by project in this repository — the Infrastructure tests
use **xUnit**, the Application and Server.UI tests use **NUnit** with FluentAssertions, and
the component tests use **bUnit**. Adapt as needed; what matters is what each test would
catch.

#### 2.28a `tests/Infrastructure.UnitTests/Security/IdleTimeoutPolicyTests.cs` — **new**

Three fixtures in one file: the provider arithmetic, the enforcer, and the settings
validation. In full.

```csharp
using System.Security.Claims;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Domain.Entities;
using CleanArchitecture.Blazor.Domain.Identity;
using CleanArchitecture.Blazor.Infrastructure.Configurations;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using CleanArchitecture.Blazor.Infrastructure.Services.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ZiggyCreatures.Caching.Fusion;

namespace CleanArchitecture.Blazor.Infrastructure.UnitTests.Security;

/// <summary>
/// A context factory over one open in-memory SQLite connection, so every context sees the same
/// database for the life of a test.
/// </summary>
internal sealed class TestDbContextFactory(DbContextOptions<ApplicationDbContext> options)
    : IDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext() => new(options);
}

/// <summary>
/// The effective-policy arithmetic: what an administrator sets, what a user may do to it, and what
/// the deployment's bounds do to both.
/// </summary>
/// <remarks>
/// The direction of the user preference is the security-relevant part and the easy one to get
/// backwards. An idle timeout guards an unattended workstation; a user who could LENGTHEN their own
/// would simply set it to eight hours and the control would be gone. These tests pin the asymmetry
/// in the place that enforces it - read time - rather than only in the screen that offers it.
/// </remarks>
public class IdleTimeoutPolicyProviderTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestDbContextFactory _factory;

    public IdleTimeoutPolicyProviderTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        using (var db = new ApplicationDbContext(options))
        {
            db.Database.EnsureCreated();
        }

        _factory = new TestDbContextFactory(options);
    }

    public void Dispose() => _connection.Dispose();

    private IdleTimeoutPolicyProvider Provider(IdleTimeoutSettings? settings = null) => new(
        _factory,
        // A fresh cache per provider, so one test's invalidation semantics cannot leak into another.
        new FusionCache(new FusionCacheOptions()),
        settings ?? new IdleTimeoutSettings(),
        NullLogger<IdleTimeoutPolicyProvider>.Instance);

    private static ClaimsPrincipal User(string id = "u1") =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id)], "test"));

    private async Task SetUserPreferenceAsync(string userId, int? minutes)
    {
        await using var db = _factory.CreateDbContext();
        db.Users.Add(new ApplicationUser { Id = userId, UserName = userId, IdleTimeoutMinutes = minutes });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task WithNoRow_TheAdministeredPolicyIsSeededFromConfiguration()
    {
        // A database provisioned before this feature existed has no row. The first read writes one
        // rather than requiring a data migration.
        var settings = new IdleTimeoutSettings { DefaultIdleTimeoutMinutes = 25, DefaultCountdownSeconds = 45 };

        var administered = await Provider(settings).GetAdministeredAsync();

        Assert.Equal(25, administered.IdleMinutes);
        Assert.Equal(45, administered.CountdownSeconds);

        await using var db = _factory.CreateDbContext();
        Assert.Single(db.SecurityPolicies);
    }

    [Fact]
    public async Task AStoredPolicyOutsideTheBounds_IsClampedOnTheWayOut()
    {
        // Not merely on save. The authentication cookie is sized from these bounds, so a row that
        // predates a tightening - or was edited around the screen - must not reach enforcement.
        await using (var db = _factory.CreateDbContext())
        {
            db.SecurityPolicies.Add(new SecurityPolicy { IdleTimeoutMinutes = 9_000, CountdownSeconds = 60 });
            await db.SaveChangesAsync();
        }

        var settings = new IdleTimeoutSettings { MinIdleTimeoutMinutes = 5, MaxIdleTimeoutMinutes = 60 };

        var administered = await Provider(settings).GetAdministeredAsync();

        Assert.Equal(60, administered.IdleMinutes);
    }

    [Fact]
    public async Task AUserPreferenceShorterThanThePolicy_IsHonoured()
    {
        await SetUserPreferenceAsync("u1", 5);
        await using (var db = _factory.CreateDbContext())
        {
            db.SecurityPolicies.Add(new SecurityPolicy { IdleTimeoutMinutes = 30, CountdownSeconds = 60 });
            await db.SaveChangesAsync();
        }

        var effective = await Provider().GetEffectiveAsync(User());

        Assert.Equal(5, effective.IdleMinutes);
    }

    [Fact]
    public async Task AUserPreferenceLongerThanThePolicy_IsIgnored()
    {
        // The whole asymmetry, in one assertion. If this ever reads 240 the control is gone: anyone
        // who finds the timeout inconvenient can simply opt out of it.
        await SetUserPreferenceAsync("u1", 240);
        await using (var db = _factory.CreateDbContext())
        {
            db.SecurityPolicies.Add(new SecurityPolicy { IdleTimeoutMinutes = 30, CountdownSeconds = 60 });
            await db.SaveChangesAsync();
        }

        var effective = await Provider().GetEffectiveAsync(User());

        Assert.Equal(30, effective.IdleMinutes);
    }

    [Fact]
    public async Task AUserPreferenceBelowTheFloor_IsClamped()
    {
        // "Clamped at read time" - the case where a value was forced into the database directly.
        await SetUserPreferenceAsync("u1", 0);
        var settings = new IdleTimeoutSettings { MinIdleTimeoutMinutes = 3 };

        var effective = await Provider(settings).GetEffectiveAsync(User());

        Assert.True(effective.IdleMinutes >= 3);
    }

    [Fact]
    public async Task WithUserOverrideSwitchedOff_AnExistingPreferenceIsIgnored()
    {
        // Ignored rather than honoured: turning the option off is a decision that users do not set
        // this, and a preference saved while it was on must not outlive that decision.
        await SetUserPreferenceAsync("u1", 5);
        await using (var db = _factory.CreateDbContext())
        {
            db.SecurityPolicies.Add(new SecurityPolicy { IdleTimeoutMinutes = 30, CountdownSeconds = 60 });
            await db.SaveChangesAsync();
        }

        var settings = new IdleTimeoutSettings { AllowUserOverride = false };

        var effective = await Provider(settings).GetEffectiveAsync(User());

        Assert.Equal(30, effective.IdleMinutes);
    }

    [Fact]
    public async Task TheCountdownIsNeverNarrowedByAUserPreference()
    {
        // It is a warning, not a policy: how long the dialog is shown is not the user's to shorten.
        await SetUserPreferenceAsync("u1", 5);
        await using (var db = _factory.CreateDbContext())
        {
            db.SecurityPolicies.Add(new SecurityPolicy { IdleTimeoutMinutes = 30, CountdownSeconds = 45 });
            await db.SaveChangesAsync();
        }

        var effective = await Provider().GetEffectiveAsync(User());

        Assert.Equal(45, effective.CountdownSeconds);
    }

    [Fact]
    public async Task WhenTheFeatureIsOff_TheEffectivePolicyIsDisabled()
    {
        var effective = await Provider(new IdleTimeoutSettings { Enabled = false }).GetEffectiveAsync(User());

        Assert.False(effective.Enabled);
    }

    [Fact]
    public async Task InvalidatingThePolicy_MakesTheNextReadSeeTheNewValue()
    {
        // The mechanism by which an administrator's change reaches sessions already open. Without
        // the invalidation the cached policy would stand until its (deliberately long) duration
        // elapsed, and "takes effect on live sessions" would be false.
        var provider = Provider();

        await using (var db = _factory.CreateDbContext())
        {
            db.SecurityPolicies.Add(new SecurityPolicy { IdleTimeoutMinutes = 30, CountdownSeconds = 60 });
            await db.SaveChangesAsync();
        }

        Assert.Equal(30, (await provider.GetAdministeredAsync()).IdleMinutes);

        await using (var db = _factory.CreateDbContext())
        {
            var row = db.SecurityPolicies.Single();
            row.IdleTimeoutMinutes = 2;
            await db.SaveChangesAsync();
        }

        Assert.Equal(30, (await provider.GetAdministeredAsync()).IdleMinutes);   // still cached

        provider.Invalidate();

        Assert.Equal(2, (await provider.GetAdministeredAsync()).IdleMinutes);
    }

    [Fact]
    public async Task InvalidatingOneUser_MakesTheNextReadSeeTheirNewPreference()
    {
        var provider = Provider();
        await SetUserPreferenceAsync("u1", 20);

        // The administered window has to be wider than either preference, or the min() would be what
        // this test observed rather than the cache.
        await using (var db = _factory.CreateDbContext())
        {
            db.SecurityPolicies.Add(new SecurityPolicy { IdleTimeoutMinutes = 30, CountdownSeconds = 60 });
            await db.SaveChangesAsync();
        }

        Assert.Equal(20, (await provider.GetEffectiveAsync(User())).IdleMinutes);

        await using (var db = _factory.CreateDbContext())
        {
            var user = db.Users.Single(u => u.Id == "u1");
            user.IdleTimeoutMinutes = 4;
            await db.SaveChangesAsync();
        }

        provider.InvalidateUser("u1");

        Assert.Equal(4, (await provider.GetEffectiveAsync(User())).IdleMinutes);
    }
}

/// <summary>
/// The server-side enforcement: what actually ends a session, independent of any browser.
/// </summary>
public class IdleSessionEnforcerTests
{
    private sealed class StubPolicy(IdleTimeoutPolicy policy, bool enabled = true) : IIdleTimeoutPolicyProvider
    {
        public bool Enabled => enabled;
        public Task<AdministeredIdleTimeoutPolicy> GetAdministeredAsync(CancellationToken ct = default) =>
            Task.FromResult(new AdministeredIdleTimeoutPolicy(policy.IdleMinutes, policy.CountdownSeconds));
        public Task<IdleTimeoutPolicy> GetEffectiveAsync(ClaimsPrincipal user, CancellationToken ct = default) =>
            Task.FromResult(policy);
        public void Invalidate() { }
        public void InvalidateUser(string userId) { }
    }

    private static CookieValidatePrincipalContext Context(
        DateTimeOffset? lastActivity, string path = "/some/page", DateTimeOffset? issued = null)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = path;

        var principal = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "u1")], "cookie"));

        var properties = new AuthenticationProperties { IssuedUtc = issued ?? DateTimeOffset.UtcNow };
        if (lastActivity is { } stamp)
        {
            properties.Items[IdleSessionEnforcer.LastActivityKey] =
                stamp.ToUnixTimeMilliseconds().ToString();
        }

        var ticket = new AuthenticationTicket(principal, properties, "Identity.Application");

        return new CookieValidatePrincipalContext(
            httpContext,
            new AuthenticationScheme("Identity.Application", null, typeof(CookieAuthenticationHandler)),
            new CookieAuthenticationOptions(),
            ticket);
    }

    private static IdleSessionEnforcer Enforcer(int idleMinutes = 15, int countdownSeconds = 60, bool enabled = true) =>
        new(new StubPolicy(new IdleTimeoutPolicy(enabled, idleMinutes, countdownSeconds), enabled),
            NullLogger<IdleSessionEnforcer>.Instance);

    [Fact]
    public async Task ASessionInsideItsWindow_Survives()
    {
        var context = Context(DateTimeOffset.UtcNow.AddMinutes(-5));

        Assert.True(await Enforcer(idleMinutes: 15).IsStillValidAsync(context));
    }

    [Fact]
    public async Task ASessionPastIdlePlusCountdown_IsRejected()
    {
        // The window is idle + countdown, not idle alone: while the warning is counting down the
        // user may still click Stay Logged In, and the server must not have ended the session
        // underneath the dialog it is showing.
        var context = Context(DateTimeOffset.UtcNow.AddMinutes(-16).AddSeconds(-1));

        Assert.False(await Enforcer(idleMinutes: 15, countdownSeconds: 60).IsStillValidAsync(context));
    }

    [Fact]
    public async Task DuringTheCountdown_TheSessionStillSurvives()
    {
        var context = Context(DateTimeOffset.UtcNow.AddMinutes(-15).AddSeconds(-30));

        Assert.True(await Enforcer(idleMinutes: 15, countdownSeconds: 60).IsStillValidAsync(context));
    }

    [Fact]
    public async Task AKeepAliveRequest_StampsActivityAndRenewsTheTicket()
    {
        var context = Context(DateTimeOffset.UtcNow.AddMinutes(-5), IdleTimeoutRoutes.KeepAlive);

        Assert.True(await Enforcer().IsStillValidAsync(context));
        Assert.True(context.ShouldRenew);
        Assert.True(context.Properties.Items.ContainsKey(IdleSessionEnforcer.LastActivityKey));
    }

    [Fact]
    public async Task AnyOtherRequest_DoesNotCountAsActivity()
    {
        // The load-bearing negative. If an ordinary authenticated request renewed the window, an
        // unattended workstation would keep itself signed in through whatever its browser happened
        // to fetch, and the idle timeout would never fire.
        var stamp = DateTimeOffset.UtcNow.AddMinutes(-5);
        var context = Context(stamp, "/system/audittrails");

        Assert.True(await Enforcer().IsStillValidAsync(context));
        Assert.False(context.ShouldRenew);
        Assert.Equal(
            stamp.ToUnixTimeMilliseconds().ToString(),
            context.Properties.Items[IdleSessionEnforcer.LastActivityKey]);
    }

    [Fact]
    public async Task WithNoStampYet_TheTicketsIssueTimeIsUsed()
    {
        // A freshly issued ticket carries no stamp. Treating "absent" as the epoch would sign every
        // user out on their first request after signing in.
        var context = Context(lastActivity: null, issued: DateTimeOffset.UtcNow.AddMinutes(-1));

        Assert.True(await Enforcer(idleMinutes: 15).IsStillValidAsync(context));
    }

    [Fact]
    public async Task WithNoStampAndAnOldTicket_TheSessionIsRejected()
    {
        var context = Context(lastActivity: null, issued: DateTimeOffset.UtcNow.AddHours(-3));

        Assert.False(await Enforcer(idleMinutes: 15).IsStillValidAsync(context));
    }

    [Fact]
    public async Task WhenTheFeatureIsOff_NothingIsEverRejected()
    {
        var context = Context(DateTimeOffset.UtcNow.AddDays(-2));

        Assert.True(await Enforcer(enabled: false).IsStillValidAsync(context));
    }
}

/// <summary>
/// The startup validation. These values size the authentication cookie, so a bad combination has to
/// fail the process rather than produce sessions that end at a time nobody chose.
/// </summary>
public class IdleTimeoutSettingsValidationTests
{
    private static string[] Errors(IdleTimeoutSettings settings) =>
        settings.Validate(new System.ComponentModel.DataAnnotations.ValidationContext(settings))
            .Select(r => r.ErrorMessage!)
            .ToArray();

    [Fact]
    public void TheShippedDefaults_AreValid()
    {
        // Worth asserting explicitly: the defaults put the countdown (60s) exactly at the shortest
        // permitted window (1 minute). Equal is allowed, exceeding is not - so an off-by-one in the
        // comparison would fail every generated project at startup.
        Assert.Empty(Errors(new IdleTimeoutSettings()));
    }

    [Fact]
    public void ACountdownLongerThanTheShortestWindow_FailsStartup()
    {
        var errors = Errors(new IdleTimeoutSettings { MinIdleTimeoutMinutes = 1, DefaultCountdownSeconds = 90 });

        Assert.Contains(errors, e => e.Contains("exceeds the shortest idle window"));
    }

    [Fact]
    public void AMaximumAboveEightHours_FailsStartup()
    {
        var errors = Errors(new IdleTimeoutSettings { MaxIdleTimeoutMinutes = 600 });

        Assert.Contains(errors, e => e.Contains(nameof(IdleTimeoutSettings.MaxIdleTimeoutMinutes)));
    }

    [Fact]
    public void ADefaultOutsideTheBounds_FailsStartup()
    {
        var errors = Errors(new IdleTimeoutSettings
        {
            MinIdleTimeoutMinutes = 10,
            MaxIdleTimeoutMinutes = 20,
            DefaultIdleTimeoutMinutes = 45,
            DefaultCountdownSeconds = 30
        });

        Assert.Contains(errors, e => e.Contains(nameof(IdleTimeoutSettings.DefaultIdleTimeoutMinutes)));
    }

    [Fact]
    public void AMaximumBelowTheMinimum_FailsStartup()
    {
        var errors = Errors(new IdleTimeoutSettings { MinIdleTimeoutMinutes = 60, MaxIdleTimeoutMinutes = 30 });

        Assert.Contains(errors, e => e.Contains("must be greater than"));
    }

    [Fact]
    public void WhenTheFeatureIsOff_NothingIsValidated()
    {
        // The values are inert when the feature is off; failing a start over a setting that does
        // nothing would be noise.
        var errors = Errors(new IdleTimeoutSettings
        {
            Enabled = false,
            MinIdleTimeoutMinutes = 0,
            MaxIdleTimeoutMinutes = 9_999,
            DefaultCountdownSeconds = 5_000
        });

        Assert.Empty(errors);
    }

    [Fact]
    public void TheCookieLifetime_CoversTheWidestWindowPlusCountdownAndGrace()
    {
        // The cookie must outlive the longest session any policy could produce, or enforcement would
        // never get to run - the cookie would expire first and the user would be bounced to login
        // with no explanation.
        var settings = new IdleTimeoutSettings
        {
            MaxIdleTimeoutMinutes = 120,
            DefaultCountdownSeconds = 60,
            CookieGraceMinutes = 2
        };

        Assert.Equal(TimeSpan.FromMinutes(123), settings.CookieLifetime);
    }
}
```

**What each test in that file would catch:**

| Test | Would catch |
|---|---|
| `WithNoRow_TheAdministeredPolicyIsSeededFromConfiguration` | Lazy seeding removed — the feature breaking on a database provisioned before the table existed |
| `AStoredPolicyOutsideTheBounds_IsClampedOnTheWayOut` | Clamping moved to write-only — a stale row escaping the deployment's limits and outliving the cookie |
| `AUserPreferenceShorterThanThePolicy_IsHonoured` | The narrowing being dropped entirely, making the profile screen inert |
| `AUserPreferenceLongerThanThePolicy_IsIgnored` | **`Math.Min` becoming `Math.Max`** — the single assertion that keeps the control from being opt-out |
| `AUserPreferenceBelowTheFloor_IsClamped` | A value forced into the database below `MinIdleTimeoutMinutes` reaching enforcement |
| `WithUserOverrideSwitchedOff_AnExistingPreferenceIsIgnored` | A preference saved while overrides were allowed outliving the decision to disallow them |
| `TheCountdownIsNeverNarrowedByAUserPreference` | The countdown being treated as user-adjustable policy rather than as a warning duration |
| `WhenTheFeatureIsOff_TheEffectivePolicyIsDisabled` | `Enabled: false` not reaching the effective policy |
| `InvalidatingThePolicy_MakesTheNextReadSeeTheNewValue` | Explicit invalidation replaced by a TTL — an administrator's change not reaching open sessions |
| `InvalidatingOneUser_MakesTheNextReadSeeTheirNewPreference` | The same, per user; also that the per-user cache key is actually per user |
| `ASessionInsideItsWindow_Survives` | An off-by-one that signs everybody out immediately |
| `ASessionPastIdlePlusCountdown_IsRejected` | Enforcement being removed or the comparison inverted |
| `DuringTheCountdown_TheSessionStillSurvives` | The window being `idle` rather than `idle + countdown` — the server killing the session underneath the dialog that is still offering to save it |
| `AKeepAliveRequest_StampsActivityAndRenewsTheTicket` | **`ShouldRenew` being dropped** — the stamp mutation silently discarded and active users signed out |
| `AnyOtherRequest_DoesNotCountAsActivity` | The load-bearing negative: any authenticated request renewing the window, so an unattended workstation keeps itself signed in |
| `WithNoStampYet_TheTicketsIssueTimeIsUsed` | "Absent" being read as the epoch — every user signed out on their first request after signing in |
| `WithNoStampAndAnOldTicket_TheSessionIsRejected` | The `IssuedUtc` fallback becoming an unconditional pass |
| `WhenTheFeatureIsOff_NothingIsEverRejected` | The enforcer running with the feature disabled |
| `TheShippedDefaults_AreValid` | An off-by-one in the countdown-vs-minimum comparison failing every generated project at startup (the defaults put a 60s countdown exactly at a 1-minute minimum — equal is allowed) |
| `ACountdownLongerThanTheShortestWindow_FailsStartup` | An incoherent policy where the warning would open before the user had finished going idle |
| `AMaximumAboveEightHours_FailsStartup` | A deployment configuring the control into irrelevance |
| `ADefaultOutsideTheBounds_FailsStartup` | A seed value the administrator screen could never re-enter |
| `AMaximumBelowTheMinimum_FailsStartup` | An empty permitted range |
| `WhenTheFeatureIsOff_NothingIsValidated` | Startup failing over inert settings |
| `TheCookieLifetime_CoversTheWidestWindowPlusCountdownAndGrace` | The cookie-lifetime arithmetic changing so the cookie expires before enforcement can run |

**Note on the last one.** It asserts `123` minutes for `Max=120`, `DefaultCountdownSeconds=60`,
`Grace=2`. The name says "widest window plus countdown", but the arithmetic uses the
**default** countdown, not the maximum permitted one. See §8.

#### 2.28b `tests/Server.UI.IntegrationTests/IdleTimeoutWiringTests.cs` — **new**

Measures the wiring on the booted application rather than on options built for the occasion.
In full.

```csharp
#nullable enable
using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Infrastructure.Configurations;
using CleanArchitecture.Blazor.Infrastructure.Services.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Server.UI.IntegrationTests;

/// <summary>
/// How the idle timeout is wired into the real cookie handler, measured on the booted application
/// rather than on options built for the occasion.
/// </summary>
/// <remarks>
/// One of these assertions matters far more than the rest. Identity installs its security-stamp
/// validator as <c>OnValidatePrincipal</c>, and that is the mechanism by which "changing a user's
/// roles or password signs their existing sessions out" is true - the escalation guards depend on
/// it. Adding an idle check there means CHAINING, and a future edit that assigns over the delegate
/// instead would delete that guarantee in a way nothing else notices: the application would compile,
/// boot, pass its own permission tests, and quietly stop ending sessions whose permissions had been
/// revoked. <see cref="TheIdleCheck_DoesNotReplaceTheSecurityStampValidator"/> is what notices.
/// </remarks>
[TestFixture]
public class IdleTimeoutWiringTests
{
    private GxWebApplicationFactory _factory = null!;

    [OneTimeSetUp]
    public void StartTheApplication()
    {
        _factory = new GxWebApplicationFactory();
        _ = _factory.Services;
    }

    [OneTimeTearDown]
    public void StopTheApplication() => _factory.Dispose();

    private CookieAuthenticationOptions CookieOptions() =>
        _factory.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);

    [Test]
    public void TheCookieLifetime_IsSizedFromTheConfiguredMaximum()
    {
        // Not from the administered policy: the cookie is issued once at sign-in and cannot be
        // shortened afterwards, so it has to cover the widest window an administrator could ever
        // set. Tightening is enforced per request instead.
        var settings = _factory.Services.GetRequiredService<IdleTimeoutSettings>();

        CookieOptions().ExpireTimeSpan.Should().Be(settings.CookieLifetime);
    }

    [Test]
    public void TheCookieStillSlides()
    {
        // The keep-alive ping only helps if the cookie renews on requests.
        CookieOptions().SlidingExpiration.Should().BeTrue();
    }

    [Test]
    public async Task TheIdleCheck_DoesNotReplaceTheSecurityStampValidator()
    {
        // Driven rather than inspected: the delegate is a lambda, so there is nothing to compare it
        // against. Instead, hand it a principal that the STAMP validator must reject - one with an
        // identity Identity does not recognise - and assert the principal comes back null. If the
        // idle check had been assigned over the stamp validator, this principal would survive.
        var options = CookieOptions();
        using var scope = _factory.Services.CreateScope();
        // Two details make this test isolate the stamp validator rather than confound the two
        // checks. The ticket is issued two hours ago, because SecurityStampValidator returns early
        // for a ticket younger than its validation interval and would otherwise never run at all.
        // Activity is stamped as NOW, so the idle check passes - leaving the stamp validator as the
        // only thing that can null this principal.
        var context = await ValidateAsync(options, scope.ServiceProvider,
            issued: DateTimeOffset.UtcNow.AddHours(-2),
            lastActivity: DateTimeOffset.UtcNow);

        context.Principal.Should().BeNull(
            "the security-stamp validator must still run - assigning over OnValidatePrincipal would " +
            "silently disable it and every escalation guard that depends on it");
    }

    [Test]
    public void TheKeepAliveEndpoint_IsTheOneTheEnforcerRecognises()
    {
        // Two layers name this route - the UI maps it, Infrastructure matches it - and they read
        // the same constant so they cannot drift. Asserted because a mismatch is silent: pings
        // would return 204 and no session would ever renew.
        IdleTimeoutRoutes.KeepAlive.Should().Be("/account/keep-alive");
    }

    private static AuthenticationProperties Properties(DateTimeOffset issued, DateTimeOffset lastActivity)
    {
        var properties = new AuthenticationProperties { IssuedUtc = issued };
        properties.Items[IdleSessionEnforcer.LastActivityKey] =
            lastActivity.ToUnixTimeMilliseconds().ToString();
        return properties;
    }

    private static async Task<CookieValidatePrincipalContext> ValidateAsync(
        CookieAuthenticationOptions options,
        IServiceProvider services,
        DateTimeOffset issued,
        DateTimeOffset lastActivity)
    {
        var httpContext = new DefaultHttpContext
        {
            RequestServices = services
        };
        httpContext.Request.Path = "/";

        // The stamp validator rejects by calling SignInManager.SignOutAsync, which reads the ambient
        // HttpContext from the accessor rather than from the validation context.
        if (services.GetService<IHttpContextAccessor>() is { } accessor)
        {
            accessor.HttpContext = httpContext;
        }

        var principal = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "nobody")], "cookie"));

        var ticket = new AuthenticationTicket(
            principal,
            Properties(issued, lastActivity),
            IdentityConstants.ApplicationScheme);

        var context = new CookieValidatePrincipalContext(
            httpContext,
            new AuthenticationScheme(
                IdentityConstants.ApplicationScheme, null, typeof(CookieAuthenticationHandler)),
            options,
            ticket);

        await options.Events.ValidatePrincipal(context);
        return context;
    }
}
```

`GxWebApplicationFactory` is this repository's `WebApplicationFactory<Program>` subclass — it
boots the real host against throwaway SQLite databases and temporary storage roots. Any
equivalent test host works; the requirement is that the **real** `ConfigureApplicationCookie`
has run, because the point is to inspect what the application actually built.

| Test | Would catch |
|---|---|
| `TheCookieLifetime_IsSizedFromTheConfiguredMaximum` | The cookie being sized from the administered policy instead, so an administrator's change could never reach an open session |
| `TheCookieStillSlides` | `SlidingExpiration` turned off, making the keep-alive ping pointless |
| `TheIdleCheck_DoesNotReplaceTheSecurityStampValidator` | **The chain being replaced by an assignment** — see below |
| `TheKeepAliveEndpoint_IsTheOneTheEnforcerRecognises` | The two layers' route constants drifting apart, which is silent: pings return 204 and no session ever renews |

**On `TheIdleCheck_DoesNotReplaceTheSecurityStampValidator`.** The delegate is a lambda, so
there is nothing to compare it against — the test *drives* it instead. It hands the real
`options.Events.ValidatePrincipal` a principal the stamp validator must reject (an identity
Identity does not recognise) and asserts `context.Principal` comes back null. Two details make
it isolate the stamp validator rather than confound the two checks:

- The ticket is issued **two hours ago**, because `SecurityStampValidator` returns early for a
  ticket younger than its validation interval and would otherwise never run at all.
- Activity is stamped as **now**, so the idle check passes — leaving the stamp validator as
  the only thing that can null the principal.

**Prove this test has teeth by mutation, not by trusting it.** Temporarily change the
production code from

```csharp
var securityStampValidation = options.Events.OnValidatePrincipal;
options.Events.OnValidatePrincipal = async context => { await securityStampValidation(context); /* ... */ };
```

to a plain assignment that drops the captured delegate, run the test, and confirm it fails.
Then revert. A test of this shape can pass for the wrong reason — a principal that would have
been nulled by something else, an early return, a harness that never reaches the delegate —
and the failure it guards against is invisible in every other way. **This repository contains
no record that the mutation was performed**; treat it as a step to carry out in the target
repository rather than as one already done.

#### 2.28c `tests/Server.UI.IntegrationTests/IdleTimeoutDialogComponentTests.cs` — **new**

The dialog's close mechanism, observed where it actually happens. In full.

```csharp
#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using Bunit.TestDoubles;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Caching;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Server.UI.Components.Security;
using CleanArchitecture.Blazor.Server.UI.Services;
using CleanArchitecture.Blazor.Server.UI.Services.Layout;
using CleanArchitecture.Blazor.Server.UI.Services.UserPreferences;
using FluentAssertions;
using Mapster;
using Mediator;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using MudBlazor.Services;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Server.UI.IntegrationTests;

/// <summary>
/// Hosts the monitor alongside MudBlazor's providers, exactly as <c>MainLayout</c> does.
/// </summary>
/// <remarks>
/// <b>Read this before changing these tests.</b> The providers are not decoration. An inline
/// <c>MudDialog</c> is rendered by <see cref="MudDialogProvider"/>, not by the component that
/// declares it - so without the providers in the tree the monitor renders nothing at all, and a test
/// can wrongly conclude the dialog was never shown. It cost Pass 18 an hour.
/// </remarks>
public sealed class IdleMonitorHost : ComponentBase
{
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenComponent<MudPopoverProvider>(0);
        builder.CloseComponent();
        builder.OpenComponent<MudDialogProvider>(1);
        builder.CloseComponent();
        builder.OpenComponent<IdleTimeoutMonitor>(2);
        builder.CloseComponent();
    }
}

/// <summary>
/// The idle warning dialog's close mechanism, observed where it actually happens.
/// </summary>
/// <remarks>
/// The defect these exist for: the monitor used to close the dialog by dropping it from the render
/// tree behind an <c>@if</c>, which does not tell the provider to close anything. The dialog stayed
/// on screen, undismissable by design (<c>BackdropClick</c> and <c>CloseOnEscapeKey</c> are both
/// false), and its overlay swallowed every click - a frozen page, on every close path, whatever the
/// keep-alive returned.
/// <para>
/// <b>Every assertion here is on the HOST's markup, never the monitor's own.</b> That distinction is
/// the whole lesson: during the defect the monitor's own markup was empty - it had "closed" the
/// dialog as far as it was concerned - while the provider went on rendering it. A test that asserted
/// on the component alone would have passed for the entire life of the bug.
/// </para>
/// </remarks>
[TestFixture]
public class IdleTimeoutDialogComponentTests
{
    private const string StayLoggedIn = "Stay Logged In";

    private BunitContext _ctx = null!;
    private BunitJSModuleInterop _module = null!;

    [SetUp]
    public void SetUp()
    {
        _ctx = new BunitContext();

        // MudBlazor's popover provider reaches for JS on render. None of it affects the close
        // decision, so it is answered permissively rather than mocked call by call.
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var services = _ctx.Services;
        services.AddLogging();
        services.AddLocalization();
        services.AddMudServices();
        services.AddScoped<DialogServiceHelper>();
        services.AddSingleton(new TypeAdapterConfig());
        services.AddSingleton(Mock.Of<IApplicationSettings>());
        services.AddSingleton(Mock.Of<IUserProfileState>());
        services.AddSingleton(Mock.Of<IValidationService>());
        services.AddSingleton(Mock.Of<IMediator>());
        services.AddSingleton(Mock.Of<IAppCache>());
        services.AddSingleton(Mock.Of<IPermissionService>());
        services.AddSingleton(Mock.Of<IObjectMapper>());
        services.AddSingleton(Mock.Of<IUserPreferencesService>());
        services.AddScoped<LayoutService>();

        var policy = new Mock<IIdleTimeoutPolicyProvider>();
        policy.SetupGet(p => p.Enabled).Returns(true);
        policy.Setup(p => p.GetEffectiveAsync(
                  It.IsAny<System.Security.Claims.ClaimsPrincipal>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new IdleTimeoutPolicy(true, 1, 15));
        services.AddSingleton(policy.Object);

        _ctx.AddAuthorization().SetAuthorized("someone");

        _module = _ctx.JSInterop.SetupModule("./js/gxIdleTimeout.js");
        _module.SetupVoid("initialize", _ => true).SetVoidResult();
    }

    [TearDown]
    public async Task TearDown() => await _ctx.DisposeAsync();

    private IRenderedComponent<IdleMonitorHost> RenderAndOpenWarning()
    {
        var host = _ctx.Render<IdleMonitorHost>();
        var monitor = host.FindComponent<IdleTimeoutMonitor>().Instance;

        // What the JS tick does a second after the idle window elapses.
        host.InvokeAsync(() => monitor.OnIdleWarning(15)).GetAwaiter().GetResult();

        DialogShown(host).Should().BeTrue("the rest of each test is meaningless if it never opened");
        return host;
    }

    private static bool DialogShown(IRenderedComponent<IdleMonitorHost> host) =>
        host.Markup.Contains(StayLoggedIn, StringComparison.Ordinal);

    private static void ClickStayLoggedIn(IRenderedComponent<IdleMonitorHost> host) =>
        host.FindAll("button")
            .First(b => b.TextContent.Contains(StayLoggedIn, StringComparison.Ordinal))
            .Click();

    [Test]
    public void OnASuccessfulKeepAlive_TheDialogCloses()
    {
        var host = RenderAndOpenWarning();
        _module.SetupVoid("extend").SetVoidResult();

        ClickStayLoggedIn(host);

        DialogShown(host).Should().BeFalse(
            "Stay Logged In must return the page to the user; an undismissable dialog left over a " +
            "live session is a frozen page");
    }

    [Test]
    public void WhenTheModuleCallThrows_NothingEscapesAndTheDialogStillCloses()
    {
        // A JSException awaited in a click handler propagates into the circuit and tears it down -
        // an independent way to freeze the page. The dialog must close and the failure must not
        // reach the circuit.
        var host = RenderAndOpenWarning();
        _module.SetupVoid("extend").SetException(new InvalidOperationException("boom in JS"));

        var click = () => ClickStayLoggedIn(host);

        click.Should().NotThrow("a failed keep-alive is a session question, not a reason to kill the page");
        DialogShown(host).Should().BeFalse();
    }

    [Test]
    public void WhenTheModuleCallNeverSettles_TheDialogStillCloses()
    {
        // The dialog is closed before the module is called and the call is not awaited, so a call
        // that never returns cannot hold the dialog open.
        var host = RenderAndOpenWarning();
        _module.SetupVoid("extend");   // planned, deliberately never completed

        ClickStayLoggedIn(host);

        DialogShown(host).Should().BeFalse(
            "the dialog's fate must not depend on a network round trip that may never finish");
    }

    [Test]
    public void WhenAnotherTabReportsActivity_TheDialogCloses()
    {
        // No click involved. This path had the same defect and is the worse one: an undismissable
        // dialog over a session that is not idle at all, because the user is working in another tab.
        var host = RenderAndOpenWarning();
        var monitor = host.FindComponent<IdleTimeoutMonitor>().Instance;

        host.InvokeAsync(() => monitor.OnActivityResumed()).GetAwaiter().GetResult();

        DialogShown(host).Should().BeFalse();
    }

    [Test]
    public void SignOutNow_AlsoClosesTheDialog()
    {
        // signOut() navigates away, so in a browser the page is leaving anyway - but if the module
        // call fails the user must not be left holding an undismissable dialog.
        var host = RenderAndOpenWarning();
        _module.SetupVoid("signOut").SetException(new InvalidOperationException("boom in JS"));

        var click = () => host.FindAll("button")
            .First(b => b.TextContent.Contains("Sign Out Now", StringComparison.Ordinal))
            .Click();

        click.Should().NotThrow();
        DialogShown(host).Should().BeFalse();
    }
}
```

| Test | Would catch |
|---|---|
| `OnASuccessfulKeepAlive_TheDialogCloses` | The close-by-`@if` defect on the ordinary happy path |
| `WhenTheModuleCallThrows_NothingEscapesAndTheDialogStillCloses` | A JS exception escaping the handler and tearing down the circuit — an independent way to freeze the page |
| `WhenTheModuleCallNeverSettles_TheDialogStillCloses` | The handler awaiting the module before closing, so a hanging call holds an undismissable dialog open indefinitely |
| `WhenAnotherTabReportsActivity_TheDialogCloses` | The same defect on the no-click path — the worse one, since the session is not idle at all |
| `SignOutNow_AlsoClosesTheDialog` | A failed `signOut()` leaving the user holding a live dialog with no way out |

**Two harness rules that are the actual lesson:**

1. **`IdleMonitorHost` puts `MudPopoverProvider` and `MudDialogProvider` in the tree above the
   monitor.** They are not decoration. An inline `MudDialog` is rendered by the provider, not
   by the component that declares it — so without them the monitor renders nothing at all and
   a test can wrongly conclude the dialog was never shown.
2. **Every assertion is on the *host's* markup (`host.Markup`), never the monitor's own.**
   During the close-by-`@if` defect the monitor's own markup was empty — it had "closed" the
   dialog as far as it was concerned — while the provider went on rendering it. A test that
   asserted on the component alone would have passed for the entire life of the bug.

`_module.SetupVoid("extend")` with no `.SetVoidResult()` is bUnit's way of planning an
invocation that never completes — that is how the hanging-call test is built.

#### 2.28d `tests/Server.UI.IntegrationTests/ProfileSecurityTabComponentTests.cs` — **new**

Whether the profile page offers a Security tab at all. In full.

```csharp
#nullable enable
using System.Linq;
using System.Threading.Tasks;
using Bunit;
using Bunit.TestDoubles;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Caching;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Domain.Identity;
using CleanArchitecture.Blazor.Infrastructure.Configurations;
using CleanArchitecture.Blazor.Server.UI.Pages.Identity.Users;
using CleanArchitecture.Blazor.Server.UI.Pages.Identity.Users.Components;
using CleanArchitecture.Blazor.Server.UI.Services;
using CleanArchitecture.Blazor.Server.UI.Services.Layout;
using CleanArchitecture.Blazor.Server.UI.Services.UserPreferences;
using FluentAssertions;
using Mapster;
using Mediator;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor.Services;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Server.UI.IntegrationTests;

/// <summary>
/// Whether the profile page offers a Security tab at all.
/// </summary>
/// <remarks>
/// This can only be seen by rendering. The page answers 200 either way - the app renders at
/// <c>InteractiveServerRenderMode(prerender: false)</c>, so an HTTP response carries the shell and
/// none of the tabs - which is exactly how Pass 16A found an **empty** Security tab shipping while
/// every HTTP test stayed green.
/// <para>
/// The tab is absent, not disabled, in both off states: the feature switched off entirely, and user
/// overrides switched off. A greyed-out or empty tab invites a support call asking what belongs in
/// it, and neither state is something the user can act on.
/// </para>
/// </remarks>
[TestFixture]
public class ProfileSecurityTabComponentTests
{
    private BunitContext _ctx = null!;

    [TearDown]
    public async Task TearDown() => await _ctx.DisposeAsync();

    private void Arrange(bool enabled, bool allowUserOverride)
    {
        _ctx = new BunitContext();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var services = _ctx.Services;
        services.AddLogging();
        services.AddLocalization();
        services.AddMudServices();

        services.AddSingleton(Mock.Of<IUserPreferencesService>());
        services.AddScoped<LayoutService>();
        services.AddScoped<DialogServiceHelper>();
        services.AddSingleton(new TypeAdapterConfig());

        services.AddSingleton(Mock.Of<IApplicationSettings>());
        services.AddSingleton(Mock.Of<IUserProfileState>());
        services.AddSingleton(Mock.Of<IValidationService>());
        services.AddSingleton(Mock.Of<IMediator>());
        services.AddSingleton(Mock.Of<IAppCache>());
        services.AddSingleton(Mock.Of<IPermissionService>());
        services.AddSingleton(Mock.Of<IObjectMapper>());
        services.AddSingleton(Mock.Of<IUserStore<ApplicationUser>>());
        services.AddIdentityCore<ApplicationUser>();

        // The real settings object, so the page reads the same shape production does.
        services.AddSingleton<IIdleTimeoutSettings>(new IdleTimeoutSettings
        {
            Enabled = enabled,
            AllowUserOverride = allowUserOverride
        });
        services.AddSingleton(Mock.Of<IIdleTimeoutPolicyProvider>());

        // The tab CONTENTS are not under test and drag in the whole profile stack; stubbing them
        // keeps this about which panels the page declares.
        _ctx.ComponentFactories.AddStub<ProfileInformationTab>();
        _ctx.ComponentFactories.AddStub<ChangePasswordTab>();
        _ctx.ComponentFactories.AddStub<OrgChartTab>();
        _ctx.ComponentFactories.AddStub<SecurityTab>();
    }

    private static bool HasSecurityTab(IRenderedComponent<Profile> page) =>
        page.Markup.Contains("Security", System.StringComparison.Ordinal);

    [Test]
    public void WhenEnabledAndOverridesAllowed_TheSecurityTabIsOffered()
    {
        Arrange(enabled: true, allowUserOverride: true);

        var page = _ctx.Render<Profile>();

        HasSecurityTab(page).Should().BeTrue("the tab is the only place a user can shorten their own window");
    }

    [Test]
    public void WhenTheFeatureIsDisabled_TheSecurityTabIsAbsent()
    {
        Arrange(enabled: false, allowUserOverride: true);

        var page = _ctx.Render<Profile>();

        HasSecurityTab(page).Should().BeFalse("an empty tab is worse than no tab");
        page.FindComponents<Stub<SecurityTab>>().Should().BeEmpty();
    }

    [Test]
    public void WhenUserOverridesAreDisallowed_TheSecurityTabIsAbsent()
    {
        // AllowUserOverride: false is a decision that this is not the user's to set. A tab that
        // renders nothing says the opposite - that there should be something there.
        Arrange(enabled: true, allowUserOverride: false);

        var page = _ctx.Render<Profile>();

        HasSecurityTab(page).Should().BeFalse();
        page.FindComponents<Stub<SecurityTab>>().Should().BeEmpty();
    }

    [Test]
    public void TheOtherTabs_AreUnaffectedInEveryState()
    {
        // The blast radius: gating one panel must not drop the others. Asserted on the tab HEADERS,
        // because MudTabs renders only the active panel's content - the reason the first attempt at
        // this test failed looking for a stub that was never going to be in the DOM.
        foreach (var (enabled, allowOverride) in new[] { (true, true), (false, true), (true, false) })
        {
            Arrange(enabled, allowOverride);
            var markup = _ctx.Render<Profile>().Markup;

            markup.Should().Contain("Change Password", $"enabled={enabled} allowOverride={allowOverride}");
            markup.Should().Contain("Org Chart", $"enabled={enabled} allowOverride={allowOverride}");

            _ctx.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
```

| Test | Would catch |
|---|---|
| `WhenEnabledAndOverridesAllowed_TheSecurityTabIsOffered` | The tab being gated away entirely, leaving users no way to shorten their own window |
| `WhenTheFeatureIsDisabled_TheSecurityTabIsAbsent` | An **empty** tab shipping with the feature off |
| `WhenUserOverridesAreDisallowed_TheSecurityTabIsAbsent` | The same, for the `AllowUserOverride: false` decision |
| `TheOtherTabs_AreUnaffectedInEveryState` | The blast radius — gating one panel dropping the others |

**Why this can only be seen by rendering.** The application renders at
`InteractiveServerRenderMode(prerender: false)`, so an HTTP request for `/user/profile`
returns the shell and none of the tabs. Every HTTP-level test stays green while an empty
Security tab ships. The last test asserts on tab **headers**, not on stubbed contents,
because `MudTabs` renders only the active panel's content.

#### 2.28e `tests/Application.UnitTests/Middlewares/SecuritySettingsPageMiddlewareTests.cs` — **new**

In full.

```csharp
#nullable enable
using CleanArchitecture.Blazor.Server.UI.Middlewares;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.Middlewares;

/// <summary>
/// "Enabled: false makes the idle timeout inert" has to include its screens, or the claim is only
/// half true. Pass 16A found the security-settings route still answering 200 with the feature off,
/// rendering a panel that explained the feature was off - which is not the same thing as the feature
/// not being there.
///
/// Same shape and same answer as <see cref="SelfRegistrationMiddleware"/>: 404, because with the
/// feature disabled the screen does not exist, and 403 would confirm that it does.
/// </summary>
[TestFixture]
public class SecuritySettingsPageMiddlewareTests
{
    [Test]
    public void WhenTheIdleTimeoutIsOff_TheSettingsRouteIsBlocked()
    {
        SecuritySettingsPageMiddleware
            .ShouldBlock(new PathString("/system/security-settings"), idleTimeoutEnabled: false)
            .Should().BeTrue();
    }

    [Test]
    public void WhenTheIdleTimeoutIsOn_TheSettingsRouteIsNotBlocked()
    {
        SecuritySettingsPageMiddleware
            .ShouldBlock(new PathString("/system/security-settings"), idleTimeoutEnabled: true)
            .Should().BeFalse();
    }

    [TestCase("/system/security-settings/")]
    [TestCase("/system/security-settings/anything")]
    [TestCase("/SYSTEM/SECURITY-SETTINGS")]
    public void TrailingSegmentsAndCasing_AreNotAWayAround(string path)
    {
        SecuritySettingsPageMiddleware
            .ShouldBlock(new PathString(path), idleTimeoutEnabled: false)
            .Should().BeTrue();
    }

    [TestCase("/")]
    [TestCase("/system/logs")]
    [TestCase("/system/audittrails")]
    [TestCase("/system/picklistset")]
    [TestCase("/user/profile")]
    [TestCase("/account/login")]
    [TestCase("/account/keep-alive")]
    public void NoOtherPathIsAffected(string path)
    {
        // The blast radius, stated. A prefix match that caught "/system/..." would take the whole
        // System menu down with the feature.
        SecuritySettingsPageMiddleware
            .ShouldBlock(new PathString(path), idleTimeoutEnabled: false)
            .Should().BeFalse();
    }
}
```

| Test | Would catch |
|---|---|
| `WhenTheIdleTimeoutIsOff_TheSettingsRouteIsBlocked` | The route still answering 200 with the feature off |
| `WhenTheIdleTimeoutIsOn_TheSettingsRouteIsNotBlocked` | The middleware blocking the screen unconditionally |
| `TrailingSegmentsAndCasing_AreNotAWayAround` | A case-sensitive or exact-only match letting `/SYSTEM/SECURITY-SETTINGS` or a trailing slash through |
| `NoOtherPathIsAffected` | The blast radius — a prefix match on `/system/` taking the whole System menu down with the feature |

The decision is a `public static bool ShouldBlock(PathString, bool)` separated from the
pipeline precisely so it can be tested against a path directly rather than only through a
running host.

#### 2.28f Two pre-existing tests that gained an assertion

- `tests/Infrastructure.UnitTests/Persistence/GxTableNamingTests.cs` gained
  `[InlineData(typeof(SecurityPolicy), "SecurityPolicies")]` — pinning that the entity stays
  out of the `core` schema despite deriving from `BaseAuditableEntity`. Only relevant if your
  repository has a table-naming convention to opt out of.
- `tests/Application.UnitTests/Security/RequestAuthorizationRegistryTests.cs` gained two
  entries to its expected count, with the comment: *"24 since the idle-timeout pass:
  GetSecurityPolicyQuery (SecuritySettings.View) and UpdateSecurityPolicyCommand
  (SecuritySettings.Edit). Both carry their own permission rather than…"* — this pins that
  every mediator request carries a `[RequestAuthorize]` attribute.

#### 2.28g What no automated test reaches

State this plainly to whoever maintains the feature next. None of the following is reachable
from the suite above, and all of it has to be checked by hand:

- **The multi-tab matrix.** Three tabs, real `localStorage`, the asymmetry between activity in
  the tab showing the countdown and activity elsewhere. bUnit has no `localStorage` and no
  second tab.
- **The circuit-drop deadline.** Kill the SignalR connection mid-countdown and confirm the
  browser still signs out at the absolute deadline. The JS deadline is designed to outlive the
  circuit; nothing in .NET can observe that.
- **Real `localStorage` coordination**, including the housekeeping that clears a stale
  `gx:idle:signedOut` record on `initialize` — the thing that makes signing in again after an
  idle logout work at all.
- **What a frozen page looks like.** The dialog tests assert that markup disappears. They
  cannot assert that the overlay is gone, that clicks land, or that the page is usable.
- **The sliding-cookie behaviour end to end.** A unit test that constructs
  `AuthenticationProperties` directly passes whether or not `ShouldRenew` does anything real.
  See §6.
- **The `reason=idle` alert** actually appearing after a real idle sign-out.

**The hand-test list.** Set the policy to **1 minute** with a **15-second** countdown, then:

1. Idle one tab. The dialog opens after a minute, counts down, and the page returns to the
   login screen with the "signed out after a period of inactivity" alert.
2. Open three tabs and leave all idle, one of them backgrounded throughout. All three must
   land on the login page when the countdown expires — including the backgrounded one.
3. Open two tabs. Let one reach the countdown, then move the mouse in the **other** tab. The
   countdown must cancel silently.
4. In the tab showing the countdown, move the mouse. The countdown must **not** cancel; only
   the button dismisses it.
5. During a countdown, kill the SignalR connection (browser devtools, or stop and restart the
   server). The sign-out must still happen at the deadline.
6. Click **Stay Logged In** and confirm the page is usable immediately — click something
   behind where the dialog was.
7. Sign in again after an idle sign-out. You must not be bounced straight back out by a stale
   `gx:idle:signedOut` record.
8. Sleep the laptop mid-countdown and wake it after the deadline. The session must be gone,
   not resumed with a stale counter.
9. Work continuously for longer than the cookie lifetime without a full page load, then
   refresh. You must still be signed in — this is the keep-alive doing its job.
10. With two browsers signed in as the same user, change the user's roles. The other session
    must be signed out on its next request — this proves the security-stamp validator is still
    chained.

---

## 3. Configuration

### The block as shipped

Lives in `src/Server.UI/appsettings.json`, at the top level, nested one deep:

```jsonc
  "SecuritySettings": {
    "IdleTimeout": {
      // False switches the feature off end to end - no JS module is fetched, no principal check
      // runs, neither settings screen appears, and the cookie falls back to a fixed 8-hour lifetime.
      "Enabled": true,
      "DefaultIdleTimeoutMinutes": 15,
      "DefaultCountdownSeconds": 60,
      "MinIdleTimeoutMinutes": 1,
      "MaxIdleTimeoutMinutes": 120,
      // Whether users may shorten their own window. They can never lengthen it.
      "AllowUserOverride": true,
      // The browser pings an authenticated endpoint while the user is active, so the sliding cookie
      // renews. Turning this off inside a Blazor Server app means an actively working user's cookie
      // can expire underneath them - see the README.
      "KeepAlivePingEnabled": true,
      "CookieGraceMinutes": 2
    }
  },
```

The configuration path is `SecuritySettings:IdleTimeout` — a nested section, so
`GetSection` takes the **path**, not a top-level name. It is declared once as
`IdleTimeoutSettings.Key`.

**A generated project receives exactly that block**, values included. There is no
`appsettings.Development.json` and no environment override in this repository, so every
generated project starts at 15 minutes idle, a 60-second warning, bounds of 1–120 minutes,
user overrides allowed, keep-alive on, 2 minutes of cookie grace.

### Every key

| Key | Type | Default | Meaning |
|---|---|---|---|
| `Enabled` | bool | `true` | Master switch. `false` makes the feature inert end to end: no JS module fetched, no principal check, the admin route 404s, its menu entry and the profile Security tab are both absent, and the cookie falls back to a fixed 8 hours (`IdleTimeoutSettings.DisabledCookieLifetime`). |
| `DefaultIdleTimeoutMinutes` | int | `15` | The idle window seeded into a fresh database on first read. Not the value in force after that — the administered row is. |
| `DefaultCountdownSeconds` | int | `60` | The warning countdown seeded likewise. **Also the countdown term in the cookie-lifetime arithmetic** — see §8. |
| `MinIdleTimeoutMinutes` | int | `1` | Floor for every policy, administered or per-user. Applied by clamping at read time as well as by the validator. |
| `MaxIdleTimeoutMinutes` | int | `120` | Ceiling for every policy, **and the only value that sizes the authentication cookie**. A deployment decision rather than an administrator's, because a cookie is issued once and cannot be shortened afterwards. |
| `AllowUserOverride` | bool | `true` | Whether a user may shorten their own window. When `false` the Profile → Security tab is absent (not disabled) and an existing preference is **ignored rather than honoured**. |
| `KeepAlivePingEnabled` | bool | `true` | Whether the browser pings `/account/keep-alive` while the user is active. Off reintroduces the Blazor Server sliding-cookie trap: an actively working user's cookie expires underneath them. |
| `CookieGraceMinutes` | int | `2` | Slack added to the cookie's lifetime so it never lapses marginally before the enforcement meant to end the session. |

### Derived values (not configuration keys)

| Member | Value |
|---|---|
| `IdleTimeoutSettings.Key` | `"SecuritySettings:IdleTimeout"` |
| `IdleTimeoutSettings.AbsoluteMaxIdleTimeoutMinutes` | `480` (eight hours) — the ceiling on the ceiling |
| `IdleTimeoutSettings.MinCountdownSeconds` | `10` |
| `IdleTimeoutSettings.MaxCountdownSeconds` | `600` |
| `IdleTimeoutSettings.CookieLifetime` | `TimeSpan.FromMinutes(MaxIdleTimeoutMinutes + CookieGraceMinutes).Add(TimeSpan.FromSeconds(DefaultCountdownSeconds))` — **123 minutes** at the shipped defaults |
| `IdleTimeoutSettings.DisabledCookieLifetime` | `TimeSpan.FromHours(8)` — a plain fixed session, unrelated to any idle policy |

### Startup validation

Wired with `.ValidateDataAnnotations().ValidateOnStart()`, so failures kill the process at
startup naming the offending value. `IdleTimeoutSettings` implements `IValidatableObject`;
the rules are the body of its `Validate` method.

**What fails fast:**

| Rule | Message shape |
|---|---|
| `MinIdleTimeoutMinutes < 1` | *must be at least 1; found N* |
| `MaxIdleTimeoutMinutes > 480` | *must not exceed 480 (eight hours); found N* |
| `MaxIdleTimeoutMinutes <= MinIdleTimeoutMinutes` | *must be greater than MinIdleTimeoutMinutes* |
| `DefaultIdleTimeoutMinutes` outside `[Min, Max]` | *must lie within [Min, Max]* |
| `DefaultCountdownSeconds` outside `[10, 600]` | *must lie within [10, 600]* |
| `DefaultCountdownSeconds > MinIdleTimeoutMinutes * 60` | *exceeds the shortest idle window MinIdleTimeoutMinutes allows* |
| `CookieGraceMinutes < 1` | *must be at least 1* |

**What does not fail:**

- **Nothing at all is validated when `Enabled` is `false`.** `Validate` yields immediately.
  The values are inert; failing a start over a setting that does nothing would be noise.
- **A missing section does not fail.** `ConfigureApplicationCookie` falls back to
  `new IdleTimeoutSettings()`, and the options binder leaves the property defaults in place.
  A deployment that omits the block gets the shipped defaults, silently.
- **The `Enabled: false` cookie lifetime is not validated** against anything — it is a
  constant eight hours.
- **Runtime values are not startup-validated.** An administered row outside the bounds is
  clamped on read, not rejected at startup; the row is data, not configuration.

**One subtlety worth knowing.** The shipped defaults put the countdown (60 s) *exactly* at the
shortest permitted window (`MinIdleTimeoutMinutes = 1` → 60 s). Equal is allowed, exceeding is
not. An off-by-one in that comparison fails **every** generated project at startup, which is
why there is a test asserting the shipped defaults are valid.

---

## 4. The three policy levels

| Level | Who sets it | Where it is stored | Where it is edited |
|---|---|---|---|
| **Configured bounds** | The deployment (whoever owns `appsettings.json`) | `SecuritySettings:IdleTimeout` in configuration | A text editor plus a restart — `ValidateOnStart` means a bad value stops the process |
| **Administered policy** | An administrator holding `Permissions.SecuritySettings.Edit` | One row in the `SecurityPolicies` table (`IdleTimeoutMinutes`, `CountdownSeconds`) | System → Security Settings (`/system/security-settings`) |
| **Per-user preference** | The signed-in user, if `AllowUserOverride` | `AspNetUsers.IdleTimeoutMinutes`, nullable | Profile → Security tab (`/user/profile`) |

### The exact formula

Both values are computed in `IdleTimeoutPolicyProvider`. Written out:

```
administeredIdle    = clamp(row.IdleTimeoutMinutes,
                            settings.MinIdleTimeoutMinutes,
                            settings.MaxIdleTimeoutMinutes)

administeredCountdown = clamp(row.CountdownSeconds,
                              IdleTimeoutSettings.MinCountdownSeconds,   // 10
                              IdleTimeoutSettings.MaxCountdownSeconds)   // 600

preference          = settings.AllowUserOverride
                        ? (user.IdleTimeoutMinutes ?? null)
                        : null

effectiveIdle       = clamp(preference is null
                              ? administeredIdle
                              : min(preference, administeredIdle),
                            settings.MinIdleTimeoutMinutes,
                            settings.MaxIdleTimeoutMinutes)

effectiveCountdown  = administeredCountdown        // never narrowed by a preference

TotalWindow         = effectiveIdle minutes + effectiveCountdown seconds
```

and, when `settings.Enabled` is `false`, the whole thing short-circuits to
`IdleTimeoutPolicy.Disabled` — `(Enabled: false, IdleMinutes: 0, CountdownSeconds: 0)` — with
no database access at all.

**Read this out of the formula, because each point is a decision:**

- **`min`, never `max`.** A preference may only tighten. See §6.
- **The countdown is not in the `min`.** It is a warning duration, not a policy.
- **`clamp` is applied twice** — once to the administered row on the way out of the cache,
  once to the result of the `min`. That is what holds a value forced into either table to the
  deployment's limits.
- **`AllowUserOverride: false` yields `null`, not the stored value.** An existing preference
  is ignored rather than honoured; turning the option off is a decision that users do not set
  this, and a preference saved while it was on must not outlive that decision.
- **The row is read through the provider, never queried directly** — including by the
  administrator screen's own query handler, so the screen shows what enforcement will actually
  use rather than what happens to be persisted.

### Where each level is consumed

| Consumer | Calls | Uses |
|---|---|---|
| `IdleSessionEnforcer` (every authenticated request) | `GetEffectiveAsync(context.Principal)` | `Enabled`, `TotalWindow` |
| `IdleTimeoutMonitor` (once, on first render) | `GetEffectiveAsync(state.User)` | `Enabled`, `IdleMinutes`, `CountdownSeconds` |
| `GetSecurityPolicyQuery` (admin screen) | `GetAdministeredAsync()` | both values, plus the bounds from settings |
| `SecurityTab` (profile screen) | `GetAdministeredAsync()` **and** `GetEffectiveAsync(state.User)` | the administered value as the field's upper bound; the effective value for the "you will be signed out after N minutes" line |

**The monitor reads the policy once, on first render.** A policy change does not retune a
circuit that is already running — the browser keeps counting to the old window until the page
is reloaded. The *enforcement* is immediate regardless, because the enforcer re-reads on every
request; the visible effect of a tightening on an open circuit is that the user is signed out
by the server, possibly before their local countdown would have opened. That is why both
screens carry the "this takes effect on sessions that are already open" warning.

---

## 5. UI behaviour

### The timeline

```
  ┌─ last activity in ANY tab
  │
  │◄────────── IdleMinutes ──────────►│◄─── CountdownSeconds ───►│
  │                                   │                          │
  │                                   ▼                          ▼
  │                            warning opens              sign-out fires
  │                            (OnIdleWarning)            (signOutAllTabs)
  │
  │  every IdleMinutes/2 (min 15s) while NOT in countdown: keep-alive ping
  │
  └─ server-side: session valid until  IdleMinutes + CountdownSeconds  has elapsed
```

### The warning threshold

There is no separate "warn at N minutes" setting. The warning opens **when the idle window
itself has elapsed**, and the countdown is what follows it. At the shipped defaults: 15
minutes of no input, then a 60-second countdown, then sign-out — a 16-minute total window.

The JS tick runs once per second (`TICK_MS = 1000`). On each tick it computes
`idleFor = now - lastActivityAcrossTabs()`. When `idleFor >= idleMs` and no countdown is
running, it sets `countdownDeadline = now + countdownMs` and calls
`OnIdleWarning(ceil(countdownMs / 1000))`.

### The countdown

**The deadline is absolute** — `countdownDeadline` is a wall-clock millisecond value set once
when the warning opens, and every tick recomputes `remaining = max(0, deadline - now)`. It is
never a decremented counter. A laptop that sleeps for an hour mid-countdown wakes to a
correctly expired session rather than to a counter that resumes at 12.

Each tick calls `OnCountdownTick(ceil(remaining / 1000))`, which updates the displayed number
and the progress bar. When `remaining <= 0` the module signs out — **in JavaScript, not in
.NET**, deliberately, because it must still fire when the circuit is dead, which is exactly
when the dialog has stopped updating.

### The dialog

`MudDialog`, bound to `_warningOpen`, `MaxWidth.ExtraSmall`, `FullWidth`, with
`role="alertdialog"` and `aria-live="assertive"`. It shows:

- A title with a timer icon: *"Session about to expire"*.
- *"You have been inactive for a while. You will be signed out automatically unless you choose
  to stay."*
- The remaining seconds as a large centred `Typo.h3`, itself `aria-live="assertive"` so a
  screen reader announces the count.
- The word *"seconds"* beneath it.
- A warning-coloured `MudProgressLinear` whose value is
  `100 * secondsRemaining / countdownSeconds`.

Two buttons:

| Button | Style | What it does |
|---|---|---|
| **Sign Out Now** | text | Closes the dialog, then fire-and-forgets `signOut()`. The module broadcasts to other tabs via `localStorage`, POSTs the application's logout endpoint, and navigates to `/account/login?reason=idle`. |
| **Stay Logged In** | filled primary, `autofocus` | Closes the dialog, then fire-and-forgets `extend()`. The module clears the deadline, records activity, re-pings the keep-alive endpoint, and — **only if that ping returns 401 or 403** — navigates to the login page anyway. |

### The dialog's options, and why they are what they are

```csharp
    private static readonly DialogOptions _dialogOptions = new()
    {
        // Dismissal is explicit only. A backdrop click or an Escape keypress is exactly the sort of
        // stray input that must NOT be read as "I am still here".
        BackdropClick = false,
        CloseOnEscapeKey = false,
        MaxWidth = MaxWidth.ExtraSmall,
        FullWidth = true
    };
```

`BackdropClick = false` and `CloseOnEscapeKey = false` are **deliberate and load-bearing**. A
backdrop click or an Escape keypress is precisely the kind of stray input that must not be
read as "I am still here" — the same reasoning that makes activity in the tab showing the
countdown not cancel it. Dismissal requires one of the two buttons.

They are also what makes the close-mechanism defect described in §6 catastrophic rather than
merely untidy: a leftover dialog with these options is **undismissable**, and its overlay
swallows every click on the page.

`autofocus` on Stay Logged In means Enter dismisses the warning for a keyboard user — the
common case, and the safe direction, since staying signed in requires an affirmative act at
the keyboard.

### On expiry

The JS module runs `signOutAllTabs('idle')`:

1. Writes `{ t, tab, reason }` to `localStorage['gx:idle:signedOut']`.
2. Sets `leaving = true` and clears the tick interval (making everything idempotent).
3. POSTs `logoutUrl` with a `returnUrl` form field, `credentials: 'same-origin'`.
4. Navigates to `/account/login?reason=idle`.

It POSTs the existing sign-out endpoint rather than navigating to it, so there is one sign-out
endpoint in the application and no second one to drift — and so this tab chooses where it
lands afterwards.

**Independently of all of that, the server has already stopped honouring the session** once
`now - lastActivity > TotalWindow`. If the browser never runs any of the above, the next
authenticated request is rejected by `OnValidatePrincipal` and the cookie is deleted.

### Across tabs

- **Activity is shared** through `localStorage['gx:idle:lastActivity']`, written at most once
  every 2 seconds (`WRITE_THROTTLE_MS`) and carrying `{ t, tab }`.
- **Every tab measures idleness against the most recent activity in *any* tab.** A user
  working in one tab is not logged out by two others.
- **The asymmetry:** while a countdown is running in *this* tab, this tab's own activity is
  excluded from that comparison — both because `onActivity` returns early when
  `countdownDeadline !== null`, and because `lastActivityAcrossTabs()` skips a stored record
  whose `tab` is this tab. Activity in **another** tab still cancels the countdown; activity in
  **this** one does not.
- **When another tab's activity cancels a countdown**, the module calls `OnActivityResumed()`
  and the dialog closes **silently** — no snackbar, no explanation. The user was working
  elsewhere; nothing happened that they need to know about.
- **Sign-out is broadcast** through `localStorage['gx:idle:signedOut']`; every other tab's
  `storage` listener sees it and navigates immediately rather than waiting for its own tick.
- **Every tab re-verifies on `focus`** by pinging the keep-alive endpoint. This is the robust
  mechanism: it depends on no message being received, so it covers a tab that was throttled or
  asleep through the whole countdown, and it covers sign-out from *any* cause — an explicit
  logout elsewhere, an administrator disabling the account, the cookie simply expiring.
- **A failed ping signs nobody out.** `verifySession` catches and ignores network errors: the
  user may simply be offline, and the cookie remains the authority.

### On the login page afterwards

The module always lands on `/account/login?reason=idle`. `Login.razor` reads `reason` from the
query string and, when it equals `idle` (case-insensitively), shows an
**`Severity.Info`** alert above the form:

> *You were signed out after a period of inactivity.*

Informational, not an error. The user did nothing wrong, and a red panel here is the single
most common support call this feature generates.

### Two visible warnings for administrators

Both screens state the effective window in words — *"You will be signed out after 15 minutes
of inactivity."* — because a countdown that appears unexpectedly is the most common support
call, and an administrator who cannot see what they have set cannot answer it.

The admin screen additionally shows a standing warning, and repeats it as a snackbar **on
save**:

> *This takes effect on sessions that are already open, not only on new sign-ins. A user whose
> window becomes shorter than their current idle time is signed out on their next request.*

Said out loud on the save, not only in the static panel, because an administrator tightening
the window is about to sign people out.

### Long-running work with no input events

The module exports `touch()` for exactly this: a report export or a bulk import where the user
is watching a progress bar and generating no `mousemove`. Call it from the JS side of any such
feature so the user is not treated as absent. Nothing in this repository calls it yet.

---

## 6. Decisions worth carrying

Each item below has been checked against the code. Where the code contradicted the claim as
it was given to me, the correction is stated here **and** listed in §8.

### Enforcement

#### `OnValidatePrincipal` is chained, not assigned — **confirmed**

At the point `ConfigureApplicationCookie` runs, `options.Events.OnValidatePrincipal` is
already ASP.NET Identity's security-stamp validator, installed by `AddIdentityCookies`. That
validator is the mechanism behind "changing a user's roles or password signs their existing
sessions out". Assigning over it deletes that guarantee **silently**: the application still
compiles, still boots, still passes every permission test, and quietly stops ending sessions
whose permissions have been revoked.

The correct shape, verbatim from the source:

```csharp
            var securityStampValidation = options.Events.OnValidatePrincipal;

            options.Events.OnValidatePrincipal = async context =>
            {
                await securityStampValidation(context).ConfigureAwait(false);

                // The stamp validator rejects by nulling the principal. Nothing left to enforce.
                if (context.Principal is null)
                {
                    return;
                }
                // ... idle check ...
            };
```

`IdleTimeoutWiringTests.TheIdleCheck_DoesNotReplaceTheSecurityStampValidator` drives the real
delegate — it cannot inspect a lambda — with a ticket issued two hours ago (so the stamp
validator's validation interval has elapsed and it actually runs) and a last-activity stamp of
*now* (so the idle check passes), then asserts the principal comes back null.

**Prove it has teeth by mutation.** Temporarily replace the chain with a plain assignment, run
the test, confirm it fails, revert. A test of this shape can pass for the wrong reason, and
the failure it guards against is invisible in every other way. This repository holds no record
that the mutation was performed; do it in the target repository.

#### `ExpireTimeSpan` is sized from the maximum — **confirmed, with one imprecision**

A cookie is issued once and cannot be retroactively shortened, so its lifetime cannot be
derived from a runtime-administered value — an administrator's change would never reach a
session already open. It is therefore sized from the widest window any policy may reach:

```csharp
    public TimeSpan CookieLifetime => TimeSpan
        .FromMinutes(MaxIdleTimeoutMinutes + CookieGraceMinutes)
        .Add(TimeSpan.FromSeconds(DefaultCountdownSeconds));
```

**The countdown term is `DefaultCountdownSeconds`, not the maximum permitted countdown.** The
claim as given — "max idle + countdown + grace" — is right about the shape and about *why*,
but the countdown it uses is the configured default, not `MaxCountdownSeconds` (600). An
administrator may set a countdown longer than the default: the validator permits anything in
`[10, 600]` that does not exceed the idle window in seconds. At the shipped defaults an
administrator could set idle 120 / countdown 600, giving a `TotalWindow` of 130 minutes against
a 123-minute cookie.

The practical impact is small — the cookie's expiry sends the browser to the login page rather
than leaving a session alive, and the JS module lands the user on `?reason=idle` anyway — but
the invariant "the cookie always outlives the widest enforceable window" does not strictly
hold. If you want it to, use `MaxCountdownSeconds` in that expression instead. Logged in §8.

The real enforcement is `OnValidatePrincipal` reading the *current* policy on every
authenticated request, which is what makes an administrator's change take effect on sessions
already in progress. The cookie's own expiry is only the outer bound.

#### The sliding-expiration half-life — **the mechanism is present; the stated reason differs**

ASP.NET Core's cookie handler reissues a ticket only once **more than half** of
`ExpireTimeSpan` has elapsed. With `ExpireTimeSpan` at 123 minutes that is 61.5 minutes — far
longer than the 15-minute idle window. So a keep-alive request, on its own, would not cause the
ticket to be rewritten, and the last-activity value the enforcer writes into
`context.Properties.Items` would be **discarded** rather than persisted. The feature would sign
out actively working users.

The keep-alive path therefore sets `ShouldRenew` explicitly. **Exactly here**, in
`IdleSessionEnforcer.IsStillValidAsync`, verbatim:

```csharp
        // Only the keep-alive ping renews the window. Every other authenticated request - a static
        // asset, a framework callback, the browser reconnecting a circuit - must NOT count as the
        // user being present, or an unattended workstation would keep itself signed in.
        if (IsKeepAlive(context.HttpContext.Request))
        {
            context.Properties.Items[LastActivityKey] =
                now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);

            // Renews the stored ticket in place. Cheaper than re-issuing through SignInAsync, which
            // with a server-side ticket store would rotate the session key on every ping.
            context.ShouldRenew = true;
        }
```

`ShouldRenew = true` makes the handler call `ITicketStore.RenewAsync` (or rewrite the cookie,
without a store), which is what persists the mutated `Properties.Items`.

**Two honest qualifications.** First, the source comment gives a *different* reason for the
line — that renewing in place is cheaper than `SignInAsync`, which would rotate the session
key on every ping. That reason is also true. The half-life behaviour is real ASP.NET
behaviour and the line is genuinely necessary because of it, but the repository does not
document the half-life as the motivation, so treat the causal claim as an accurate description
of the framework rather than as recovered intent. Second, **the repository contains no record
of the control-vs-subject verification** described in the brief (a session that idles out
beside one that pings and survives). It is the right way to verify this and it should be done
— but it is a step to perform, not one already evidenced here.

What is certain, and is the reason to be careful: **a unit test that constructs
`AuthenticationProperties` directly proves nothing about this.** It mutates an in-memory
dictionary and asserts the mutation happened. `IdleSessionEnforcerTests.AKeepAliveRequest_StampsActivityAndRenewsTheTicket`
is exactly such a test — valuable for pinning that `ShouldRenew` is set at all, useless for
proving the ticket is actually persisted. Only a running system distinguishes the two.

### Policy

#### The user preference may only tighten — **confirmed**

`Math.Min(chosen, administered.IdleMinutes)`, never `Math.Max`. An idle timeout is a control
against unattended workstations; if a user can lengthen their own, the first person who finds
it inconvenient sets it to eight hours and the control is gone — the same reasoning that keeps
password policy out of a user profile. Tightening is both safe and genuinely useful: someone
on a shared shop-floor terminal can choose five minutes.

Enforced in three places, in descending order of authority:

1. `IdleTimeoutPolicyProvider.GetEffectiveAsync` — the `Math.Min` at read time. **This is the
   enforcement.**
2. `SecurityTab.PersistAsync` — a bounds check before saving, with an error snackbar.
3. The numeric field's `Max="_administeredMinutes"` — the control cannot express a longer
   value at all.

`IdleTimeoutPolicyProviderTests.AUserPreferenceLongerThanThePolicy_IsIgnored` is the assertion
that keeps this from being inverted.

#### The countdown is not user-adjustable — **confirmed**

`GetEffectiveAsync` returns `CountdownSeconds: administered.CountdownSeconds` unconditionally.
It is how long the warning shows, not how long a session may sit idle — a warning, not a
policy. `SecurityTab` offers no field for it.

#### Values are clamped on read, not only on write — **confirmed**

`GetAdministeredAsync` clamps on the way **out** of the cache, and `GetEffectiveAsync` clamps
the result of the `min` again. A row written before the bounds were tightened, or edited around
the screen, is still held to the deployment's limits — which matters because the
authentication cookie was sized from those limits. The validator makes a refusal *visible*;
the clamp makes it *true*.

#### The per-user preference is read from the database, not a claim — **confirmed**

A claim would be free to read on every request, and it is how the same codebase carries
`MustChangePassword`. But a claim only changes when the authentication cookie is reissued, and
reissuing means `SignInManager.RefreshSignInAsync`, which **cannot run inside a Blazor
circuit**: this application renders at `InteractiveServerRenderMode(prerender: false)`, so by
the time a component's event handler runs the response has long since started and there is no
cookie to write. A user changing their own timeout on a Blazor page would see it take effect at
their *next sign-in* — the Profile screen silently not working.

So the preference is read from `AspNetUsers.IdleTimeoutMinutes` behind a per-user cache entry,
invalidated on save. Correct on the very next request, and it costs a dictionary lookup.

**Caveat carried over from §2.10:** the XML doc comment on `ApplicationUser.IdleTimeoutMinutes`
claims the opposite — that the value is projected as a claim and that changing it refreshes the
sign-in. It is stale; `ApplicationUserClaimsPrincipalFactory` adds only the
`MustChangePassword` claim. The implementation is right and the comment is wrong.

*(For completeness: the repository does have a `/pages/authentication/refresh-signin` GET
endpoint that exists precisely to work around the circuit limitation for the password-change
flow — a full-page navigation that reissues the cookie. The idle-timeout preference
deliberately does not use it, because a database read behind a cache is simpler than a
navigation.)*

#### Cached with explicit invalidation, not a short TTL — **confirmed**

The policy is read on every authenticated request, so both the administered policy and each
user's preference sit behind a cache. The duration is **12 hours**, and that is deliberate:

```csharp
    /// <summary>
    /// Long, and deliberately so: both caches are invalidated on save, so the duration is a backstop
    /// against a missed invalidation rather than the mechanism by which changes propagate.
    /// </summary>
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(12);
```

A stale policy would mean an administrator's change not taking effect, which is precisely the
behaviour that putting the setting on a screen was meant to provide. Propagation is by
invalidation:

- `UpdateSecurityPolicyCommandHandler` calls `_provider.Invalidate()` immediately after
  `SaveChangesAsync`.
- `SecurityTab.PersistAsync` calls `PolicyProvider.InvalidateUser(_userId)` immediately after
  `UserManager.UpdateAsync`.

**The sentinel.** "No preference" is cached as **`0`** and mapped back to `null` on the way
out:

```csharp
        // Nullable<int> is not cacheable through FusionCache's generic path as cleanly as a sentinel,
        // so "no preference" is cached as 0 and mapped back here. Caching the absence matters: most
        // users never set one, and without it every request would be a database read for a null.
        var cached = await _cache.GetOrSetAsync(
            UserCacheKey(userId),
            async ct =>
            {
                await using var db = await _dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

                return await db.Users
                    .Where(u => u.Id == userId)
                    .Select(u => u.IdleTimeoutMinutes ?? 0)
                    .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            },
            options => options.SetDuration(CacheDuration),
            cancellationToken).ConfigureAwait(false);

        return cached > 0 ? cached : null;
```

**Caching the absence is the point.** Most users never set a preference. Without the sentinel,
a cache that cannot store `null` would miss on every request for every one of those users and
issue a database read to discover nothing — on every authenticated request in the application.
`0` is safe as a sentinel because `MinIdleTimeoutMinutes` is at least 1, so a genuine
preference can never be `0`. Note that `FirstOrDefaultAsync` also returns `0` for a user row
that does not exist, which lands on the same "no preference" answer — the right behaviour.

Cache keys, declared once so a multi-tenant variant has one place to change:

```csharp
    public const string CacheKey = "security-policy:idle-timeout";
    public static string UserCacheKey(string userId) => $"security-policy:idle-timeout:user:{userId}";
```

#### The policy row is seeded lazily on first read — **confirmed**

`LoadAdministeredAsync` reads the first row ordered by `Id`; if there is none it inserts one
from `DefaultIdleTimeoutMinutes` / `DefaultCountdownSeconds`, saves, and logs. Seeding here
rather than in the database initializer is what lets the feature work on a database
provisioned before it existed, with no data migration — the first read after the upgrade
writes the row.

**State the consequence plainly: a freshly provisioned database shows no row in
`SecurityPolicies` until something reads the policy.** Until then the configured defaults are
what is in force. Do not read an empty table as "the feature is not configured".

`UpdateSecurityPolicyCommandHandler` also creates the row if it is missing, so a save does not
depend on a read having happened first.

#### The policy is installation-wide, not per-tenant — **confirmed**

`SecurityPolicies` holds a single row and `CacheKey` is a constant. Every tenant in a
multi-tenant deployment shares one idle window, and one tenant's administrator sets the policy
for all of them. **Say this out loud to anyone deploying multi-tenant — they will assume
otherwise.**

It is a deliberate starting point rather than an oversight: every reader goes through
`IIdleTimeoutPolicyProvider` precisely so that adding a tenant column and keying the cache by
tenant is a migration plus one cache key, not a redesign.

### The keep-alive endpoint

#### Why it exists — **confirmed**

A Blazor Server user works inside one long-lived SignalR circuit and makes almost no HTTP
requests. A sliding authentication cookie renews only on HTTP requests, so somebody actively
working for two hours can have their cookie expire underneath them — and the next real request
(a download, a refresh, an export) bounces them to the login page mid-task. The ping at
`/account/keep-alive` exists solely to make that request. It is also why **Stay Logged In**
calls the endpoint rather than only resetting a timer in the browser.

Ping interval: `max(idleMinutes * 60_000 / 2, 15_000)` — half the idle window, floored at 15
seconds. Frequent enough that the sliding cookie always renews well before it could lapse,
rare enough to be nothing on the wire. `keepAliveMs = 0` disables it, which is what
`KeepAlivePingEnabled: false` produces.

Any existing application on this stack **without** a keep-alive is worth checking the same
way: work past the cookie lifetime without a full page load, then refresh.

#### It answers status codes, not redirects — **confirmed**

A browser `fetch` follows redirects by default. With an ordinary `RequireAuthorization()`, an
expired session's request is challenged by the fallback policy, which redirects to the login
page — and `fetch` follows it, so the client observes **`200`**. Every client-side check for a
dead session becomes inert: the focus re-verification, the ping's own check, and the "do not
resurrect a dead session" guard behind Stay Logged In.

**How the redirect was avoided without changing any other endpoint's behaviour:** the endpoint
is mapped `.AllowAnonymous()` and states its authentication check inside the handler.

```csharp
        endpoints.MapPost(IdleTimeoutRoutes.KeepAlive, (HttpContext context) =>
            {
                if (context.User.Identity?.IsAuthenticated != true)
                {
                    return Results.Json(new { signedOut = true }, statusCode: StatusCodes.Status401Unauthorized);
                }

                return ValidateRequestOrigin(context, logger)
                    ? Results.NoContent()
                    : Results.Json(new { forbidden = true }, statusCode: StatusCodes.Status403Forbidden);
            })
            .AllowAnonymous() // see above: the check is stated in the handler so the answer can be a status code
            .DisableAntiforgery();
```

The fallback policy's challenge fires *before* any handler runs, so this cannot be fixed from
inside the handler while the policy still applies — hence `AllowAnonymous` plus an explicit
check. The authorization is not lost, it is *stated here*.

**The endpoint has nothing to protect in any case.** It returns a bare status, and the
last-activity stamp is written by `IdleSessionEnforcer` during **authentication**, for an
authenticated principal only, whatever this handler decides. An anonymous caller learns only
that they are not signed in.

**Deliberately not done by touching `OnRedirectToLogin`.** That event is shared by every page
and endpoint in the application; a blanket change to authentication responses to fix one
machine-facing endpoint is far more dangerous than the problem.

#### Why the responses carry bodies — **confirmed**

`app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true)`
rewrites any 400–599 response that has **no body and no content type** into the not-found
page. A bare `Results.Unauthorized()` would come back to the `fetch` as an HTML 404 page, and
the client's `res.status === 401` check would never fire.

`Results.Json(new { signedOut = true }, statusCode: 401)` gives the response a content type
and a body, so it survives as the terse machine answer the client is reading. The same for the
403. The 204 needs no body — it is outside the rewritten range.

#### It is origin-checked, not merely antiforgery-disabled — **confirmed**

It is not exempt from CSRF thinking just because it returns nothing. **It does change
something — it renews the session.** This application sets `SameSite=None` on the
authentication cookie, so a cross-site POST carries it, and an unchecked keep-alive would let
any page the user happens to have open hold their session open indefinitely — defeating
precisely the control the endpoint serves.

`ValidateRequestOrigin` compares the `Referer` header against `{scheme}://{host}`:

- A legitimate same-origin `fetch` sends `Referer: https://host/whatever-page`, which starts
  with the expected origin → **accepted**.
- A cross-origin POST sends a different origin, or none → **refused with 403**.
- A request with **no `Referer` at all** → **refused**. That is deliberate: `IsNullOrEmpty` is
  checked first.

The same helper already guarded the login and external-login endpoints, matching their idiom
rather than inventing a second rule.

*(See the caveat in §2.15 about a `Referrer-Policy` that suppresses same-origin referrers: the
client treats 403 exactly like 401 and navigates to login, so a suppressed `Referer` produces
spurious sign-outs. Not a problem under default browser settings.)*

#### Two more properties worth carrying

- **Mapped outside the account route group**, at the absolute path, because the enforcer
  matches an absolute path that Infrastructure names (`IdleTimeoutRoutes.KeepAlive`) and a
  group prefix would put the two out of step. The mismatch is silent — pings return 204 and no
  session ever renews. `IdleTimeoutWiringTests` asserts the constant's value for this reason.
- **The `antiforgeryToken` option in the JS module is currently unused.** `initialize` accepts
  one and `ping()` would send it as a `RequestVerificationToken` header, but
  `IdleTimeoutMonitor` does not pass one and both endpoints call `.DisableAntiforgery()`. The
  capability is dormant, not broken; leave it in place if you want the option, or delete it.

### The dialog

#### It closes by binding `Visible`, never by an `@if` around `<MudDialog>` — **confirmed; this was a live defect**

MudBlazor renders an **inline** dialog through `MudDialogProvider`, not through the component
that declares it. Removing the `<MudDialog>` element from the render tree does **not** tell the
provider to close anything — the provider goes on rendering a dialog the component no longer
knows about.

With `BackdropClick = false` and `CloseOnEscapeKey = false` — both deliberate, so that a stray
click or keypress cannot dismiss a countdown — the leftover dialog is **undismissable**, and
its overlay swallows every click on the page. A frozen page.

**Every close path was affected, not just the button:** Stay Logged In, Sign Out Now, and the
silent close when another tab reports activity — regardless of what the keep-alive returned.
The last of those is the worst, because the session is not idle at all: the user is
demonstrably working in another tab and their first tab locks up.

The correct shape, verbatim:

```razor
<MudDialog @bind-Visible="_warningOpen" Options="_dialogOptions" aria-live="assertive" role="alertdialog">
```

with one and only one method that closes it:

```csharp
    private Task CloseWarningAsync()
    {
        _warningOpen = false;
        return InvokeAsync(StateHasChanged);
    }
```

`@bind-Visible` rather than a one-way `Visible` keeps the flag and the dialog's own state from
ever disagreeing: the dialog cannot close itself under these options, but if a future option
let it, the component would hear about it rather than holding a flag that had quietly become a
lie.

#### Handlers close first, then call out without awaiting — **confirmed**

```csharp
    private async Task StayLoggedInAsync()
    {
        await CloseWarningAsync();

        if (_module is null)
        {
            return;
        }

        _ = InvokeModuleSafelyAsync("extend");
    }
```

Awaiting the module and closing afterwards makes the dialog's fate depend on a network round
trip that can hang indefinitely — and this dialog is configured to be undismissable, so a
handler that fails to reach its closing line leaves the page unusable. The defect was measured
with the dialog staying open on a hanging call, on a throwing call, and on a perfectly
successful one.

**A timeout is not the answer.** It bounds the hang but still holds the dialog open for the
length of it, and buys nothing — because **nothing in the response changes what the handler
does**. `extend()` owns the session decision entirely: it re-pings, and on `401`/`403` it
navigates to the login page itself. The .NET side has no decision to make on the result, which
is why fire-and-forget is the whole answer here rather than a shortcut. A dead session still
ends, because the module ends it.

`SignOutNowAsync` has the same shape and the same reasoning: `signOut()` navigates away, so
there is nothing to wait for, and a failure to reach the module must not leave the user
staring at a live dialog.

#### A JS call that throws must not escape the handler — **confirmed**

An exception from a JS call awaited in a click handler propagates into the circuit and tears
it down — an **independent** way to freeze the page, unrelated to the dialog. Catch, log,
close anyway:

```csharp
    private async Task InvokeModuleSafelyAsync(string function)
    {
        try
        {
            await _module!.InvokeVoidAsync(function);
        }
        catch (JSDisconnectedException)
        {
            // The circuit is already gone. Ordinary on sign-out; nothing to report.
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "The idle-timeout module's {Function} call failed.", function);
        }
    }
```

`JSDisconnectedException` is caught separately and **not** logged: the circuit being gone is
the ordinary case on sign-out, not an incident. Everything else is logged and deliberately not
rethrown — by the time this runs the dialog is already closed and the user has their page
back, which is the outcome that matters. A failed keep-alive is a session question, not a
reason to destroy the page the user is looking at, and the session itself is still governed
server-side.

`DisposeAsync` applies the same `JSDisconnectedException` handling around `dispose()`.

### The JS module

#### Activity is shared across tabs — **confirmed**

`localStorage['gx:idle:lastActivity']` carries `{ t, tab }`, written at most every 2 seconds.
`lastActivityAcrossTabs()` returns the most recent activity in **any** tab, so a user working
in one tab is not logged out by two others.

#### The tab-id asymmetry — **confirmed**

Activity in *another* tab cancels the countdown (the user is demonstrably working). Activity in
*the tab showing the modal* does not (a stray mouse movement must not silently extend a session
that has already announced it is ending). Dismissal there requires the explicit button.

It falls out of two lines. In `onActivity`:

```javascript
    if (countdownDeadline !== null) return;
    recordActivity(false);
```

and in `lastActivityAcrossTabs`, which excludes this tab's own record while a countdown is
running:

```javascript
function lastActivityAcrossTabs() {
    let newest = countdownDeadline === null ? lastLocalActivity : 0;
    try {
        const raw = localStorage.getItem(STORAGE_KEY);
        if (raw) {
            const rec = JSON.parse(raw);
            if (rec.tab !== TAB_ID || countdownDeadline === null) newest = Math.max(newest, rec.t);
        }
    } catch { }
    return newest;
}
```

Both are needed: the first stops new writes, the second ignores a stale one this tab wrote
just before the countdown opened. `TAB_ID` is a per-tab `crypto.randomUUID()`, with a
`Date.now()`-plus-`Math.random()` fallback because `randomUUID` requires a secure context and a
dev proxy on plain HTTP should degrade rather than throw and take the whole module with it.

#### The countdown deadline is absolute — **confirmed**

`countdownDeadline = now + countdownMs`, set once; each tick computes
`remaining = Math.max(0, countdownDeadline - now)`. Never a decremented counter, so a sleeping
laptop wakes to a correctly expired session rather than to a counter that resumes where it
left off.

The expiry action is also in JavaScript rather than in .NET, deliberately: *"it must still fire
when the circuit is dead, which is exactly when the dialog has stopped updating."*

#### Sign-out is broadcast, and tabs re-verify on focus — **confirmed**

Two mechanisms, on purpose:

1. The tab that ends the session writes `localStorage['gx:idle:signedOut']`; every other tab's
   `storage` listener sees it and navigates at once rather than on its next tick.
2. **Any tab regaining focus re-pings the server.** This is the robust one — it depends on no
   message being received, so it covers a tab that was throttled or asleep through the whole
   countdown, and it covers sign-out from **any** cause: an explicit logout elsewhere, an
   administrator disabling the account, or the cookie simply expiring. All surface as a 401.

```javascript
async function verifySession() {
    if (leaving || document.hidden) return;
    try {
        const res = await ping();
        if (res.status === 401 || res.status === 403) leaveTo(loginUrl);
        else lastKeepAlive = Date.now();
    } catch {
        // Offline. The cookie remains the authority; do not sign anybody out over a failed fetch.
    }
}
```

Note the `catch`: a failed fetch signs nobody out. The user may simply be offline.

#### The housekeeping on the broadcast key — **confirmed**

`initialize` **clears** `gx:idle:signedOut` before doing anything else:

```javascript
    // A sign-out record left by the PREVIOUS session would bounce this freshly signed-in tab
    // straight back out, and would keep doing it. Clearing it here is what makes signing in again
    // after an idle logout work at all.
    try { localStorage.removeItem(SIGNOUT_KEY); } catch { }
```

Without it, the record written during the idle sign-out persists in `localStorage`; the user
signs in again, a new tab initializes, sees a truthy value on the key, and is bounced straight
back to the login page — repeatedly. Every `localStorage` access in the module is wrapped in
`try/catch`, because a browser in private mode or with site data blocked throws on access.

`leaving` makes navigation idempotent when two paths fire at once, and every navigation path
clears the tick interval.

#### The module lives in `wwwroot/js/` — **confirmed**

Not collocated as `IdleTimeoutMonitor.razor.js`. Collocated scripts are auto-served only for
**Razor class libraries**; in an application project the file is not served and the dynamic
`import` fails at runtime with a 404 visible only in the browser console. The import path is
`"./js/gxIdleTimeout.js"`.

### Disabled state

#### `Enabled: false` makes the feature inert, and both screens absent — **confirmed**

A greyed-out control invites a support call asking how to enable it; an empty tab is a visible
defect that invites a support call asking what belongs in it. Neither state is something the
user can act on, so neither is shown.

| Surface | Mechanism when disabled |
|---|---|
| `/system/security-settings` | `SecuritySettingsPageMiddleware` sets **404** and does not call `_next` |
| Its navigation entry | `MenuService`'s constructor removes any child whose `Href` matches the path |
| Profile → Security tab | `Profile.razor` omits the `<MudTabPanel>` entirely, behind an `@if` |
| `SecurityTab` itself | Renders an empty `@if` branch as a second guard, and returns early from `OnInitializedAsync` |
| The JS module | `IdleTimeoutMonitor.OnAfterRenderAsync` returns before the `import` when `!policy.Enabled` |
| The principal check | `ConfigureApplicationCookie` returns before chaining the event |
| The cookie | Falls back to `DisabledCookieLifetime` — a fixed 8 hours, unrelated to any idle policy |

**404, not 403.** With the idle timeout disabled the screen does not exist, and saying
"forbidden" would confirm that it is there. It is also not an authorization failure — a user
holding `Permissions.SecuritySettings.Edit` is not being refused; there is simply nothing to
edit. This matches the same repository's self-registration surface.

**The route and the tab use different mechanisms because they are different things.** The
admin screen is a route, so a middleware can close it. The Security tab is a *component* —
there is no route to close — so the page omits the panel.

`AllowUserOverride: false` removes the Security tab too, on the same reasoning, while leaving
the admin screen and the enforcement fully in place.

### Testing

#### Assert on the host's markup, not the component's own — **confirmed**

During the dialog defect the component's own markup was **empty** — it had "closed" the dialog
as far as it was concerned — while `MudDialogProvider` went on rendering it. **A test that
checked the component alone would have passed for the entire life of the bug.** Every
assertion in `IdleTimeoutDialogComponentTests` reads `host.Markup`.

#### The providers must be in the tree — **confirmed**

Rendering the monitor requires `MudDialogProvider` **and** `MudPopoverProvider` above it.
Without them it renders nothing at all, and a test can wrongly conclude the dialog was never
shown. `IdleMonitorHost` puts both in, in that order, exactly as the application layout does:

```csharp
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenComponent<MudPopoverProvider>(0);
        builder.CloseComponent();
        builder.OpenComponent<MudDialogProvider>(1);
        builder.CloseComponent();
        builder.OpenComponent<IdleTimeoutMonitor>(2);
        builder.CloseComponent();
    }
```

`JSRuntimeMode.Loose` is set because the popover provider reaches for JS on render; none of it
affects the close decision, so it is answered permissively rather than mocked call by call.

#### What no automated test can reach

Listed in full at §2.28g, with the hand-test list. In summary: the multi-tab matrix, the
circuit-drop deadline, real `localStorage` coordination (including the stale-key housekeeping),
what a frozen page actually looks like, and whether `ShouldRenew` really persists a ticket.

---

## 7. Porting notes

### What the target repository must already have

| Prerequisite | Why | If it is missing |
|---|---|---|
| **Cookie-based ASP.NET Core Identity**, configured through `ConfigureApplicationCookie` (or an equivalent named-options hook) | The enforcement lives in `OnValidatePrincipal` on the application cookie | Nothing to port. A JWT/bearer application needs an entirely different enforcement point — the idle stamp would have to live in a token or a store, and the "chain, don't assign" rule has no analogue |
| **`AddIdentityCookies` (or `AddIdentity`) having installed the security-stamp validator** | The chaining rule exists to preserve it | If nothing is installed there, the capture is harmless — `options.Events.OnValidatePrincipal` is a no-op delegate you await. Keep the chaining shape anyway; something may install one later |
| **MudBlazor**, with `MudDialogProvider` **and** `MudPopoverProvider` in the layout | The warning dialog is an inline `MudDialog` rendered by the provider | Rewrite the dialog in your own UI framework. The behavioural rules survive: close by state, never by removing the element; close before calling out; no backdrop or Escape dismissal |
| **An origin-check helper** for form-posting endpoints | The keep-alive is origin-checked | Write one. The rule is: `Referer` must start with `{scheme}://{host}`; empty `Referer` is refused |
| **A permissions mechanism** that turns constants into authorization policies | `Permissions.SecuritySettings.View` / `.Edit` | Any policy scheme works. Keep them as **two separate permissions**, and separate from a general administration right |
| **A settings/options idiom** — `AddOptions<T>().Bind(...).ValidateDataAnnotations().ValidateOnStart()` | The bounds size the cookie, so a bad combination must fail the process | Validate manually at startup and throw. Do not skip it: the failure mode is sessions ending at a time nobody chose |
| **A seeder that delivers *new* permissions to *existing* databases** | The administrator must actually hold the two new claims after an upgrade | **Fix this first.** See below |
| **A `DbContext` factory** (`IDbContextFactory<T>` or equivalent) | The provider runs inside the cookie handler, where there may be no ambient scope | Resolve a context however your application does from `HttpContext.RequestServices` |
| **A cache with explicit removal** | The policy is read per request and invalidated on save | `IMemoryCache` is sufficient |
| **A Blazor Server host** rendering interactively | The whole keep-alive rationale, and the "cannot `RefreshSignInAsync` in a circuit" constraint | On a classic MVC/Razor Pages application the keep-alive is optional and a claim would work for the preference — but the database-backed preference is still simpler |

### The seeder trap, stated once more

**A seeder guarded by "has anything been seeded?" will never deliver the two new permissions
to a database that already exists.** The role is already there, so nothing runs; nothing fails
either; no log line appears. The administrator does not hold `SecuritySettings.View`, both
screens 403 on every request, and the feature appears simply not to work after a deployment
that reported success.

The fix is to make provisioning idempotent **per item** rather than **per run**: reconcile each
role by name, and each grant by its natural key (role + permission value). See §2.20b for the
implementation. Two consequences of that shape are deliberate and worth keeping:

- **Grant-only, never revoke.** Claims the reconcile does not know about are left alone, so a
  permission an operator granted at runtime survives the next restart.
- **Log on insert, not on run.** A start that changes nothing says nothing, so a log line means
  a grant genuinely appeared — the only way to tell a no-op restart from one that repaired a
  database.

If you cannot change the seeder before shipping, grant the two claims by hand:

```sql
INSERT INTO AspNetRoleClaims (RoleId, ClaimType, ClaimValue)
SELECT Id, 'Permission', 'Permissions.SecuritySettings.View' FROM AspNetRoles WHERE Name = 'Admin';
INSERT INTO AspNetRoleClaims (RoleId, ClaimType, ClaimValue)
SELECT Id, 'Permission', 'Permissions.SecuritySettings.Edit' FROM AspNetRoles WHERE Name = 'Admin';
```

*(Composed for this document; adjust the claim type to whatever your
`ApplicationClaimTypes.Permission` constant actually is, and the role name to yours.)*

### The migration must be additive

The source repository is a project template and ships a regenerated `InitialCreate` per
provider. **Do not do that in a live project.** Generate — or hand-write — one additive
migration containing exactly two operations:

1. `CreateTable("SecurityPolicies")` — `Id` (identity), `IdleTimeoutMinutes` (int, not null),
   `CountdownSeconds` (int, not null), plus whatever audit columns your base entity carries.
2. `AddColumn<int>("IdleTimeoutMinutes", "AspNetUsers", nullable: true)`.

Nullable on the user column is load-bearing: `null` means "follow the administered policy". No
data migration is needed — `SecurityPolicies` starts empty and is seeded on first read.

### Suggested order of work

1. **Fix the seeder** (or plan the manual grant). Everything else is invisible without it.
2. Domain + EF: `SecurityPolicy`, its configuration, the `DbSet`, `ApplicationUser.IdleTimeoutMinutes`, the additive migration.
3. Settings: `IIdleTimeoutSettings`, `IdleTimeoutSettings`, the `appsettings.json` block, the options binding with `ValidateOnStart`. Confirm the process starts.
4. Provider: `IIdleTimeoutPolicyProvider`, `IdleTimeoutPolicyProvider`, registration. Port the provider tests — they are pure arithmetic and will run immediately.
5. Enforcer: `IdleTimeoutRoutes`, `IdleSessionEnforcer`, registration, and the **chained** `OnValidatePrincipal`. Port the wiring tests. **Perform the mutation check on the stamp-validator test.**
6. Keep-alive endpoint, with the origin check and the JSON bodies. Verify by hand that an expired session's ping returns a real 401 and not an HTML page.
7. Permissions, the query, the command, the validator.
8. The two screens, the menu entry, the Profile tab wiring, the gating middleware, the `reason=idle` alert.
9. The JS module and `IdleTimeoutMonitor`, rendered once inside `<Authorized>`.
10. Work the hand-test list at §2.28g end to end, at a 1-minute window with a 15-second countdown.

### Things that will not port cleanly

- **The naming-convention opt-out** in `SecurityPolicyConfiguration` is specific to this
  repository's `core`-schema convention. Drop the `ToTable` if you have no such convention.
- **`IAuditable`** and the transactional audit interceptor are this repository's. Without them
  policy changes go unrecorded — say so to whoever owns the deployment.
- **`Result<T>`, `IRequestAuthorize`, `IApplicationDbContextFactory`, `AppStrings.Localize`,
  `DialogServiceHelper`** and the `_Imports`-level injections are all local idioms. Substitute
  freely; none of them carries a decision.
- **The `Mediator` package's `ValueTask<T> Handle`** differs from MediatR's `Task<T>`.
- **`FusionCache`** is replaceable by `IMemoryCache`; only get-or-set and explicit remove
  matter.
- **`MemoryCacheTicketStore`** is not required. Without a server-side ticket store the
  last-activity stamp travels inside the encrypted cookie payload, which is equally safe;
  `ShouldRenew` then rewrites the cookie rather than the stored ticket, which is still exactly
  what is needed.

### Things that must port exactly

Reproduce these without adaptation. Each has a failure mode that is silent:

1. The **chained** `OnValidatePrincipal`, with the null-principal guard.
2. `ShouldRenew = true` on the keep-alive path, and **only** on the keep-alive path.
3. `Math.Min` for the user preference, and clamping on **read**.
4. The keep-alive answering **status codes with bodies**, not redirects.
5. The origin check on the keep-alive.
6. Closing the dialog by **binding state**, before calling out, without awaiting, with the JS
   exception caught.
7. The absolute countdown deadline, and the sign-out firing from **JavaScript**.
8. Clearing `gx:idle:signedOut` on `initialize`.
9. The cookie sized from the **maximum**, not the current policy.
10. `TotalWindow = idle + countdown`, not idle alone.

---

## 8. Corrections

The claims supplied with this brief were assembled from summaries rather than from the files.
Checked against the code, three needed correcting and two needed qualifying. Everything else
in §6 was confirmed as stated.

### 1. The cookie lifetime uses the **default** countdown, not the maximum

**Claim:** *"The cookie's `ExpireTimeSpan` is sized from the maximum any policy may reach (max
idle + countdown + grace)."*

**Code:**

```csharp
    public TimeSpan CookieLifetime => TimeSpan
        .FromMinutes(MaxIdleTimeoutMinutes + CookieGraceMinutes)
        .Add(TimeSpan.FromSeconds(DefaultCountdownSeconds));
```

The idle term is the maximum; the countdown term is `DefaultCountdownSeconds`, not
`MaxCountdownSeconds` (600). An administrator may set a countdown longer than the default —
the validator allows `[10, 600]` subject only to not exceeding the idle window in seconds — so
the widest *reachable* window (`Max` idle + 600 s) can exceed the cookie's lifetime. At the
shipped defaults: reachable 130 minutes against a 123-minute cookie.

**Impact:** small. The cookie expiring sends the browser to the login page rather than leaving
a session alive, and the JS module lands the user on `?reason=idle` regardless. But the stated
invariant does not strictly hold. Use `MaxCountdownSeconds` in that expression if you want it
to. The test `TheCookieLifetime_CoversTheWidestWindowPlusCountdownAndGrace` is named for the
invariant but asserts the arithmetic, so it would not catch this.

### 2. `ApplicationUser.IdleTimeoutMinutes` is **not** projected as a claim — the source comment says it is

**Claim (correct):** *"The per-user preference is read from the database, not a claim."*

The claim as given is right, and the implementation matches it. But the XML doc comment **on
the property itself** says the opposite:

> *"Projected onto the principal as a claim by `ApplicationUserClaimsPrincipalFactory`, so
> that the per-request principal check costs no database round-trip. Changing it therefore has
> to refresh the sign-in…"*

`ApplicationUserClaimsPrincipalFactory.GenerateClaimsAsync` adds only the `MustChangePassword`
claim; nothing projects `IdleTimeoutMinutes`, and nothing refreshes the sign-in when it
changes. The comment is stale documentation of an abandoned approach. **Do not carry that
paragraph over** — it would send a future maintainer looking for a claim that does not exist,
or worse, prompt them to "fix" the provider to read one.

### 3. The half-life is the right explanation, but it is not the reason the source records

**Claim:** *"The keep-alive path therefore sets `ShouldRenew` explicitly. Document exactly
where and how."*

**Confirmed:** `ShouldRenew = true` is set in `IdleSessionEnforcer.IsStillValidAsync`, inside
the `if (IsKeepAlive(...))` branch, immediately after writing the last-activity stamp. That is
exactly where and how.

**Qualified:** the source comment gives a different reason for the line — *"Renews the stored
ticket in place. Cheaper than re-issuing through `SignInAsync`, which with a server-side ticket
store would rotate the session key on every ping."* The half-life behaviour is real and the
line is genuinely necessary because of it, but nothing in this repository documents the
half-life as the motivation. Treat it as an accurate description of ASP.NET's behaviour rather
than as recovered intent, and keep the source comment's reason too — both are true.

### 4. No record exists of the two verifications the brief describes

**Claim:** *"prove it has teeth by mutation rather than trusting it"* (the stamp-validator
test) and *"State how it was verified (a control that idles out beside a subject that pings and
survives)"* (the sliding-expiration behaviour).

Both are the right way to verify their respective claims, and both are described in this
document as steps to perform. **Neither is evidenced in the repository** — there is no test,
comment, log or note recording that either was carried out. Rather than assert a verification I
cannot establish, §6 states them as instructions for the target repository.

### 5. The disabled-state mechanisms include one the brief did not mention

**Claim:** *"Document the mechanism used for the route (a 404…) and for the Profile tab."*

Both confirmed. There is a **third**: `MenuService`'s constructor removes the navigation entry
when the feature is off, so the menu does not offer a link straight to a 404. Worth porting —
without it the feature is inert but the menu still advertises it.

### Everything else in §6 was confirmed as stated

The chaining rule and its consequences; the cookie being the outer bound rather than the
enforcement; `min` not `max`; the countdown not being user-adjustable; clamping on read; the
12-hour duration as a backstop with invalidation as the propagation mechanism; the `0`
sentinel and why caching the absence matters; lazy seeding and its consequence for a fresh
database; installation-wide rather than per-tenant; the keep-alive's existence, its status
codes, its bodies, and its origin check; the dialog's close-by-binding rule, its deliberate
undismissability, the close-then-call-without-awaiting shape, and the caught JS exception; the
cross-tab activity sharing, the tab-id asymmetry, the absolute deadline, the two sign-out
mechanisms and the broadcast-key housekeeping; the module's location; the absent-not-disabled
screens; and both testing rules.

---

## Appendix: quick reference

**Routes**

| Path | Method | Auth | Purpose |
|---|---|---|---|
| `/account/keep-alive` | POST | `AllowAnonymous` + in-handler check + origin check | Renews the sliding cookie; 204 / 401 / 403 |
| `/account/login?reason=idle` | GET | anonymous | Landing page after an idle sign-out |
| `/pages/authentication/logout` | POST | authenticated | The application's single sign-out endpoint |
| `/system/security-settings` | GET | `Permissions.SecuritySettings.View`; 404 when disabled | Administrator screen |
| `/user/profile` | GET | authenticated | Hosts the Security tab |

**Storage keys**

| Key | Where | Contents |
|---|---|---|
| `gx:idle:lastActivity` | `localStorage` | `{ t, tab }` — most recent activity, any tab |
| `gx:idle:signedOut` | `localStorage` | `{ t, tab, reason }` — cleared on every `initialize` |
| `gx:idle:lastActivity` | authentication ticket `Properties.Items` | Unix milliseconds, invariant culture — written only on a keep-alive |

*(The last two share a string but not a namespace: one is a browser key, one is a ticket
property key. `IdleSessionEnforcer.LastActivityKey` is the constant for the ticket property.)*

**Interop names**

| Direction | Names |
|---|---|
| .NET → JS | `initialize`, `extend`, `signOut`, `touch`, `dispose` |
| JS → .NET | `OnIdleWarning(int)`, `OnCountdownTick(int)`, `OnActivityResumed()` |

**Constants**

| Constant | Value |
|---|---|
| `IdleTimeoutSettings.Key` | `"SecuritySettings:IdleTimeout"` |
| `IdleTimeoutRoutes.KeepAlive` | `"/account/keep-alive"` |
| `IdleTimeoutRoutes.LoginAfterIdle` | `"/account/login?reason=idle"` |
| `IdleSessionEnforcer.LastActivityKey` | `"gx:idle:lastActivity"` |
| `IdleTimeoutPolicyProvider.CacheKey` | `"security-policy:idle-timeout"` |
| `SecuritySettingsPageMiddleware.SecuritySettingsPath` | `"/system/security-settings"` |
| `Permissions.SecuritySettings.View` | `"Permissions.SecuritySettings.View"` |
| `Permissions.SecuritySettings.Edit` | `"Permissions.SecuritySettings.Edit"` |
