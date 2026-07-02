// Debugging aid for the MudSelect removeChild repro. Loaded BEFORE blazor.server.js
// so error/console hooks are in place from the very first render.
(function () {
    const MAX_EVENTS = 300;
    const events = [];
    window.__diagEvents = events;

    function stamp() {
        const d = new Date();
        return {
            wall: d.toISOString().slice(11, 23),      // HH:mm:ss.SSS — correlate with server logs
            perf: performance.now().toFixed(1),        // high-res, monotonic — correlate client-side ordering
        };
    }

    function record(kind, detail) {
        const s = stamp();
        const entry = { ...s, kind, detail };
        events.push(entry);
        if (events.length > MAX_EVENTS) events.shift();
        return entry;
    }

    function dumpRecent(n) {
        console.table(events.slice(-(n || 25)).map(e => ({
            wall: e.wall, perf: e.perf, kind: e.kind,
            detail: typeof e.detail === 'string' ? e.detail : JSON.stringify(e.detail),
        })));
    }

    // Called from Blazor (JS interop) to mark app-level lifecycle points:
    // timer ticks, value changes, popover-relevant events. Cheap, no return value.
    window.diagMark = function (label) {
        const e = record('mark', label);
        console.log('%c[DIAG mark]', 'color:#0af;font-weight:bold', e.wall, label);
    };

    // Capture Blazor's own internal failure log ("Error: There was an error
    // applying batch N.") plus any other console.error traffic, with full text.
    const origError = console.error.bind(console);
    console.error = function (...args) {
        const text = args.map(a => (a && a.stack) ? a.stack : String(a)).join(' | ');
        record('console.error', text);
        origError(...args);
        if (/error applying batch/i.test(text)) {
            console.log('%c[DIAG] Blazor batch-apply failure detected — recent timeline:', 'color:#f33;font-weight:bold;font-size:14px');
            dumpRecent(30);
        }
    };

    // Synchronous uncaught errors (the actual TypeError surfaces here).
    window.addEventListener('error', function (ev) {
        record('window.error', {
            message: ev.message,
            filename: ev.filename,
            lineno: ev.lineno,
            colno: ev.colno,
            stack: ev.error && ev.error.stack,
        });
        console.log('%c[DIAG] Uncaught error — recent timeline:', 'color:#f33;font-weight:bold;font-size:14px', ev.message);
        dumpRecent(30);
    });

    // In case the failure surfaces as a rejected promise instead (SignalR message
    // handling is largely async).
    window.addEventListener('unhandledrejection', function (ev) {
        const reason = ev.reason;
        record('unhandledrejection', {
            message: reason && reason.message,
            stack: reason && reason.stack,
        });
        console.log('%c[DIAG] Unhandled rejection — recent timeline:', 'color:#f33;font-weight:bold;font-size:14px', reason && reason.message);
        dumpRecent(30);
    });

    // Manual escape hatch: run `copy(dumpDiag())` in devtools console to grab the
    // full ring buffer as JSON for pasting elsewhere.
    window.dumpDiag = function () { return JSON.stringify(events, null, 2); };

    console.log('%c[DIAG] diag.js loaded — window.__diagEvents / window.dumpDiag() available', 'color:#0af;font-weight:bold');
})();
