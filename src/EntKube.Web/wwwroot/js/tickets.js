// Small helpers for the ticket views.

// Copies text to the clipboard; false when the browser refuses (no secure context, or no
// permission), so the caller can say so instead of claiming it worked.
export async function copy(text) {
    try {
        await navigator.clipboard.writeText(text);
        return true;
    } catch {
        return false;
    }
}
