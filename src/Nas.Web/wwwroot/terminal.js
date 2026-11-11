// Terminal de la machine : xterm.js (hébergé dans lib/xterm) branché sur le WebSocket de l'interface web.
let term = null, socket = null, observer = null;

function load(src) {
    return new Promise((resolve, reject) => {
        if (document.querySelector(`script[src="${src}"]`)) return resolve();
        const s = document.createElement('script');
        s.src = src; s.onload = () => resolve(); s.onerror = () => reject(new Error(src));
        document.head.appendChild(s);
    });
}

function css(href) {
    if (document.querySelector(`link[href="${href}"]`)) return;
    const l = document.createElement('link');
    l.rel = 'stylesheet'; l.href = href;
    document.head.appendChild(l);
}

export async function connect(element, url) {
    disconnect();
    css('/lib/xterm/xterm.css');
    await load('/lib/xterm/xterm.js');
    await load('/lib/xterm/addon-fit.js');
    term = new window.Terminal({ cursorBlink: true, fontSize: 14, fontFamily: 'ui-monospace, Menlo, Consolas, "DejaVu Sans Mono", monospace', scrollback: 5000 });
    const fit = new window.FitAddon.FitAddon();
    term.loadAddon(fit);
    term.open(element);
    fit.fit();
    term.focus();

    const enc = new TextEncoder();
    return new Promise(resolve => {
        socket = new WebSocket(`${url}?cols=${term.cols}&rows=${term.rows}`);
        socket.binaryType = 'arraybuffer';
        let opened = false;
        socket.onopen = () => { opened = true; resolve('connected'); };
        socket.onmessage = e => term.write(new Uint8Array(e.data));
        socket.onclose = () => { term?.write('\r\n\x1b[2m[connection closed]\x1b[0m\r\n'); resolve(opened ? 'closed' : 'failed'); };
        term.onData(d => { if (socket?.readyState === 1) socket.send(enc.encode(d)); });
        term.onResize(({ cols, rows }) => { if (socket?.readyState === 1) socket.send(JSON.stringify({ type: 'resize', cols, rows })); });
        observer = new ResizeObserver(() => { try { fit.fit(); } catch { } });
        observer.observe(element);
    });
}

export function disconnect() {
    if (observer) { observer.disconnect(); observer = null; }
    if (socket) { try { socket.close(); } catch { } socket = null; }
    if (term) { try { term.dispose(); } catch { } term = null; }
}

export function focus() { term?.focus(); }
