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
