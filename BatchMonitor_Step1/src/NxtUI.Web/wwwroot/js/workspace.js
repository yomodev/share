// Drag-resize handler for WorkspacePanel split handles.
// Updates pane flex-basis directly during drag for smooth 60fps resize;
// notifies Blazor via DotNetObjectReference only on mouseup.

window.wsResizer = (function () {

    function start(startX, startY, isH, dotnetRef, containerEl) {
        const panes    = containerEl.querySelectorAll(':scope > .ws-pane');
        if (panes.length < 2) return;

        const paneA    = panes[0];
        const paneB    = panes[1];
        const totalPx  = isH
            ? containerEl.getBoundingClientRect().width
            : containerEl.getBoundingClientRect().height;

        const startPx  = isH ? startX : startY;
        const initA    = isH ? paneA.getBoundingClientRect().width
                              : paneA.getBoundingClientRect().height;

        document.body.style.cursor     = isH ? 'col-resize' : 'row-resize';
        document.body.style.userSelect = 'none';

        function onMove(e) {
            const delta   = (isH ? e.clientX : e.clientY) - startPx;
            const newA    = Math.max(40, Math.min(totalPx - 40, initA + delta));
            const ratio   = newA / totalPx;
            const pct     = (ratio * 100).toFixed(2) + '%';
            const invPct  = ((1 - ratio) * 100).toFixed(2) + '%';
            paneA.style.flex = `0 0 ${pct}`;
            paneB.style.flex = `0 0 ${invPct}`;
        }

        function onUp(e) {
            document.removeEventListener('mousemove', onMove);
            document.removeEventListener('mouseup',   onUp);
            document.body.style.cursor     = '';
            document.body.style.userSelect = '';

            const delta  = (isH ? e.clientX : e.clientY) - startPx;
            const newA   = Math.max(40, Math.min(totalPx - 40, initA + delta));
            const ratio  = newA / totalPx;
            dotnetRef.invokeMethodAsync('SetRatio', ratio);
        }

        document.addEventListener('mousemove', onMove);
        document.addEventListener('mouseup',   onUp);
    }

    return { start };
})();
