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

const _instances = new WeakMap(); // container -> { svg, tooltip, resizeObserver, points, options, resizeTimer }

// Below this, a per-run bar (group-by None) becomes hard to see/click, and — worse —
// stacking more envs just makes every bar thinner instead of visibly adding more bars
// (the original bug report). Bars are guaranteed at least this wide; the chart grows
// wider than its container and scrolls horizontally instead of ever going below it.
const MIN_RUN_BAR_PX = 6;

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

// Bucket key + display label for a point's day ("yyyy-MM-dd"), at the given granularity.
// Week buckets to their Monday; Month buckets to the 1st. Buckets are ordered by key
// since all three formats ("yyyy-MM-dd" / week-Monday "yyyy-MM-dd" / "yyyy-MM") sort
// chronologically as plain strings.
function bucketOf(day, granularity) {
    if (granularity === 'Month') return day.slice(0, 7);
    if (granularity === 'Week') {
        const d = new Date(`${day}T00:00:00Z`);
        const dow = (d.getUTCDay() + 6) % 7; // 0 = Monday
        d.setUTCDate(d.getUTCDate() - dow);
        return d.toISOString().slice(0, 10);
    }
    return day; // 'Day' or 'None' (per-run bars still bucket by day for line alignment)
}

function bucketLabel(key, granularity) {
    if (granularity === 'Month') return key;
    if (granularity === 'Week') return `Wk ${key}`;
    return key;
}

// Draws a temporary smooth line through one env's per-bucket average duration (from the
// already-aggregated `bars` array), floating at each bucket's band midpoint like the main
// average line — shown only while hovering that env's bar, cleared on mouseleave.
function showEnvAvgLine(layer, bars, env, color, x0, yAxisScale) {
    const pts = bars
        .filter(b => b.env === env)
        .map(b => ({ bucket: b.bucket, avg: b.avgDuration, x: x0(b.bucket) + x0.bandwidth() / 2 }))
        .sort((a, b) => a.bucket.localeCompare(b.bucket));

    layer.selectAll('*').remove();
    if (pts.length === 0) return;

    const lineGen = d3.line().x(d => d.x).y(d => yAxisScale(d.avg)).curve(d3.curveMonotoneX);

    layer.append('path')
        .datum(pts)
        .attr('class', 'bm-runstats-envline')
        .attr('fill', 'none')
        .attr('stroke', color || '#FFFFFF')
        .attr('d', lineGen);

    layer.selectAll('.bm-runstats-envline-point').data(pts).join('circle')
        .attr('class', 'bm-runstats-envline-point')
        .attr('cx', d => d.x)
        .attr('cy', d => yAxisScale(d.avg))
        .attr('r', 3)
        .attr('fill', color || '#FFFFFF');
}

function render(container, points, options) {
    const { groupBy = 'None', colorByEnv = false } = options || {};
    const existing = _instances.get(container);
    const resizeObserver = existing?.resizeObserver; // kept alive across re-renders — only dispose() tears it down
    clearTimeout(existing?.resizeTimer);
    clearDom(container);
    if (!container || !points || points.length === 0) {
        if (container) {
            d3.select(container).append('div').attr('class', 'bm-muted bm-runstats-empty').text('No runs match the current filter.');
            // Keep the map entry (with the now-empty points) up to date so a pending/
            // future resize doesn't resurrect stale data from before the filter emptied it.
            _instances.set(container, { resizeObserver, points: points ?? [], options, mode: null });
        }
        return;
    }

    const containerWidth = Math.max(320, container.clientWidth || 800);
    const height  = Math.max(280, container.clientHeight || 420);
    const margin  = { top: 16, right: 16, bottom: 46, left: 56 };
    const innerH  = height - margin.top - margin.bottom;

    // Line granularity always follows the bar bucketing (even in 'None' mode, where bars
    // are per-run but the average line still buckets/floats at day granularity) — this is
    // what keeps the line's points aligned under the center of whatever band they summarize.
    const lineGranularity = groupBy === 'None' ? 'Day' : groupBy;
    for (const p of points) p._bucket = bucketOf(p.day, lineGranularity);

    const buckets = [...new Set(points.map(p => p._bucket))].sort();
    const grouped = groupBy !== 'None';

    // Ungrouped (None) mode's per-bucket bandwidth gets stretched (see neededBandwidth
    // below) to fit the busiest day's run count at a legible minimum width — a FIXED
    // padding fraction of that (now much wider) bandwidth turned into a huge absolute gap
    // between days, especially next to a sparse day. Grouped mode's bars stay a fixed,
    // legible width regardless of bucket count, so its bandwidth never inflates the same
    // way — its padding can stay larger without ballooning in absolute pixels.
    const X0_PAD_INNER = grouped ? 0.12 : 0.04, X0_PAD_OUTER = 0.04;

    // (bucket, env) -> average duration — feeds the hover-triggered per-env average line in
    // BOTH modes (grouped bars already compute their own via `bars`; ungrouped/None mode has
    // no per-(bucket,env) aggregate otherwise, since its bars are per-run).
    const envBucketAvgs = [];
    for (const [bucket, byEnv] of d3.rollup(points, v => d3.mean(v, d => d.durationSeconds), d => d._bucket, d => d.env))
        for (const [env, avgDuration] of byEnv)
            envBucketAvgs.push({ bucket, env, avgDuration });

    // Content-driven width: in ungrouped (None) mode, sub-band width is shared across
    // every run in a bucket, so a fixed container width means MORE envs/runs = THINNER
    // bars, not visibly more of them (the reported bug). Compute the minimum total width
    // needed to keep every per-run bar at least MIN_RUN_BAR_PX wide, and only stretch to
    // fill the container when the content actually fits — otherwise render at the wider
    // natural size and let the container (CSS overflow-x:auto) scroll horizontally.
    let innerW = containerWidth - margin.left - margin.right;
    if (!grouped && buckets.length > 0) {
        const maxPerBucket = d3.max([...d3.group(points, p => p._bucket).values()], v => v.length) || 1;
        const neededBandwidth = maxPerBucket * MIN_RUN_BAR_PX / (1 - 0.08); // 0.08 = x1's own padding
        const n = buckets.length;
        const neededInnerW = (neededBandwidth / (1 - X0_PAD_INNER)) * (n - X0_PAD_INNER + 2 * X0_PAD_OUTER);
        innerW = Math.max(innerW, neededInnerW);
    }
    const width = innerW + margin.left + margin.right;
    const scrolling = width > containerWidth + 0.5; // content wider than the container — scroll, don't stretch

    const x0 = d3.scaleBand().domain(buckets).range([0, innerW]).paddingInner(X0_PAD_INNER).paddingOuter(X0_PAD_OUTER);

    // viewBox (not fixed pixel width/height) + CSS width:100%/height:100% (see app.css) is
    // what makes this scale to fill its container instead of overflowing it with scrollbars
    // — but only when the content actually fits. When it doesn't (see above), the svg gets
    // an explicit pixel width instead and the container scrolls horizontally to it, so bars
    // never get squeezed below MIN_RUN_BAR_PX just to avoid a scrollbar.
    const svg = d3.select(container).append('svg').attr('class', 'bm-runstats-svg');
    if (scrolling) {
        svg.attr('width', width).attr('height', height)
           .style('width', `${width}px`).style('height', '100%');
    } else {
        svg.attr('viewBox', `0 0 ${width} ${height}`).attr('preserveAspectRatio', 'none');
    }
    const g = svg.append('g').attr('transform', `translate(${margin.left},${margin.top})`);
    const tooltip = ensureTooltip(container);

    let bars;
    let maxY;

    if (grouped) {
        // Aggregate by (bucket, env): average duration, run count, failed count.
        const envs = [...new Set(points.map(p => p.env))];
        const envColor = new Map(points.map(p => [p.env, p.envColor]));
        const rolled = d3.rollup(
            points,
            v => ({
                avgDuration: d3.mean(v, d => d.durationSeconds),
                count: v.length,
                failed: v.filter(d => d.status === 'Failed').length,
            }),
            d => d._bucket, d => d.env,
        );

        bars = [];
        for (const [bucket, byEnv] of rolled) {
            for (const [env, agg] of byEnv) {
                bars.push({ bucket, env, ...agg, failRate: agg.count > 0 ? agg.failed / agg.count : 0 });
            }
        }
        maxY = d3.max(bars, b => b.avgDuration) || 1;

        const x1 = d3.scaleBand().domain(envs).range([0, x0.bandwidth()]).padding(0.12);
        const y = d3.scaleLinear().domain([0, maxY]).nice().range([innerH, 0]);

        const barG = g.selectAll('.bm-runstats-bar').data(bars).join('g').attr('class', 'bm-runstats-bar')
            .attr('transform', d => `translate(${x0(d.bucket) + x1(d.env)},0)`);

        barG.append('rect')
            .attr('y', d => y(d.avgDuration))
            .attr('width', x1.bandwidth())
            .attr('height', d => innerH - y(d.avgDuration))
            .attr('fill', d => envColor.get(d.env) || '#546E7A')
            .attr('rx', 2);

        // Failure-rate overlay: a translucent red wash whose opacity scales with the
        // fraction of that bucket+env's runs that failed — the bar's height/color already
        // carries duration+env, so this is the only channel left for failure rate.
        barG.filter(d => d.failRate > 0).append('rect')
            .attr('y', d => y(d.avgDuration))
            .attr('width', x1.bandwidth())
            .attr('height', d => innerH - y(d.avgDuration))
            .attr('fill', '#F85149')
            .attr('opacity', d => d.failRate * 0.65)
            .attr('rx', 2);

        // Env envelope layer for the temporary per-env average line drawn on hover — kept
        // above the bars/legend but doesn't participate in the bars' own data join.
        const envLineLayer = g.append('g').attr('class', 'bm-runstats-envline-layer');

        barG.style('cursor', 'pointer')
            .on('mousemove', (event, d) => showTooltip(tooltip, container, event,
                `<strong>${esc(d.env)}</strong> · ${esc(bucketLabel(d.bucket, groupBy))}<br>` +
                `avg ${formatDuration(d.avgDuration)} · ${d.count} run${d.count === 1 ? '' : 's'}` +
                (d.failed > 0 ? `<br><span class="bm-runstats-tt-fail">${d.failed} failed (${Math.round(d.failRate * 100)}%)</span>` : '')))
            .on('mouseleave', () => hideTooltip(tooltip))
            .on('mouseenter.envfocus', (event, d) => {
                barG.classed('bm-runstats-bar-dim', b => b.env !== d.env);
                showEnvAvgLine(envLineLayer, bars, d.env, envColor.get(d.env), x0, yAxisScale);
            })
            .on('mouseleave.envfocus', () => {
                barG.classed('bm-runstats-bar-dim', false);
                envLineLayer.selectAll('*').remove();
            });

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
        // Per-run: one bar per run, grouped by day band, colored by status (default) or env (toggle).
        maxY = d3.max(points, p => p.durationSeconds) || 1;
        const y = d3.scaleLinear().domain([0, maxY]).nice().range([innerH, 0]);

        const byBucket = d3.group(points, p => p._bucket);
        const maxPerBucket = d3.max([...byBucket.values()], v => v.length) || 1;
        const x1 = d3.scaleBand().domain(d3.range(maxPerBucket)).range([0, x0.bandwidth()]).padding(0.08);

        for (const [, runs] of byBucket) runs.sort((a, b) => a.start.localeCompare(b.start));

        const barG = g.selectAll('.bm-runstats-bar').data(points).join('g').attr('class', 'bm-runstats-bar')
            .attr('transform', d => {
                const idx = byBucket.get(d._bucket).indexOf(d);
                return `translate(${x0(d._bucket) + x1(idx)},0)`;
            });

        // Env envelope layer for the temporary per-env average line drawn on hover — same
        // behavior as grouped mode, since every point already carries an env regardless of
        // whether bars are currently colored by status or by env.
        const envLineLayer = g.append('g').attr('class', 'bm-runstats-envline-layer');
        const runEnvColor = new Map(points.map(p => [p.env, p.envColor]));
        const multiEnv = new Set(points.map(p => p.env)).size > 1;

        barG.append('rect')
            .attr('y', d => y(d.durationSeconds))
            .attr('width', x1.bandwidth())
            .attr('height', d => innerH - y(d.durationSeconds))
            .attr('fill', d => colorByEnv ? (d.envColor || '#546E7A') : (STATUS_COLOR[d.status] || STATUS_COLOR.Unknown))
            .attr('rx', 2)
            .style('cursor', 'pointer')
            .on('mousemove', (event, d) => showTooltip(tooltip, container, event,
                `<strong>${esc(d.runId)}</strong> · ${esc(d.env)}<br>` +
                `${esc(d.description || '')}<br>` +
                `${esc(d.status)} · ${formatDuration(d.durationSeconds)}`))
            .on('mouseleave', () => hideTooltip(tooltip));

        if (multiEnv) {
            barG.style('cursor', 'pointer')
                .on('mouseenter.envfocus', (event, d) => {
                    barG.classed('bm-runstats-bar-dim', b => b.env !== d.env);
                    showEnvAvgLine(envLineLayer, envBucketAvgs, d.env, runEnvColor.get(d.env), x0, yAxisScale);
                })
                .on('mouseleave.envfocus', () => {
                    barG.classed('bm-runstats-bar-dim', false);
                    envLineLayer.selectAll('*').remove();
                });
        }

        if (colorByEnv) {
            const envs = [...new Set(points.map(p => p.env))];
            const envColor = new Map(points.map(p => [p.env, p.envColor]));
            g.append('g').attr('class', 'bm-runstats-legend')
                .selectAll('.bm-runstats-legend-item').data(envs).join('g')
                .attr('class', 'bm-runstats-legend-item')
                .attr('transform', (d, i) => `translate(${i * 90},${-margin.top + 2})`)
                .call(sel => {
                    sel.append('rect').attr('width', 10).attr('height', 10).attr('rx', 2).attr('fill', d => envColor.get(d) || '#546E7A');
                    sel.append('text').attr('x', 14).attr('y', 9).attr('class', 'bm-runstats-legend-text').text(d => d);
                });
        } else {
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
    }

    // Single combined average-duration line across the whole filtered dataset (not per-env
    // — the user filters by env if they want one), aggregated at the same granularity as
    // the bars and plotted "floating" at the horizontal midpoint of each bucket's band.
    const avgByBucket = d3.rollup(points, v => d3.mean(v, d => d.durationSeconds), d => d._bucket);
    const linePoints = buckets
        .filter(b => avgByBucket.has(b))
        .map(b => ({ bucket: b, avg: avgByBucket.get(b), x: x0(b) + x0.bandwidth() / 2 }));

    // Y-domain must cover both the bars and the average line.
    const yDomainMax = Math.max(maxY, d3.max(linePoints, d => d.avg) || 0) || 1;
    const yAxisScale = d3.scaleLinear().domain([0, yDomainMax]).nice().range([innerH, 0]);

    if (linePoints.length > 0) {
        const lineGen = d3.line()
            .x(d => d.x)
            .y(d => yAxisScale(d.avg))
            .curve(d3.curveMonotoneX);

        g.append('path')
            .datum(linePoints)
            .attr('class', 'bm-runstats-avgline')
            .attr('fill', 'none')
            .attr('d', lineGen);

        g.selectAll('.bm-runstats-avgpoint').data(linePoints).join('circle')
            .attr('class', 'bm-runstats-avgpoint')
            .attr('cx', d => d.x)
            .attr('cy', d => yAxisScale(d.avg))
            .attr('r', 3.5)
            .style('cursor', 'pointer')
            .on('mousemove', (event, d) => showTooltip(tooltip, container, event,
                `<strong>Average</strong> · ${esc(bucketLabel(d.bucket, lineGranularity))}<br>${formatDuration(d.avg)}`))
            .on('mouseleave', () => hideTooltip(tooltip));
    }

    // Axes.
    g.append('g').attr('class', 'bm-runstats-axis')
        .call(d3.axisLeft(yAxisScale).ticks(6).tickFormat(formatDuration));

    g.append('g').attr('class', 'bm-runstats-axis')
        .attr('transform', `translate(0,${innerH})`)
        .call(d3.axisBottom(x0).tickFormat(d => bucketLabel(d, lineGranularity)))
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
            entry.resizeTimer = setTimeout(() => render(container, entry.points, entry.options), 120);
        });
        ro.observe(container);
    }

    _instances.set(container, { svg, tooltip, resizeObserver: ro, points, options });
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
