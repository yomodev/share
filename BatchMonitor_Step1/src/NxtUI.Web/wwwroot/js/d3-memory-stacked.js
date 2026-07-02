// Batch Monitor — Memory Stacked Area Chart
// series: [{ name, color, timestamps: number[] (ms), values: number[] (MB) }]
// windowHours: null = show all, number = initial window size in hours

window.memoryStackedChart = (() => {

    const MONO = "'JetBrains Mono','Cascadia Code',Consolas,monospace";

    function fmtY(v) { return v >= 1000 ? (v / 1024).toFixed(1) + 'G' : v.toFixed(0) + 'M'; }

    // Linear interpolation of a sorted point array at time t
    function interpolate(points, t) {
        if (points.length === 0) return 0;
        if (t <= points[0].t) return points[0].v;
        if (t >= points[points.length - 1].t) return points[points.length - 1].v;
        let lo = 0, hi = points.length - 1;
        while (hi - lo > 1) {
            const mid = (lo + hi) >> 1;
            if (points[mid].t <= t) lo = mid; else hi = mid;
        }
        const p0 = points[lo], p1 = points[hi];
        const ratio = (t - p0.t) / (p1.t - p0.t);
        return p0.v + (p1.v - p0.v) * ratio;
    }

    function render(mainCt, brushCt, series, windowHours) {
        d3.select(mainCt).selectAll('*').remove();
        d3.select(brushCt).selectAll('*').remove();
        if (!series || series.length === 0) return;

        const parsed = series.map(s => ({
            key:    s.name,
            color:  s.color,
            points: s.timestamps.map((t, i) => ({ t: new Date(t), v: s.values[i] }))
                        .sort((a, b) => a.t - b.t)
        })).filter(s => s.points.length > 0);
        if (parsed.length === 0) return;

        // Union time axis — merge all series timestamps and sort
        const allMs = [...new Set(parsed.flatMap(s => s.points.map(p => p.t.getTime())))].sort((a, b) => a - b);
        const tMin  = new Date(allMs[0]);
        const tMax  = new Date(allMs[allMs.length - 1]);

        // Build a flat row per timestamp, each series value interpolated
        const keys = parsed.map(s => s.key);
        const rows = allMs.map(ms => {
            const t = ms;
            const row = { t: new Date(ms) };
            parsed.forEach(s => { row[s.key] = interpolate(s.points, t); });
            return row;
        });

        const colorMap = Object.fromEntries(parsed.map(s => [s.key, s.color]));

        const stack = d3.stack().keys(keys).order(d3.stackOrderNone).offset(d3.stackOffsetNone);
        const layers = stack(rows);
        const yMax = d3.max(rows, r => keys.reduce((sum, k) => sum + r[k], 0)) || 1;

        // Initial window
        let winStart = tMin, winEnd = tMax;
        if (windowHours > 0) {
            winEnd   = tMax;
            winStart = new Date(tMax.getTime() - windowHours * 3_600_000);
            if (winStart < tMin) winStart = tMin;
        }

        // ── Main chart ────────────────────────────────────────────────────────
        const mR = mainCt.getBoundingClientRect();
        const W  = mR.width  || 600;
        const H  = mR.height || 340;
        const mL = 58, mRt = 20, mT = 14, mB = 28;
        const iW = W - mL - mRt;
        const iH = H - mT - mB;

        const svgM = d3.select(mainCt).append('svg').attr('width', W).attr('height', H);
        svgM.append('defs').append('clipPath').attr('id', 'mg-stk-clip')
            .append('rect').attr('width', iW).attr('height', iH + 1);
        const gM = svgM.append('g').attr('transform', `translate(${mL},${mT})`);

        const xM = d3.scaleTime().domain([winStart, winEnd]).range([0, iW]);
        const yM = d3.scaleLinear().domain([0, yMax * 1.05]).range([iH, 0]);

        // Grid
        gM.append('g').attr('class', 'mg-grid')
          .call(d3.axisLeft(yM).ticks(5).tickSize(-iW).tickFormat(''))
          .selectAll('line').attr('stroke', 'rgba(128,128,128,0.12)');
        gM.select('.mg-grid .domain').remove();

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

        const areaGen = d3.area()
            .x(d => xM(d.data.t))
            .y0(d => yM(d[0]))
            .y1(d => yM(d[1]))
            .curve(d3.curveMonotoneX);

        const areasG = gM.append('g').attr('clip-path', 'url(#mg-stk-clip)');
        const areaPaths = {};
        layers.forEach(layer => {
            areaPaths[layer.key] = areasG.append('path')
                .datum(layer)
                .attr('fill', colorMap[layer.key] || '#888')
                .attr('fill-opacity', 0.72)
                .attr('stroke', colorMap[layer.key] || '#888')
                .attr('stroke-width', 0.8)
                .attr('stroke-opacity', 0.9)
                .attr('d', areaGen);
        });

        // Tooltip + crosshair
        const tip = d3.select(mainCt).append('div')
            .style('position', 'absolute').style('pointer-events', 'none')
            .style('background', 'rgba(18,18,18,0.92)').style('color', '#e8e8e8')
            .style('padding', '6px 10px').style('border-radius', '4px')
            .style('font-size', '11px').style('line-height', '1.7').style('display', 'none')
            .style('z-index', '9999').style('font-family', MONO);

        const vline = gM.append('line').attr('y1', 0).attr('y2', iH)
            .attr('stroke', 'rgba(160,160,160,0.4)').attr('stroke-width', 1).attr('display', 'none');

        gM.append('rect').attr('width', iW).attr('height', iH)
          .attr('fill', 'transparent').style('cursor', 'crosshair')
          .on('mousemove', event => {
              const [mx] = d3.pointer(event);
              const hover = xM.invert(mx).getTime();
              vline.attr('x1', mx).attr('x2', mx).attr('display', null);

              // Find closest row by time
              let closest = rows[0];
              let bestDiff = Math.abs(rows[0].t.getTime() - hover);
              for (const r of rows) {
                  const d = Math.abs(r.t.getTime() - hover);
                  if (d < bestDiff) { bestDiff = d; closest = r; }
              }
              const total = keys.reduce((s, k) => s + closest[k], 0);
              const rowsHtml = [...parsed].reverse().map(s =>
                  `<span style="color:${s.color}">■</span> ${s.key}: ${closest[s.key].toFixed(1)} MB`
              ).join('<br>');
              tip.style('display', 'block')
                 .html(`<strong>${d3.timeFormat('%H:%M:%S')(closest.t)}</strong> &nbsp;Σ ${total.toFixed(1)} MB<br>${rowsHtml}`);
              const cr = mainCt.getBoundingClientRect();
              let lx = event.clientX - cr.left + 14;
              if (lx + 220 > W) lx = event.clientX - cr.left - 230;
              tip.style('left', lx + 'px').style('top', (event.clientY - cr.top - 20) + 'px');
          })
          .on('mouseleave', () => { vline.attr('display', 'none'); tip.style('display', 'none'); });

        // ── Brush / navigator ─────────────────────────────────────────────────
        if (!brushCt) return;
        const bR  = brushCt.getBoundingClientRect();
        const bW  = bR.width  || W;
        const bH  = bR.height || 28;
        const bL  = mL, bRt2 = mRt, bT = 4, bB = 22;
        const biW = bW - bL - bRt2;
        const biH = bH - bT - bB;

        const svgB = d3.select(brushCt).append('svg').attr('width', bW).attr('height', bH);
        const gB   = svgB.append('g').attr('transform', `translate(${bL},${bT})`);

        const xB = d3.scaleTime().domain([tMin, tMax]).range([0, biW]);
        const yB = d3.scaleLinear().domain([0, yMax]).range([biH, 0]);

        gB.append('rect').attr('width', biW).attr('height', biH)
          .attr('fill', 'rgba(128,128,128,0.06)').attr('rx', 2);

        // Mini stacked areas inside the brush overview
        const miniArea = d3.area()
            .x(d => xB(d.data.t))
            .y0(d => yB(d[0]))
            .y1(d => yB(d[1]))
            .curve(d3.curveMonotoneX);

        layers.forEach(layer => {
            gB.append('path').datum(layer)
              .attr('fill', colorMap[layer.key] || '#888')
              .attr('fill-opacity', 0.6)
              .attr('d', miniArea);
        });

        gB.append('g').attr('transform', `translate(0,${biH})`)
          .call(d3.axisBottom(xB).ticks(8).tickFormat(d3.timeFormat('%H:%M')))
          .selectAll('text').attr('font-size', '9px');
        gB.selectAll('.domain').attr('stroke', 'rgba(128,128,128,0.25)');
        gB.selectAll('.tick line').attr('stroke', 'rgba(128,128,128,0.25)');

        const brushX = d3.brushX()
            .extent([[0, 0], [biW, biH]])
            .on('brush end', ({ selection, sourceEvent }) => {
                if (!selection || !sourceEvent) return;
                const [d0, d1] = selection.map(xB.invert);
                xM.domain([d0, d1]);
                drawAxes();
                layers.forEach(layer => areaPaths[layer.key].attr('d', areaGen));
                vline.attr('display', 'none');
            });

        const brushG = gB.append('g').call(brushX);
        brushG.select('.selection')
              .attr('fill', 'rgba(100,150,255,0.15)')
              .attr('stroke', 'rgba(100,150,255,0.5)')
              .attr('stroke-width', 1);
        brushG.selectAll('.handle')
              .attr('fill', 'rgba(120,160,255,0.75)')
              .attr('rx', 3);

        brushG.call(brushX.move, [xB(winStart), xB(winEnd)]);
    }

    return { render };
})();
