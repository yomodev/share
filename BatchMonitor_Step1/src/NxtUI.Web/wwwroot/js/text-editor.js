// text-editor.js — ES module wrapping CodeMirror 6 for the Config page (and, later, any
// other text/code file viewer). CodeMirror ships as many small npm packages with internal
// cross-package imports rather than one drop-in file — this app has no bundler, so each
// package is pulled from esm.sh, a CDN built specifically for this scenario.
//
// IMPORTANT — do not switch these to jsDelivr's "+esm" endpoint (or mix CDNs): every
// dependent package (e.g. @codemirror/lang-json) also imports @codemirror/state (and
// @lezer/highlight, for tagging syntax) internally, and if those imports resolve to a
// DIFFERENT bundled copy than the ones imported directly below, CodeMirror's extension
// system — which uses `instanceof` checks internally — throws "Unrecognized extension
// value ... multiple instances of @codemirror/state are loaded" (or silently fails to
// match syntax-highlighting tags). esm.sh's `?deps=` query param is what prevents that:
// it forces every package on this page to resolve these shared packages to the exact
// same module instance instead of each bundling its own copy.
//
// Adding a new language is just one more entry in LANG_LOADERS, with the same `?deps=`
// tail listing every core package already imported above — that's the whole "pluggable
// syntax highlighting" story.
//
// ── Running offline / on an intranet without access to esm.sh ──────────────────────────
// All imports below are plain ESM URLs — swap them for local files and nothing else in
// this module needs to change. To vendor a local copy:
//   1. On a machine with npm and internet access:
//        npm init -y
//        npm install esbuild codemirror @codemirror/state @codemirror/view \
//            @codemirror/commands @codemirror/language @codemirror/search \
//            @codemirror/lang-json @codemirror/lang-xml @codemirror/lang-javascript \
//            @lezer/highlight
//   2. Bundle ALL of it into ONE file with esbuild (this sidesteps the whole multi-
//      instance problem above, since everything shares one bundle):
//        npx esbuild --bundle --format=esm --outfile=wwwroot/js/vendor/codemirror-bundle.js entry.js
//      where entry.js re-exports everything this file needs, e.g.:
//        export * from "@codemirror/state";
//        export * from "@codemirror/view";
//        export * from "@codemirror/commands";
//        export * from "@codemirror/language";
//        export * from "@codemirror/search";
//        export { tags } from "@lezer/highlight";
//        export { json } from "@codemirror/lang-json";
//        export { xml } from "@codemirror/lang-xml";
//        export { javascript } from "@codemirror/lang-javascript";
//   3. Copy codemirror-bundle.js into wwwroot/js/vendor/, then replace every import
//      below (including inside LANG_LOADERS) with `from "./vendor/codemirror-bundle.js"`
//      — a single shared import source, so the multi-instance problem this file's esm.sh
//      URLs work around doesn't apply to a local bundle in the first place.
//   4. Bump the cache-busting ?v= query param wherever text-editor.js itself is
//      referenced (ConfigPage.razor's JS.InvokeAsync("import", ...) call).
// No other code in this app needs to change — Blazor only ever calls the functions
// exported at the bottom of this file.
import { EditorState, Compartment } from "https://esm.sh/@codemirror/state@6";
import { EditorView, keymap, lineNumbers, highlightActiveLine } from "https://esm.sh/@codemirror/view@6?deps=@codemirror/state@6";
import { defaultKeymap, history, historyKeymap, indentWithTab, undo, redo } from "https://esm.sh/@codemirror/commands@6?deps=@codemirror/state@6,@codemirror/view@6";
import { HighlightStyle, syntaxHighlighting, indentOnInput, foldGutter, foldKeymap, codeFolding, unfoldAll, foldable, syntaxTree, foldEffect } from "https://esm.sh/@codemirror/language@6?deps=@codemirror/state@6,@codemirror/view@6,@lezer/highlight@1";
import { search, searchKeymap, highlightSelectionMatches, openSearchPanel } from "https://esm.sh/@codemirror/search@6?deps=@codemirror/state@6,@codemirror/view@6";
import { tags as t } from "https://esm.sh/@lezer/highlight@1";

const LANG_DEPS = "deps=@codemirror/state@6,@codemirror/view@6,@codemirror/language@6,@lezer/highlight@1";

// Extension (lowercase, with dot) -> async loader returning a CodeMirror language extension.
// Anything not listed here (including .txt and .ps1, per product decision — no real
// PowerShell grammar) falls back to plain text: still gets line numbers/editing/undo,
// just no keyword coloring.
const LANG_LOADERS = {
    '.json': async () => (await import(`https://esm.sh/@codemirror/lang-json@6?${LANG_DEPS}`)).json(),
    '.xml':  async () => (await import(`https://esm.sh/@codemirror/lang-xml@6?${LANG_DEPS}`)).xml(),
    '.js':   async () => (await import(`https://esm.sh/@codemirror/lang-javascript@6?${LANG_DEPS}`)).javascript(),
};

const _views = new WeakMap(); // container -> { view, readOnlyCompartment, styleCompartment, cleanDoc, dotNetRef, isDirty }

// While ANY editor instance is dirty, warn on tab close/refresh (the browser-level
// safety net — in-app "discard changes?" confirms for Reload/backup-restore are
// handled by ConfigPage.razor itself via OnDirtyChanged below).
let _dirtyCount = 0;
window.addEventListener('beforeunload', (e) => {
    if (_dirtyCount > 0) { e.preventDefault(); e.returnValue = ''; }
});

// ── Theming ──────────────────────────────────────────────────────────────────
// CodeMirror's @codemirror/language `defaultHighlightStyle` is tuned for a WHITE
// background (its "string"/"attribute" color is a dark maroon-red — fine on white,
// unreadable on this app's #0E1117 dark background). Defining our own per-theme
// styles avoids that, and — deliberately, per product feedback — avoids red/maroon
// tones in the dark palette entirely, since red-on-dark reads poorly regardless of
// the exact shade.
function buildTheme(dark) {
    const bg      = dark ? '#0E1117' : '#FFFFFF';
    const fg      = dark ? '#E6EDF3' : '#1F2328';
    const gutterBg= dark ? '#0E1117' : '#FFFFFF';
    const gutterFg= dark ? '#484F58' : '#8C959F';
    const activeLn= dark ? '#161B22' : '#F6F8FA';
    const panelBg = dark ? '#161B22' : '#F6F8FA';
    const border  = dark ? '#30363D' : '#D0D7DE';
    const matchBg = dark ? '#3FB95040' : '#2DA04440';
    const matchSel= dark ? '#3FB95080' : '#2DA04480';

    const theme = EditorView.theme({
        '&':                    { backgroundColor: bg, color: fg, height: '100%' },
        '.cm-content':          { caretColor: fg },
        '.cm-gutters':          { backgroundColor: gutterBg, color: gutterFg, border: 'none' },
        '.cm-activeLine':       { backgroundColor: activeLn },
        '.cm-activeLineGutter': { backgroundColor: activeLn },
        '.cm-scroller':         { fontFamily: 'var(--bm-font-mono, monospace)', fontSize: '13px' },
        // CodeMirror's built-in search/replace panel (Ctrl+F / Ctrl+H) assumes a light
        // page background by default — restyle it to match the editor either way.
        '.cm-panels':           { backgroundColor: panelBg, color: fg },
        '.cm-panels input':     { backgroundColor: bg, color: fg, border: `1px solid ${border}` },
        '.cm-panels button':    { backgroundColor: dark ? '#21262D' : '#EAEEF2', color: fg, border: `1px solid ${border}` },
        '.cm-searchMatch':      { backgroundColor: matchBg },
        '.cm-searchMatch-selected': { backgroundColor: matchSel },
        '.cm-foldPlaceholder':  { backgroundColor: dark ? '#21262D' : '#EAEEF2', color: gutterFg, border: `1px solid ${border}` },
    }, { dark });

    // Colors deliberately avoid red/maroon in the dark palette (see comment above).
    // Light palette stays closer to convention since red-on-white contrasts fine.
    const highlight = dark
        ? HighlightStyle.define([
            { tag: t.keyword,                          color: '#c297ff' },   // purple
            { tag: [t.string, t.special(t.string)],    color: '#7ee787' },   // green
            { tag: t.propertyName,                     color: '#79c0ff' },   // blue (JSON keys)
            { tag: [t.number, t.bool, t.null],          color: '#f2cc60' },   // gold
            { tag: t.atom,                              color: '#ffa657' },   // orange
            { tag: t.comment,                           color: '#8b949e', fontStyle: 'italic' },
            { tag: [t.operator, t.punctuation, t.bracket], color: '#c9d1d9' },
            { tag: t.tagName,                            color: '#d2a8ff' },   // light purple (XML tags)
            { tag: t.attributeName,                      color: '#79c0ff' },
            { tag: t.invalid,                            color: '#ffa657', textDecoration: 'underline' },
          ])
        : HighlightStyle.define([
            { tag: t.keyword,                          color: '#8250df' },
            { tag: [t.string, t.special(t.string)],    color: '#0a3069' },
            { tag: t.propertyName,                     color: '#0550ae' },
            { tag: [t.number, t.bool, t.null],          color: '#953800' },
            { tag: t.atom,                              color: '#cf222e' },
            { tag: t.comment,                           color: '#6e7781', fontStyle: 'italic' },
            { tag: [t.operator, t.punctuation, t.bracket], color: '#1f2328' },
            { tag: t.tagName,                            color: '#116329' },
            { tag: t.attributeName,                      color: '#0550ae' },
            { tag: t.invalid,                            color: '#cf222e', textDecoration: 'underline' },
          ]);

    return [theme, syntaxHighlighting(highlight, { fallback: true })];
}

function extensionOf(fileNameOrPath) {
    const i = fileNameOrPath.lastIndexOf('.');
    return i < 0 ? '' : fileNameOrPath.slice(i).toLowerCase();
}

// ── Commands ─────────────────────────────────────────────────────────────────
// The built-in @codemirror/language `foldAll` only folds the OUTERMOST foldable range
// at each position (folding a parent already hides its children, so it doesn't bother
// descending) — the product ask here is every node folds individually, so unfolding
// the outer level still shows inner objects/arrays already collapsed. Walk the whole
// syntax tree and fold every foldable range found, not just the top ones.
function foldAllDeep(view) {
    const effects = [];
    syntaxTree(view.state).iterate({
        enter: (node) => {
            const range = foldable(view.state, node.from, node.to);
            if (range) effects.push(foldEffect.of(range));
        },
    });
    if (effects.length) view.dispatch({ effects });
    return true;
}

function unfoldAllCmd(view) { unfoldAll(view); return true; }

function invokeDotNet(view, method) {
    const entry = [..._views.values()].find(e => e.view === view);
    entry?.dotNetRef?.invokeMethodAsync(method).catch(() => {});
    return true;
}

async function init(container, initialText, fileNameOrPath, editable, dotNetRef, isDark, wordWrap) {
    dispose(container);

    const langLoader = LANG_LOADERS[extensionOf(fileNameOrPath)];
    const langExt = langLoader ? await langLoader() : [];
    const readOnlyCompartment = new Compartment();
    const styleCompartment = new Compartment();
    const wrapCompartment = new Compartment();
    const initial = initialText ?? '';

    const updateListener = EditorView.updateListener.of((update) => {
        if (!update.docChanged) return;
        const entry = _views.get(container);
        if (!entry) return;
        setDirty(container, entry, entry.view.state.doc.toString() !== entry.cleanDoc);
    });

    const state = EditorState.create({
        doc: initial,
        extensions: [
            lineNumbers(),
            foldGutter(),
            codeFolding(),
            highlightActiveLine(),
            highlightSelectionMatches(),
            history(),
            indentOnInput(),
            search(),
            // Custom bindings first so they win over any coincidentally-overlapping
            // default from foldKeymap/searchKeymap/defaultKeymap.
            keymap.of([
                { key: 'Mod-Alt-[', run: foldAllDeep },
                { key: 'Mod-Alt-]', run: unfoldAllCmd },
                { key: 'Mod-s',     run: (v) => invokeDotNet(v, 'OnSaveShortcut') },
                { key: 'Mod-Alt-r', run: (v) => invokeDotNet(v, 'OnReloadShortcut') },
                { key: 'Mod-Alt-w', run: (v) => { toggleWrap(container); return true; } },
                ...foldKeymap, ...searchKeymap, indentWithTab, ...defaultKeymap, ...historyKeymap,
            ]),
            wrapCompartment.of(wordWrap ? EditorView.lineWrapping : []),
            styleCompartment.of(buildTheme(!!isDark)),
            langExt,
            readOnlyCompartment.of(EditorView.editable.of(!!editable)),
            updateListener,
        ],
    });

    const view = new EditorView({ state, parent: container });
    _views.set(container, { view, readOnlyCompartment, styleCompartment, wrapCompartment, cleanDoc: initial, dotNetRef, isDirty: false, wordWrap: !!wordWrap });
}

function setDirty(container, entry, dirty) {
    if (entry.isDirty === dirty) return;
    entry.isDirty = dirty;
    _dirtyCount += dirty ? 1 : -1;
    entry.dotNetRef?.invokeMethodAsync('OnDirtyChanged', dirty).catch(() => {});
}

function isDirty(container) {
    return _views.get(container)?.isDirty ?? false;
}

function getValue(container) {
    const entry = _views.get(container);
    return entry ? entry.view.state.doc.toString() : '';
}

// Replaces the whole document AND resets the "clean" baseline — used when loading a
// fresh read from disk (Reload) or previewing a backup's content: the user hasn't
// made any edits relative to what's now on screen, so it should not read as dirty.
function setValue(container, text) {
    const entry = _views.get(container);
    if (!entry) return;
    const value = text ?? '';
    entry.view.dispatch({
        changes: { from: 0, to: entry.view.state.doc.length, insert: value },
    });
    entry.cleanDoc = value;
    setDirty(container, entry, false);
}

// Marks the CURRENT content as clean without touching the document (used right after a
// successful Save — the just-typed content is now what's on disk, so it shouldn't keep
// reading as dirty, but resetting it via setValue would also wipe undo history).
function markClean(container) {
    const entry = _views.get(container);
    if (!entry) return;
    entry.cleanDoc = entry.view.state.doc.toString();
    setDirty(container, entry, false);
}

function setEditable(container, editable) {
    const entry = _views.get(container);
    if (!entry) return;
    entry.view.dispatch({
        effects: entry.readOnlyCompartment.reconfigure(EditorView.editable.of(!!editable)),
    });
}

// Called when the app-wide theme toggle changes, so the editor follows it live
// instead of staying frozen on whatever theme was active at mount time.
function setTheme(container, isDark) {
    const entry = _views.get(container);
    if (!entry) return;
    entry.view.dispatch({
        effects: entry.styleCompartment.reconfigure(buildTheme(!!isDark)),
    });
}

function getWordWrap(container) {
    return _views.get(container)?.wordWrap ?? false;
}

function setWordWrap(container, enabled) {
    const entry = _views.get(container);
    if (!entry) return;
    entry.wordWrap = !!enabled;
    entry.view.dispatch({
        effects: entry.wrapCompartment.reconfigure(entry.wordWrap ? EditorView.lineWrapping : []),
    });
    entry.dotNetRef?.invokeMethodAsync('OnWordWrapChanged', entry.wordWrap).catch(() => {});
}

function toggleWrap(container) {
    setWordWrap(container, !getWordWrap(container));
}

// ── Toolbar commands ─────────────────────────────────────────────────────────
function withView(container, fn) {
    const entry = _views.get(container);
    if (entry) fn(entry.view);
}
function cmUndo(container)      { withView(container, undo); }
function cmRedo(container)      { withView(container, redo); }
function cmFoldAll(container)   { withView(container, foldAllDeep); }
function cmUnfoldAll(container) { withView(container, unfoldAllCmd); }
function cmFind(container)      { withView(container, openSearchPanel); }
function cmToggleWrap(container) { toggleWrap(container); }

function dispose(container) {
    const entry = _views.get(container);
    if (!entry) return;
    if (entry.isDirty) _dirtyCount--;
    entry.view.destroy();
    _views.delete(container);
}

const api = {
    init, getValue, setValue, markClean, setEditable, setTheme, getWordWrap, setWordWrap, isDirty, dispose,
    cmUndo, cmRedo, cmFoldAll, cmUnfoldAll, cmFind, cmToggleWrap,
};
window.bmTextEditor = api;
export default api;
export { init, getValue, setValue, markClean, setEditable, setTheme, getWordWrap, setWordWrap, isDirty, dispose, cmUndo, cmRedo, cmFoldAll, cmUnfoldAll, cmFind, cmToggleWrap };
