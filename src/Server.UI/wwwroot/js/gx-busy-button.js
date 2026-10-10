// Pass 52: the busy state of a GxSubmitButton ([data-gx-busy-button]) when its form really submits: a native post
// (static server rendering) or a Blazor enhanced post. On an interactive page the circuit handles the submit and
// Blazor prevents its default; nothing is submitted, the component shows the busy state itself, and this script
// leaves the button alone.
//
// Busy means: the spinner shows, the busy label replaces the label, aria-busy="true", and further submits of the form
// are blocked. The button is NOT given the disabled attribute: a disabled submitter is left out of the form data, so
// the button's own name=value would be lost, and (pass 52) MudBlazor draws :disabled grey. Instead it gets
// aria-disabled and the component's busy class, gx-busy (GxSubmitButton.BusyClass, styled in wwwroot/css/app.css:
// its own colour at reduced opacity, no pointer events), and a capture-phase listener blocks any further submit.
//
// Restored: on pageshow from the back/forward cache, on Blazor's enhancedload (the enhanced post came back, e.g. with a
// validation error), and after 30 s without navigation, so the user can retry.
(function () {
    'use strict';

    var SELECTOR = '[data-gx-busy-button]';
    var BUSY_CLASS = 'gx-busy'; // GxSubmitButton.BusyClass
    var RESTORE_AFTER_MS = 30000;
    var busyButtons = new Set();
    var submitting = null; // the submit event being dispatched, for Blazor's enhancednavigationstart
    var hooked = false;

    function isEnhanced(form) {
        var value = form.getAttribute('data-enhance');
        return value === '' || (value !== null && value.toLowerCase() === 'true');
    }

    function busyButtonOf(form, submitter) {
        if (submitter && submitter.matches(SELECTOR)) return submitter;
        return submitter ? null : form.querySelector(SELECTOR);
    }

    function setBusy(form, button) {
        if (busyButtons.has(button)) return;
        busyButtons.add(button);
        var text = button.querySelector('.gx-busy-text');
        var spinner = button.querySelector('.gx-busy-spinner');
        var state = { form: form, label: text ? text.textContent : null, addedClass: false, timer: 0 };
        button.gxBusy = state;
        form.setAttribute('data-gx-busy', '');
        if (spinner) spinner.hidden = false;
        if (text && button.hasAttribute('data-gx-busy-label')) text.textContent = button.getAttribute('data-gx-busy-label');
        button.setAttribute('aria-busy', 'true');
        button.setAttribute('aria-disabled', 'true');
        if (!button.classList.contains(BUSY_CLASS)) {
            button.classList.add(BUSY_CLASS);
            state.addedClass = true;
        }
        state.timer = setTimeout(function () { restore(button); }, RESTORE_AFTER_MS);
    }

    function restore(button) {
        var state = button.gxBusy;
        if (!state) return;
        clearTimeout(state.timer);
        busyButtons.delete(button);
        button.gxBusy = null;
        state.form.removeAttribute('data-gx-busy');
        var text = button.querySelector('.gx-busy-text');
        var spinner = button.querySelector('.gx-busy-spinner');
        if (spinner) spinner.hidden = true;
        if (text && state.label !== null) text.textContent = state.label;
        button.removeAttribute('aria-busy');
        button.removeAttribute('aria-disabled');
        if (state.addedClass) button.classList.remove(BUSY_CLASS);
    }

    function restoreAll() {
        Array.from(busyButtons).forEach(restore);
    }

    // Blazor's enhanced post calls preventDefault and then starts its fetch inside the same submit event, announcing it
    // with enhancednavigationstart. That is how a real enhanced submit is told from one a validator cancelled.
    function hookBlazor() {
        if (hooked || !window.Blazor || typeof window.Blazor.addEventListener !== 'function') return;
        hooked = true;
        window.Blazor.addEventListener('enhancednavigationstart', function () {
            if (submitting) {
                var button = busyButtonOf(submitting.form, submitting.submitter);
                if (button) setBusy(submitting.form, button);
            }
        });
        window.Blazor.addEventListener('enhancedload', restoreAll);
    }

    // Capture phase on window: runs before any other submit listener. Blocks a second submit of a busy form.
    window.addEventListener('submit', function (e) {
        var form = e.target;
        if (!(form instanceof HTMLFormElement)) return;
        if (form.hasAttribute('data-gx-busy')) {
            e.preventDefault();
            e.stopImmediatePropagation();
            return;
        }
        hookBlazor();
        var entry = { form: form, submitter: e.submitter || null };
        submitting = entry;
        // Blazor announces an enhanced post during this dispatch. If a listener stops the event before the bubble
        // listener below runs, this still forgets it.
        setTimeout(function () { if (submitting === entry) submitting = null; }, 0);
    }, true);

    // Bubble phase on window: runs after every other submit listener, so defaultPrevented is final.
    window.addEventListener('submit', function (e) {
        var current = submitting;
        submitting = null;
        var form = e.target;
        if (!current || current.form !== form) return;
        if (e.defaultPrevented) return; // a script, an interactive handler, or an enhanced post (handled above)
        var button = busyButtonOf(form, current.submitter);
        if (button) setBusy(form, button);
    });

    window.addEventListener('pageshow', function (e) {
        if (e.persisted) restoreAll();
    });

    hookBlazor();
    document.addEventListener('DOMContentLoaded', hookBlazor);
})();
