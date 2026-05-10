// Document-level keyboard listener for the admin UI's panic
// shortcut. Attaches the DOM listener once per page load and stores
// the latest .NET reference in a module variable so Blazor circuit
// reconnects (which re-create the Index component and call
// registerKeyboardShortcuts again) replace the previous reference
// instead of leaving a stale one behind.
//
// The previous attempt used Blazor's @onkeydown on a wrapper element
// with tabindex=0, but that only fires when the wrapper has focus.
// After page load focus is on body, so Esc never reached the
// handler. A document-level listener works regardless of which
// element has focus.

let _listenerAttached = false;
let _currentDotNetRef = null;

export function registerKeyboardShortcuts(dotNetRef) {
    // Always update the latest ref. On a Blazor circuit reconnect
    // the prior component disposes its DotNetObjectReference and a
    // new one calls in here; without this update the listener would
    // keep invoking the disposed ref forever (caught and dropped),
    // silently breaking the Esc shortcut until full page reload.
    _currentDotNetRef = dotNetRef;

    if (_listenerAttached) return;
    _listenerAttached = true;

    document.addEventListener('keydown', async (event) => {
        if (event.key !== 'Escape') return;

        // Don't preventDefault — let other Esc handlers (modal close,
        // future ?-help overlay, etc.) still fire if anyone adds
        // them later. The panic handler is non-destructive of other
        // Esc semantics.
        const ref = _currentDotNetRef;
        if (ref === null) return;
        try {
            await ref.invokeMethodAsync('OnEscapePressed');
        } catch (e) {
            // The ref became invalid between the read above and the
            // invoke (e.g., the component disposed during the await).
            // Drop silently — a fresh registerKeyboardShortcuts call
            // from the next component instance will replace it.
        }
    });
}

export function unregisterKeyboardShortcuts(dotNetRef) {
    // Only clear the slot if it's still pointing at *this* ref.
    // A late dispose from a previously-replaced component must not
    // clobber the newly-registered ref the user is currently using.
    if (_currentDotNetRef === dotNetRef) {
        _currentDotNetRef = null;
    }
}
