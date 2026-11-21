// Aide à la saisie d'un compose.yml dans une zone de texte ordinaire : retrait conservé à Entrée, un niveau de plus après « : »,
// listes « - » prolongées, Tab / Maj+Tab pour décaler (le YAML interdit les tabulations : ce sont des espaces), Retour arrière
// qui efface un niveau de retrait. Échap puis Tab laisse sortir du champ au clavier.
//
// edit() est pure : elle reçoit le texte, la sélection et la touche, et renvoie la modification à faire
// ({ from, to, text, start, end }) ou null pour laisser faire le navigateur. Elle est testable sans page.

const INDENT = 2;

const lineStartOf = (value, pos) => value.lastIndexOf('\n', pos - 1) + 1;
const lineEndOf = (value, pos) => { const i = value.indexOf('\n', pos); return i < 0 ? value.length : i; };
const leadingSpaces = line => line.length - line.trimStart().length;

export function edit(value, start, end, key, shift = false) {
    if (key === 'Enter') return enter(value, start, end);
    if (key === 'Tab') return shift ? outdent(value, start, end) : indent(value, start, end);
    if (key === 'Backspace' && !shift) return backspace(value, start, end);
    return null;
}

function enter(value, start, end) {
    const lineStart = lineStartOf(value, start);
    const before = value.slice(lineStart, start);           // la ligne jusqu'au curseur
    const atEnd = value.slice(end, lineEndOf(value, end)).trim() === ''; // rien d'autre après le curseur
    const pad = leadingSpaces(before);
    const body = before.trim();

    // Un « - » seul : Entrée le retire et sort de la liste.
    if (atEnd && /^-$/.test(body) && start === end) {
        const text = ' '.repeat(pad);
        return { from: lineStart, to: end, text, start: lineStart + text.length, end: lineStart + text.length };
    }

    let next = pad;
    let prefix = '';
    const item = /^(\s*)-\s+(.*)$/.exec(before);
    const opensBlock = atEnd && /:\s*$/.test(before.replace(/\s+#.*$/, '')) && !/^\s*#/.test(before);
    if (opensBlock) next = (item ? item[1].length + 2 : pad) + INDENT;   // « services: » ou « - name: » : un niveau de plus
    else if (atEnd && item) { next = item[1].length; prefix = '- '; }     // la ligne suivante est un nouvel élément de la liste

    const text = '\n' + ' '.repeat(next) + prefix;
    return { from: start, to: end, text, start: start + text.length, end: start + text.length };
}

function indent(value, start, end) {
    if (value.slice(start, end).includes('\n')) return shiftLines(value, start, end, +1);
    const col = start - lineStartOf(value, start);
    const text = ' '.repeat(INDENT - (col % INDENT)); // jusqu'au prochain multiple de 2
    return { from: start, to: end, text, start: start + text.length, end: start + text.length };
}

function outdent(value, start, end) {
    return shiftLines(value, start, end, -1);
}

// Décale toutes les lignes touchées par la sélection (ou la ligne du curseur) d'un niveau, vers la droite (+1) ou la gauche (-1).
function shiftLines(value, start, end, direction) {
    const from = lineStartOf(value, start);
    // Une sélection qui se termine tout au début d'une ligne ne touche pas cette ligne.
    const last = end > start && value[end - 1] === '\n' ? end - 1 : end;
    const to = lineEndOf(value, last);
    const lines = value.slice(from, to).split('\n');
    let firstDelta = 0, total = 0;
    const out = lines.map((line, i) => {
        let delta;
        if (direction > 0) { delta = line.trim() === '' ? 0 : INDENT; line = ' '.repeat(delta) + line; }
        else { delta = -Math.min(INDENT, leadingSpaces(line)); line = line.slice(-delta); }
        if (i === 0) firstDelta = delta;
        total += delta;
        return line;
    });
    const text = out.join('\n');
    if (text === value.slice(from, to)) return { from, to, text, start, end };
    // Le début de la sélection suit sa ligne ; la fin suit toutes les lignes décalées.
    return { from, to, text, start: Math.max(from, start + firstDelta), end: Math.max(from, end + total) };
}

// Retour arrière dans le retrait d'une ligne : efface jusqu'au niveau précédent au lieu d'un seul espace.
function backspace(value, start, end) {
    if (start !== end) return null;
    const lineStart = lineStartOf(value, start);
    const col = start - lineStart;
    if (col < 2 || value.slice(lineStart, start).trim() !== '') return null;
    const n = col % INDENT === 0 ? INDENT : col % INDENT;
    return { from: start - n, to: start, text: '', start: start - n, end: start - n };
}

// ---- Branchement sur la zone de texte

const handlers = new WeakMap();

function apply(textarea, change) {
    textarea.setSelectionRange(change.from, change.to);
    // insertText garde l'historique d'annulation (Ctrl+Z) du navigateur ; repli sur setRangeText s'il est refusé.
    if (!document.execCommand('insertText', false, change.text)) {
        textarea.setRangeText(change.text, change.from, change.to, 'end');
        textarea.dispatchEvent(new Event('input', { bubbles: true }));
    }
    textarea.setSelectionRange(change.start, change.end);
}

export function attach(id) {
    const textarea = document.getElementById(id);
    if (!textarea || handlers.has(textarea)) return;
    let free = false; // Échap vient d'être pressé : le prochain Tab quitte le champ
    const onKeyDown = e => {
        if (e.isComposing || e.ctrlKey || e.metaKey || e.altKey) return;
        if (e.key === 'Escape') { free = true; return; }
        const release = free && e.key === 'Tab';
        if (e.key !== 'Shift') free = false;
        if (release) return;
        const change = edit(textarea.value, textarea.selectionStart, textarea.selectionEnd, e.key, e.shiftKey);
        if (!change) return;
        e.preventDefault();
        apply(textarea, change);
    };
    const onBlur = () => { free = false; };
    textarea.addEventListener('keydown', onKeyDown);
    textarea.addEventListener('blur', onBlur);
    handlers.set(textarea, { onKeyDown, onBlur });
}

export function detach(id) {
    const textarea = document.getElementById(id);
    const h = textarea && handlers.get(textarea);
    if (!h) return;
    textarea.removeEventListener('keydown', h.onKeyDown);
    textarea.removeEventListener('blur', h.onBlur);
    handlers.delete(textarea);
}
