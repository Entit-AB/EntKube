// The highlighter (docs/helpdesk.md, phase 6): marks passages agents chose in ticket entries.
//
// Painted with the CSS Custom Highlight API, which colours ranges of text WITHOUT changing the
// page's markup. That matters here: Blazor owns these elements and diffs them, and wrapping text in
// <mark> would leave it diffing a DOM it no longer recognises. Browsers without the API simply show
// no colour; the highlights are still listed on the ticket.

const COLORS = ['Yellow', 'Green', 'Blue', 'Pink', 'Purple'];

// The selection, if it lies inside one entry: { entry, text }. Null otherwise.
export function selection() {
    const s = window.getSelection();
    if (!s || s.isCollapsed || s.rangeCount === 0) return null;

    const range = s.getRangeAt(0);
    const at = n => (n.nodeType === Node.ELEMENT_NODE ? n : n.parentElement)?.closest('[data-entry]');
    const start = at(range.startContainer), end = at(range.endContainer);
    if (!start || start !== end) return null;

    const text = s.toString().trim();
    return text ? { entry: start.dataset.entry, text } : null;
}

// Finds `text` in the element's text, ignoring whitespace on both sides (a selection across
// paragraphs carries line breaks the page's text nodes do not), and returns a Range over it.
function find(element, text) {
    const needle = text.replace(/\s+/g, '');
    if (!needle) return null;

    const chars = []; // [node, offset] for every non-whitespace character, in order
    const walker = document.createTreeWalker(element, NodeFilter.SHOW_TEXT);
    let hay = '';
    for (let node = walker.nextNode(); node; node = walker.nextNode()) {
        const value = node.nodeValue;
        for (let i = 0; i < value.length; i++) {
            if (!/\s/.test(value[i])) { chars.push([node, i]); hay += value[i]; }
        }
    }

    const at = hay.indexOf(needle);
    if (at < 0) return null;

    const range = document.createRange();
    const [startNode, startOffset] = chars[at];
    const [endNode, endOffset] = chars[at + needle.length - 1];
    range.setStart(startNode, startOffset);
    range.setEnd(endNode, endOffset + 1);
    return range;
}

// Paints every highlight: items are { entry, text, color }. Returns false where the browser
// cannot (no CSS.highlights), so the caller can say so.
export function paint(items) {
    if (!window.CSS || !CSS.highlights || typeof Highlight === 'undefined') return false;

    const byColor = Object.fromEntries(COLORS.map(c => [c, []]));
    for (const { entry, text, color } of items || []) {
        const element = document.querySelector(`[data-entry="${CSS.escape(entry)}"]`);
        const range = element && find(element, text);
        if (range && byColor[color]) byColor[color].push(range);
    }

    for (const c of COLORS) {
        const name = `entkube-${c.toLowerCase()}`;
        if (byColor[c].length) CSS.highlights.set(name, new Highlight(...byColor[c]));
        else CSS.highlights.delete(name);
    }

    return true;
}
