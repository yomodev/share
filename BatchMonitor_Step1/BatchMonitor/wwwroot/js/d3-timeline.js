// Batch Monitor — Timeline Tab (Step 10 rev 9 — clean multi-instance rewrite)
//
// Each Timeline tab gets an isolated instance via a unique key.
// All internal functions receive the instance state `s` as first argument.
// No module-level mutable state — everything is in the instance map.

window.BatchMonitor = window.BatchMonitor || {};

window.BatchMonitor.Timeline = (function () {

    const _instances = new Map();

    // ── Constants ─────────────────────────────────────────────────────────
    const HEADER_H  = 26;
    const SUBROW_H  = 12;
    const ROW_PAD   = 3;
    const BLOCK_GAP = 2;
    const BLOCK_RX  = 4;
    const SECTION_GAP = 6;
    const TICK_H    = 22;
    const HEAT_H    = 14;
    const SEL_H     = 26;
    const BOT_PAD_T = 8;
    const BOT_PAD_B = 8;
    const PAD       = 8;
    const BOTTOM_H  = TICK_H + BOT_PAD_T + HEAT_H + BOT_PAD_B;

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

    // ── Public API ─────────────────────────────────────────────────────────

    function init(key, laneEl, bottomEl, options) {
        // Dispose any existing instance for this key.
        if (_instances.has(key)) disposeInstance(_instances.get(key));

        const s = createInstance(laneEl, bottomEl, options);
        _instances.set(key, s);
        onResize(s);
    }

    function update(key, payload) {
        const s = _instances.get(key);
        if (!s) return;
        s.data = payload;

        let newMax = 0;
        for (const b of (payload.batches || []))
            for (const e of (b.events || []))
                newMax = Math.max(newMax, e.finishMs ?? e.startMs ?? 0);
        if (newMax < 1000) newMax = 1000;
        s.globalMax = newMax;

        if (s.frozenDomainMax === null) {
            s.frozenDomainMax = newMax;
            s.xScale.domain([0, newMax]);
            s.xZoom = d3.zoomIdentity;
            applyZoom(s, d3.zoomIdentity);
        }

        computeLayout(s);
        updateLaneSvgHeight(s);
        renderAll(s);
    }

    function resetView(key) {
        const s = _instances.get(key);
        if (!s || !s.frozenDomainMax) return;
        const gMax = s.globalMax || s.frozenDomainMax;
        const fMax = s.frozenDomainMax;
        const k = gMax <= fMax ? 1 : fMax / gMax;
        const t = d3.zoomIdentity.scale(k);
        s.xZoom = t;
        applyZoom(s, t);
    }

    function exportCsv(key) {
        const s = _instances.get(key);
        if (!s?.data) return;
        doExport(s);
    }

    function dispose(key) {
        const s = _instances.get(key);
        if (!s) return;
        disposeInstance(s);
        _instances.delete(key);
    }

    // ── Instance creation ─────────────────────────────────────────────────

    function createInstance(laneEl, bottomEl, options) {
        // Lane SVG.
        const laneSvg = d3.select(laneEl).append('svg')
            .attr('class', 'bm-tl-svg')
            .attr('width', '100%').attr('height', '100%');

        const gridL   = laneSvg.append('g').attr('class', 'bm-tl-grid-layer');
        const cursorL = laneSvg.append('g').attr('class', 'bm-tl-cursor-layer').style('pointer-events','none');
        const blockL  = laneSvg.append('g').attr('class', 'bm-tl-block-layer');
        const headerL = laneSvg.append('g').attr('class', 'bm-tl-header-layer');
        const cursorLine = cursorL.append('line')
            .style('stroke', CURSOR_COLOR).style('stroke-width', 1.5).style('opacity', 0);

        // Bottom SVG.
        const botSvg = d3.select(bottomEl).append('svg')
            .attr('class', 'bm-tl-bot-svg')
            .attr('width', '100%').attr('height', BOTTOM_H);

        const HEAT_Y     = TICK_H + BOT_PAD_T;
        const SEL_OFFSET = HEAT_Y + Math.floor((HEAT_H - SEL_H) / 2);
        const tickL      = botSvg.append('g').attr('class','bm-tl-tick-layer');
        const heatL      = botSvg.append('g').attr('class','bm-tl-heat-layer')
            .attr('transform', `translate(0,${HEAT_Y})`);
        const selL       = botSvg.append('g').attr('class','bm-tl-sel-layer')
            .attr('transform', `translate(0,${SEL_OFFSET})`);
        const curBotL    = botSvg.append('g').attr('class','bm-tl-curbot-layer')
            .style('pointer-events','none');

        const tooltip = d3.select(laneEl).append('div')
            .attr('class','bm-tl-tooltip')
            .style('opacity',0).style('pointer-events','none');

        const xScale = d3.scaleLinear().domain([0,1]).range([0,800]);

        // State object — must exist before wiring event handlers.
        const s = {
            laneEl, bottomEl, laneSvg, botSvg,
            gridL, cursorL, blockL, headerL, cursorLine,
            heatL, selL, tickL, curBotL, tooltip,
            xScale, xZoom: d3.zoomIdentity,
            data: null, layout: null,
            svgW: 800, viewH: 600,
            frozenDomainMax: null,
            globalMax: null,
            cursorX: null, cursorTimer: null,
            hoveredBatchIdx: -1,
            hoveredChunkId: null,
            subrowHOverride: null,
            selFn: null,
        };

        // Wire D3 zoom — captures s via closure, safe because s is already assigned.
        const zoom = d3.zoom()
            .scaleExtent([1, 5000])
            .filter(event => {
                if (event.type === 'wheel' && !event.ctrlKey) {
                    const wrap = laneEl.parentElement;
                    if (wrap && wrap.scrollHeight > wrap.clientHeight + 2) return false;
                }
                return !event.button;
            })
            .on('zoom', event => {
                if (!s.frozenDomainMax) return;
                const t    = event.transform;
                const W    = s.svgW;
                const fMax = s.frozenDomainMax;
                const gMax = s.globalMax || fMax;
                const rightLimit = -(t.k * (gMax / fMax) * W - W);
                const tx = Math.max(rightLimit, Math.min(0, t.x));
                s.xZoom = d3.zoomIdentity.translate(tx, 0).scale(t.k);
                renderLanes(s);
                renderTicks(s);
                syncSelector(s);
            });

        s.zoom = zoom;
        laneSvg.call(zoom).style('cursor','crosshair')
            .on('mousemove.cur', ev => onMouseMove(s, ev))
            .on('mouseleave.cur', () => onMouseLeave(s));

        const kd = e => handleKey(s, e);
        document.addEventListener('keydown', kd);
        s.keydownFn = kd;

        // Brush selector.
        buildSelector(s);

        // Resize observer.
        const ro = new ResizeObserver(() => { if (!s.disposed) onResize(s); });
        ro.observe(laneEl.parentElement || laneEl);
        s.resizeObserver = ro;

        return s;
    }

    function disposeInstance(s) {
        s.disposed = true;
        document.removeEventListener('keydown', s.keydownFn);
        s.resizeObserver?.disconnect();
        clearTimeout(s.cursorTimer);
        try { s.laneSvg.remove(); } catch {}
        try { s.botSvg.remove(); } catch {}
        try { s.tooltip.remove(); } catch {}
    }

    // Apply a zoom transform without triggering the zoom event (avoids loops).
    function applyZoom(s, t) {
        s.zoom.on('zoom', null);
        s.laneSvg.call(s.zoom.transform, t);
        s.zoom.on('zoom', event => {
            if (!s.frozenDomainMax) return;
            const W    = s.svgW;
            const fMax = s.frozenDomainMax;
            const gMax = s.globalMax || fMax;
            const rightLimit = -(event.transform.k * (gMax / fMax) * W - W);
            const tx = Math.max(rightLimit, Math.min(0, event.transform.x));
            s.xZoom = d3.zoomIdentity.translate(tx, 0).scale(event.transform.k);
            renderLanes(s);
            renderTicks(s);
            syncSelector(s);
        });
    }

    // ── Resize ────────────────────────────────────────────────────────────

    function onResize(s) {
        const rect = s.laneEl.getBoundingClientRect();
        s.svgW  = rect.width  || 800;
        s.viewH = s.laneEl.parentElement?.clientHeight || rect.height || 600;
        s.xScale.range([0, s.svgW]);
        s.botSvg.attr('width', s.svgW);
        if (s.selFn) {
            s.selFn.extent([[PAD, 0], [s.svgW - PAD, SEL_H]]);
            s.selL.call(s.selFn);
            syncSelector(s);
        }
        if (s.layout) { updateLaneSvgHeight(s); renderAll(s); }
    }

    // ── Layout ────────────────────────────────────────────────────────────

    function computeLayout(s) {
        if (!s.data) return;
        const groupBy  = s.data.groupBy  || 'ServicePipeline';
        const colourBy = s.data.colourBy || 'Source';
        const filter   = (s.data.filter  || '').toLowerCase();
        const stack    = s.data.stackView || false;

        let y = 0;
        const sections = [];
        for (const batch of (s.data.batches || [])) {
            const groups  = stack ? buildStackGroups(s, batch, filter) : buildGroups(s, batch, groupBy, filter);
            const sectionH = HEADER_H + groups.reduce((a, g) => a + g.rowH, 0) + SECTION_GAP;
            sections.push({ batch, groups, y, sectionH });
            y += sectionH;
        }
        s.layout = { sections, totalH: y };
    }

    function buildGroups(s, batch, groupBy, filter) {
        const map = new Map();
        for (const e of (batch.events || [])) {
            if (e.finishMs == null) continue;
            if (filter && !matchesFilter(e, filter)) continue;
            const key = groupKey(e, groupBy);
            if (!map.has(key)) map.set(key, []);
            map.get(key).push(e);
        }
        const groups = [];
        for (const [key, events] of map) {
            const subrows = packSubrows(events);
            const SH = s.subrowHOverride ?? SUBROW_H;
            const rowH = ROW_PAD * 2 + subrows.length * (SH + BLOCK_GAP);
            groups.push({ key, events, subrows, rowH });
        }
        return groups;
    }

    function buildStackGroups(s, batch, filter) {
        const events = (batch.events || []).filter(e =>
            e.finishMs != null && (!filter || matchesFilter(e, filter)));
        const chunks = new Map();
        for (const e of events) {
            if (!chunks.has(e.chunkId)) chunks.set(e.chunkId, []);
            chunks.get(e.chunkId).push(e);
        }
        for (const [, ev] of chunks) ev.sort((a, b) => a.startMs - b.startMs);
        const groups = [];
        for (const [chunkId, evts] of chunks) {
            let cursor = 0;
            const subrow = [];
            for (const e of evts) {
                const dur = (e.finishMs ?? e.startMs) - e.startMs;
                subrow.push({ event: e, xStart: cursor, xEnd: cursor + dur, stack: true });
                cursor += dur;
            }
            const SH  = s.subrowHOverride ?? SUBROW_H;
            const rowH = ROW_PAD * 2 + SH + BLOCK_GAP;
            groups.push({ key: chunkId, events: evts, subrows: [subrow], rowH });
        }
        return groups;
    }

    function packSubrows(events) {
        const AVG = 5000;
        const sorted = [...events].sort((a, b) => a.startMs - b.startMs);
        const rows = [];
        for (const e of sorted) {
            const xs = e.startMs;
            const xePack = e.finishMs ?? (xs + AVG);
            const xeRend = e.finishMs ?? xs;
            let placed = false;
            for (const row of rows) {
                if (xs >= row[row.length - 1].xEndPack) {
                    row.push({ event: e, xStart: xs, xEnd: xeRend, xEndPack: xePack });
                    placed = true; break;
                }
            }
            if (!placed) rows.push([{ event: e, xStart: xs, xEnd: xeRend, xEndPack: xePack }]);
        }
        return rows;
    }

    function updateLaneSvgHeight(s) {
        if (!s.layout) return;
        const h = Math.max(s.viewH, s.layout.totalH + 16);
        s.laneSvg.attr('height', h);
    }

    // ── Render ────────────────────────────────────────────────────────────

    function renderAll(s) {
        renderLanes(s);
        renderTicks(s);
        renderHeatmap(s);
        syncSelector(s);
    }

    function renderLanes(s) {
        if (!s.layout) return;
        const xS = s.xZoom.rescaleX(s.xScale);
        const W  = s.svgW;
        const H  = parseFloat(s.laneSvg.attr('height')) || s.viewH;
        const colourBy = s.data?.colourBy || 'Source';

        // Global vertical grid.
        s.gridL.selectAll('*').remove();
        const ticks = xS.ticks(Math.floor(W / 90));
        const step  = ticks.length > 1 ? ticks[1] - ticks[0] : 0;
        if (step > 0) {
            for (const t of ticks.map(t => t + step / 2)) {
                s.gridL.append('line')
                    .attr('x1', xS(t)).attr('x2', xS(t))
                    .attr('y1', 0).attr('y2', H)
                    .style('stroke', GRID_MID).style('stroke-width', 0.5);
            }
        }
        for (const t of ticks) {
            s.gridL.append('line')
                .attr('x1', xS(t)).attr('x2', xS(t))
                .attr('y1', 0).attr('y2', H)
                .style('stroke', GRID_MAJOR).style('stroke-width', 1);
        }

        // Sections.
        const secSel = s.blockL.selectAll('g.bm-tl-sec')
            .data(s.layout.sections, d => d.batch.runId);
        secSel.exit().remove();
        const secEnter = secSel.enter().append('g').attr('class', 'bm-tl-sec');
        secEnter.append('g').attr('class', 'bm-tl-sec-lines');
        secEnter.append('g').attr('class', 'bm-tl-sec-blocks');
        secEnter.merge(secSel)
            .attr('transform', d => `translate(0,${d.y})`)
            .each(function (sec) {
                renderSectionLines(s, d3.select(this).select('.bm-tl-sec-lines'), sec, W);
                renderSectionBlocks(s, d3.select(this).select('.bm-tl-sec-blocks'), sec, xS, W, colourBy);
            });

        // Headers.
        const hdrSel = s.headerL.selectAll('g.bm-tl-hdr')
            .data(s.layout.sections, d => d.batch.runId);
        hdrSel.exit().remove();
        hdrSel.enter().append('g').attr('class', 'bm-tl-hdr')
            .merge(hdrSel)
            .attr('transform', d => `translate(0,${d.y})`)
            .each(function (sec) { renderHeader(s, d3.select(this), sec); });

        // Cursor line.
        s.cursorLine.attr('y1', 0).attr('y2', H);
        if (s.cursorX != null)
            s.cursorLine.attr('x1', s.cursorX).attr('x2', s.cursorX);
    }

    function renderSectionLines(s, sel, sec, W) {
        sel.selectAll('*').remove();
        const secH = sec.sectionH - SECTION_GAP;
        sel.append('line')
            .attr('x1', 0).attr('x2', W).attr('y1', secH).attr('y2', secH)
            .style('stroke', GRID_MAJOR).style('stroke-width', 1);
        let rowY = HEADER_H;
        for (const grp of sec.groups) {
            sel.append('line')
                .attr('x1', 0).attr('x2', W).attr('y1', rowY).attr('y2', rowY)
                .style('stroke', SEPARATOR).style('stroke-width', 1)
                .style('stroke-dasharray', '3 5');
            rowY += grp.rowH;
        }
    }

    function renderSectionBlocks(s, sel, sec, xS, W, colourBy) {
        const SH = s.subrowHOverride ?? SUBROW_H;
        const blockData = [];
        let rowY = HEADER_H;
        for (let gi = 0; gi < sec.groups.length; gi++) {
            const grp = sec.groups[gi];
            for (let si = 0; si < grp.subrows.length; si++) {
                const subrow = grp.subrows[si];
                const sy = rowY + ROW_PAD + si * (SH + BLOCK_GAP);
                for (let bi = 0; bi < subrow.length; bi++) {
                    const item = subrow[bi];
                    const e    = item.event;
                    const xSt  = item.stack ? item.xStart : xS(e.startMs);
                    const xEn  = item.stack ? item.xEnd   : xS(e.finishMs ?? e.startMs);
                    const x    = item.stack ? xSt : xSt;
                    const w    = Math.max(0, (item.stack ? xEn - xSt : xS(e.finishMs ?? e.startMs) - xS(e.startMs)));
                    if (!item.stack && (xS(e.startMs) > W || xS(e.finishMs ?? e.startMs) < 0)) continue;
                    const ck   = colourKey(e, colourBy);
                    blockData.push({
                        e, ck,
                        x: item.stack ? xSt : xS(e.startMs),
                        w: Math.max(1, w),
                        colour: colourForKey(ck, colourBy),
                        bse:    sec.batch.batchStartEpochMs || 0,
                        stableKey: `${sec.batch.runId}:${gi}:${si}:${bi}`,
                        y: sy,
                        h: Math.max(1, SH - 1),
                    });
                }
            }
            rowY += grp.rowH;
        }

        const rects = sel.selectAll('rect.bm-tl-block').data(blockData, d => d.stableKey);
        rects.exit().remove();
        rects.enter().append('rect').attr('class', 'bm-tl-block').attr('rx', BLOCK_RX)
            .merge(rects)
            .attr('x', d => d.x)
            .attr('y', d => d.y)
            .attr('width',  d => d.w)
            .attr('height', d => d.h)
            .style('fill',         d => d.colour)
            .style('fill-opacity', d => {
                if (s.hoveredChunkId == null) return d.e.status === 'inprogress' ? 0.6 : 1;
                return d.e.chunkId === s.hoveredChunkId ? 1 : 0.12;
            })
            .style('stroke',       d => d3.color(d.colour)?.darker(.5) ?? d.colour)
            .style('stroke-width', d => d.e.chunkId === s.hoveredChunkId ? '1.5px' : '0.5px')
            .on('mouseenter', (ev, d) => onBlockEnter(s, ev, d))
            .on('mouseleave', (ev, d) => onBlockLeave(s, ev, d));
    }

    function renderHeader(s, sel, sec) {
        sel.selectAll('*').remove();
        const cy  = HEADER_H / 2;
        const lx  = 8;
        if (sec.batch.isLive) {
            sel.append('circle').attr('class', 'bm-tl-live-dot')
                .attr('cx', lx).attr('cy', cy).attr('r', 5).style('fill', '#3FB950');
        }
        const tx   = sec.batch.isLive ? lx + 14 : lx;
        const MAX  = 28;
        const name = sec.batch.batchName.length > MAX
            ? sec.batch.batchName.slice(0, MAX) + '…'
            : sec.batch.batchName;
        const bgW  = name.length * 7 + sec.batch.runId.length * 6 + 28;
        sel.append('rect')
            .attr('x', tx - 4).attr('y', 2)
            .attr('width', bgW).attr('height', HEADER_H - 4)
            .attr('rx', 3).style('fill', BG_COLOR).style('opacity', 0.88);
        sel.append('text').attr('class', 'bm-tl-batch-name')
            .attr('x', tx).attr('y', cy).attr('dy', '0.32em').text(name);
        sel.append('text').attr('class', 'bm-tl-batch-runid')
            .attr('x', tx + name.length * 7 + 10).attr('y', cy).attr('dy', '0.32em')
            .text(sec.batch.runId);
    }

    // ── Tick scale ────────────────────────────────────────────────────────

    function renderTicks(s) {
        if (!s.layout) return;
        const xS  = s.xZoom.rescaleX(s.xScale);
        const W   = s.svgW;
        const bse = getHoveredBatchStart(s);

        s.tickL.selectAll('*').remove();
        s.tickL.append('rect').attr('width', W).attr('height', TICK_H).style('fill', BG_COLOR);
        s.tickL.append('line').attr('x1',0).attr('x2',W).attr('y1',0).attr('y2',0)
            .style('stroke', GRID_MAJOR).style('stroke-width', 1);

        const ticks = xS.ticks(Math.floor(W / 100));
        const step  = ticks.length > 1 ? ticks[1] - ticks[0] : 0;
        if (step > 0) {
            for (const t of ticks.map(t => t + step / 2)) {
                const x = xS(t);
                if (!isFinite(x)) continue;
                s.tickL.append('line').attr('x1',x).attr('x2',x)
                    .attr('y1', TICK_H-3).attr('y2', TICK_H)
                    .style('stroke', GRID_MID).style('stroke-width', 0.5);
            }
        }
        for (const t of ticks) {
            const x = xS(t);
            if (!isFinite(x)) continue;
            s.tickL.append('line').attr('x1',x).attr('x2',x)
                .attr('y1', TICK_H-5).attr('y2', TICK_H)
                .style('stroke', GRID_MAJOR).style('stroke-width', 1);
            s.tickL.append('text').attr('class', 'bm-tl-tick-label')
                .attr('x', x).attr('y', TICK_H-8).attr('text-anchor', 'middle')
                .text(bse ? fmtAbsHMS(bse + t) : fmtRelHMS(t));
        }
    }

    function getHoveredBatchStart(s) {
        if (!s.layout) return null;
        const secs = s.layout.sections;
        const idx  = s.hoveredBatchIdx;
        const sec  = (idx >= 0 && idx < secs.length) ? secs[idx] : secs[0];
        return sec?.batch?.batchStartEpochMs || null;
    }

    // ── Heatmap ───────────────────────────────────────────────────────────

    function renderHeatmap(s) {
        if (!s.layout || !s.frozenDomainMax) return;
        const gMax    = s.globalMax || s.frozenDomainMax;
        const W       = s.svgW - PAD * 2;
        const buckets = Math.min(500, Math.floor(W));
        const counts  = new Array(buckets).fill(0);
        for (const sec of s.layout.sections)
            for (const e of (sec.batch.events || [])) {
                const b = Math.floor((e.startMs / gMax) * (buckets - 1));
                if (b >= 0 && b < buckets) counts[b]++;
            }
        const maxC = Math.max(1, ...counts);
        const bW   = W / buckets;

        s.heatL.selectAll('*').remove();
        s.heatL.append('rect').attr('x', PAD).attr('width', W).attr('height', HEAT_H)
            .style('fill', HEAT_BG);
        for (let i = 0; i < buckets; i++) {
            if (!counts[i]) continue;
            const op = 0.12 + 0.88 * (counts[i] / maxC);
            s.heatL.append('line')
                .attr('x1', PAD + i * bW + bW / 2).attr('x2', PAD + i * bW + bW / 2)
                .attr('y1', 0).attr('y2', HEAT_H)
                .style('stroke', HEAT_LINE)
                .style('stroke-width', Math.max(1, bW - 0.5))
                .style('opacity', op);
        }
    }

    // ── Selector ─────────────────────────────────────────────────────────

    function buildSelector(s) {
        const W     = s.svgW;
        const brush = d3.brushX()
            .extent([[PAD, 0], [W - PAD, SEL_H]])
            .on('brush', event => {
                if (!event.selection || !event.sourceEvent) return;
                const [px0, px1] = event.selection;
                applyBrushSelection(s, px0, px1);
            });
        s.selFn = brush;
        s.selL.call(brush);
        syncSelector(s);
    }

    function applyBrushSelection(s, px0, px1) {
        if (!s.frozenDomainMax) return;
        const gMax = s.globalMax || s.frozenDomainMax;
        const fMax = s.frozenDomainMax;
        const W    = s.svgW;
        const brushScale = d3.scaleLinear().domain([PAD, W - PAD]).range([0, gMax]);
        const t0ms = brushScale(px0);
        const t1ms = brushScale(px1);
        const visMs = t1ms - t0ms;
        if (visMs < 1) return;
        const k  = fMax / visMs;
        const tx = -(t0ms / fMax) * W * k;
        const t  = d3.zoomIdentity.translate(tx, 0).scale(k);
        s.xZoom = t;
        applyZoom(s, t);
    }

    function syncSelector(s) {
        if (!s.selFn || !s.frozenDomainMax) return;
        const gMax = s.globalMax || s.frozenDomainMax;
        const fMax = s.frozenDomainMax;
        const W    = s.svgW;
        const xS   = s.xZoom.rescaleX(s.xScale);
        const brushScale = d3.scaleLinear().domain([0, gMax]).range([PAD, W - PAD]);
        const s0 = Math.max(PAD,   Math.min(W - PAD, brushScale(xS.invert(0))));
        const s1 = Math.max(PAD,   Math.min(W - PAD, brushScale(xS.invert(W))));
        if (isFinite(s0) && isFinite(s1) && s1 > s0 + 1)
            s.selL.call(s.selFn.move, [s0, s1]);
    }

    // ── Cursor ────────────────────────────────────────────────────────────

    function onMouseMove(s, event) {
        if (!s.layout) return;
        const [mx, my] = d3.pointer(event);
        s.cursorX = mx;
        s.cursorLine.attr('x1', mx).attr('x2', mx).style('opacity', 1);

        const secs = s.layout.sections;
        let hovIdx = -1;
        for (let i = 0; i < secs.length; i++) {
            const sec = secs[i];
            if (my >= sec.y && my < sec.y + sec.sectionH) { hovIdx = i; break; }
        }
        if (s.hoveredBatchIdx !== hovIdx) {
            s.hoveredBatchIdx = hovIdx;
            renderTicks(s);
        }

        const xS     = s.xZoom.rescaleX(s.xScale);
        const timeMs = xS.invert(mx);
        const bse    = getHoveredBatchStart(s);

        renderCursorBot(s, mx, timeMs, bse);

        clearTimeout(s.cursorTimer);
        s.cursorTimer = setTimeout(() => {
            if (!s.disposed) {
                s.cursorLine.style('opacity', 0);
                s.cursorX = null;
                s.curBotL.selectAll('*').remove();
            }
        }, 2000);
    }

    function renderCursorBot(s, mx, timeMs, bse) {
        s.curBotL.selectAll('*').remove();
        s.curBotL.append('line')
            .attr('x1', mx).attr('x2', mx).attr('y1', 0).attr('y2', TICK_H + HEAT_H / 2)
            .style('stroke', CURSOR_COLOR).style('stroke-width', 1.5).style('opacity', 0.8);

        const relL = fmtRelHMS(timeMs);
        const absL = bse ? ` (${fmtAbsHMS(bse + timeMs)})` : '';
        const label = relL + absL;
        const lw  = label.length * 6.5 + 12;
        const lh  = 15;
        let   lx  = mx - lw / 2;
        lx = Math.max(2, Math.min((s.svgW || 800) - lw - 2, lx));

        s.curBotL.append('rect').attr('x', lx).attr('y', 2)
            .attr('width', lw).attr('height', lh).attr('rx', 3)
            .style('fill', CURSOR_COLOR);
        s.curBotL.append('text').attr('x', lx + lw / 2).attr('y', 2 + lh / 2)
            .attr('text-anchor', 'middle').attr('dy', '0.35em')
            .style('fill', '#fff').style('font-size', '0.6rem')
            .style('font-weight', '700')
            .style('font-family', 'var(--bm-font-mono,monospace)')
            .text(label);
    }

    function onMouseLeave(s) {
        s.cursorX = null;
        s.cursorLine.style('opacity', 0);
        s.curBotL.selectAll('*').remove();
    }

    // ── Block hover ───────────────────────────────────────────────────────

    function onBlockEnter(s, event, d) {
        s.hoveredChunkId = d.e.chunkId;
        s.blockL.selectAll('rect.bm-tl-block')
            .style('fill-opacity', b => b.e.chunkId === s.hoveredChunkId ? 1 : 0.12)
            .style('stroke-width', b => b.e.chunkId === s.hoveredChunkId ? '1.5px' : '0.5px');
        showTooltip(s, event, d);
    }

    function onBlockLeave(s, event, d) {
        s.hoveredChunkId = null;
        s.blockL.selectAll('rect.bm-tl-block')
            .style('fill-opacity', b => b.e.status === 'inprogress' ? 0.6 : 1)
            .style('stroke-width', '0.5px');
        hideTooltip(s);
    }

    // ── Tooltip ───────────────────────────────────────────────────────────

    function showTooltip(s, event, d) {
        const e   = d.e;
        const bse = d.bse;
        const dur = e.finishMs != null ? Math.round(e.finishMs - e.startMs) : null;
        const durStr  = dur != null ? `${dur.toLocaleString()}ms` : '(in progress)';
        const stLabel = e.status==='done'?'✓ Done':e.status==='inprogress'?'⟳ In Progress':'✗ Error';
        const stClass = `bm-tl-tt-${e.status}`;
        const sRel = fmtRelMs(e.startMs);
        const sAbs = bse ? fmtAbsMs(bse + e.startMs) : '';
        const startStr = sAbs ? `${sRel} (${sAbs})` : sRel;
        const fRel = e.finishMs != null ? fmtRelMs(e.finishMs) : `${fmtRelMs(e.startMs)}…`;
        const fAbs = bse && e.finishMs != null ? fmtAbsMs(bse + e.finishMs) : '';
        const finStr = fAbs ? `${fRel} (${fAbs})` : fRel;

        s.tooltip.html(`
            <div class="bm-tl-tt-header">
                <span class="bm-tl-tt-id">${esc(e.chunkId)}</span>
                <span class="bm-tl-tt-status ${stClass}">${stLabel}</span>
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
            ${e.error ? `<div class="bm-tl-tt-row bm-tl-tt-err"><span>Error</span><span>${esc(e.error)}</span></div>` : ''}
            <div class="bm-tl-tt-sep"></div>
            <div class="bm-tl-tt-footer" id="bm-tl-tt-footer">Ctrl+click to copy</div>
        `).style('opacity', 1).style('pointer-events', 'auto');

        s.tooltip.on('click', ev => {
            if (!ev.ctrlKey) return;
            navigator.clipboard?.writeText(
                [`ChunkId: ${e.chunkId}`, `Source: ${e.source}`,
                 `Start: ${startStr}`, `Finish: ${finStr}`,
                 `Duration: ${durStr}`].join('\n'));
            const f = document.getElementById('bm-tl-tt-footer');
            if (f) { f.textContent = 'Copied ✓'; setTimeout(() => { if(f) f.textContent='Ctrl+click to copy'; }, 1000); }
        });

        positionTooltip(s, event);
    }

    function positionTooltip(s, event) {
        const rect = s.laneEl.getBoundingClientRect();
        let x = event.clientX - rect.left + 12;
        let y = event.clientY - rect.top  - 8;
        const pw = 280, ph = 230;
        if (x + pw > rect.width  - 8) x = event.clientX - rect.left - pw - 12;
        if (y + ph > rect.height - 8) y = event.clientY - rect.top  - ph - 12;
        if (y < 0) y = event.clientY - rect.top + 16;
        x = Math.max(4, x); y = Math.max(4, y);
        s.tooltip.style('left', `${x}px`).style('top', `${y}px`);
    }

    function hideTooltip(s) {
        s.tooltip.style('opacity', 0).style('pointer-events', 'none');
    }

    // ── Keyboard ──────────────────────────────────────────────────────────

    function handleKey(s, e) {
        if (!s || s.disposed) return;
        const tag = document.activeElement?.tagName;
        if (tag === 'INPUT' || tag === 'TEXTAREA') return;
        const W    = s.svgW || 800;
        const t    = s.xZoom;
        const wrap = s.laneEl?.parentElement;

        if (e.key === 'ArrowUp' || e.key === 'ArrowDown') {
            e.preventDefault();
            const vScroll = wrap && wrap.scrollHeight > wrap.clientHeight + 2;
            if (vScroll && !e.ctrlKey) {
                if (wrap) wrap.scrollTop += e.key === 'ArrowDown' ? 60 : -60;
            } else {
                const sh = s.subrowHOverride ?? SUBROW_H;
                s.subrowHOverride = Math.max(1, sh + (e.key === 'ArrowUp' ? 1 : -1));
                computeLayout(s);
                updateLaneSvgHeight(s);
                renderAll(s);
            }
            return;
        }

        let nt = null;
        switch (e.key) {
            case '=': case '+':
                nt = d3.zoomIdentity.translate(t.x, 0).scale(t.k * 1.25); break;
            case '-':
                nt = d3.zoomIdentity.translate(t.x, 0).scale(Math.max(1, t.k / 1.25)); break;
            case 'ArrowLeft': {
                const dx = e.shiftKey ? W * 0.5 : W * 0.1;
                nt = d3.zoomIdentity.translate(Math.min(0, t.x + dx), 0).scale(t.k); break;
            }
            case 'ArrowRight': {
                const dx   = e.shiftKey ? W * 0.5 : W * 0.1;
                const gMx  = s.globalMax || s.frozenDomainMax || 1;
                const fMx  = s.frozenDomainMax || 1;
                const rl   = -(t.k * (gMx / fMx) * W - W);
                nt = d3.zoomIdentity.translate(Math.max(rl, t.x - dx), 0).scale(t.k); break;
            }
            case 'Home':
                nt = d3.zoomIdentity; break;
            default: return;
        }
        if (nt) { e.preventDefault(); applyZoom(s, nt); }
    }

    // ── Export ────────────────────────────────────────────────────────────

    async function doExport(s) {
        const rows = ['RunId,BatchName,ChunkId,Source,Pipeline,Service,PID,Server,StartRelMs,FinishRelMs,DurationMs,Status,Error'];
        for (const b of (s.data.batches || []))
            for (const e of (b.events || []))
                rows.push([csv(b.runId), csv(b.batchName), csv(e.chunkId),
                    csv(e.source), csv(e.pipeline), csv(e.service),
                    csv(e.processId), csv(e.server),
                    e.startMs.toFixed(1), e.finishMs ?? '',
                    e.finishMs != null ? (e.finishMs - e.startMs).toFixed(0) : '',
                    csv(e.status), csv(e.error ?? '')].join(','));
        const content = rows.join('\r\n');
        const fname   = `timeline_${new Date().toISOString().slice(0,19).replace(/[:T]/g,'-')}.csv`;
        if (window.showSaveFilePicker) {
            try {
                const fh = await window.showSaveFilePicker({ suggestedName: fname,
                    types: [{ description: 'CSV', accept: { 'text/csv': ['.csv'] } }] });
                const w = await fh.createWritable();
                await w.write(content); await w.close(); return;
            } catch (ex) { if (ex.name === 'AbortError') return; }
        }
        const blob = new Blob([content], { type: 'text/csv;charset=utf-8;' });
        const url  = URL.createObjectURL(blob);
        const a    = Object.assign(document.createElement('a'), { href: url, download: fname, style: 'display:none' });
        document.body.appendChild(a); a.click();
        setTimeout(() => { document.body.removeChild(a); URL.revokeObjectURL(url); }, 200);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    function fmtRelHMS(ms) {
        if (!isFinite(ms) || ms < 0) ms = 0;
        const s = Math.floor(ms / 1000), h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), sec = s % 60;
        return `${String(h).padStart(2,'0')}:${String(m).padStart(2,'0')}:${String(sec).padStart(2,'0')}`;
    }
    function fmtAbsHMS(epochMs) {
        if (!isFinite(epochMs)) return '';
        const d = new Date(epochMs);
        return `${String(d.getHours()).padStart(2,'0')}:${String(d.getMinutes()).padStart(2,'0')}:${String(d.getSeconds()).padStart(2,'0')}`;
    }
    function fmtRelMs(ms) {
        if (!isFinite(ms) || ms < 0) ms = 0;
        const s = ms / 1000, h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), sec = (s % 60).toFixed(3);
        return `${String(h).padStart(2,'0')}:${String(m).padStart(2,'0')}:${sec.padStart(6,'0')}`;
    }
    function fmtAbsMs(epochMs) {
        if (!isFinite(epochMs)) return '';
        const d = new Date(epochMs);
        return `${String(d.getHours()).padStart(2,'0')}:${String(d.getMinutes()).padStart(2,'0')}:${String(d.getSeconds()).padStart(2,'0')}.${String(d.getMilliseconds()).padStart(3,'0')}`;
    }
    function groupKey(e, g) {
        switch (g) {
            case 'ServicePipeline':    return `${e.service} / ${e.pipeline}`;
            case 'PidServicePipeline': return `${e.server}:${e.processId} / ${e.service} / ${e.pipeline}`;
            case 'Service':  return e.service;
            case 'Pipeline': return e.pipeline;
            case 'Pid':      return `${e.server}:${e.processId}`;
            default:         return `${e.service} / ${e.pipeline}`;
        }
    }
    function colourKey(e, c) {
        switch (c) {
            case 'Pipeline': return e.pipeline;
            case 'Service':  return e.service;
            case 'Status':   return e.status;
            default:         return e.source;
        }
    }
    function colourForKey(key, c) {
        return c === 'Status' ? (STATUS_COLOR[key] || '#8B949E') : PALETTE[hashStr(key) % PALETTE.length];
    }
    function hashStr(s) {
        let h = 0;
        for (let i = 0; i < s.length; i++) h = Math.imul(31, h) + s.charCodeAt(i) | 0;
        return Math.abs(h);
    }
    function matchesFilter(e, f) {
        return [e.chunkId, e.source, e.pipeline, e.service, e.processId, e.server]
            .some(v => (v || '').toLowerCase().includes(f));
    }
    function esc(s) {
        return String(s ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
    }
    function csv(v) { return `"${String(v ?? '').replace(/"/g, '""')}"`; }

    return { init, update, resetView, exportCsv, dispose };

})();
