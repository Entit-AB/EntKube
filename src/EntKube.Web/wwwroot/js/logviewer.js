// Small helpers for the customer log viewer: keep the newest line in view while
// following, and let customers copy or download the currently shown logs.

export function scrollToBottom(el) {
    if (el) el.scrollTop = el.scrollHeight;
}

export async function copy(text) {
    try {
        await navigator.clipboard.writeText(text);
        return true;
    } catch {
        return false;
    }
}

export function download(filename, text) {
    const blob = new Blob([text], { type: 'text/plain;charset=utf-8' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = filename;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    URL.revokeObjectURL(url);
}
