// Batch Monitor — Memory Sunburst Chart
// Renders a two-ring sunburst: outer ring = groups, inner ring = individual series.
// Data format: { total: number, groups: [{ name, value, color, children: [{ name, value, color }] }] }

const MemorySunburst = (() => {

    function destroy(container) {
        d3.select(container).selectAll('*').remove();
    }

    function render(container, data, opts) {
        destroy(container);
        if (!data || !data.groups || data.groups.length === 0) return;

        const rect  = container.getBoundingClientRect();
        const W     = rect.width  || 600;
        const H     = rect.height || 480;
        const cx    = W / 2;
        const cy    = H / 2;
        const R     = Math.min(cx, cy) * 0.88;
        const inner = R * 0.38;     // hole
        const mid   = R * 0.68;     // split between outer and inner ring

        const svg = d3.select(container).append('svg')
            .attr('width', W).attr('height', H);

        const g = svg.append('g').attr('transform', `translate(${cx},${cy})`);

        // ── build hierarchy ──────────────────────────────────────────────
        const root = { name: 'root', value: 0, children: data.groups };

        // flatten: outer arcs = groups, inner arcs = leaves
        const groups  = data.groups;
        const total   = data.total || d3.sum(groups, d => d.value);

        const pie  = d3.pie().value(d => d.value).sort(null).padAngle(0.012);
        const arc  = d3.arc().innerRadius(inner).outerRadius(mid);
        const arc2 = d3.arc().innerRadius(mid + 2).outerRadius(R);

        // tooltip
        const tip = d3.select(container).append('div')
            .style('position', 'absolute')
            .style('pointer-events', 'none')
            .style('background', 'rgba(20,20,20,0.85)')
            .style('color', '#e8e8e8')
            .style('padding', '6px 10px')
            .style('border-radius', '4px')
            .style('font-size', '12px')
            .style('line-height', '1.6')
            .style("font-family", "'JetBrains Mono','Cascadia Code',Consolas,monospace")
            .style('display', 'none')
            .style('z-index', '9999');

        function showTip(event, name, valueMb, pct) {
            tip.style('display', 'block')
               .html(`<strong>${name}</strong><br>${valueMb.toFixed(1)} MB &nbsp;(${pct.toFixed(1)}%)`);
            moveTip(event);
        }
        function moveTip(event) {
            const cr = container.getBoundingClientRect();
            tip.style('left',  (event.clientX - cr.left + 12) + 'px')
               .style('top',   (event.clientY - cr.top  - 10) + 'px');
        }
        function hideTip() { tip.style('display', 'none'); }

        // ── outer ring: groups ───────────────────────────────────────────
        const slices = pie(groups);

        g.selectAll('.arc-outer')
         .data(slices)
         .join('path')
         .attr('class', 'arc-outer')
         .attr('d', arc)
         .attr('fill', d => d.data.color || '#888')
         .attr('opacity', 0.9)
         .style('cursor', 'pointer')
         .on('mousemove', (event, d) => {
             showTip(event, d.data.name, d.data.value / 1e6, d.data.value / total * 100);
             moveTip(event);
         })
         .on('mouseleave', hideTip);

        // group labels (only if arc is wide enough)
        g.selectAll('.label-outer')
         .data(slices)
         .join('text')
         .attr('class', 'label-outer')
         .attr('transform', d => `translate(${arc.centroid(d)})`)
         .attr('text-anchor', 'middle')
         .attr('dy', '0.35em')
         .attr('font-size', d => {
             const span = d.endAngle - d.startAngle;
             return span > 0.35 ? '11px' : '0px';
         })
         .attr('fill', '#fff')
         .attr('pointer-events', 'none')
         .text(d => {
             const span = d.endAngle - d.startAngle;
             if (span < 0.3) return '';
             const name = d.data.name;
             return name.length > 12 ? name.slice(0, 11) + '…' : name;
         });

        // ── inner ring: children (leaves) ───────────────────────────────
        // For each group slice we partition its angular range among its children.
        slices.forEach(slice => {
            const children = slice.data.children;
            if (!children || children.length === 0) return;
            const gTotal = d3.sum(children, c => c.value);
            const span   = slice.endAngle - slice.startAngle;
            let cursor   = slice.startAngle + (slice.data.padAngle || 0) / 2;

            const childArcs = children.map(c => {
                const a0 = cursor;
                const a1 = cursor + (c.value / gTotal) * span;
                cursor   = a1;
                return { data: c, startAngle: a0, endAngle: a1 };
            });

            g.selectAll(null)
             .data(childArcs)
             .join('path')
             .attr('d', arc2)
             .attr('fill', d => d.data.color || slice.data.color || '#888')
             .attr('opacity', 0.85)
             .style('cursor', 'pointer')
             .on('mousemove', (event, d) => {
                 showTip(event, d.data.name, d.data.value / 1e6, d.data.value / total * 100);
                 moveTip(event);
             })
             .on('mouseleave', hideTip);
        });

        // ── centre label ─────────────────────────────────────────────────
        const totalMb = total / 1e6;
        g.append('text').attr('text-anchor', 'middle').attr('dy', '-0.4em')
         .attr('font-size', '11px').attr('fill', 'var(--mud-palette-text-secondary)')
         .text('TOTAL MEMORY');
        g.append('text').attr('text-anchor', 'middle').attr('dy', '1.1em')
         .attr('font-size', '22px').attr('font-weight', '600')
         .attr('fill', 'var(--mud-palette-text-primary)')
         .text(totalMb.toFixed(1) + ' MB');
    }

    return { render, destroy };
})();

window.memorySunburst = MemorySunburst;

// ── Line chart with brush navigator ──────────────────────────────────────────
// series: [{ name, color, timestamps: number[] (ms), values: number[] (MB) }]
// windowHours: null = show all, number = initial window size in hours
window.memoryLineChart = (() => {

    const MONO = "'JetBrains Mono','Cascadia Code',Consolas,monospace";

    function fmtY(v) { return v >= 1000 ? (v / 1024).toFixed(1) + 'G' : v.toFixed(0) + 'M'; }

    function render(mainCt, brushCt, series, windowHours) {
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
          })
          .on('mouseleave', () => { vline.attr('display', 'none'); tip.style('display', 'none'); });

        // ── Brush / time-range navigator ──────────────────────────────────
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

        // Brush
        const brushX = d3.brushX()
            .extent([[0, 0], [biW, biH]])
            .on('brush end', ({ selection, sourceEvent }) => {
                if (!selection || !sourceEvent) return;
                const [d0, d1] = selection.map(xB.invert);
                xM.domain([d0, d1]);
                drawAxes();
                parsed.forEach(s => paths[s.name].attr('d', lineGen));
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

        // Set initial selection without triggering brush event
        brushG.call(brushX.move, [xB(winStart), xB(winEnd)]);
    }

    return { render };
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
