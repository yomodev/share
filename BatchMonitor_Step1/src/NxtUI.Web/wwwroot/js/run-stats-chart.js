// run-stats-chart.js — ES module, grouped-bar duration histogram for the Run Stats page.
// D3 itself is loaded globally (see _Host.cshtml's <script src="https://d3js.org/d3.v7.min.js">
// — same as d3-timeline.js/d3-graph.js), so this file uses the `d3` global directly
// rather than importing it as a package.

const STATUS_COLOR = {
    Running:    '#3FB950',
    Completed:  '#58A6FF',
    Failed:     '#F85149',
    Terminated: '#D29922',
    Purged:     '#8B949E',
    Unknown:    '#8B949E',
};

const _instances = new WeakMap(); // container -> { svg, tooltip, resizeObserver, points, mode, resizeTimer }

function formatDuration(totalSeconds) {
    const s = Math.max(0, Math.round(totalSeconds));
    const h = Math.floor(s / 3600);
    const m = Math.floor((s % 3600) / 60);
    const sec = s % 60;
    return h > 0
        ? `${h}:${String(m).padStart(2, '0')}:${String(sec).padStart(2, '0')}`
        : `${m}:${String(sec).padStart(2, '0')}`;
}

function ensureTooltip(container) {
    let tooltip = container.querySelector('.bm-runstats-tooltip');
    if (!tooltip) {
        tooltip = document.createElement('div');
        tooltip.className = 'bm-runstats-tooltip';
        container.appendChild(tooltip);
    }
    return tooltip;
}

function showTooltip(tooltip, container, event, html) {
    tooltip.innerHTML = html;
    tooltip.style.opacity = '1';
    const rect = container.getBoundingClientRect();
    tooltip.style.left = `${event.clientX - rect.left + 12}px`;
    tooltip.style.top = `${event.clientY - rect.top + 12}px`;
}

function hideTooltip(tooltip) {
    tooltip.style.opacity = '0';
}

function render(container, points, mode) {
    const existing = _instances.get(container);
    const resizeObserver = existing?.resizeObserver; // kept alive across re-renders — only dispose() tears it down
    clearTimeout(existing?.resizeTimer);
    clearDom(container);
    if (!container || !points || points.length === 0) {
        if (container) {
            d3.select(container).append('div').attr('class', 'bm-muted bm-runstats-empty').text('No runs match the current filter.');
            // Keep the map entry (with the now-empty points) up to date so a pending/
            // future resize doesn't resurrect stale data from before the filter emptied it.
            _instances.set(container, { resizeObserver, points: points ?? [], mode });
        }
        return;
    }

    const width  = Math.max(320, container.clientWidth || 800);
    const height = Math.max(280, container.clientHeight || 420);
    const margin = { top: 16, right: 16, bottom: 46, left: 56 };
    const innerW = width - margin.left - margin.right;
    const innerH = height - margin.top - margin.bottom;

    const days = [...new Set(points.map(p => p.day))].sort();
    const x0 = d3.scaleBand().domain(days).range([0, innerW]).paddingInner(0.25).paddingOuter(0.1);

    // viewBox (not fixed pixel width/height) + CSS width:100%/height:100% (see app.css)
    // is what makes this scale to fill its container instead of overflowing it with
    // scrollbars — the coordinate system below is still laid out in the measured
    // width/height, just presented at whatever size the container actually is.
    const svg = d3.select(container).append('svg')
        .attr('viewBox', `0 0 ${width} ${height}`)
        .attr('preserveAspectRatio', 'none')
        .attr('class', 'bm-runstats-svg');
    const g = svg.append('g').attr('transform', `translate(${margin.left},${margin.top})`);
    const tooltip = ensureTooltip(container);

    let bars;
    let maxY;

    if (mode === 'avgPerEnv') {
        // Aggregate by (day, env): average duration, run count, failed count.
        const envs = [...new Set(points.map(p => p.env))];
        const envColor = new Map(points.map(p => [p.env, p.envColor]));
        const grouped = d3.rollup(
            points,
            v => ({
                avgDuration: d3.mean(v, d => d.durationSeconds),
                count: v.length,
                failed: v.filter(d => d.status === 'Failed').length,
            }),
            d => d.day, d => d.env,
        );

        bars = [];
        for (const [day, byEnv] of grouped) {
            for (const [env, agg] of byEnv) {
                bars.push({ day, env, ...agg, failRate: agg.count > 0 ? agg.failed / agg.count : 0 });
            }
        }
        maxY = d3.max(bars, b => b.avgDuration) || 1;

        const x1 = d3.scaleBand().domain(envs).range([0, x0.bandwidth()]).padding(0.15);
        const y = d3.scaleLinear().domain([0, maxY]).nice().range([innerH, 0]);

        const barG = g.selectAll('.bm-runstats-bar').data(bars).join('g').attr('class', 'bm-runstats-bar')
            .attr('transform', d => `translate(${x0(d.day) + x1(d.env)},0)`);

        barG.append('rect')
            .attr('y', d => y(d.avgDuration))
            .attr('width', x1.bandwidth())
            .attr('height', d => innerH - y(d.avgDuration))
            .attr('fill', d => envColor.get(d.env) || '#546E7A')
            .attr('rx', 2);

        // Failure-rate overlay: a translucent red wash whose opacity scales with the
        // fraction of that day+env's runs that failed — the bar's height/color already
        // carries duration+env, so this is the only channel left for failure rate.
        barG.filter(d => d.failRate > 0).append('rect')
            .attr('y', d => y(d.avgDuration))
            .attr('width', x1.bandwidth())
            .attr('height', d => innerH - y(d.avgDuration))
            .attr('fill', '#F85149')
            .attr('opacity', d => d.failRate * 0.65)
            .attr('rx', 2);

        barG.style('cursor', 'pointer')
            .on('mousemove', (event, d) => showTooltip(tooltip, container, event,
                `<strong>${esc(d.env)}</strong> · ${esc(d.day)}<br>` +
                `avg ${formatDuration(d.avgDuration)} · ${d.count} run${d.count === 1 ? '' : 's'}` +
                (d.failed > 0 ? `<br><span class="bm-runstats-tt-fail">${d.failed} failed (${Math.round(d.failRate * 100)}%)</span>` : '')))
            .on('mouseleave', () => hideTooltip(tooltip));

        g.append('g').attr('class', 'bm-runstats-legend')
            .selectAll('.bm-runstats-legend-item').data(envs).join('g')
            .attr('class', 'bm-runstats-legend-item')
            .attr('transform', (d, i) => `translate(${i * 90},${-margin.top + 2})`)
            .call(sel => {
                sel.append('rect').attr('width', 10).attr('height', 10).attr('rx', 2).attr('fill', d => envColor.get(d) || '#546E7A');
                sel.append('text').attr('x', 14).attr('y', 9).attr('class', 'bm-runstats-legend-text').text(d => d);
            });
    }
    else {
        // Per-run: one bar per run, grouped by day, colored by status.
        maxY = d3.max(points, p => p.durationSeconds) || 1;
        const y = d3.scaleLinear().domain([0, maxY]).nice().range([innerH, 0]);

        const byDay = d3.group(points, p => p.day);
        const maxPerDay = d3.max([...byDay.values()], v => v.length) || 1;
        const x1 = d3.scaleBand().domain(d3.range(maxPerDay)).range([0, x0.bandwidth()]).padding(0.1);

        for (const [day, runs] of byDay) runs.sort((a, b) => a.start.localeCompare(b.start));

        const barG = g.selectAll('.bm-runstats-bar').data(points).join('g').attr('class', 'bm-runstats-bar')
            .attr('transform', d => {
                const idx = byDay.get(d.day).indexOf(d);
                return `translate(${x0(d.day) + x1(idx)},0)`;
            });

        barG.append('rect')
            .attr('y', d => y(d.durationSeconds))
            .attr('width', x1.bandwidth())
            .attr('height', d => innerH - y(d.durationSeconds))
            .attr('fill', d => STATUS_COLOR[d.status] || STATUS_COLOR.Unknown)
            .attr('rx', 2)
            .style('cursor', 'pointer')
            .on('mousemove', (event, d) => showTooltip(tooltip, container, event,
                `<strong>${esc(d.runId)}</strong> · ${esc(d.env)}<br>` +
                `${esc(d.description || '')}<br>` +
                `${esc(d.status)} · ${formatDuration(d.durationSeconds)}`))
            .on('mouseleave', () => hideTooltip(tooltip));

        const statuses = [...new Set(points.map(p => p.status))];
        g.append('g').attr('class', 'bm-runstats-legend')
            .selectAll('.bm-runstats-legend-item').data(statuses).join('g')
            .attr('class', 'bm-runstats-legend-item')
            .attr('transform', (d, i) => `translate(${i * 90},${-margin.top + 2})`)
            .call(sel => {
                sel.append('rect').attr('width', 10).attr('height', 10).attr('rx', 2).attr('fill', d => STATUS_COLOR[d] || STATUS_COLOR.Unknown);
                sel.append('text').attr('x', 14).attr('y', 9).attr('class', 'bm-runstats-legend-text').text(d => d);
            });
    }

    // Axes — shared by both modes.
    const yAxisScale = d3.scaleLinear().domain([0, maxY]).nice().range([innerH, 0]);
    g.append('g').attr('class', 'bm-runstats-axis')
        .call(d3.axisLeft(yAxisScale).ticks(6).tickFormat(formatDuration));

    g.append('g').attr('class', 'bm-runstats-axis')
        .attr('transform', `translate(0,${innerH})`)
        .call(d3.axisBottom(x0))
        .selectAll('text')
        .attr('transform', 'rotate(-30)')
        .style('text-anchor', 'end');

    // Re-render (not just visually stretch) on container resize — a stretched viewBox
    // alone would leave axis ticks/legend spacing laid out for the OLD size. Debounced
    // so continuous drag-resizing doesn't thrash D3.
    let ro = resizeObserver;
    if (!ro) {
        ro = new ResizeObserver(() => {
            const entry = _instances.get(container);
            if (!entry) return;
            clearTimeout(entry.resizeTimer);
            entry.resizeTimer = setTimeout(() => render(container, entry.points, entry.mode), 120);
        });
        ro.observe(container);
    }

    _instances.set(container, { svg, tooltip, resizeObserver: ro, points, mode });
}

function esc(s) {
    return String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

function clearDom(container) {
    if (container) container.innerHTML = '';
}

// Full teardown — disconnects the resize observer too, unlike the internal re-render
// path above (which keeps the same observer alive across render() calls).
function dispose(container) {
    if (!container) return;
    const entry = _instances.get(container);
    clearTimeout(entry?.resizeTimer);
    entry?.resizeObserver?.disconnect();
    _instances.delete(container);
    clearDom(container);
}

window.bmRunStatsChart = { render, dispose };
export default { render, dispose };
export { render, dispose };
