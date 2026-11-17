// Keyboard shortcuts and ticket tabs for the support queue (docs/helpdesk.md, phase 6).
//
// Shortcuts click whatever is on the page marked for them — data-shortcut="r" on the Reply
// button — so a shortcut can never do anything the screen does not offer at that moment. They are
// ignored while typing, and can be turned off per browser (Zammad keeps this per browser too).

const OFF_KEY = 'entkube.shortcuts.off';
const TABS_KEY = 'entkube.ticket-tabs';
const MAX_TABS = 30;
let installed = false;
let dots = 0, dotTimer = null;

function typing(e) {
    const t = e.target;
    return t && (t.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(t.tagName));
}

// Storage can be blocked (private windows, policies); then shortcuts are simply on.
function isOff() {
    try { return localStorage.getItem(OFF_KEY) === '1'; } catch { return false; }
}

function setOff(off) {
    try { off ? localStorage.setItem(OFF_KEY, '1') : localStorage.removeItem(OFF_KEY); } catch { /* storage off */ }
}

function click(selector) {
    const el = document.querySelector(selector);
    if (el && !el.disabled) { el.click(); return true; }
    return false;
}

const help = [
    ['?', 'Show or hide this list'],
    ['s', 'Search tickets'],
    ['n', 'New ticket'],
    ['r', 'Reply on the open ticket'],
    ['x', 'Add a note'],
    ['Shift + C', 'Resolve an open ticket, close a resolved one'],
    ['.  ..  ...', 'Copy the number; with the title; with the link'],
    ['Shift + ← / →', 'Previous / next ticket tab'],
    ['Esc', 'Back to the queue'],
];

function toggleHelp() {
    const existing = document.getElementById('entkube-shortcut-help');
    if (existing) { existing.remove(); return; }

    const box = document.createElement('div');
    box.id = 'entkube-shortcut-help';
    box.className = 'position-fixed top-50 start-50 translate-middle bg-body border rounded shadow p-3 small';
    box.style.zIndex = 2000;
    box.style.minWidth = '22rem';

    const title = document.createElement('div');
    title.className = 'fw-semibold mb-2';
    title.textContent = 'Keyboard shortcuts';
    box.appendChild(title);

    const table = document.createElement('table');
    table.className = 'table table-sm mb-2';
    for (const [key, what] of help) {
        const row = table.insertRow();
        const k = row.insertCell(); k.innerHTML = ''; const kbd = document.createElement('kbd'); kbd.textContent = key; k.appendChild(kbd);
        row.insertCell().textContent = what;
    }
    box.appendChild(table);

    const off = document.createElement('label');
    const check = document.createElement('input');
    check.type = 'checkbox';
    check.className = 'form-check-input me-1';
    check.checked = !isOff();
    check.onchange = () => setOff(!check.checked);
    off.appendChild(check);
    off.append('Shortcuts on in this browser');
    box.appendChild(off);

    document.body.appendChild(box);
}

function copyLevel(level) {
    click(`[data-shortcut-copy="${level}"]`);
}

export function install() {
    if (installed) return;
    installed = true;

    document.addEventListener('keydown', e => {
        if (e.ctrlKey || e.metaKey || e.altKey) return;
        if (e.key === 'Escape' && document.getElementById('entkube-shortcut-help')) { toggleHelp(); return; }
        if (typing(e)) return;
        if (e.key === '?') { toggleHelp(); e.preventDefault(); return; }
        if (isOff()) return;
        // Esc belongs to an open dialog or menu first; it must not navigate away from under one.
        if (e.key === 'Escape' && document.querySelector('.modal.show, .dropdown-menu.show')) return;

        if (e.key === '.') {
            // ".", ".." and "..." within half a second: Zammad's copy-number, -title, -link.
            dots = Math.min(3, dots + 1);
            clearTimeout(dotTimer);
            dotTimer = setTimeout(() => { copyLevel(dots); dots = 0; }, 450);
            e.preventDefault();
            return;
        }

        const handled =
            (e.key === 's' && (() => { const el = document.querySelector('[data-shortcut-focus="s"]'); if (el) { el.focus(); return true; } return false; })()) ||
            (e.key === 'n' && click('[data-shortcut="n"]')) ||
            (e.key === 'r' && click('[data-shortcut="r"]')) ||
            (e.key === 'x' && click('[data-shortcut="x"]')) ||
            (e.key === 'C' && e.shiftKey && click('[data-shortcut="C"]')) ||
            (e.key === 'Escape' && click('[data-shortcut="Escape"]')) ||
            (e.key === 'ArrowLeft' && e.shiftKey && click('[data-shortcut="tab-prev"]')) ||
            (e.key === 'ArrowRight' && e.shiftKey && click('[data-shortcut="tab-next"]'));

        if (handled) e.preventDefault();
    });
}

// ── Ticket tabs: the tickets opened in this browser, newest last, at most 30 ──

function readTabs(scope) {
    try { return JSON.parse(localStorage.getItem(`${TABS_KEY}.${scope}`) || '[]'); } catch { return []; }
}

function writeTabs(scope, tabs) {
    try { localStorage.setItem(`${TABS_KEY}.${scope}`, JSON.stringify(tabs)); } catch { /* storage off */ }
}

export function tabsAdd(scope, number, title) {
    // An open tab keeps its place (its title refreshed); a new one goes last, the oldest dropping off.
    let tabs = readTabs(scope);
    const at = tabs.findIndex(t => t.number === number);
    if (at >= 0) tabs[at] = { number, title }; else tabs.push({ number, title });
    if (tabs.length > MAX_TABS) tabs = tabs.slice(tabs.length - MAX_TABS);
    writeTabs(scope, tabs);
    return tabs;
}

export function tabsRemove(scope, number) {
    const tabs = readTabs(scope).filter(t => t.number !== number);
    writeTabs(scope, tabs);
    return tabs;
}

export function tabsList(scope) {
    return readTabs(scope);
}
