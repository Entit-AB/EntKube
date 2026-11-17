// The ticket mail composer: Quill (wwwroot/lib/quill, vendored), loaded only when a composer
// opens. Whatever this produces is sanitised again on the server (RichText) before it is stored
// or sent, so nothing here is a security boundary — it is a convenience.

const editors = new Map();
let loading = null;

function ensureQuill() {
    if (window.Quill) return Promise.resolve();
    if (loading) return loading;

    loading = new Promise((resolve, reject) => {
        const css = document.createElement('link');
        css.rel = 'stylesheet';
        css.href = '/lib/quill/quill.snow.css';
        document.head.appendChild(css);

        const script = document.createElement('script');
        script.src = '/lib/quill/quill.js';
        script.onload = () => resolve();
        script.onerror = () => reject(new Error('The editor could not be loaded.'));
        document.head.appendChild(script);
    });

    return loading;
}

// "::word" just before the cursor: the text-module trigger. "??words": the knowledge-base one.
const trigger = /::([^\s:]{0,30})$/;
const kbTrigger = /\?\?([^?\n]{0,40})$/;

function triggerBefore(quill, index, pattern = trigger) {
    const start = Math.max(0, index - 50);
    const m = pattern.exec(quill.getText(start, index - start));
    return m ? { query: m[1], length: m[0].length } : null;
}

export async function create(element, dotnet, placeholder) {
    await ensureQuill();

    const quill = new window.Quill(element, {
        theme: 'snow',
        modules: {
            toolbar: [
                ['bold', 'italic', 'underline', 'strike'],
                [{ list: 'ordered' }, { list: 'bullet' }],
                ['blockquote', 'code-block', 'link'],
                ['clean'],
            ],
        },
        placeholder: placeholder || 'Write to the customer… (:: for a text module, ?? for a help answer)',
    });

    // Pasted images would become data: URIs the server strips anyway; files go as attachments.
    quill.clipboard.addMatcher('IMG', () => ({ ops: [] }));

    quill.on('text-change', () => {
        const sel = quill.getSelection();
        const found = sel ? triggerBefore(quill, sel.index) : null;
        const kb = sel ? triggerBefore(quill, sel.index, kbTrigger) : null;
        dotnet.invokeMethodAsync('OnTextModuleTrigger', found ? found.query : null);
        dotnet.invokeMethodAsync('OnKnowledgeTrigger', kb ? kb.query : null);
    });

    editors.set(element, quill);
}

export function html(element) {
    const quill = editors.get(element);
    return quill ? quill.getSemanticHTML() : '';
}

// Replaces the "::word" being typed with a text module's (already filled-in) text. Inserted as
// TEXT, never as HTML, so a module's content cannot inject markup.
export function insertModule(element, text) {
    const quill = editors.get(element);
    if (!quill) return;
    const sel = quill.getSelection(true);
    const found = triggerBefore(quill, sel.index);
    let at = sel.index;
    if (found) {
        at -= found.length;
        quill.deleteText(at, found.length, 'user');
    }
    quill.insertText(at, text, 'user');
    quill.setSelection(at + text.length, 0, 'user');
}

// Replaces the "??words" being typed with a help answer. The HTML comes from the server, which
// sanitised it when it was saved and again before handing it here; Quill keeps only the formats
// it knows, and the server sanitises the whole mail once more before sending.
export function insertAnswer(element, html) {
    const quill = editors.get(element);
    if (!quill) return;
    const sel = quill.getSelection(true);
    const found = triggerBefore(quill, sel.index, kbTrigger);
    let at = sel.index;
    if (found) {
        at -= found.length;
        quill.deleteText(at, found.length, 'user');
    }
    quill.clipboard.dangerouslyPasteHTML(at, html, 'user');
}

// Loads existing (server-sanitised) HTML into an editor — an answer being edited.
export function setHtml(element, html) {
    const quill = editors.get(element);
    if (!quill) return;
    quill.setContents([]);
    quill.clipboard.dangerouslyPasteHTML(0, html || '', 'silent');
}

// Quotes earlier text at the end of what is being written, as Zammad's select-and-reply does.
export function quote(element, text, header) {
    const quill = editors.get(element);
    if (!quill) return;
    let at = quill.getLength() - 1;
    if (header) {
        quill.insertText(at, header + '\n', 'user');
        at += header.length + 1;
    }
    for (const line of text.split('\n')) {
        quill.insertText(at, line + '\n', { blockquote: true }, 'user');
        at += line.length + 1;
    }
    quill.insertText(at, '\n', { blockquote: false }, 'user');
}

export function clear(element) {
    editors.get(element)?.setText('');
}

export function dispose(element) {
    editors.delete(element);
}
