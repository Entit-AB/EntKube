// Browser end of a VM console. Both kinds connect to EntKube's /vm-console endpoint on this same origin,
// which bridges to KubeVirt. The libraries are self-hosted under wwwroot/lib (absolute paths so they
// resolve on deep routes), and loaded only when a console is opened.

const sessions = new Map();
let nextId = 0;

function socketUrl(path) {
    const scheme = location.protocol === 'https:' ? 'wss:' : 'ws:';
    return `${scheme}//${location.host}${path}`;
}

function ensureStylesheet(href) {
    if (document.querySelector(`link[href="${href}"]`)) return;
    const link = document.createElement('link');
    link.rel = 'stylesheet';
    link.href = href;
    document.head.appendChild(link);
}

function report(dotNetRef, state) {
    // The component may already be gone when a socket closes behind it.
    dotNetRef.invokeMethodAsync('OnConsoleState', state).catch(() => { });
}

export async function openVnc(container, path, dotNetRef) {
    const { default: RFB } = await import('/lib/novnc/core/rfb.js');

    container.replaceChildren();
    // No subprotocol: EntKube's endpoint accepts the browser without one and speaks KubeVirt's
    // plain.kubevirt.io upstream itself.
    const rfb = new RFB(container, socketUrl(path), { wsProtocols: [] });
    rfb.scaleViewport = true;
    rfb.background = '#000';

    let connected = false;
    rfb.addEventListener('connect', () => { connected = true; report(dotNetRef, 'connected'); });
    rfb.addEventListener('disconnect', e => {
        // A refusal before the upgrade (no access, VM not running) reaches the browser only as an
        // unclean close with no reason, so "never connected" is the most it can honestly say.
        report(dotNetRef, connected ? (e.detail.clean ? 'closed' : 'lost') : 'refused');
    });

    const id = ++nextId;
    sessions.set(id, { close: () => rfb.disconnect(), ctrlAltDel: () => rfb.sendCtrlAltDel() });
    return id;
}

export async function openSerial(container, path, dotNetRef) {
    const [{ Terminal }, { FitAddon }] = await Promise.all([
        import('/lib/xterm/xterm.mjs'),
        import('/lib/xterm/addon-fit.mjs'),
    ]);
    ensureStylesheet('/lib/xterm/xterm.css');

    container.replaceChildren();
    const term = new Terminal({ cursorBlink: true, fontSize: 13, scrollback: 5000 });
    const fit = new FitAddon();
    term.loadAddon(fit);
    term.open(container);
    fit.fit();

    const ws = new WebSocket(socketUrl(path));
    ws.binaryType = 'arraybuffer';
    const encoder = new TextEncoder();

    let connected = false;
    ws.onopen = () => {
        connected = true;
        report(dotNetRef, 'connected');
        // A serial console prints nothing until something is typed; a newline brings up the prompt.
        term.writeln('\x1b[2m[connected — press Enter for a prompt]\x1b[0m');
        term.focus();
    };
    ws.onmessage = e => term.write(new Uint8Array(e.data));
    ws.onclose = e => {
        term.writeln('\r\n\x1b[2m[console closed]\x1b[0m');
        report(dotNetRef, connected ? (e.wasClean ? 'closed' : 'lost') : 'refused');
    };

    const input = term.onData(data => {
        if (ws.readyState === WebSocket.OPEN) ws.send(encoder.encode(data));
    });
    const onResize = () => fit.fit();
    window.addEventListener('resize', onResize);

    const id = ++nextId;
    sessions.set(id, {
        close: () => {
            input.dispose();
            window.removeEventListener('resize', onResize);
            if (ws.readyState <= WebSocket.OPEN) ws.close();
            term.dispose();
        },
    });
    return id;
}

export function sendCtrlAltDel(id) {
    sessions.get(id)?.ctrlAltDel?.();
}

export function close(id) {
    const session = sessions.get(id);
    if (!session) return;
    sessions.delete(id);
    session.close();
}
