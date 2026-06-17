// Batch Monitor — Timeline Tab (Step 10 rev 8 — clean architecture)
//
// Architecture:
//
// X-AXIS (time, relative ms from 0 per batch):
//   - xScale: linear, domain [0, frozenDomainMax], range [0, svgW]
//   - frozenDomainMax: set on first data load (max finish time across all
//     batches), then ONLY grows — never shrinks. xZoom is NEVER touched
//     by incoming data updates. Blocks stay put.
//   - xZoom: only changes via user interaction (wheel, drag, keyboard,
//     range selector). Hard limits: left=0, right=frozenDomainMax.
//   - New events beyond frozenDomainMax: frozenDomainMax grows,
//     xScale domain expands, xZoom untouched → blocks don't move,
//     new events appear in heatmap/selector only until user pans right.
//
// Y-AXIS (rows):
//   - Canvas wrap scrolls vertically; header + bottom panel are fixed.
//   - Per batch: 1 header row + N group rows.
//   - Group row height = number of sub-rows × SUBROW_H (no cap, no LOD).
//   - Sub-rows: greedy interval packing, no limit.
//   - Dashed lines separate group rows.
//
// HIGHLIGHTING:
//   - Hovering a block highlights all blocks with same chunkId across
//     all batches (same chunk processed by multiple services).
//
// BOTTOM PANEL (fixed):
//   - Global tick scale (absolute time if batchStartEpochMs known, else relative)
//   - Heatmap: density vertical lines on neutral bg
//   - Range selector: draggable viewport window overlay

window.BatchMonitor = window.BatchMonitor || {};

window.BatchMonitor.Timeline = (function () {

    // ── Constants ─────────────────────────────────────────────────────────
    const HEADER_H   = 26;   // batch title row
    const SUBROW_H   = 12;   // height of one sub-row inside a group row
    const ROW_PAD    = 3;    // padding above/below sub-rows within group row
    const BLOCK_GAP  = 2;    // vertical gap between sub-rows inside a group row
    const SECTION_GAP = 6;   // gap between batch sections
    const BLOCK_RX   = 4;
    const MIN_BLOCK_W = 1;   // blocks narrower than this become LOD lines
    const TICK_H     = 22;
    const HEAT_H     = 14;
    const SEL_H      = 26;
    const PAD        = 8;    // left/right padding in bottom panel
    const BOT_PAD_T  = 8;    // top padding between tick scale and heatmap
    const BOT_PAD_B  = 8;    // bottom padding below heatmap
    const BOTTOM_H   = TICK_H + BOT_PAD_T + HEAT_H + BOT_PAD_B;  // 22+8+14+8 = 52

    const PALETTE = [
        '#388BFD','#3FB950','#F0A500','#DB61A2','#A371F7',
        '#E06C75','#56B6C2','#D19A66','#61AFEF','#98C379',
        '#C678DD','#E5C07B','#BE5046','#2BBAC5','#FF8C42',
        '#79C0FF','#56D364','#E3B341','#F78166',
    ];
    const STATUS_COLOR = { done:'#3FB950', inprogress:'#388BFD', error:'#F85149' };
    const CURSOR_COLOR = '#FF00FF';
    const GRID_MAJOR   = 'rgba(255,255,255,0.10)';
    const GRID_MID     = 'rgba(255,255,255,0.04)';
    const SEPARATOR    = 'rgba(255,255,255,0.08)';
    const HEAT_BG      = '#161620';
    const HEAT_LINE    = '#388BFD';
    const BG_COLOR     = '#13131a';

    let _state = null;

    // ── Init ──────────────────────────────────────────────────────────────

    function init(laneEl, bottomEl, options) {
        if (!laneEl || !bottomEl) return;
        if (_state) dispose();

        // Lane SVG — width 100%, height set by JS to content height.
        // Scrolling is handled by the .bm-tl-canvas-wrap CSS overflow:auto.
        const laneSvg = d3.select(laneEl).append('svg')
            .attr('class', 'bm-tl-svg')
            .attr('width', '100%')
            .attr('height', '100%');

        // Layer order: grid → cursor → blocks → headers (headers on top for readability)
        const gridL   = laneSvg.append('g').attr('class', 'bm-tl-grid-layer');
        const cursorL = laneSvg.append('g').attr('class', 'bm-tl-cursor-layer').style('pointer-events','none');
        const blockL  = laneSvg.append('g').attr('class', 'bm-tl-block-layer');
        const headerL = laneSvg.append('g').attr('class', 'bm-tl-header-layer');

        const cursorLine = cursorL.append('line')
            .style('stroke', CURSOR_COLOR).style('stroke-width', 1.5).style('opacity', 0);

        // Bottom SVG — fixed height.
        const botSvg = d3.select(bottomEl).append('svg')
            .attr('class', 'bm-tl-bot-svg')
            .attr('width', '100%').attr('height', BOTTOM_H);

        // Bottom layers (paint order matters):
        //   1. tickL    — opaque background + tick labels (at y=0)
        //   2. heatL    — density lines (at y=TICK_H)
        //   3. selL     — brush overlay on heatmap (centred, taller than heatmap)
        //   4. curBotL  — cursor label pill (on top of everything)
        const tickL   = botSvg.append('g').attr('class','bm-tl-tick-layer');
        // Heatmap: below tick scale + top padding, padded left/right.
        const HEAT_Y  = TICK_H + BOT_PAD_T;
        const heatL   = botSvg.append('g').attr('class','bm-tl-heat-layer')
            .attr('transform', `translate(0,${HEAT_Y})`);
        // Selector: centred over heatmap vertically (taller → protrudes above/below).
        const SEL_OFFSET = HEAT_Y + Math.floor((HEAT_H - SEL_H) / 2);
        const selL    = botSvg.append('g').attr('class','bm-tl-sel-layer')
            .attr('transform', `translate(0,${SEL_OFFSET})`);
        const curBotL = botSvg.append('g').attr('class','bm-tl-curbot-layer').style('pointer-events','none');

        // Block hover tooltip.
        const tooltip = d3.select(laneEl).append('div')
            .attr('class','bm-tl-tooltip')
            .style('opacity',0).style('pointer-events','none');

        // X-scale and zoom.
        const xScale = d3.scaleLinear().domain([0,1]).range([0,800]);

        const zoom = d3.zoom()
            .scaleExtent([1, 5000])  // min scale=1: can't zoom out past "show all"
            .on('zoom', event => {
                if (!_state) return;
                const t    = event.transform;
                const W    = _state.svgW;
                const fMax = _state.frozenDomainMax || 1;

                // Clamp rules:
                //   tx <= 0         → leftmost visible time never goes negative
                //   tx >= W*(1-t.k) → rightmost pixel of frozenDomainMax stays reachable
                //   But globalMax may exceed frozenDomainMax (new live events), so also
                //   allow panning far enough right to see globalMax:
                //   xS_rescaled(globalMax) = tx + k * (gMax/fMax * W) >= 0
                //   → tx >= -k * gMax/fMax * W
                const gMax = _state.globalMax || fMax;
                const rightLimit = -t.k * (gMax / fMax) * W;
                const txClamped = Math.max(rightLimit, Math.min(0, t.x));
                const clamped = d3.zoomIdentity.translate(txClamped, 0).scale(t.k);
                _state.xZoom = clamped;
                renderLanes();
                renderTicks();
                syncSelector();
            });

        laneSvg.call(zoom).style('cursor','crosshair')
            .on('mousemove.cur', onMouseMove)
            .on('mouseleave.cur', onMouseLeave);

        const kd = e => handleKey(e);
        document.addEventListener('keydown', kd);

        _state = {
            laneEl, bottomEl, laneSvg, botSvg,
            gridL, cursorL, blockL, headerL, cursorLine,
            heatL, selL, tickL, curBotL,
            tooltip,
            xScale, xZoom: d3.zoomIdentity, zoom,
            data: null, layout: null,
            svgW: 800, viewH: 600,
            frozenDomainMax: null,   // set on first load, grows but never shrinks
            cursorX: null, cursorTimer: null,
            hoveredBatchIdx: -1,
            hoveredChunkId: null,
            keydownFn: kd,
        };

        buildSelector();

        const ro = new ResizeObserver(() => { if (_state) onResize(); });
        ro.observe(laneEl.parentElement || laneEl);
        _state.resizeObserver = ro;
        onResize();
    }

    // ── Resize ────────────────────────────────────────────────────────────

    function onResize() {
        if (!_state) return;
        const rect = _state.laneEl.getBoundingClientRect();
        _state.svgW  = rect.width  || 800;
        _state.viewH = _state.laneEl.parentElement?.clientHeight || rect.height || 600;
        _state.xScale.range([0, _state.svgW]);
        _state.botSvg.attr('width', _state.svgW);
        if (_state.selFn) {
            _state.selFn.extent([[PAD, 0], [_state.svgW - PAD, SEL_H]]);
            _state.selL.call(_state.selFn);
        }
        renderAll();
    }

    // ── Update (from Blazor — NEVER modifies xZoom) ───────────────────────

    function update(payload) {
        if (!_state) return;
        _state.data = payload;

        // Compute globalMax from all batch events.
        let newMax = 0;
        for (const b of (payload.batches || [])) {
            for (const e of (b.events || [])) {
                newMax = Math.max(newMax, e.finishMs ?? e.startMs ?? 0);
            }
        }
        if (newMax < 1000) newMax = 1000;

        // Always track globalMax separately (for heatmap/selector proportions).
        _state.globalMax = newMax;

        if (_state.frozenDomainMax === null) {
            // FIRST LOAD ONLY: freeze xScale domain to current data range.
            // xScale.domain([0, frozenDomainMax]) NEVER changes after this.
            // xZoom NEVER changes from incoming data after this.
            // New events beyond frozenDomainMax are off-screen until user pans.
            _state.frozenDomainMax = newMax;
            _state.xScale.domain([0, newMax]);
            _state.xZoom = d3.zoomIdentity;
            // Apply without triggering the zoom event handler.
            const prevOn = _state.zoom.on('zoom');
            _state.zoom.on('zoom', null);
            _state.laneSvg.call(_state.zoom.transform, d3.zoomIdentity);
            _state.zoom.on('zoom', prevOn);
        }
        // On subsequent updates: xScale.domain and xZoom are NEVER touched here.
        // globalMax grows silently; heatmap/selector use globalMax for proportions.

        computeLayout();
        updateLaneSvgHeight();
        renderLanes();
        renderTicks();
        renderHeatmap();
        syncSelector();
    }

    function resetView() {
        if (!_state) return;
        _state.xZoom = d3.zoomIdentity;
        _state.laneSvg.call(_state.zoom.transform, d3.zoomIdentity);
    }

    function dispose() {
        if (!_state) return;
        document.removeEventListener('keydown', _state.keydownFn);
        _state.resizeObserver?.disconnect();
        _state.laneSvg.selectAll('*').remove();
        _state.botSvg.selectAll('*').remove();
        _state.tooltip.remove();
        clearTimeout(_state.cursorTimer);
        _state = null;
    }

    // ── Layout ────────────────────────────────────────────────────────────

    function computeLayout() {
        if (!_state?.data) return;
        const { data } = _state;
        const groupBy  = data.groupBy  || 'ServicePipeline';
        const colourBy = data.colourBy || 'Source';
        const filter   = (data.filter || '').toLowerCase();

        let y = 0;
        const sections = [];

        for (const batch of (data.batches || [])) {
            const groups = buildGroups(batch, groupBy, colourBy, filter);
            // Total height for this section.
            const sectionH = HEADER_H + groups.reduce((s, g) => s + g.rowH, 0) + SECTION_GAP;
            sections.push({ batch, groups, y, sectionH });
            y += sectionH;
        }

        _state.layout = { sections, totalH: y };
    }

    function buildGroups(batch, groupBy, colourBy, filter) {
        // Group events by key.
        // Skip in-progress events (no finish time) — they have no definite end
        // so cannot be meaningfully positioned on a fixed-viewport timeline.
        const map = new Map();
        for (const e of (batch.events || [])) {
            if (e.finishMs == null) continue;   // skip in-progress
            if (filter && !matchesFilter(e, filter)) continue;
            const key = groupKey(e, groupBy);
            if (!map.has(key)) map.set(key, []);
            map.get(key).push(e);
        }

        const groups = [];
        for (const [key, events] of map) {
            // Pack into sub-rows (no cap, no LOD).
            const subrows = packSubrows(events);
            const rowH    = ROW_PAD * 2 + subrows.length * (SUBROW_H + BLOCK_GAP);
            groups.push({ key, events, subrows, rowH, colourBy });
        }
        return groups;
    }

    function packSubrows(events) {
        // Sort by start time, greedy interval packing (first-fit from top).
        // For packing purposes, in-progress events use a small estimated width
        // so they don't permanently block all subsequent events from the row.
        // The rendered block still uses xS(finishMs ?? frozenDomainMax).
        const AVG_DURATION_MS = 5000;  // packing estimate for in-progress events
        const sorted = [...events].sort((a, b) => a.startMs - b.startMs);
        const subrows = [];
        for (const e of sorted) {
            const xs   = e.startMs;
            // For packing: use actual finish if known; estimate if in-progress.
            const xePack = e.finishMs ?? (xs + AVG_DURATION_MS);
            let placed = false;
            for (const row of subrows) {
                if (xs >= row[row.length-1].xEndPack) {
                    row.push({ event: e, xStart: xs, xEnd: e.finishMs ?? (xs + (_state.frozenDomainMax||1000)), xEndPack: xePack });
                    placed = true;
                    break;
                }
            }
            if (!placed) subrows.push([{ event: e, xStart: xs, xEnd: e.finishMs ?? (xs + (_state.frozenDomainMax||1000)), xEndPack: xePack }]);
        }
        return subrows;
    }

    function updateLaneSvgHeight() {
        if (!_state?.layout) return;
        const h = Math.max(_state.viewH, _state.layout.totalH + 16);
        _state.laneSvg.attr('height', h);
    }

    // ── Render lanes ──────────────────────────────────────────────────────

    function renderAll() {
        if (!_state?.layout) return;
        renderLanes();
        renderTicks();
        renderHeatmap();
        syncSelector();
    }

    function renderLanes() {
        if (!_state?.layout) return;
        const xS = _state.xZoom.rescaleX(_state.xScale);
        const W  = _state.svgW;
        const H  = parseFloat(_state.laneSvg.attr('height')) || _state.viewH;
        const colourBy = _state.data?.colourBy || 'Source';
        const { sections } = _state.layout;

        // ── Global vertical grid lines (spanning full SVG height) ──
        _state.gridL.selectAll('*').remove();
        const ticks = xS.ticks(Math.floor(W / 90));
        const step  = ticks.length > 1 ? ticks[1] - ticks[0] : 0;
        if (step > 0) {
            for (const t of ticks.map(t => t + step/2)) {
                _state.gridL.append('line')
                    .attr('x1',xS(t)).attr('x2',xS(t))
                    .attr('y1',0).attr('y2',H)
                    .style('stroke',GRID_MID).style('stroke-width',0.5);
            }
        }
        for (const t of ticks) {
            _state.gridL.append('line')
                .attr('x1',xS(t)).attr('x2',xS(t))
                .attr('y1',0).attr('y2',H)
                .style('stroke',GRID_MAJOR).style('stroke-width',1);
        }

        // ── Sections ──
        const secSel = _state.blockL.selectAll('g.bm-tl-sec')
            .data(sections, d => d.batch.runId);
        secSel.exit().remove();
        const secEnter = secSel.enter().append('g').attr('class','bm-tl-sec');
        secEnter.append('g').attr('class','bm-tl-sec-lines');
        secEnter.append('g').attr('class','bm-tl-sec-blocks');
        const secMerged = secEnter.merge(secSel);
        secMerged.attr('transform', d => `translate(0,${d.y})`);
        secMerged.each(function(sec) {
            renderSectionLines(d3.select(this).select('.bm-tl-sec-lines'), sec, W);
            renderSectionBlocks(d3.select(this).select('.bm-tl-sec-blocks'), sec, xS, W, colourBy);
        });

        // ── Headers (on top, bg rect only under text) ──
        const hdrSel = _state.headerL.selectAll('g.bm-tl-hdr')
            .data(sections, d => d.batch.runId);
        hdrSel.exit().remove();
        const hdrEnter = hdrSel.enter().append('g').attr('class','bm-tl-hdr');
        hdrEnter.merge(hdrSel)
            .attr('transform', d => `translate(0,${d.y})`)
            .each(function(sec) { renderHeader(d3.select(this), sec); });

        // ── Cursor line (full SVG height) ──
        _state.cursorLine.attr('y1',0).attr('y2',H);
        if (_state.cursorX != null)
            _state.cursorLine.attr('x1',_state.cursorX).attr('x2',_state.cursorX);
    }

    function renderSectionLines(sel, sec, W) {
        sel.selectAll('*').remove();
        // Bottom border of section.
        const secH = sec.sectionH - SECTION_GAP;
        sel.append('line')
            .attr('x1',0).attr('x2',W)
            .attr('y1',secH).attr('y2',secH)
            .style('stroke',GRID_MAJOR).style('stroke-width',1);

        // Group row separator dashes.
        let rowY = HEADER_H;
        for (const grp of sec.groups) {
            sel.append('line')
                .attr('x1',0).attr('x2',W)
                .attr('y1',rowY).attr('y2',rowY)
                .style('stroke',SEPARATOR).style('stroke-width',1)
                .style('stroke-dasharray','3 5');
            rowY += grp.rowH;
        }
    }

    function renderSectionBlocks(sel, sec, xS, W, colourBy) {
        const blockData = [];

        let rowY = HEADER_H;
        for (let gi = 0; gi < sec.groups.length; gi++) {
            const grp = sec.groups[gi];
            for (let si = 0; si < grp.subrows.length; si++) {
                const subrow = grp.subrows[si];
                const sy = rowY + ROW_PAD + si * SUBROW_H;
                for (let bi = 0; bi < subrow.length; bi++) {
                    const e   = subrow[bi].event;
                    const x   = xS(e.startMs);
                    const xe  = e.finishMs != null ? xS(e.finishMs) : xS(_state.frozenDomainMax || 1000);
                    const w   = Math.max(0, xe - x);
                    // Viewport cull.
                    if (x > W || x + Math.max(w, MIN_BLOCK_W) < 0) continue;
                    const key = colourKey(e, colourBy);
                    blockData.push({
                        e, x, w, key,
                        colour:      colourForKey(key, colourBy),
                        batchRunId:  sec.batch.runId,
                        batchBse:    sec.batch.batchStartEpochMs || 0,
                        // Stable key: never pixel-based.
                        stableKey: `${sec.batch.runId}:${gi}:${si}:${bi}`,
                        y: sy,
                        h: SUBROW_H - 1,
                    });
                }
            }
            rowY += grp.rowH;
        }

        const rects = sel.selectAll('rect.bm-tl-block').data(blockData, d => d.stableKey);
        rects.exit().remove();
        rects.enter().append('rect').attr('class','bm-tl-block').attr('rx', BLOCK_RX)
            .on('mouseenter', (ev, d) => onBlockEnter(ev, d))
            .on('mouseleave', (ev, d) => onBlockLeave(ev, d))
            .merge(rects)
            .attr('x', d => d.x)
            .attr('y', d => d.y)
            .attr('width',  d => Math.max(MIN_BLOCK_W, d.w))
            .attr('height', d => d.h)
            .style('fill',         d => d.colour)
            .style('fill-opacity', d => {
                if (_state.hoveredChunkId == null) return d.e.status === 'inprogress' ? 0.6 : 1;
                return d.e.chunkId === _state.hoveredChunkId ? 1 : 0.15;
            })
            .style('stroke', d => d3.color(d.colour)?.darker(.5) ?? d.colour)
            .style('stroke-width', d => d.e.chunkId === _state.hoveredChunkId ? 1.5 : 0.5);
    }

    function renderHeader(sel, sec) {
        sel.selectAll('*').remove();
        const lx  = 8;
        const cy  = HEADER_H / 2;

        if (sec.batch.isLive) {
            sel.append('circle').attr('class','bm-tl-live-dot')
                .attr('cx', lx).attr('cy', cy).attr('r', 5).style('fill','#3FB950');
        }

        const tx = sec.batch.isLive ? lx + 14 : lx;
        const nameW   = sec.batch.batchName.length * 7;
        const runIdW  = sec.batch.runId.length   * 6;
        const bgW     = nameW + runIdW + 24;
        const bgH     = HEADER_H - 4;

        // Background only under the text (not full row width).
        sel.append('rect')
            .attr('x', tx - 4).attr('y', (HEADER_H - bgH)/2)
            .attr('width', bgW).attr('height', bgH)
            .attr('rx', 3)
            .style('fill', BG_COLOR).style('opacity', 0.88);

        // Truncate batch name if too long to prevent overlap with runId.
        const MAX_NAME_CHARS = 28;
        const displayName = sec.batch.batchName.length > MAX_NAME_CHARS
            ? sec.batch.batchName.slice(0, MAX_NAME_CHARS) + '…'
            : sec.batch.batchName;
        const displayNameW = displayName.length * 7;

        sel.append('text').attr('class','bm-tl-batch-name')
            .attr('x', tx).attr('y', cy).attr('dy','0.32em')
            .text(displayName);

        sel.append('text').attr('class','bm-tl-batch-runid')
            .attr('x', tx + displayNameW + 10).attr('y', cy).attr('dy','0.32em')
            .text(sec.batch.runId);
    }

    // ── Tick scale ────────────────────────────────────────────────────────

    function renderTicks() {
        if (!_state?.layout) return;
        const xS  = _state.xZoom.rescaleX(_state.xScale);
        const W   = _state.svgW;
        const bse = getHoveredBatchStart();

        _state.tickL.selectAll('*').remove();

        // Background.
        _state.tickL.append('rect')
            .attr('width', W).attr('height', TICK_H)
            .style('fill', BG_COLOR);
        _state.tickL.append('line')
            .attr('x1',0).attr('x2',W).attr('y1',0).attr('y2',0)
            .style('stroke', GRID_MAJOR).style('stroke-width',1);

        const ticks = xS.ticks(Math.floor(W / 100));
        const step  = ticks.length > 1 ? ticks[1] - ticks[0] : 0;

        if (step > 0) {
            for (const t of ticks.map(t => t + step/2)) {
                const x = xS(t);
                if (!isFinite(x)) continue;
                _state.tickL.append('line')
                    .attr('x1',x).attr('x2',x)
                    .attr('y1',TICK_H-3).attr('y2',TICK_H)
                    .style('stroke',GRID_MID).style('stroke-width',0.5);
            }
        }

        for (const t of ticks) {
            const x = xS(t);
            if (!isFinite(x)) continue;
            _state.tickL.append('line')
                .attr('x1',x).attr('x2',x)
                .attr('y1',TICK_H-5).attr('y2',TICK_H)
                .style('stroke',GRID_MAJOR).style('stroke-width',1);
            _state.tickL.append('text').attr('class','bm-tl-tick-label')
                .attr('x',x).attr('y',TICK_H-8).attr('text-anchor','middle')
                .text(bse ? fmtAbsHMS(bse+t) : fmtRelHMS(t));
        }
    }

    function getHoveredBatchStart() {
        if (!_state?.layout) return null;
        const secs = _state.layout.sections;
        const idx  = _state.hoveredBatchIdx;
        const sec  = (idx >= 0 && idx < secs.length) ? secs[idx] : secs[0];
        return sec?.batch?.batchStartEpochMs || null;
    }

    // ── Heatmap ───────────────────────────────────────────────────────────

    function renderHeatmap() {
        if (!_state?.layout || !_state.frozenDomainMax) return;
        // Use globalMax so heatmap shows ALL events including those beyond viewport.
        const fMax    = _state.globalMax || _state.frozenDomainMax;
        const W       = _state.svgW - PAD * 2;
        const buckets = Math.min(500, Math.floor(W));
        const counts  = new Array(buckets).fill(0);

        for (const sec of _state.layout.sections) {
            for (const e of (sec.batch.events || [])) {
                const b = Math.floor((e.startMs / fMax) * (buckets - 1));
                if (b >= 0 && b < buckets) counts[b]++;
            }
        }

        const maxC = Math.max(1, ...counts);
        const bW   = W / buckets;

        _state.heatL.selectAll('*').remove();
        _state.heatL.append('rect')
            .attr('x', PAD).attr('width', W).attr('height', HEAT_H)
            .style('fill', HEAT_BG);

        for (let i = 0; i < buckets; i++) {
            if (counts[i] === 0) continue;
            const op = 0.12 + 0.88 * (counts[i] / maxC);
            _state.heatL.append('line')
                .attr('x1', PAD + i*bW + bW/2)
                .attr('x2', PAD + i*bW + bW/2)
                .attr('y1', 0).attr('y2', HEAT_H)
                .style('stroke', HEAT_LINE)
                .style('stroke-width', Math.max(1, bW - 0.5))
                .style('opacity', op);
        }
    }

    // ── Range selector ────────────────────────────────────────────────────

    function buildSelector() {
        if (!_state) return;
        const W = _state.svgW;
        // d3.brushX for simplicity — but ONLY its visual; drag interaction
        // is wired separately so it doesn't interfere with lane zoom.
        const brush = d3.brushX()
            .extent([[PAD, 0], [W - PAD, SEL_H]])
            .on('brush', event => {
                if (!event.sourceEvent || !event.selection) return;
                const [px0, px1] = event.selection;
                applyBrushSelection(px0, px1);
            });

        _state.selFn = brush;
        _state.selL.call(brush);
        syncSelector();
    }

    function applyBrushSelection(px0, px1) {
        if (!_state?.frozenDomainMax) return;
        const gMax    = _state.globalMax || _state.frozenDomainMax;
        const fMax    = _state.frozenDomainMax;
        const W       = _state.svgW;
        const usableW = W - PAD * 2;
        // px0, px1 are in [PAD, W-PAD] mapped over [0, gMax].
        // Convert brush pixels to time values in [0, gMax].
        const brushScale = d3.scaleLinear().domain([PAD, W - PAD]).range([0, gMax]);
        const t0ms = brushScale(px0);
        const t1ms = brushScale(px1);
        const visMs = t1ms - t0ms;
        if (visMs < 1) return;
        // Convert [t0ms, t1ms] to xZoom on xScale (which maps [0, fMax] → [0, W]).
        // xS(t) = xZoom.x + t * xZoom.k.  We want xS(t0ms)=0 and xS(t1ms)=W.
        // xZoom.k = W / (visMs / fMax * W) = fMax / visMs
        // xZoom.x = -t0ms * k / fMax * W
        const k  = fMax / visMs;
        const tx = -(t0ms / fMax) * W * k;
        const t  = d3.zoomIdentity.translate(tx, 0).scale(k);
        _state.xZoom = t;
        _state.laneSvg.call(_state.zoom.transform, t);
    }

    function syncSelector() {
        if (!_state?.selFn || !_state.frozenDomainMax) return;
        // Use globalMax so selector shows viewport window as fraction of ALL data.
        const gMax    = _state.globalMax || _state.frozenDomainMax;
        const fMax    = _state.frozenDomainMax;
        const W       = _state.svgW;
        const xS      = _state.xZoom.rescaleX(_state.xScale);

        // The viewport shows [t0ms, t1ms] in the frozen domain.
        // The selector maps [0, gMax] → [PAD, W-PAD].
        // Position selector handles at t0ms and t1ms, clamped.
        const t0ms = xS.invert(0);
        const t1ms = xS.invert(W);
        const brushScale = d3.scaleLinear().domain([0, gMax]).range([PAD, W - PAD]);
        const s0 = Math.max(PAD,   Math.min(W - PAD, brushScale(Math.max(0, t0ms))));
        const s1 = Math.max(PAD,   Math.min(W - PAD, brushScale(Math.min(gMax, t1ms))));

        if (isFinite(s0) && isFinite(s1) && s1 > s0 + 1) {
            _state.selL.call(_state.selFn.move, [s0, s1]);
        }
    }

    // ── Cursor ────────────────────────────────────────────────────────────

    function onMouseMove(event) {
        if (!_state?.layout) return;
        const [mx, my] = d3.pointer(event);
        _state.cursorX = mx;

        _state.cursorLine.attr('x1',mx).attr('x2',mx).style('opacity',1);

        // Determine which section mouse is over.
        const secs = _state.layout.sections;
        let hovIdx = -1;
        for (let i = 0; i < secs.length; i++) {
            const s = secs[i];
            if (my >= s.y && my < s.y + s.sectionH) { hovIdx = i; break; }
        }
        if (_state.hoveredBatchIdx !== hovIdx) {
            _state.hoveredBatchIdx = hovIdx;
            renderTicks();
        }

        const xS     = _state.xZoom.rescaleX(_state.xScale);
        const timeMs = xS.invert(mx);
        const bse    = getHoveredBatchStart();

        renderCursorLabel(mx, timeMs, bse);

        clearTimeout(_state.cursorTimer);
        _state.cursorTimer = setTimeout(() => {
            if (!_state) return;
            _state.cursorLine.style('opacity',0);
            _state.cursorBotG?.selectAll('*').remove();
            _state.curBotL.selectAll('*').remove();
        }, 2000);
    }

    function renderCursorLabel(mx, timeMs, bse) {
        const g = _state.curBotL;
        g.selectAll('*').remove();

        // Vertical line in bottom panel.
        g.append('line')
            .attr('x1',mx).attr('x2',mx)
            .attr('y1',0).attr('y2',TICK_H + HEAT_H/2)
            .style('stroke',CURSOR_COLOR).style('stroke-width',1.5).style('opacity',0.8);

        const relLabel = fmtRelHMS(timeMs);
        const absLabel = bse ? ` (${fmtAbsHMS(bse + timeMs)})` : '';
        const label    = relLabel + absLabel;
        const lw       = label.length * 6.5 + 12;
        const lh       = 15;
        let   lx       = mx - lw/2;
        lx = Math.max(2, Math.min((_state.svgW||800) - lw - 2, lx));

        g.append('rect')
            .attr('x',lx).attr('y',2)
            .attr('width',lw).attr('height',lh).attr('rx',3)
            .style('fill',CURSOR_COLOR);
        g.append('text')
            .attr('x',lx+lw/2).attr('y',2+lh/2)
            .attr('text-anchor','middle').attr('dy','0.35em')
            .style('fill','#fff').style('font-size','0.6rem')
            .style('font-weight','700')
            .style('font-family','var(--bm-font-mono,monospace)')
            .text(label);
    }

    function onMouseLeave() {
        if (!_state) return;
        _state.cursorX = null;
        _state.cursorLine.style('opacity',0);
        _state.curBotL.selectAll('*').remove();
    }

    // ── Block hover / highlight ───────────────────────────────────────────

    function onBlockEnter(event, d) {
        _state.hoveredChunkId = d.e.chunkId;
        // Re-render all sections to apply highlight across batches.
        if (_state.layout) {
            const xS = _state.xZoom.rescaleX(_state.xScale);
            const W  = _state.svgW;
            const colourBy = _state.data?.colourBy || 'Source';
            _state.blockL.selectAll('g.bm-tl-sec').each(function(sec) {
                d3.select(this).select('.bm-tl-sec-blocks')
                    .selectAll('rect.bm-tl-block')
                    .style('fill-opacity', b => b.e.chunkId === _state.hoveredChunkId ? 1 : 0.12)
                    .style('stroke-width', b => b.e.chunkId === _state.hoveredChunkId ? '1.5px' : '0.5px');
            });
        }
        showTooltip(event, d);
    }

    function onBlockLeave(event, d) {
        _state.hoveredChunkId = null;
        _state.blockL.selectAll('rect.bm-tl-block')
            .style('fill-opacity', b => b.e.status === 'inprogress' ? 0.6 : 1)
            .style('stroke-width','0.5px');
        hideTooltip();
    }

    // ── Tooltip ───────────────────────────────────────────────────────────

    function showTooltip(event, d) {
        const e    = d.e;
        const bse  = d.batchBse;
        const durMs = e.finishMs != null ? Math.round(e.finishMs - e.startMs) : null;
        const durStr = durMs != null ? `${durMs.toLocaleString()}ms` : '(in progress)';
        const statusLabel = e.status==='done'?'✓ Done':e.status==='inprogress'?'⟳ In Progress':'✗ Error';
        const statusClass = `bm-tl-tt-${e.status}`;

        const startRel = fmtRelMs(e.startMs);
        const startAbs = bse ? fmtAbsMs(bse+e.startMs) : '';
        const startStr = startAbs ? `${startRel} (${startAbs})` : startRel;

        const finRel = e.finishMs!=null ? fmtRelMs(e.finishMs) : `${fmtRelMs(e.startMs)}…`;
        const finAbs = bse&&e.finishMs!=null ? fmtAbsMs(bse+e.finishMs) : '';
        const finStr = finAbs ? `${finRel} (${finAbs})` : finRel;

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
            <div class="bm-tl-tt-sep"></div>
            <div class="bm-tl-tt-row"><span>Start</span><span>${startStr}</span></div>
            <div class="bm-tl-tt-row"><span>Finish</span><span>${finStr}</span></div>
            <div class="bm-tl-tt-row"><span>Duration</span><span>${durStr}</span></div>
            ${e.error?`<div class="bm-tl-tt-row bm-tl-tt-err"><span>Error</span><span>${esc(e.error)}</span></div>`:''}
            <div class="bm-tl-tt-sep"></div>
            <div class="bm-tl-tt-footer" id="bm-tl-tt-footer">Ctrl+click to copy</div>
        `)
        .style('opacity',1).style('pointer-events','auto');

        _state.tooltip.on('click', ev => {
            if (!ev.ctrlKey) return;
            navigator.clipboard?.writeText(
                [`ChunkId: ${e.chunkId}`,`Source: ${e.source}`,
                 `Start: ${startStr}`,`Finish: ${finStr}`,
                 `Duration: ${durStr}`].join('\n'));
            const f = document.getElementById('bm-tl-tt-footer');
            if (f) { f.textContent='Copied ✓'; setTimeout(()=>{if(f)f.textContent='Ctrl+click to copy';},1000); }
        });

        positionTooltip(event);
    }

    function positionTooltip(event) {
        if (!_state) return;
        const rect = _state.laneEl.getBoundingClientRect();
        let x=event.clientX-rect.left+12, y=event.clientY-rect.top-8;
        const pw=280,ph=230;
        if (x+pw>rect.width-8)  x=event.clientX-rect.left-pw-12;
        if (y+ph>rect.height-8) y=event.clientY-rect.top-ph-12;
        if (y<0) y=event.clientY-rect.top+16;
        x=Math.max(4,x); y=Math.max(4,y);
        _state.tooltip.style('left',`${x}px`).style('top',`${y}px`);
    }

    function hideTooltip() { _state?.tooltip.style('opacity',0).style('pointer-events','none'); }

    // ── Keyboard ──────────────────────────────────────────────────────────

    function handleKey(e) {
        if (!_state) return;
        const tag=document.activeElement?.tagName;
        if (tag==='INPUT'||tag==='TEXTAREA') return;
        const W    = _state.svgW||800;
        const t    = _state.xZoom;
        let nt = null;
        switch(e.key) {
            case '=': case '+':
                nt = d3.zoomIdentity.translate(t.x, 0).scale(t.k * 1.25);
                break;
            case '-':
                nt = d3.zoomIdentity.translate(t.x, 0).scale(Math.max(1, t.k / 1.25));
                break;
            case 'ArrowLeft': {
                const dx = e.shiftKey ? W*0.5 : W*0.1;
                // Clamp: don't go past t=0 (tx <= 0).
                const newTx = Math.min(0, t.x + dx);
                nt = d3.zoomIdentity.translate(newTx, 0).scale(t.k);
                break;
            }
            case 'ArrowRight': {
                const dx = e.shiftKey ? W*0.5 : W*0.1;
                const gMaxKb = _state.globalMax || _state.frozenDomainMax || 1;
                const fMaxKb = _state.frozenDomainMax || 1;
                const rightLimitKb = -t.k * (gMaxKb / fMaxKb) * W;
                const newTx = Math.max(rightLimitKb, t.x - dx);
                nt = d3.zoomIdentity.translate(newTx, 0).scale(t.k);
                break;
            }
            case 'Home':
                nt = d3.zoomIdentity;
                break;
            default: return;
        }
        if (nt) { e.preventDefault(); _state.laneSvg.call(_state.zoom.transform, nt); }
    }

    // ── Format helpers ────────────────────────────────────────────────────

    // HH:MM:SS — for tick scale and cursor label.
    function fmtRelHMS(ms) {
        if (!isFinite(ms)||ms<0) ms=0;
        const s=Math.floor(ms/1000), h=Math.floor(s/3600), m=Math.floor((s%3600)/60), sec=s%60;
        return `${String(h).padStart(2,'0')}:${String(m).padStart(2,'0')}:${String(sec).padStart(2,'0')}`;
    }
    function fmtAbsHMS(epochMs) {
        if (!isFinite(epochMs)) return '';
        const d=new Date(epochMs);
        return `${String(d.getHours()).padStart(2,'0')}:${String(d.getMinutes()).padStart(2,'0')}:${String(d.getSeconds()).padStart(2,'0')}`;
    }
    // HH:MM:SS.mmm — for tooltip only.
    function fmtRelMs(ms) {
        if (!isFinite(ms)||ms<0) ms=0;
        const s=ms/1000, h=Math.floor(s/3600), m=Math.floor((s%3600)/60), sec=(s%60).toFixed(3);
        return `${String(h).padStart(2,'0')}:${String(m).padStart(2,'0')}:${sec.padStart(6,'0')}`;
    }
    function fmtAbsMs(epochMs) {
        if (!isFinite(epochMs)) return '';
        const d=new Date(epochMs);
        return `${String(d.getHours()).padStart(2,'0')}:${String(d.getMinutes()).padStart(2,'0')}:${String(d.getSeconds()).padStart(2,'0')}.${String(d.getMilliseconds()).padStart(3,'0')}`;
    }

    // ── Colour / group helpers ────────────────────────────────────────────

    function groupKey(e, g) {
        switch(g) {
            case 'ServicePipeline':    return `${e.service} / ${e.pipeline}`;
            case 'PidServicePipeline': return `${e.server}:${e.processId} / ${e.service} / ${e.pipeline}`;
            case 'Service':  return e.service;
            case 'Pipeline': return e.pipeline;
            case 'Pid':      return `${e.server}:${e.processId}`;
            default:         return `${e.service} / ${e.pipeline}`;
        }
    }
    function colourKey(e, c) {
        switch(c) {
            case 'Pipeline': return e.pipeline;
            case 'Service':  return e.service;
            case 'Status':   return e.status;
            default:         return e.source;
        }
    }
    function colourForKey(key, c) {
        return c==='Status'?(STATUS_COLOR[key]||'#8B949E'):PALETTE[hashStr(key)%PALETTE.length];
    }
    function hashStr(s) {
        let h=0; for(let i=0;i<s.length;i++) h=Math.imul(31,h)+s.charCodeAt(i)|0; return Math.abs(h);
    }
    function matchesFilter(e, f) {
        return [e.chunkId,e.source,e.pipeline,e.service,e.processId,e.server]
            .some(v => (v||'').toLowerCase().includes(f));
    }

    function esc(s) {
        return String(s??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
    }
    function csv(v) { return `"${String(v??'').replace(/"/g,'""')}"`; }

    // ── Export ────────────────────────────────────────────────────────────

    async function exportCsv() {
        if (!_state?.data) return;
        const rows=['RunId,BatchName,ChunkId,Source,Pipeline,Service,PID,Server,StartRelMs,FinishRelMs,DurationMs,Status,Error'];
        for (const b of (_state.data.batches||[])) {
            for (const e of (b.events||[])) {
                rows.push([csv(b.runId),csv(b.batchName),csv(e.chunkId),
                    csv(e.source),csv(e.pipeline),csv(e.service),
                    csv(e.processId),csv(e.server),
                    e.startMs.toFixed(1),e.finishMs??'',
                    e.finishMs!=null?(e.finishMs-e.startMs).toFixed(0):'',
                    csv(e.status),csv(e.error??'')].join(','));
            }
        }
        const content = rows.join('\r\n');
        const fname   = `timeline_${new Date().toISOString().slice(0,19).replace(/[:T]/g,'-')}.csv`;
        if (window.showSaveFilePicker) {
            try {
                const fh = await window.showSaveFilePicker({
                    suggestedName: fname,
                    types:[{description:'CSV',accept:{'text/csv':['.csv']}}],
                });
                const w = await fh.createWritable();
                await w.write(content); await w.close(); return;
            } catch(ex) { if (ex.name==='AbortError') return; }
        }
        const blob=new Blob([content],{type:'text/csv;charset=utf-8;'});
        const url=URL.createObjectURL(blob);
        const a=document.createElement('a');
        a.href=url; a.download=fname; a.style.display='none';
        document.body.appendChild(a); a.click();
        setTimeout(()=>{document.body.removeChild(a);URL.revokeObjectURL(url);},200);
    }

    return { init, update, resetView, exportCsv, dispose };

})();
