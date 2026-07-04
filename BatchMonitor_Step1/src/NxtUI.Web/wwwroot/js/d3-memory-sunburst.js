// Batch Monitor — Memory line chart (per-service RAM over time) + legend resize.
// (The sunburst chart this file was originally named for was superseded by the
// line chart below and has been removed — no Razor code ever called it.)

// ── Line chart with brush navigator ──────────────────────────────────────────
// series: [{ name, color, timestamps: number[] (ms), values: number[] (MB) }]
// windowHours: null = show all, number = initial window size in hours
window.memoryLineChart = (() => {

    const MONO = "'JetBrains Mono','Cascadia Code',Consolas,monospace";

    function fmtY(v) { return v >= 1000 ? (v / 1024).toFixed(1) + 'G' : v.toFixed(0) + 'M'; }

    // Cached per-container so a ResizeObserver-triggered repaint (container size
    // changed — e.g. dragging the legend panel wider, or a browser resize) can
    // redraw at the new dimensions without needing a fresh call from Blazor.
    const _mgState = new WeakMap();

    function render(mainCt, brushCt, series, windowHours) {
        _mgState.set(mainCt, { brushCt, series, windowHours });
        wireResizeObserver(mainCt);

        d3.select(mainCt).selectAll('*').remove();
        d3.select(brushCt).selectAll('*').remove();
        if (!series || series.length === 0) return;

        // Convert ms timestamps to Date points per series
        const parsed = series.map(s => ({
            name:    s.name,
            label:   s.label || null,
            color:   s.color,
            isFaded: !!s.isFaded,
            points:  s.timestamps.map((t, i) => ({ t: new Date(t), v: s.values[i] }))
        })).filter(s => s.points.length > 0);
        if (parsed.length === 0) return;

        const allTs  = parsed.flatMap(s => s.points.map(p => p.t));
        const tMin   = d3.min(allTs);
        const tMax   = d3.max(allTs);
        const allVals = parsed.flatMap(s => s.points.map(p => p.v));
        const yMax   = Math.max(d3.max(allVals) || 1, 1);

        // Initial window
        let winStart = tMin, winEnd = tMax;
        if (windowHours > 0) {
            winEnd   = tMax;
            winStart = new Date(tMax.getTime() - windowHours * 3_600_000);
            if (winStart < tMin) winStart = tMin;
        }

        // ── Main chart ────────────────────────────────────────────────────
        const mR = mainCt.getBoundingClientRect();
        const W  = mR.width  || 600;
        const H  = mR.height || 340;
        const mL = 58, mRt = 20, mT = 14, mB = 28;
        const iW = W - mL - mRt;
        const iH = H - mT  - mB;

        const svgM = d3.select(mainCt).append('svg').attr('width', W).attr('height', H);
        svgM.append('defs').append('clipPath').attr('id', 'mg-main-clip')
            .append('rect').attr('width', iW).attr('height', iH + 1);
        const gM = svgM.append('g').attr('transform', `translate(${mL},${mT})`);

        const xM = d3.scaleTime().domain([winStart, winEnd]).range([0, iW]);
        const yM = d3.scaleLinear().domain([0, yMax * 1.1]).range([iH, 0]);

        // Grid
        gM.append('g').attr('class', 'mg-grid')
          .call(d3.axisLeft(yM).ticks(5).tickSize(-iW).tickFormat(''))
          .selectAll('line').attr('stroke', 'rgba(128,128,128,0.12)');
        gM.select('.mg-grid .domain').remove();

        // Axes (mutable — updated on brush)
        const xAxisG = gM.append('g').attr('transform', `translate(0,${iH})`);
        const yAxisG = gM.append('g');
        function drawAxes() {
            xAxisG.call(d3.axisBottom(xM).ticks(8).tickFormat(d3.timeFormat('%H:%M')))
                  .selectAll('text').attr('font-size', '10px');
            yAxisG.call(d3.axisLeft(yM).ticks(5).tickFormat(fmtY))
                  .selectAll('text').attr('font-size', '10px');
            gM.selectAll('.domain').attr('stroke', 'rgba(128,128,128,0.3)');
        }
        drawAxes();

        // Lines (clipped)
        const linesG = gM.append('g').attr('clip-path', 'url(#mg-main-clip)');
        const lineGen = d3.line().x(p => xM(p.t)).y(p => yM(p.v)).curve(d3.curveMonotoneX);
        const paths = {};
        parsed.forEach(s => {
            paths[s.name] = linesG.append('path').datum(s.points)
                .attr('fill', 'none').attr('stroke', s.color)
                .attr('stroke-width', s.isFaded ? 1.4 : 1.8)
                .attr('stroke-opacity', s.isFaded ? 0.45 : 1)
                .attr('d', lineGen);
        });

        // Tooltip + crosshair
        const tip = d3.select(mainCt).append('div')
            .attr('class', 'bm-chart-tip')
            .style('position', 'absolute').style('pointer-events', 'none')
            .style('padding', '6px 10px').style('border-radius', '4px')
            .style('font-size', '11px').style('line-height', '1.7').style('display', 'none')
            .style('z-index', '9999').style('font-family', MONO);

        const vline = gM.append('line').attr('y1', 0).attr('y2', iH)
            .attr('stroke', 'rgba(160,160,160,0.4)').attr('stroke-width', 1).attr('display', 'none');

        function redraw() {
            drawAxes();
            parsed.forEach(s => paths[s.name].attr('d', lineGen));
        }

        function showTooltip(event) {
            const [mx] = d3.pointer(event);
            const hover = xM.invert(mx);
            vline.attr('x1', mx).attr('x2', mx).attr('display', null);
            const rows = parsed.map(s => {
                const closest = s.points.reduce((a, b) =>
                    Math.abs(b.t - hover) < Math.abs(a.t - hover) ? b : a, s.points[0]);
                const lbl = s.label || s.name;
                const opacity = s.isFaded ? '0.5' : '1';
                return `<span style="color:${s.color};opacity:${opacity}">■</span> ${lbl}: ${closest.v.toFixed(1)} MB`;
            }).join('<br>');
            tip.style('display', 'block')
               .html(`<strong>${d3.timeFormat('%H:%M:%S')(hover)}</strong><br>${rows}`);
            const cr = mainCt.getBoundingClientRect();
            let lx = event.clientX - cr.left + 14;
            if (lx + 210 > W) lx = event.clientX - cr.left - 220;
            tip.style('left', lx + 'px').style('top', (event.clientY - cr.top - 20) + 'px');
        }
        function hideTooltip() { vline.attr('display', 'none'); tip.style('display', 'none'); }

        // ── Brush / time-range navigator ──────────────────────────────────
        let brushX = null, brushG = null, xB = null;
        if (brushCt) {
            const bR  = brushCt.getBoundingClientRect();
            const bW  = bR.width  || W;
            const bH  = bR.height || 28;
            const bL  = mL, bRt2 = mRt, bT = 4, bB = 22;
            const biW = bW - bL - bRt2;
            const biH = bH - bT - bB;

            const svgB = d3.select(brushCt).append('svg').attr('width', bW).attr('height', bH);
            const gB   = svgB.append('g').attr('transform', `translate(${bL},${bT})`);

            xB = d3.scaleTime().domain([tMin, tMax]).range([0, biW]);

            // Subtle background track
            gB.append('rect')
              .attr('width', biW).attr('height', biH)
              .attr('fill', 'rgba(128,128,128,0.06)')
              .attr('rx', 2);

            // Time axis
            gB.append('g').attr('transform', `translate(0,${biH})`)
              .call(d3.axisBottom(xB).ticks(8).tickFormat(d3.timeFormat('%H:%M')))
              .selectAll('text').attr('font-size', '9px');
            gB.selectAll('.domain').attr('stroke', 'rgba(128,128,128,0.25)');
            gB.selectAll('.tick line').attr('stroke', 'rgba(128,128,128,0.25)');

            brushX = d3.brushX()
                .extent([[0, 0], [biW, biH]])
                .on('brush end', ({ selection, sourceEvent }) => {
                    if (!selection || !sourceEvent || suppressSync) return;
                    const [d0, d1] = selection.map(xB.invert);
                    xM.domain([d0, d1]);
                    redraw();
                    hideTooltip();
                    // Keep the zoom transform in sync so a wheel-zoom right after a
                    // brush drag continues smoothly instead of jumping back.
                    suppressSync = true;
                    const k = (tMax - tMin) / (d1 - d0);
                    overlay.call(zoom.transform, d3.zoomIdentity.scale(k).translate(-xFull(d0), 0));
                    suppressSync = false;
                });

            brushG = gB.append('g').call(brushX);

            brushG.select('.selection')
                  .attr('fill', 'rgba(100,150,255,0.15)')
                  .attr('stroke', 'rgba(100,150,255,0.5)')
                  .attr('stroke-width', 1);
            brushG.selectAll('.handle')
                  .attr('fill', 'rgba(120,160,255,0.75)')
                  .attr('rx', 3);

            // Set initial selection without triggering brush event
            brushG.call(brushX.move, [xB(winStart), xB(winEnd)]);
        }

        // ── Zoom (wheel) + pan (drag) on the main chart ────────────────────
        // xFull is a fixed reference spanning the whole data range — zoom
        // transforms are always expressed relative to it, never to the
        // (mutable) current xM, so repeated zoom/pan gestures stay consistent.
        let suppressSync = false;
        const xFull = d3.scaleTime().domain([tMin, tMax]).range([0, iW]);

        const zoom = d3.zoom()
            .scaleExtent([1, 500])
            .translateExtent([[0, 0], [iW, iH]])
            .extent([[0, 0], [iW, iH]])
            .on('zoom', event => {
                if (suppressSync) return;
                const rescaled = event.transform.rescaleX(xFull);
                xM.domain(rescaled.domain());
                redraw();
                hideTooltip();
                if (brushX && brushG) {
                    suppressSync = true;
                    brushG.call(brushX.move, xM.domain().map(xB));
                    suppressSync = false;
                }
            });

        const overlay = gM.append('rect').attr('width', iW).attr('height', iH)
          .attr('fill', 'transparent').style('cursor', 'crosshair')
          .call(zoom)
          .on('mousemove', showTooltip)
          .on('mouseleave', hideTooltip);
    }

    function wireResizeObserver(mainCt) {
        if (mainCt._mgResizeObserver) return;
        let timer = null;
        mainCt._mgResizeObserver = new ResizeObserver(() => {
            clearTimeout(timer);
            timer = setTimeout(() => {
                const cached = _mgState.get(mainCt);
                if (cached && mainCt.isConnected) render(mainCt, cached.brushCt, cached.series, cached.windowHours);
            }, 120);
        });
        mainCt._mgResizeObserver.observe(mainCt);
    }

    function destroy(mainCt) {
        if (mainCt && mainCt._mgResizeObserver) {
            mainCt._mgResizeObserver.disconnect();
            delete mainCt._mgResizeObserver;
        }
        if (mainCt) _mgState.delete(mainCt);
    }

    return { render, destroy };
})();

// ── Legend panel resize (right side drag handle) ──────────────────────────────
window.mgLegendResize = function (handleEl, legendEl) {
    if (!handleEl || !legendEl) return;
    handleEl.addEventListener('mousedown', e => {
        const startX = e.clientX;
        const startW = legendEl.offsetWidth;
        const onMove = e2 => {
            // handle is on the LEFT edge of the right panel → drag left = wider
            const w = Math.max(150, Math.min(520, startW + (startX - e2.clientX)));
            legendEl.style.width = w + 'px';
        };
        const onUp = () => {
            document.removeEventListener('mousemove', onMove);
            document.removeEventListener('mouseup',  onUp);
        };
        document.addEventListener('mousemove', onMove);
        document.addEventListener('mouseup',  onUp);
        e.preventDefault();
    });
};
