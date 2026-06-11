const MONACO_VERSION = '0.52.0';
const MONACO_BASE = `https://cdnjs.cloudflare.com/ajax/libs/monaco-editor/${MONACO_VERSION}/min/vs`;

let _monacoPromise = null;

function loadMonaco() {
    if (_monacoPromise) return _monacoPromise;

    _monacoPromise = new Promise((resolve, reject) => {
        if (window.monaco) {
            resolve(window.monaco);
            return;
        }

        const script = document.createElement('script');
        script.src = `${MONACO_BASE}/loader.js`;
        script.onload = () => {
            window.require.config({ paths: { vs: MONACO_BASE } });
            window.require(['vs/editor/editor.main'], () => resolve(window.monaco));
        };
        script.onerror = () => reject(new Error('Failed to load Monaco editor'));
        document.head.appendChild(script);
    });

    return _monacoPromise;
}

export async function createEditor(container, value, language, readOnly, dotNetRef, autoHeight, maxHeightPx) {
    const monaco = await loadMonaco();

    const editor = monaco.editor.create(container, {
        value: value ?? '',
        language: language ?? 'yaml',
        theme: 'vs',
        readOnly: readOnly,
        minimap: { enabled: false },
        scrollBeyondLastLine: false,
        automaticLayout: true,
        fontSize: 13,
        fontFamily: "ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, 'Liberation Mono', monospace",
        tabSize: 2,
        lineNumbers: 'on',
        wordWrap: 'off',
        fixedOverflowWidgets: true,
        overviewRulerLanes: 0,
        padding: { top: 8, bottom: 8 },
        scrollbar: {
            vertical: 'auto',
            horizontal: 'auto',
            useShadows: false,
            verticalScrollbarSize: 8,
            horizontalScrollbarSize: 8,
        },
        renderLineHighlight: 'line',
        smoothScrolling: true,
    });

    editor.onDidChangeModelContent(() => {
        dotNetRef.invokeMethodAsync('OnValueChanged', editor.getValue());
    });

    if (autoHeight) {
        function updateAutoHeight() {
            const contentHeight = editor.getContentHeight();
            const h = maxHeightPx > 0 ? Math.min(contentHeight, maxHeightPx) : contentHeight;
            container.style.height = Math.max(100, h) + 'px';
            editor.layout();
        }
        editor._updateAutoHeight = updateAutoHeight;
        editor.onDidContentSizeChange(updateAutoHeight);
        updateAutoHeight();
    }

    return editor;
}

export function relayoutEditor(editor, container, isFullscreen) {
    if (isFullscreen) {
        container.style.height = '100%';
    } else if (editor._updateAutoHeight) {
        editor._updateAutoHeight();
        return;
    }
    requestAnimationFrame(() => editor.layout());
}

export function setEditorValue(editor, value) {
    if (editor.getValue() !== value) {
        const model = editor.getModel();
        model.pushEditOperations(
            [],
            [{ range: model.getFullModelRange(), text: value }],
            () => null
        );
    }
}

export function disposeEditor(editor) {
    editor.dispose();
}
