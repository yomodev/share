// Batch Monitor — Timeline Tab  (Step 10)
//
// Renders a multi-batch timeline using D3 v7 (no dagre — layout is manual).
//
// Architecture:
//   - One SVG fills the canvas div; horizontal zoom/pan is shared across all batches.
//   - Each batch gets a vertical "section": batch header, lane rows, tick scale.
//   - Below all sections: global range selector + global heatmap (fixed height, scrolls with sections).
//   - Zoom/pan: D3 zoom, x-axis only (scaleX drives everything; y unchanged).
//
// Key layout constants (all in pixels unless noted):
//   HEADER_H  = 28     — batch header row
//   LANE_H    = 26     — minimum lane height (one sub-row)
//   SUBROW_H  = 22     — individual sub-row within a lane
//   TICK_H    = 28     — tick scale strip at bottom of each batch
//   HEATMAP_H = 32     — global heatmap strip
//   SELECTOR_H= 24     — global range selector brush strip
//   LABEL_W   = 0      — no lane labels per spec (§5: "no lane labels")
//   BLOCK_RX  = 2      — message block corner radius
//   MIN_BLOCK_W = 2    — below this width blocks merge into LOD density

window.BatchMonitor = window.BatchMonitor || {};

window.BatchMonitor.Timeline = (function () {

    // ── Constants ─────────────────────────────────────────────────────────
    const HEADER_H    = 28;
    const LANE_H_MIN  = 26;
    const SUBROW_H    = 22;
    const SUBROW_PAD  = 2;
    const TICK_H      = 28;
    const HEATMAP_H   = 32;
    const SELECTOR_H  = 24;
    const SECTION_GAP = 16;
    const BLOCK_RX    = 2;
    const MIN_BLOCK_W = 2;
    const LOD_OPACITY_MIN = 0.25;
    const LOD_OPACITY_MAX = 0.85;
    const T           = 250;  // transition ms

    // Curated colour palette — deterministic hash maps colour-key → index.
    const PALETTE = [
        '#388BFD','#3FB950','#F0A500','#DB61A2','#A371F7',
        '#E06C75','#56B6C2','#D19A66','#61AFEF','#98C379',
        '#C678DD','#E5C07B','#BE5046','#2BBAC5','#FF8C42',
    ];

    const STATUS_COLOR = {
        done:       '#3FB950',
        inprogress: '#388BFD',
        error:      '#F85149',
    };

    const CURSOR_COLOR = '#FF00FF';
    const GRID_MAJOR   = '#30363D';
    const GRID_MINOR   = '#21262D';
    const SEPARATOR    = '#30363D';

    // ── State ─────────────────────────────────────────────────────────────

    let _state = null;

    // ── Public API ─────────────────────────────────────────────────────────

    function init(containerEl, options) {
        if (!containerEl) return;
        if (_state) dispose();

        const svg = d3.select(containerEl).append('svg')
            .attr('class', 'bm-tl-svg')
            .attr('width', '100%')
            .attr('height', '100%');

        // Clip path for the lane area (prevents blocks overflowing into headers/ticks).
        const defs = svg.append('defs');
        defs.append('clipPath').attr('id', 'bm-tl-lane-clip')
            .append('rect').attr('id', 'bm-tl-lane-clip-rect');

        const root  = svg.append('g').attr('class', 'bm-tl-root');
        const lanes = root.append('g').attr('class', 'bm-tl-lanes');
        const cursor= root.append('g').attr('class', 'bm-tl-cursor').style('pointer-events', 'none');
        const bottom= root.append('g').attr('class', 'bm-tl-bottom');

        // Tooltip div.
        const tooltip = d3.select(containerEl).append('div')
            .attr('class', 'bm-tl-tooltip')
            .style('opacity', 0)
            .style('pointer-events', 'none');

        // Cursor line elements.
        const cursorLine = cursor.append('line')
            .attr('class', 'bm-tl-cursor-line')
            .style('stroke', CURSOR_COLOR)
            .style('stroke-width', 1)
            .style('opacity', 0);

        const cursorLabels = cursor.append('g').attr('class', 'bm-tl-cursor-labels');

        // D3 zoom (x-only).
        const xScale = d3.scaleLinear();  // domain set in update
        let   xZoom  = d3.zoomIdentity;

        const zoom = d3.zoom()
            .scaleExtent([0.05, 200])
            .on('zoom', (event) => {
                xZoom = event.transform;
                renderWithZoom();
            });

        svg.call(zoom)
            .on('mousemove', onMouseMove)
            .on('mouseleave', onMouseLeave)
            .call(d3.drag()
                .on('start', () => svg.style('cursor', 'grabbing'))
                .on('end',   () => svg.style('cursor', 'crosshair')));

        svg.style('cursor', 'crosshair');

        // Keyboard shortcuts.
        const keydown = (e) => handleKey(e);
        document.addEventListener('keydown', keydown);

        // Cursor fade timer.
        let cursorTimer = null;

        _state = {
            containerEl, svg, root, lanes, cursor, bottom, tooltip,
            cursorLine, cursorLabels, defs,
            xScale, xZoom, zoom,
            data: null,
            layout: null,
            cursorTimer,
            keydownFn: keydown,
        };

        // Resize observer on the scrollable wrapper (parent of canvas).
        const ro = new ResizeObserver(() => {
            if (_state) { computeSvgSize(); updateSvgHeight(); renderWithZoom(); renderBottom(); }
        });
        const wrapper = containerEl.parentElement || containerEl;
        ro.observe(wrapper);
        _state.resizeObserver = ro;

        computeSvgSize();
    }

    function update(payload) {
        if (!_state) return;
        _state.data = payload;
        computeLayout();
        renderAll();
    }

    function resetView() {
        if (!_state) return;
        _state.svg.call(_state.zoom.transform, d3.zoomIdentity);
    }

    function dispose() {
        if (!_state) return;
        document.removeEventListener('keydown', _state.keydownFn);
        _state.resizeObserver?.disconnect();
        // Clear SVG contents but don't remove the element itself — Blazor
        // owns the container div and will crash with "removeChild" if D3
        // removes a child element that Blazor still references.
        _state.svg.selectAll('*').remove();
        _state.tooltip.remove();
        clearTimeout(_state.cursorTimer);
        _state = null;
    }

    function exportCsv() {
        if (!_state?.data) return;
        const rows = [];
        rows.push('RunId,BatchName,ChunkId,Source,Pipeline,Service,PID,Server,StartAbs,FinishAbs,StartRelMs,FinishRelMs,DurationMs,Status,Error');

        for (const batch of (_state.data.batches || [])) {
            for (const e of (batch.events || [])) {
                const finishMs  = e.finishMs ?? '';
                const durMs     = e.finishMs != null ? (e.finishMs - e.startMs).toFixed(0) : '';
                rows.push([
                    csv(batch.runId), csv(batch.batchName), csv(e.chunkId),
                    csv(e.source), csv(e.pipeline), csv(e.service),
                    csv(e.processId), csv(e.server),
                    '', '',   // StartAbs / FinishAbs (not available client-side without batch start date)
                    e.startMs.toFixed(1), finishMs, durMs,
                    csv(e.status), csv(e.error ?? ''),
                ].join(','));
            }
        }

        const blob = new Blob([rows.join('\n')], { type: 'text/csv' });
        const url  = URL.createObjectURL(blob);
        const a    = document.createElement('a');
        a.href = url; a.download = 'timeline.csv'; a.click();
        URL.revokeObjectURL(url);
    }

    function csv(v) { return `"${String(v ?? '').replace(/"/g, '""')}"`; }

    // ── SVG sizing ────────────────────────────────────────────────────────

    function computeSvgSize() {
        if (!_state) return;
        const rect = _state.containerEl.getBoundingClientRect();
        _state.svgW  = rect.width  || 800;
        // Viewport height = parent wrapper's visible height (for heatmap positioning).
        _state.viewH = _state.containerEl.parentElement?.clientHeight || rect.height || 600;

        _state.defs.select('#bm-tl-lane-clip-rect')
            .attr('width', _state.svgW)
            .attr('height', _state.viewH);
    }

    function updateSvgHeight() {
        if (!_state?.layout) return;
        const contentH = _state.layout.totalLanesH + SELECTOR_H + HEATMAP_H + 16;
        _state.svgH = Math.max(_state.viewH || 600, contentH);
        _state.svg.attr('height', _state.svgH);

        _state.defs.select('#bm-tl-lane-clip-rect')
            .attr('width', _state.svgW)
            .attr('height', _state.svgH);
    }

    // ── Layout computation ────────────────────────────────────────────────

    function computeLayout() {
        if (!_state?.data) return;

        const { data } = _state;
        const batches  = data.batches || [];
        const groupBy  = data.groupBy  || 'ServicePipeline';
        const colourBy = data.colourBy || 'Source';
        const filter   = (data.filter || '').toLowerCase();
        const stack    = data.stackView || false;

        // Build per-batch layout sections.
        const sections = batches.map(batch => buildSection(batch, groupBy, colourBy, filter, stack));

        // Global time domain: union of all batch relative ranges.
        let globalMax = 0;
        for (const sec of sections) globalMax = Math.max(globalMax, sec.domainMax);
        if (globalMax <= 0) globalMax = 1000;

        _state.xScale.domain([0, globalMax]);

        // Assign Y offsets to sections (stacked vertically).
        let y = 0;
        for (const sec of sections) {
            sec.y = y;
            y += HEADER_H + sec.laneHeight + TICK_H + SECTION_GAP;
        }

        const totalLanesH = y;
        _state.layout = { sections, globalMax, totalLanesH };
    }

    function buildSection(batch, groupBy, colourBy, filter, stack) {
        // Group events by the GroupBy key.
        const groups = new Map();
        for (const e of (batch.events || [])) {
            if (filter && !matchesFilter(e, filter)) continue;
            const key = groupKey(e, groupBy);
            if (!groups.has(key)) groups.set(key, []);
            groups.get(key).push(e);
        }

        const lanes = [];
        let laneHeight = 0;

        for (const [key, events] of groups) {
            const subrows = packSubrows(events, stack);
            const h = Math.max(LANE_H_MIN, subrows.length * SUBROW_H);
            lanes.push({ key, events, subrows, h });
            laneHeight += h;
        }
        if (lanes.length === 0) laneHeight = LANE_H_MIN;

        // Domain max for this batch.
        let domainMax = 0;
        for (const e of (batch.events || [])) {
            domainMax = Math.max(domainMax, e.finishMs ?? e.startMs);
        }

        return { batch, lanes, laneHeight, domainMax, y: 0 };
    }

    function matchesFilter(e, filter) {
        return (e.chunkId   || '').toLowerCase().includes(filter)
            || (e.source    || '').toLowerCase().includes(filter)
            || (e.pipeline  || '').toLowerCase().includes(filter)
            || (e.service   || '').toLowerCase().includes(filter)
            || (e.processId || '').toLowerCase().includes(filter)
            || (e.server    || '').toLowerCase().includes(filter);
    }

    function groupKey(e, groupBy) {
        switch (groupBy) {
            case 'ServicePipeline':    return `${e.service} / ${e.pipeline}`;
            case 'PidServicePipeline': return `${e.server}:${e.processId} / ${e.service} / ${e.pipeline}`;
            case 'Service':            return e.service;
            case 'Pipeline':           return e.pipeline;
            case 'Pid':                return `${e.server}:${e.processId}`;
            default:                   return `${e.service} / ${e.pipeline}`;
        }
    }

    // ── Sub-row packing ───────────────────────────────────────────────────
    // Greedy interval packing: assign each event to the first sub-row
    // where it doesn't overlap with already-placed events.
    // Capped at MAX_SUBROWS per lane — events that don't fit become LOD.

    const MAX_SUBROWS = 8;

    function packSubrows(events, stack) {
        const sorted = [...events].sort((a, b) => a.startMs - b.startMs);
        const subrows = [];

        for (const e of sorted) {
            const xStart = e.startMs;
            const xEnd   = e.finishMs ?? e.startMs + 1;

            let placed = false;
            for (const row of subrows) {
                const last = row[row.length - 1];
                if (xStart >= last.xEnd) {
                    row.push({ event: e, xStart, xEnd });
                    placed = true;
                    break;
                }
            }
            // Only create a new subrow if under the cap; otherwise let the
            // block render as LOD (width < MIN_BLOCK_W check in renderBlocks).
            if (!placed && subrows.length < MAX_SUBROWS) {
                subrows.push([{ event: e, xStart, xEnd }]);
            } else if (!placed) {
                // Add to the last subrow — it will likely render as LOD.
                subrows[subrows.length - 1].push({ event: e, xStart, xEnd });
            }
        }

        return subrows;
    }

    // ── Colour mapping ────────────────────────────────────────────────────

    function colourKey(e, colourBy) {
        switch (colourBy) {
            case 'Pipeline': return e.pipeline;
            case 'Service':  return e.service;
            case 'Status':   return e.status;
            default:         return e.source;
        }
    }

    function colourForKey(key, colourBy) {
        if (colourBy === 'Status') return STATUS_COLOR[key] || '#8B949E';
        return PALETTE[hashStr(key) % PALETTE.length];
    }

    function hashStr(s) {
        let h = 0;
        for (let i = 0; i < s.length; i++) h = Math.imul(31, h) + s.charCodeAt(i) | 0;
        return Math.abs(h);
    }

    // ── Full render ────────────────────────────────────────────────────────

    function renderAll() {
        if (!_state?.layout) return;
        computeSvgSize();
        updateSvgHeight();
        _state.xScale.range([0, _state.svgW]);
        renderWithZoom();
        renderBottom();
    }

    function renderWithZoom() {
        if (!_state?.layout) return;
        const { sections } = _state.layout;
        const xS = _state.xZoom.rescaleX(_state.xScale);

        const secSel = _state.lanes.selectAll('g.bm-tl-section')
            .data(sections, d => d.batch.runId);

        secSel.exit().transition().duration(T).style('opacity', 0).remove();

        const secEnter = secSel.enter().append('g')
            .attr('class', 'bm-tl-section')
            .attr('transform', d => `translate(0,${d.y})`)
            .style('opacity', 0);

        secEnter.append('g').attr('class', 'bm-tl-grid');
        secEnter.append('g').attr('class', 'bm-tl-blocks');
        secEnter.append('g').attr('class', 'bm-tl-batch-header');
        secEnter.append('g').attr('class', 'bm-tl-tick-scale');

        // Fade in only on first appearance.
        secEnter.transition().duration(T).style('opacity', 1);

        const secMerged = secEnter.merge(secSel);

        // Existing sections: update position immediately, no opacity transition.
        secSel.style('opacity', 1)
            .transition().duration(T)
            .attr('transform', d => `translate(0,${d.y})`);

        secMerged.each(function(sec) {
            renderSection(d3.select(this), sec, xS);
        });
    }

    function renderSection(sel, sec, xS) {
        renderBatchHeader(sel.select('.bm-tl-batch-header'), sec);
        renderGrid(sel.select('.bm-tl-grid'), sec, xS);
        renderBlocks(sel.select('.bm-tl-blocks'), sec, xS);
        renderTickScale(sel.select('.bm-tl-tick-scale'), sec, xS);
    }

    // ── Batch header ──────────────────────────────────────────────────────

    function renderBatchHeader(sel, sec) {
        sel.selectAll('*').remove();

        // Separator line.
        sel.append('line')
            .attr('x1', 0).attr('x2', _state.svgW)
            .attr('y1', HEADER_H - 0.5).attr('y2', HEADER_H - 0.5)
            .style('stroke', SEPARATOR).style('stroke-width', 1);

        // LIVE dot.
        const liveX = 10;
        if (sec.batch.isLive) {
            sel.append('circle').attr('class', 'bm-tl-live-dot')
                .attr('cx', liveX).attr('cy', HEADER_H / 2)
                .attr('r', 5).style('fill', '#3FB950');
        }

        // Batch name + RunId.
        const textX = sec.batch.isLive ? liveX + 12 : liveX;
        sel.append('text').attr('class', 'bm-tl-batch-name')
            .attr('x', textX).attr('y', HEADER_H / 2)
            .attr('dy', '0.32em')
            .text(sec.batch.batchName);

        sel.append('text').attr('class', 'bm-tl-batch-runid')
            .attr('x', textX + measureText(sec.batch.batchName, '0.82rem', 600) + 10)
            .attr('y', HEADER_H / 2)
            .attr('dy', '0.32em')
            .text(sec.batch.runId);
    }

    function measureText(text, size, weight) {
        // Rough estimate: 0.55 × font-size × char count.
        const px = parseFloat(size) * 16 * 0.55;
        return px * text.length;
    }

    // ── Background grid ────────────────────────────────────────────────────

    function renderGrid(sel, sec, xS) {
        sel.selectAll('*').remove();

        const top    = HEADER_H;
        const height = sec.laneHeight + TICK_H;

        const ticks = xS.ticks(Math.floor(_state.svgW / 80));

        // Minor ticks (every step between major ticks).
        const step   = ticks.length > 1 ? ticks[1] - ticks[0] : 0;
        const minorStep = step / 5;
        if (minorStep > 0) {
            const minorTicks = d3.range(
                Math.floor(xS.invert(0) / minorStep) * minorStep,
                xS.invert(_state.svgW) + minorStep,
                minorStep
            );
            sel.selectAll('line.minor').data(minorTicks).enter()
                .append('line').attr('class', 'minor')
                .attr('x1', d => xS(d)).attr('x2', d => xS(d))
                .attr('y1', top).attr('y2', top + height)
                .style('stroke', GRID_MINOR).style('stroke-width', 0.5);
        }

        // Major ticks.
        sel.selectAll('line.major').data(ticks).enter()
            .append('line').attr('class', 'major')
            .attr('x1', d => xS(d)).attr('x2', d => xS(d))
            .attr('y1', top).attr('y2', top + height)
            .style('stroke', GRID_MAJOR).style('stroke-width', 1);

        // Lane separator horizontal lines.
        let laneY = HEADER_H;
        for (const lane of sec.lanes) {
            sel.append('line')
                .attr('x1', 0).attr('x2', _state.svgW)
                .attr('y1', laneY).attr('y2', laneY)
                .style('stroke', SEPARATOR).style('stroke-width', 1)
                .style('stroke-dasharray', '4 4');
            laneY += lane.h;
        }
    }

    // ── Message blocks ─────────────────────────────────────────────────────

    function renderBlocks(sel, sec, xS) {
        const colourBy = _state.data?.colourBy || 'Source';

        // Flatten all events with their absolute y positions.
        const blockData = [];
        let laneY = HEADER_H;

        for (const lane of sec.lanes) {
            let subrowY = laneY;
            for (const subrow of lane.subrows) {
                for (const { event: e } of subrow) {
                    const x  = xS(e.startMs);
                    const x2 = xS(e.finishMs ?? (e.startMs + (_state.layout?.globalMax ?? 1000)));
                    const w  = Math.max(0, x2 - x);
                    const key = colourKey(e, colourBy);
                    blockData.push({
                        e, x, w, key,
                        y: subrowY + SUBROW_PAD,
                        h: SUBROW_H - SUBROW_PAD * 2,
                        colour: colourForKey(key, colourBy),
                        lod: w < MIN_BLOCK_W,
                    });
                }
                subrowY += SUBROW_H;
            }
            laneY += lane.h;
        }

        // Viewport culling: only render blocks visible in [0, svgW].
        const visible = blockData.filter(d => d.x < _state.svgW && d.x + Math.max(d.w, 1) > 0);

        // Separate normal blocks from LOD blocks.
        const normal = visible.filter(d => !d.lod);
        const lod    = visible.filter(d => d.lod);

        // Normal blocks.
        const rects = sel.selectAll('rect.bm-tl-block').data(normal, d => d.e.chunkId + d.y);

        rects.exit().remove();

        rects.enter().append('rect').attr('class', 'bm-tl-block')
            .attr('rx', BLOCK_RX)
            .style('cursor', 'crosshair')
            .on('mouseenter', (event, d) => onBlockHover(event, d, sel, colourBy))
            .on('mouseleave', () => onBlockLeave(sel))
            .merge(rects)
            .attr('x', d => d.x)
            .attr('y', d => d.y)
            .attr('width', d => Math.max(1, d.w))
            .attr('height', d => d.h)
            .style('fill', d => d.colour)
            .style('stroke', d => d3.color(d.colour)?.darker(0.4) ?? d.colour)
            .style('stroke-width', d => d.e.status === 'inprogress' ? 0 : 0.5)
            .style('stroke-dasharray', d => d.e.status === 'inprogress' ? '3 3' : null)
            .style('fill-opacity', d => d.e.status === 'inprogress' ? 0.65 : 1);

        // LOD density bars (per-lane, vertical lines with opacity ∝ density).
        const lodBars = sel.selectAll('line.bm-tl-lod').data(lod, d => d.e.chunkId + d.y);
        lodBars.exit().remove();
        lodBars.enter().append('line').attr('class', 'bm-tl-lod')
            .merge(lodBars)
            .attr('x1', d => d.x).attr('x2', d => d.x)
            .attr('y1', d => d.y)
            .attr('y2', d => d.y + d.h)
            .style('stroke', d => d.colour)
            .style('stroke-width', 1)
            .style('opacity', LOD_OPACITY_MAX);
    }

    // ── Tick scale ─────────────────────────────────────────────────────────

    function renderTickScale(sel, sec, xS) {
        sel.selectAll('*').remove();

        const y = HEADER_H + sec.laneHeight;

        sel.append('line')
            .attr('x1', 0).attr('x2', _state.svgW)
            .attr('y1', y).attr('y2', y)
            .style('stroke', GRID_MAJOR).style('stroke-width', 1);

        const ticks = xS.ticks(Math.floor(_state.svgW / 80));
        const fmt   = d => {
            const ms = Math.round(d);
            const s  = Math.floor(ms / 1000);
            const m  = Math.floor(s / 60);
            const h  = Math.floor(m / 60);
            if (h > 0)  return `${h}h${String(m % 60).padStart(2,'0')}m`;
            if (m > 0)  return `${m}m${String(s % 60).padStart(2,'0')}s`;
            if (s > 0)  return `${s}s`;
            return `${ms}ms`;
        };

        for (const t of ticks) {
            const x = xS(t);
            sel.append('line')
                .attr('x1', x).attr('x2', x)
                .attr('y1', y).attr('y2', y + 6)
                .style('stroke', GRID_MAJOR).style('stroke-width', 1);

            sel.append('text').attr('class', 'bm-tl-tick-label')
                .attr('x', x).attr('y', y + 18)
                .attr('text-anchor', 'middle')
                .text(fmt(t));
        }
    }

    // ── Global heatmap + range selector (bottom) ──────────────────────────

    function renderBottom() {
        if (!_state?.layout) return;

        const { totalLanesH, globalMax } = _state.layout;
        const y0 = totalLanesH;
        const W  = _state.svgW;

        _state.bottom.attr('transform', `translate(0,${y0})`);
        _state.bottom.selectAll('*').remove();

        // Range selector brush.
        const brushY  = 0;
        const heatY   = SELECTOR_H;

        // Brush background.
        _state.bottom.append('rect')
            .attr('x', 0).attr('y', brushY)
            .attr('width', W).attr('height', SELECTOR_H)
            .style('fill', '#0E1117');

        // Brush: maps full domain → full SVG width, bidirectionally linked to zoom.
        const brushScale = d3.scaleLinear().domain([0, globalMax]).range([0, W]);

        const brush = d3.brushX()
            .extent([[0, brushY], [W, brushY + SELECTOR_H - 2]])
            .on('brush end', (event) => {
                if (event.sourceEvent?.type === 'zoom') return;
                if (!event.selection) return;
                const [x0, x1] = event.selection;
                const newXScale = _state.xScale.copy().range([
                    -x0 * (W / (x1 - x0)),
                    W - x0 * (W / (x1 - x0)),
                ]).domain([0, globalMax]);
                const t = d3.zoomIdentity
                    .scale(W / (x1 - x0))
                    .translate(-x0, 0);
                _state.svg.call(_state.zoom.transform, t);
            });

        _state.bottom.append('g').attr('class', 'bm-tl-brush').call(brush);

        // Reflect current zoom in brush selection.
        const xS = _state.xZoom.rescaleX(_state.xScale);
        const sel0 = Math.max(0, brushScale(xS.invert(0)));
        const sel1 = Math.min(W, brushScale(xS.invert(W)));
        if (sel1 > sel0 + 1) {
            _state.bottom.select('.bm-tl-brush').call(brush.move, [sel0, sel1]);
        }

        // Global heatmap below brush.
        renderHeatmap(_state.bottom, heatY, W);

        // Reset view label.
        _state.bottom.append('text').attr('class', 'bm-tl-reset-link')
            .attr('x', W - 6).attr('y', SELECTOR_H / 2)
            .attr('dy', '0.32em').attr('text-anchor', 'end')
            .text('↺ Reset view')
            .style('cursor', 'pointer')
            .on('click', resetView);
    }

    function renderHeatmap(parent, y, W) {
        if (!_state?.layout) return;

        const { globalMax } = _state.layout;
        const buckets = 200;
        const counts  = new Array(buckets).fill(0);

        for (const sec of _state.layout.sections) {
            for (const e of (sec.batch.events || [])) {
                const b = Math.floor((e.startMs / globalMax) * (buckets - 1));
                if (b >= 0 && b < buckets) counts[b]++;
            }
        }

        const maxCount = Math.max(1, ...counts);
        const bW = W / buckets;

        // Colour scale: #0E1117 → #388BFD → #3FB950 → #E6EDF3
        const heatScale = d3.scaleSequential()
            .domain([0, maxCount])
            .interpolator(d3.interpolateRgbBasis(['#0E1117','#388BFD','#3FB950','#E6EDF3']));

        parent.selectAll('rect.bm-tl-heat').data(counts).enter()
            .append('rect').attr('class', 'bm-tl-heat')
            .attr('x', (_, i) => i * bW)
            .attr('y', y)
            .attr('width', Math.ceil(bW) + 0.5)
            .attr('height', HEATMAP_H)
            .style('fill', d => d > 0 ? heatScale(d) : '#0E1117');
    }

    // ── Block hover ────────────────────────────────────────────────────────

    function onBlockHover(event, d, sel, colourBy) {
        // Highlight same-key blocks, dim others.
        sel.selectAll('rect.bm-tl-block')
            .style('opacity', b => colourKey(b.e, colourBy) === d.key ? 1 : 0.3)
            .style('stroke', b => colourKey(b.e, colourBy) === d.key
                ? d3.color(b.colour)?.brighter(0.5) ?? b.colour : b.colour)
            .style('stroke-width', b => colourKey(b.e, colourBy) === d.key ? '2px' : '0.5px');

        showTooltip(event, d);
    }

    function onBlockLeave(sel) {
        // Restore all blocks.
        sel.selectAll('rect.bm-tl-block')
            .transition().duration(150)
            .style('opacity', null)
            .style('stroke', b => d3.color(b.colour)?.darker(0.4) ?? b.colour)
            .style('stroke-width', '0.5px');

        hideTooltip();
    }

    function showTooltip(event, d) {
        const e = d.e;
        const dur  = e.finishMs != null ? `${Math.round(e.finishMs - e.startMs).toLocaleString()}ms` : '—';
        const statusLabel = e.status === 'done' ? '✓ Done'
            : e.status === 'inprogress' ? '⟳ In Progress' : '✗ Error';
        const statusClass = `bm-tl-tt-${e.status}`;
        const fmtMs = ms => {
            const s = ms / 1000;
            return `+${String(Math.floor(s/3600)).padStart(2,'0')}:${String(Math.floor((s%3600)/60)).padStart(2,'0')}:${(s%60).toFixed(3).padStart(6,'0')}`;
        };

        _state.tooltip.html(`
            <div class="bm-tl-tt-header">
                <span class="bm-tl-tt-id">${esc(e.chunkId)}</span>
                <span class="bm-tl-tt-status ${statusClass}">${statusLabel}</span>
            </div>
            <div class="bm-tl-tt-sep"></div>
            <div class="bm-tl-tt-row"><span>Source</span><span>${esc(e.source)}</span></div>
            <div class="bm-tl-tt-row"><span>Pipeline</span><span>${esc(e.pipeline)}</span></div>
            <div class="bm-tl-tt-row"><span>Service</span><span>${esc(e.service)}</span></div>
            <div class="bm-tl-tt-row"><span>PID</span><span>${esc(e.processId)}</span></div>
            <div class="bm-tl-tt-row"><span>Server</span><span>${esc(e.server)}</span></div>
            <div class="bm-tl-tt-row"><span>Start</span><span>${fmtMs(e.startMs)}</span></div>
            ${e.finishMs != null ? `<div class="bm-tl-tt-row"><span>End</span><span>${fmtMs(e.finishMs)}</span></div>` : ''}
            <div class="bm-tl-tt-row"><span>Duration</span><span>${dur}</span></div>
            ${e.error ? `<div class="bm-tl-tt-row bm-tl-tt-error"><span>Error</span><span>${esc(e.error)}</span></div>` : ''}
            <div class="bm-tl-tt-sep"></div>
            <div class="bm-tl-tt-footer" id="bm-tl-tt-footer">Ctrl+click to copy</div>
        `)
        .style('opacity', 1)
        .style('pointer-events', 'auto');

        // Ctrl+click to copy.
        _state.tooltip.on('click', (ev) => {
            if (!ev.ctrlKey) return;
            const text = [
                `ChunkId: ${e.chunkId}`, `Source: ${e.source}`,
                `Pipeline: ${e.pipeline}`, `Service: ${e.service}`,
                `PID: ${e.processId}`, `Server: ${e.server}`,
                `Status: ${e.status}`, `Duration: ${dur}`,
            ].join('\n');
            navigator.clipboard?.writeText(text);
            document.getElementById('bm-tl-tt-footer').textContent = 'Copied ✓';
            setTimeout(() => {
                const f = document.getElementById('bm-tl-tt-footer');
                if (f) f.textContent = 'Ctrl+click to copy';
            }, 1000);
        });

        positionTooltip(event);
    }

    function positionTooltip(event) {
        if (!_state) return;
        const rect = _state.containerEl.getBoundingClientRect();
        let x = event.clientX - rect.left + 12;
        let y = event.clientY - rect.top - 8;

        const ttW = 280, ttH = 200;
        if (x + ttW > rect.width  - 8) x = event.clientX - rect.left - ttW - 12;
        if (y + ttH > rect.height - 8) y = event.clientY - rect.top  - ttH - 12;
        if (y < 0) y = event.clientY - rect.top + 16;

        _state.tooltip.style('left', `${x}px`).style('top', `${y}px`);
    }

    function hideTooltip() {
        _state?.tooltip.style('opacity', 0).style('pointer-events', 'none');
    }

    function esc(s) {
        return String(s ?? '').replace(/[&<>"']/g,
            c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
    }

    // ── Cursor line ────────────────────────────────────────────────────────

    function onMouseMove(event) {
        if (!_state?.layout) return;
        const [mx] = d3.pointer(event);
        const xS = _state.xZoom.rescaleX(_state.xScale);

        const totalH = _state.layout.totalLanesH + SELECTOR_H + HEATMAP_H;

        _state.cursorLine
            .attr('x1', mx).attr('x2', mx)
            .attr('y1', 0).attr('y2', totalH)
            .style('opacity', 1);

        // Time label at each batch section.
        const timeMs = xS.invert(mx);
        _state.cursorLabels.selectAll('*').remove();

        for (const sec of _state.layout.sections) {
            const labelY = sec.y + HEADER_H + sec.laneHeight + TICK_H - 4;
            const label  = fmtRelTime(timeMs);

            _state.cursorLabels.append('rect')
                .attr('x', Math.min(mx + 4, _state.svgW - 130))
                .attr('y', labelY - 12)
                .attr('width', 120).attr('height', 16)
                .attr('rx', 3)
                .style('fill', '#13131a').style('opacity', 0.85);

            _state.cursorLabels.append('text').attr('class', 'bm-tl-cursor-label')
                .attr('x', Math.min(mx + 8, _state.svgW - 126))
                .attr('y', labelY)
                .text(label);
        }

        // Fade after 1.5s of stillness.
        clearTimeout(_state.cursorTimer);
        _state.cursorTimer = setTimeout(() => {
            _state?.cursorLine.style('opacity', 0);
            _state?.cursorLabels.selectAll('*').remove();
        }, 1500);
    }

    function onMouseLeave() {
        _state?.cursorLine.style('opacity', 0);
        _state?.cursorLabels.selectAll('*').remove();
    }

    function fmtRelTime(ms) {
        if (!isFinite(ms)) return '';
        const s   = Math.max(0, ms) / 1000;
        const h   = Math.floor(s / 3600);
        const m   = Math.floor((s % 3600) / 60);
        const sec = (s % 60).toFixed(3);
        return `+${String(h).padStart(2,'0')}:${String(m).padStart(2,'0')}:${sec.padStart(6,'0')}`;
    }

    // ── Keyboard shortcuts ─────────────────────────────────────────────────

    function handleKey(e) {
        if (!_state) return;
        const tag = document.activeElement?.tagName;
        if (tag === 'INPUT' || tag === 'TEXTAREA') return;

        const W  = _state.svgW || 800;
        const t  = _state.xZoom;
        const k  = t.k;

        let newT = null;
        switch (e.key) {
            case '+': case '=': newT = t.scale(k * 1.3); break;
            case '-':           newT = t.scale(k / 1.3); break;
            case 'ArrowLeft':
                newT = e.shiftKey ? t.translate(W * 0.5, 0) : t.translate(W * 0.1, 0); break;
            case 'ArrowRight':
                newT = e.shiftKey ? t.translate(-W * 0.5, 0) : t.translate(-W * 0.1, 0); break;
            case 'Home':
                newT = d3.zoomIdentity.translate(0, 0); break;
            case 'End':
                if (_state.layout) {
                    const xS  = t.rescaleX(_state.xScale);
                    const maxX = xS(_state.layout.globalMax);
                    newT = t.translate(W - maxX - 20, 0);
                }
                break;
            default: return;
        }
        if (e.key === '0' && e.ctrlKey) { resetView(); return; }
        if (newT) {
            e.preventDefault();
            _state.svg.call(_state.zoom.transform, newT);
        }
    }

    return { init, update, resetView, exportCsv, dispose };

})();
