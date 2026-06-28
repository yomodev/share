// Drag-resize handler for WorkspacePanel split handles.
// Panes use flex-grow (not flex-basis %) so the handle's 4px is
// automatically excluded from the distributed space.

window.wsResizer = (function () {

    function start(startX, startY, isH, dotnetRef, containerEl) {
        const panes = containerEl.querySelectorAll(':scope > .ws-pane');
        if (panes.length < 2) return;

        const handle = containerEl.querySelector(':scope > .ws-handle-h, :scope > .ws-handle-v');

        const paneA = panes[0];
        const paneB = panes[1];

        const containerRect = containerEl.getBoundingClientRect();
        const handlePx  = handle
            ? (isH ? handle.getBoundingClientRect().width : handle.getBoundingClientRect().height)
            : 0;
        const totalPx   = (isH ? containerRect.width : containerRect.height) - handlePx;
        const startPx   = isH ? startX : startY;
        const initA     = isH ? paneA.getBoundingClientRect().width
                               : paneA.getBoundingClientRect().height;

        document.body.style.cursor     = isH ? 'col-resize' : 'row-resize';
        document.body.style.userSelect = 'none';

        function setGrow(ratio) {
            const growA = (ratio * 10000).toFixed(0);
            const growB = ((1 - ratio) * 10000).toFixed(0);
            paneA.style.flex = `${growA} ${growA} 0`;
            paneB.style.flex = `${growB} ${growB} 0`;
        }

        function onMove(e) {
            const delta = (isH ? e.clientX : e.clientY) - startPx;
            const newA  = Math.max(40, Math.min(totalPx - 40, initA + delta));
            setGrow(newA / totalPx);
        }

        function onUp(e) {
            document.removeEventListener('mousemove', onMove);
            document.removeEventListener('mouseup',   onUp);
            document.body.style.cursor     = '';
            document.body.style.userSelect = '';

            const delta = (isH ? e.clientX : e.clientY) - startPx;
            const newA  = Math.max(40, Math.min(totalPx - 40, initA + delta));
            dotnetRef.invokeMethodAsync('SetRatio', newA / totalPx);
        }

        document.addEventListener('mousemove', onMove);
        document.addEventListener('mouseup',   onUp);
    }

    return { start };
})();
