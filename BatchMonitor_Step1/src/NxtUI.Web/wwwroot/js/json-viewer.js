/**
 * Lightweight interactive JSON tree viewer.
 * Usage: bmJsonViewer.render(domElement, jsonString)
 * Clicking the ▾/▸ chevron collapses or expands any object or array.
 */
window.bmJsonViewer = (() => {

    function render(el, json) {
        el.innerHTML = '';
        el.className = 'bm-jv';

        let parsed;
        try { parsed = JSON.parse(json); }
        catch { el.classList.add('bm-jv-raw'); el.textContent = json; return; }

        el.appendChild(buildNode(parsed, true));
    }

    // ── node builders ─────────────────────────────────────────────────────────

    function buildNode(val, isRoot) {
        if (val === null)              return tok('bm-jv-null', 'null');
        if (typeof val === 'boolean')  return tok('bm-jv-bool', String(val));
        if (typeof val === 'number')   return tok('bm-jv-num',  String(val));
        if (typeof val === 'string')   return tok('bm-jv-str',  JSON.stringify(val));
        if (Array.isArray(val))        return buildArray(val, isRoot);
        if (typeof val === 'object')   return buildObject(val, isRoot);
        return tok('', String(val));
    }

    function buildObject(obj, isRoot) {
        const entries = Object.entries(obj);
        const wrap    = document.createElement('span');
        wrap.className = 'bm-jv-coll';

        if (entries.length === 0) {
            wrap.appendChild(tok('bm-jv-bracket', '{ }'));
            return wrap;
        }

        const { toggle, body, summary } = buildShell('{', '}', `{…${entries.length}}`, isRoot);
        entries.forEach(([k, v], i) => {
            const row = document.createElement('div');
            row.className = 'bm-jv-row';
            row.appendChild(tok('bm-jv-key', JSON.stringify(k) + ': '));
            row.appendChild(buildNode(v, false));
            if (i < entries.length - 1) row.appendChild(tok('bm-jv-punct', ','));
            body.appendChild(row);
        });

        wrap.appendChild(toggle);
        wrap.appendChild(tok('bm-jv-bracket', '{'));
        wrap.appendChild(summary);
        wrap.appendChild(body);
        wrap.appendChild(tok('bm-jv-bracket', '}'));
        return wrap;
    }

    function buildArray(arr, isRoot) {
        const wrap = document.createElement('span');
        wrap.className = 'bm-jv-coll';

        if (arr.length === 0) {
            wrap.appendChild(tok('bm-jv-bracket', '[]'));
            return wrap;
        }

        const { toggle, body, summary } = buildShell('[', ']', `[…${arr.length}]`, isRoot);
        arr.forEach((v, i) => {
            const row = document.createElement('div');
            row.className = 'bm-jv-row';
            row.appendChild(buildNode(v, false));
            if (i < arr.length - 1) row.appendChild(tok('bm-jv-punct', ','));
            body.appendChild(row);
        });

        wrap.appendChild(toggle);
        wrap.appendChild(tok('bm-jv-bracket', '['));
        wrap.appendChild(summary);
        wrap.appendChild(body);
        wrap.appendChild(tok('bm-jv-bracket', ']'));
        return wrap;
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    function buildShell(open, close, summaryText, isRoot) {
        const body    = document.createElement('div');
        body.className = 'bm-jv-body';

        const summary  = document.createElement('span');
        summary.className = 'bm-jv-summary';
        summary.textContent = summaryText;
        summary.style.display = 'none';

        const toggle   = document.createElement('span');
        toggle.className = 'bm-jv-toggle';
        toggle.textContent = '▾';

        // Root nodes start expanded; nested nodes can be toggled.
        toggle.addEventListener('click', (e) => {
            e.stopPropagation();
            const collapsed = body.style.display === 'none';
            body.style.display    = collapsed ? ''     : 'none';
            summary.style.display = collapsed ? 'none' : 'inline';
            toggle.textContent    = collapsed ? '▾'    : '▸';
        });

        return { toggle, body, summary };
    }

    function tok(cls, text) {
        const s = document.createElement('span');
        if (cls) s.className = cls;
        s.textContent = text;
        return s;
    }

    // ── expand / collapse all ───────────────────────────────────────────────

    function setAllCollapsed(el, collapsed) {
        el.querySelectorAll('.bm-jv-toggle').forEach(toggle => {
            const wrap    = toggle.parentElement;
            const body    = wrap && wrap.querySelector(':scope > .bm-jv-body');
            const summary = wrap && wrap.querySelector(':scope > .bm-jv-summary');
            if (!body || !summary) return;
            body.style.display    = collapsed ? 'none' : '';
            summary.style.display = collapsed ? 'inline' : 'none';
            toggle.textContent    = collapsed ? '▸' : '▾';
        });
    }

    return { render, setAllCollapsed };
})();
