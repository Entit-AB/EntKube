// The bell's sound (docs/helpdesk.md, phase 9): a short two-note chime when new notifications
// arrive, if this browser has it switched on. Made with Web Audio, so there is no sound file to
// ship or to fail to load. Off by default, and kept per browser, like the keyboard shortcuts.

const KEY = 'entkube.notify.sound';
let context = null;

export function enabled() {
    try { return localStorage.getItem(KEY) === '1'; } catch { return false; }
}

// Called from the click on the switch: a user gesture, which is what lets a browser start audio.
export function setEnabled(on) {
    try { on ? localStorage.setItem(KEY, '1') : localStorage.removeItem(KEY); } catch { /* storage off */ }
    if (on) { audio(); chime(); }
}

function audio() {
    const Ctx = window.AudioContext || window.webkitAudioContext;
    if (!Ctx) return null;
    context = context || new Ctx();
    if (context.state === 'suspended') context.resume().catch(() => {});
    return context;
}

// A browser may refuse audio until the page has been clicked; the first click wakes it.
document.addEventListener('pointerdown', () => { if (enabled()) audio(); }, { once: true, capture: true });

export function chime() {
    if (!enabled()) return false;
    const ctx = audio();
    if (!ctx) return false;

    const start = ctx.currentTime;
    [[880, 0], [1320, 0.12]].forEach(([frequency, offset]) => {
        const tone = ctx.createOscillator();
        const gain = ctx.createGain();
        tone.type = 'sine';
        tone.frequency.value = frequency;
        gain.gain.setValueAtTime(0.0001, start + offset);
        gain.gain.exponentialRampToValueAtTime(0.15, start + offset + 0.02);
        gain.gain.exponentialRampToValueAtTime(0.0001, start + offset + 0.25);
        tone.connect(gain).connect(ctx.destination);
        tone.start(start + offset);
        tone.stop(start + offset + 0.3);
    });
    return true;
}
