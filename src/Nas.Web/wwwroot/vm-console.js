// Console d'une machine virtuelle : noVNC (chargé depuis un CDN) branché sur le WebSocket de l'interface web.
let rfb = null;

export async function connect(element, url) {
    disconnect();
    // Le paquet npm ne contient que la version CommonJS (lib/) : jsDelivr la convertit en module avec « +esm ».
    const mod = await import('https://cdn.jsdelivr.net/npm/@novnc/novnc@1.5.0/+esm');
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
