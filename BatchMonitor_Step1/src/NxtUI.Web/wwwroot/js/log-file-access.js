// Client-side (browser-native) file reads for the Log Browser, using the File
// System Access API. Lets a double-click open a log file directly from the
// browser's own access to a shared network folder, instead of the server
// reading it and shipping the content over the SignalR circuit.
//
// Chromium-only (Chrome/Edge) — feature-detected. Every entry point resolves
// to null on any failure (unsupported browser, user cancelled, permission
// denied, path not found under the granted folder) so callers fall back to
// the existing server-read path.
window.bmLogFileAccess = (function () {
    const DB_NAME = 'bm-log-file-access';
    const STORE   = 'dirHandles';

    function openDb() {
        return new Promise((resolve, reject) => {
            const req = indexedDB.open(DB_NAME, 1);
            req.onupgradeneeded = () => req.result.createObjectStore(STORE);
            req.onsuccess = () => resolve(req.result);
            req.onerror   = () => reject(req.error);
        });
    }

    async function getStoredHandle(server) {
        try {
            const db = await openDb();
            return await new Promise((resolve, reject) => {
                const tx = db.transaction(STORE, 'readonly');
                const rq = tx.objectStore(STORE).get(server);
                rq.onsuccess = () => resolve(rq.result || null);
                rq.onerror   = () => reject(rq.error);
            });
        } catch (err) {
            console.warn('[bmLogFileAccess] getStoredHandle failed:', err);
            return null;
        }
    }

    async function storeHandle(server, handle) {
        // Best-effort — a failed persist just means the next open re-prompts.
        try {
            const db = await openDb();
            await new Promise((resolve, reject) => {
                const tx = db.transaction(STORE, 'readwrite');
                tx.objectStore(STORE).put(handle, server);
                tx.oncomplete = resolve;
                tx.onerror    = () => reject(tx.error);
            });
        } catch (err) {
            console.warn('[bmLogFileAccess] storeHandle failed (will re-prompt next time):', err);
        }
    }

    async function resolveFile(dirHandle, relDir, fileName) {
        let dir = dirHandle;
        for (const seg of relDir.split(/[\\/]/).filter(Boolean))
            dir = await dir.getDirectoryHandle(seg);
        return await dir.getFileHandle(fileName);
    }

    // There's no way to hint the native folder picker at a specific starting folder, so
    // the user could grant any ancestor of the true per-server log root: the exact root
    // itself (Logs:RootFolder with {server} expanded), its parent, or further up.
    // rootTemplate is that same config value (e.g. "C:\Temp\logs\{server}"). Expand
    // {server} and split it into segments, then try resolving as if the granted folder
    // were each possible depth along that chain, closest-to-leaf first — this covers
    // whatever level the user actually picked instead of guessing one specific ancestor
    // depth. Doesn't try to match the granted folder's name against anything.
    function candidatePaths(server, rootTemplate, relDir) {
        const candidates = [relDir]; // exact-root case: granted folder IS the per-server root
        if (rootTemplate) {
            const segs = rootTemplate.split(/[\\/]+/).filter(Boolean)
                .map(s => s.replace(/\{server\}/gi, server));
            for (let i = segs.length - 1; i >= 0; i--)
                candidates.push([...segs.slice(i + 1), relDir].filter(Boolean).join('\\'));
        }
        return candidates;
    }

    async function resolveFileFlexible(dirHandle, server, rootTemplate, relDir, fileName) {
        const candidates = candidatePaths(server, rootTemplate, relDir);
        let lastErr;
        for (const path of candidates) {
            try {
                return await resolveFile(dirHandle, path, fileName);
            } catch (err) {
                lastErr = err;
            }
        }
        console.warn('[bmLogFileAccess] could not resolve', JSON.stringify(fileName), 'under granted folder',
            JSON.stringify(dirHandle.name), '- tried relative paths:', candidates, lastErr);
        throw lastErr;
    }

    let _seq = 0;
    function stashText(text) {
        window._localFiles = window._localFiles || new Map();
        const key = 'lf' + (++_seq);
        window._localFiles.set(key, text);
        return key;
    }

    return {
        isSupported: () => !!window.showDirectoryPicker,

        // Reuses a previously-granted folder for this server — no folder-picker
        // dialog. Chrome/Edge don't persist the *permission grant* itself across
        // page reloads (only the handle survives in IndexedDB), so this still
        // has to re-request permission on that same handle if it reverted to
        // 'prompt'; that's a lightweight one-line confirm bar, not a folder
        // reselection, and still only fires as an effect of the caller's own
        // user gesture (e.g. the double-click that led here).
        // Returns a _localFiles key on success, or null if there's no stored
        // handle at all, or the user declines the permission re-confirm
        // (caller should then fall back to openWithGrant).
        tryOpenSilently: async function (server, rootTemplate, relDir, fileName) {
            if (!window.showDirectoryPicker) return null;
            try {
                const handle = await getStoredHandle(server);
                if (!handle) return null;

                let perm = await handle.queryPermission({ mode: 'read' });
                if (perm !== 'granted') perm = await handle.requestPermission({ mode: 'read' });
                if (perm !== 'granted') return null;

                const fh = await resolveFileFlexible(handle, server, rootTemplate, relDir, fileName);
                const file = await fh.getFile();
                return stashText(await file.text());
            } catch (err) {
                console.warn('[bmLogFileAccess] tryOpenSilently failed:', err);
                return null;
            }
        },

        // Prompts the user to grant access to a folder (native picker — call this from
        // a user-gesture context, e.g. a click handler), then resolves the file from it.
        // Returns a _localFiles key, or null if the user cancelled, the browser doesn't
        // support this API, or the granted folder doesn't contain the expected path at
        // any ancestor depth along rootTemplate.
        openWithGrant: async function (server, rootTemplate, relDir, fileName) {
            if (!window.showDirectoryPicker) return null;
            try {
                const handle = await window.showDirectoryPicker({ id: 'bm-log-root-' + server, mode: 'read' });
                const fh = await resolveFileFlexible(handle, server, rootTemplate, relDir, fileName);
                const file = await fh.getFile();
                const text = await file.text();
                await storeHandle(server, handle); // persist only once it's confirmed to resolve
                return stashText(text);
            } catch (err) {
                if (err && err.name === 'AbortError') return null; // user cancelled — not an error
                console.warn('[bmLogFileAccess] grant/open failed:', err);
                return null;
            }
        },
    };
})();

// Single-file picker for the File Browser's "Open local file" toolbar button —
// unrelated to the granted-folder flow above (reads whatever file the user
// picks, doesn't try to match it against any configured log root).
window.bmOpenLocalLogFile = async function (dotNetRef) {
    try {
        let file;
        if (window.showOpenFilePicker) {
            const [fh] = await window.showOpenFilePicker({ multiple: false });
            file = await fh.getFile();
        } else {
            file = await new Promise((resolve, reject) => {
                const input = document.createElement('input');
                input.type = 'file';
                input.onchange = () => resolve(input.files[0] || null);
                input.onerror   = reject;
                input.click();
            });
        }
        if (!file) return;
        window._localFiles = window._localFiles || new Map();
        const key = 'lf-local-' + Date.now();
        window._localFiles.set(key, await file.text());
        await dotNetRef.invokeMethodAsync('OnLocalFileOpened', key, file.name);
    } catch (err) {
        if (err && err.name === 'AbortError') return;
        console.warn('[bmOpenLocalLogFile] failed:', err);
    }
};
