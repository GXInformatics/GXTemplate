// gxReconnect.js - what the application does when the Blazor circuit goes down.
//
// WHY THIS IS A FILE AND NOT AN INLINE SCRIPT. It used to live inside ReconnectModal.razor, where
// nothing could execute it: bUnit renders markup and never runs script, and C# cannot host the
// logic because the logic has to keep working while the circuit - and therefore all C# - is gone.
// Moved here so ReconnectUiTests can drive the real code through the real state sequences. The
// markup and the CSS stay in the component; only the decisions live here.
//
// THE DEFECT THIS REPLACES, because the shape of it is easy to write again. The previous script
// watched three CSS classes, and on ANY of them began probing the server every two seconds and
// reloaded the page the moment the probe returned 200. The trigger was therefore not "the circuit
// is dead" but "the server is reachable" - which it almost always is - so:
//
//   * a transient drop that Blazor reconnected from perfectly reloaded the page anyway, destroying
//     whatever the user had typed;
//   * a deliberate navigation reloaded the page, because a full-page navigation tears down the
//     circuit and the server being posted to is by definition reachable. That fired on EVERY login,
//     which is a JS-built form POST in auth.js, and on every tenant switch and forceLoad;
//   * once the probe had seen the server down, a SUCCESSFUL reconnection was explicitly discarded
//     ("Ignored fake reconnection signal") and the user was held behind a modal on a working page.
//
// THE RULE NOW: a transient state is never acted on. Only a terminal one is. That single change
// also removes the login race for free, because a page that is navigating away only ever reaches
// the first transient state before it is gone.
//
// STATES. .NET 10 emits seven, not the three the old script knew - `paused` and `resume-failed`
// are the pause-and-resume feature, which is exactly the sleeping-tab case. Read off
// blazor.web.js rather than from documentation: see the pass report for the extracted state
// machine. Classes are used as the source of truth in preference to the newer
// `components-reconnect-state-changed` CustomEvent, because the class contract has been stable
// since .NET 8 and the CSS in the component already depends on it.
(function (global) {
    'use strict';

    var CLASS_TO_STATE = {
        'components-reconnect-show': 'show',
        'components-reconnect-retrying': 'retrying',
        'components-reconnect-paused': 'paused',
        'components-reconnect-hide': 'hide',
        'components-reconnect-failed': 'failed',
        'components-reconnect-resume-failed': 'resume-failed',
        'components-reconnect-rejected': 'rejected'
    };

    // Blazor removes every class before adding the next, so at most one is present. `retrying` is
    // the exception: it is ADDED alongside `show` from the second attempt onwards, so it has to be
    // read first or a retry looks like a fresh drop.
    var PRECEDENCE = [
        'components-reconnect-rejected',
        'components-reconnect-resume-failed',
        'components-reconnect-failed',
        'components-reconnect-paused',
        'components-reconnect-retrying',
        'components-reconnect-hide',
        'components-reconnect-show'
    ];

    var PROBE_INTERVAL_MS = 2000;

    function stateFromClasses(classList) {
        for (var i = 0; i < PRECEDENCE.length; i++) {
            if (classList.contains(PRECEDENCE[i])) return CLASS_TO_STATE[PRECEDENCE[i]];
        }
        return null;
    }

    /// Builds the controller over an injected environment, so the test can supply a stub document,
    /// a stub fetch and a reload it can observe instead of perform.
    function create(env) {
        var doc = env.document;
        var reload = env.reload;
        var fetchFn = env.fetch;
        var setIntervalFn = env.setInterval;
        var clearIntervalFn = env.clearInterval;
        var blazor = env.blazor || {};

        var modal = doc.getElementById('components-reconnect-modal');
        var probeHandle = null;

        // Latched when a probe actually FAILS. The old script latched the same fact and then used
        // it to override a successful reconnection; it is used here for the one thing it is
        // evidence of - that the server went away and may come back - and nothing else.
        var sawServerDown = false;
        var state = null;

        // A reload is a navigation, and a second one is at best wasted and at worst another race of
        // exactly the kind this file exists to stop. Latched so every path below can call it
        // freely: probes already in flight when the first one decides to reload cannot decide again.
        var reloading = false;

        function doReload() {
            if (reloading) return;
            reloading = true;
            stopProbe();
            reload();
        }

        function el(id) { return doc.getElementById(id); }

        function setText(title, desc) {
            var t = el('reconnect-title');
            var d = el('reconnect-desc');
            if (t) t.innerText = title;
            if (d) d.innerHTML = desc;
        }

        function showButtons(which) {
            ['reconnect-retry', 'reconnect-resume', 'reconnect-reload'].forEach(function (id) {
                var b = el(id);
                if (b) b.style.display = which.indexOf(id) >= 0 ? '' : 'none';
            });
        }

        function openModal() {
            if (modal && !modal.open && modal.showModal) modal.showModal();
        }

        function closeModal() {
            if (modal && modal.open && modal.close) modal.close();
        }

        function stopProbe() {
            if (probeHandle !== null) { clearIntervalFn(probeHandle); probeHandle = null; }
        }

        // Only started for a TERMINAL state. Its whole job is the deployment case the original
        // script was written for and got right in intent: the server is being restarted behind the
        // page, and when it comes back the page should come back with it.
        function startProbe() {
            if (probeHandle !== null) return;
            probeHandle = setIntervalFn(probe, PROBE_INTERVAL_MS);
            probe();
        }

        async function probe() {
            var ok = false;
            try {
                var res = await fetchFn('/_framework/blazor.web.js?t=' + Date.now(),
                    { method: 'HEAD', cache: 'no-store' });
                // A reverse proxy answers 502/503/504 while the app behind it restarts, and fetch
                // does not throw for those - so the status has to be read, not just the promise.
                ok = !!res && res.ok === true;
            } catch (e) {
                ok = false;
            }

            if (!ok) {
                sawServerDown = true;
                setText('Server unavailable', 'Waiting for the application to come back...');
                return;
            }

            // THE CONDITION THAT MAKES THIS SAFE. Reload only if the server was seen to be DOWN and
            // has now come back. A server that was reachable all along means the failure was on
            // this end, and reloading then would destroy what is on screen to fix nothing that
            // Retry does not fix better. That distinction is the whole difference between this and
            // the script it replaces, which reloaded on any 200.
            if (sawServerDown) {
                doReload();
            }
        }

        function onVisible() {
            if (doc.visibilityState === 'visible') retry();
        }

        function watchVisibility(on) {
            if (!doc.addEventListener || !doc.removeEventListener) return;
            if (on) doc.addEventListener('visibilitychange', onVisible);
            else doc.removeEventListener('visibilitychange', onVisible);
        }

        async function retry() {
            watchVisibility(false);
            setText('Reconnecting', 'Trying to rejoin the server...');
            showButtons([]);
            try {
                var rejoined = blazor.reconnect ? await blazor.reconnect() : false;
                if (!rejoined && blazor.resumeCircuit) rejoined = await blazor.resumeCircuit();
                if (!rejoined) doReload();
            } catch (e) {
                terminal('failed');
            }
        }

        async function resume() {
            try {
                var resumed = blazor.resumeCircuit ? await blazor.resumeCircuit() : false;
                if (!resumed) doReload();
            } catch (e) {
                terminal('resume-failed');
            }
        }

        function terminal(kind) {
            openModal();
            if (kind === 'resume-failed') {
                setText('Session could not be resumed',
                    'Resume the session, or reload to start again.<br />' +
                    '<span class="sub-text">Anything you have typed is still on this page.</span>');
                showButtons(['reconnect-resume', 'reconnect-reload']);
            } else {
                setText('Could not rejoin the server',
                    'Retry, or reload to start again.<br />' +
                    '<span class="sub-text">Anything you have typed is still on this page.</span>');
                showButtons(['reconnect-retry', 'reconnect-reload']);
            }
            watchVisibility(true);
            startProbe();
        }

        function onState(next) {
            if (next === null || next === state) return;
            state = next;

            switch (next) {
                // TRANSIENT. Blazor is retrying and will very probably succeed. Say so and do
                // nothing else - no probe, no reload, no navigation. This is the case that was
                // destroying work, and the case a page that is navigating away lands in.
                case 'show':
                case 'retrying':
                    stopProbe();
                    watchVisibility(false);
                    openModal();
                    showButtons([]);
                    setText('Connection lost', 'Reconnecting...');
                    break;

                // A DELIBERATE pause, not a failure: the server holds the circuit and can hand it
                // back. Offer that rather than deciding for the user.
                case 'paused':
                    stopProbe();
                    watchVisibility(false);
                    openModal();
                    showButtons(['reconnect-resume']);
                    setText('Session paused', 'This session was paused by the server.');
                    break;

                // RECOVERED. Everything the user had is still there, because nothing was reloaded.
                case 'hide':
                    stopProbe();
                    watchVisibility(false);
                    sawServerDown = false;
                    showButtons([]);
                    closeModal();
                    break;

                case 'failed':
                case 'resume-failed':
                    terminal(next);
                    break;

                // The server has refused the circuit outright: its state is gone and no retry can
                // bring it back. Blazor's own default display reloads here and nothing else, which
                // is the behaviour being matched.
                case 'rejected':
                    watchVisibility(false);
                    doReload();
                    break;
            }
        }

        function wire() {
            if (!modal) return;
            var retryBtn = el('reconnect-retry');
            var resumeBtn = el('reconnect-resume');
            var reloadBtn = el('reconnect-reload');
            if (retryBtn && retryBtn.addEventListener) retryBtn.addEventListener('click', retry);
            if (resumeBtn && resumeBtn.addEventListener) resumeBtn.addEventListener('click', resume);
            if (reloadBtn && reloadBtn.addEventListener) reloadBtn.addEventListener('click', doReload);
            showButtons([]);
        }

        wire();

        return {
            onState: onState,
            readFromClasses: function () {
                if (modal) onState(stateFromClasses(modal.classList));
            },
            get state() { return state; },
            get probing() { return probeHandle !== null; }
        };
    }

    function start(global) {
        var doc = global.document;
        var modal = doc.getElementById('components-reconnect-modal');
        if (!modal) { global.setTimeout(function () { start(global); }, 50); return; }
        if (global.gxReconnectStarted) return;
        global.gxReconnectStarted = true;

        var controller = create({
            document: doc,
            reload: function () { global.location.reload(); },
            fetch: function (u, o) { return global.fetch(u, o); },
            setInterval: function (f, ms) { return global.setInterval(f, ms); },
            clearInterval: function (h) { return global.clearInterval(h); },
            blazor: {
                reconnect: function () { return global.Blazor && global.Blazor.reconnect(); },
                resumeCircuit: function () { return global.Blazor && global.Blazor.resumeCircuit(); }
            }
        });

        new global.MutationObserver(function () { controller.readFromClasses(); })
            .observe(modal, { attributes: true, attributeFilter: ['class'] });

        controller.readFromClasses();
        global.gxReconnectController = controller;
    }

    global.GxReconnect = { create: create, start: start, stateFromClasses: stateFromClasses };

    // The test sets this before executing the file; the browser never does.
    if (!global.gxReconnectNoAutoStart && typeof global.document !== 'undefined') {
        if (global.document.readyState === 'loading') {
            global.document.addEventListener('DOMContentLoaded', function () { start(global); });
        } else {
            start(global);
        }
    }
})(typeof globalThis !== 'undefined' ? globalThis : this);
