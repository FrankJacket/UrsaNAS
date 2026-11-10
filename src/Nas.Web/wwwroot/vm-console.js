// Console d'une machine virtuelle : noVNC (hébergé dans lib/novnc) branché sur le WebSocket de l'interface web.
let rfb = null;

export async function connect(element, url) {
    disconnect();
    const mod = await import('./lib/novnc/novnc.esm.js');
    const RFB = mod.default?.default ?? mod.default;
    rfb = new RFB(element, url);
    rfb.scaleViewport = true;
    rfb.resizeSession = false;
    return new Promise(resolve => {
        rfb.addEventListener('connect', () => resolve('connected'));
        rfb.addEventListener('disconnect', e => resolve(e.detail.clean ? 'closed' : 'failed'));
    });
}

export function disconnect() {
    if (rfb) { try { rfb.disconnect(); } catch { } rfb = null; }
}

export function sendCtrlAltDel() { if (rfb) rfb.sendCtrlAltDel(); }
