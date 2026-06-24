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

// ── Line chart ────────────────────────────────────────────────────────────────
// series: [{ name, color, data: number[], labels: string[] }]
window.memoryLineChart = (() => {
    function render(container, series) {
        d3.select(container).selectAll('*').remove();
        if (!series || series.length === 0) return;

        const rect   = container.getBoundingClientRect();
        const W      = rect.width  || 600;
        const H      = rect.height || 400;
        const margin = { top: 16, right: 24, bottom: 36, left: 56 };
        const iw     = W - margin.left - margin.right;
        const ih     = H - margin.top  - margin.bottom;

        const svg = d3.select(container).append('svg')
            .attr('width', W).attr('height', H);
        const g = svg.append('g').attr('transform', `translate(${margin.left},${margin.top})`);

        const n = series[0].data.length;
        const x = d3.scaleLinear().domain([0, n - 1]).range([0, iw]);
        const allVals = series.flatMap(s => s.data);
        const yMax = Math.max(...allVals, 1);
        const y = d3.scaleLinear().domain([0, yMax * 1.08]).range([ih, 0]);

        // Grid lines
        g.append('g').attr('class', 'grid')
         .call(d3.axisLeft(y).ticks(5).tickSize(-iw).tickFormat(''))
         .selectAll('line').attr('stroke', 'rgba(128,128,128,0.15)');
        g.select('.grid .domain').remove();

        // Axes
        const labels = series[0].labels;
        g.append('g').attr('transform', `translate(0,${ih})`)
         .call(d3.axisBottom(x).ticks(Math.min(n, 10))
               .tickFormat(i => labels[Math.round(i)] || ''))
         .selectAll('text').attr('font-size', '10px');
        g.append('g')
         .call(d3.axisLeft(y).ticks(5).tickFormat(v => v >= 1000 ? (v/1024).toFixed(1)+'G' : v.toFixed(0)+'M'))
         .selectAll('text').attr('font-size', '10px');
        g.select('.domain').attr('stroke', 'rgba(128,128,128,0.3)');

        // Tooltip
        const tip = d3.select(container).append('div')
            .style('position', 'absolute').style('pointer-events', 'none')
            .style('background', 'rgba(20,20,20,0.88)').style('color', '#e8e8e8')
            .style('padding', '6px 10px').style('border-radius', '4px')
            .style('font-size', '11px').style('line-height', '1.7').style('display', 'none')
            .style('z-index', '9999')
            .style("font-family", "'JetBrains Mono','Cascadia Code',Consolas,monospace");

        // Lines
        const line = d3.line().x((_, i) => x(i)).y(d => y(d)).curve(d3.curveMonotoneX);
        series.forEach(s => {
            g.append('path').datum(s.data)
             .attr('fill', 'none').attr('stroke', s.color)
             .attr('stroke-width', 2).attr('d', line);
        });

        // Hover overlay
        const overlay = g.append('rect')
            .attr('width', iw).attr('height', ih)
            .attr('fill', 'transparent').style('cursor', 'crosshair');

        const vline = g.append('line').attr('y1', 0).attr('y2', ih)
            .attr('stroke', 'rgba(128,128,128,0.5)').attr('stroke-width', 1)
            .attr('display', 'none');

        overlay.on('mousemove', event => {
            const [mx] = d3.pointer(event);
            const idx  = Math.round(x.invert(mx));
            if (idx < 0 || idx >= n) return;
            vline.attr('x1', x(idx)).attr('x2', x(idx)).attr('display', null);

            const label = labels[idx] || '';
            const rows  = series.map(s => `<span style="color:${s.color}">■</span> ${s.name}: ${s.data[idx].toFixed(1)} MB`).join('<br>');
            tip.style('display', 'block').html(`<strong>${label}</strong><br>${rows}`);

            const cr = container.getBoundingClientRect();
            let lx = event.clientX - cr.left + 14;
            if (lx + 180 > W) lx = event.clientX - cr.left - 180;
            tip.style('left', lx + 'px').style('top', (event.clientY - cr.top - 20) + 'px');
        }).on('mouseleave', () => {
            vline.attr('display', 'none');
            tip.style('display', 'none');
        });
    }

    return { render };
})();
