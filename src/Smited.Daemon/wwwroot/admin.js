// Document-level keyboard listener for the admin UI's panic
// shortcut. Attaches once per page load; fires the supplied .NET
// reference's invokeMethodAsync with the key name when an Escape
// key is pressed anywhere on the document.
//
// The previous attempt used Blazor's @onkeydown on a wrapper
// element with tabindex=0, but that only fires when the wrapper
// has focus. After page load focus is on body, so Esc never
// reached the handler. A document-level listener works regardless
// of which element has focus.

let _registered = false;

export function registerKeyboardShortcuts(dotNetRef) {
    if (_registered) return;
    _registered = true;

    document.addEventListener('keydown', async (event) => {
        if (event.key !== 'Escape') return;

        // Don't preventDefault — let other Esc handlers (modal close,
        // future ?-help overlay, etc.) still fire if anyone adds
        // them later. The panic handler is non-destructive of other
        // Esc semantics.
        try {
            await dotNetRef.invokeMethodAsync('OnEscapePressed');
        } catch (e) {
            // Component disposed; the .NET ref becomes invalid
            // when the circuit disposes the component. Silently
            // drop the event — the next page load registers a
            // fresh listener anyway.
        }
    });
}
