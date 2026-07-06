// Batch Monitor — Timeline Tab (Step 10 rev 9 — clean multi-instance rewrite)
//
// Each Timeline tab gets an isolated instance via a unique key.
// All internal functions receive the instance state `s` as first argument.
// No module-level mutable state — everything is in the instance map.

// ES module — exported functions are the public API consumed via Blazor JS isolation.
import { parse, evaluate } from './filter.js?v=2';

// Field aliases and searchable fields for the timeline event objects (camelCase).
const TIMELINE_SEARCHABLE_FIELDS = ['service', 'pipeline', 'source', 'name', 'server'];
const TIMELINE_ALIASES = {
    svc: 'service',      service:   'service',
    pipe: 'pipeline',    pipeline:  'pipeline',
    srv: 'server',       server:    'server',
    pid: 'processId',    processid: 'processId',
    src: 'source',       source:    'source',
    chunk: 'name',    chunkid:   'name',
    dur: 'duration',     duration:  'duration',
    start: 'startUtc',   startutc:  'startUtc',
    finish: 'finishUtc', finishutc: 'finishUtc',
};

    const _instances = new Map();

    // ── Constants ─────────────────────────────────────────────────────────
    const HEADER_H  = 26;
    const SUBROW_H  = 9;
    const ROW_PAD   = 3;
    const BLOCK_GAP = 2;
    const BLOCK_RX  = 4;
    const SECTION_GAP = 6;
    const TICK_H    = 22;
    const HEAT_H    = 14;
    const SEL_H     = 26;
    const BOT_PAD_T = 8;
    const BOT_PAD_B = 12;
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
    let   GRID_MAJOR   = 'rgba(255,255,255,0.10)';
    let   GRID_MID     = 'rgba(255,255,255,0.04)';
    let   SEPARATOR    = 'rgba(255,255,255,0.08)';
    let   HEAT_BG      = '#161620';
    const HEAT_LINE    = '#388BFD';
    let   BG_COLOR     = '#13131a';

    function _cssVar(name) {
        return getComputedStyle(document.documentElement).getPropertyValue(name).trim();
    }
    function refreshColors() {
        // MudBlazor stores palette values as bare "r,g,b" components (no rgb() wrapper).
        // Wrap them so the canvas can parse them, then normalise to #rrggbb.
        function _mudColor(varName) {
            const v = _cssVar(varName);
            if (!v) return '';
            // Already a valid CSS colour (hex, named, rgb(...)):
            if (v.startsWith('#') || v.startsWith('rgb') || /^[a-z]+$/.test(v)) return v;
            // Bare "r,g,b" — wrap it.
            return `rgb(${v})`;
        }
        const raw = _mudColor('--mud-palette-surface') || _mudColor('--mud-palette-background') || '#ffffff';
        const ctx = document.createElement('canvas').getContext('2d');
        ctx.fillStyle = '#808080';
        ctx.fillStyle = raw;
        const hex = ctx.fillStyle; // #rrggbb after normalisation
        const r = parseInt(hex.slice(1,3), 16);
        const g = parseInt(hex.slice(3,5), 16);
        const b = parseInt(hex.slice(5,7), 16);
        const dark = (0.299*r + 0.587*g + 0.114*b) < 128;

        BG_COLOR   = raw;
        HEAT_BG    = dark ? '#161620' : (_mudColor('--mud-palette-background-grey') || '#eeeeee');
        GRID_MAJOR = dark ? 'rgba(255,255,255,0.10)' : 'rgba(0,0,0,0.12)';
        GRID_MID   = dark ? 'rgba(255,255,255,0.04)' : 'rgba(0,0,0,0.05)';
        SEPARATOR  = dark ? 'rgba(255,255,255,0.08)' : 'rgba(0,0,0,0.07)';
    }

    // ── Public API ─────────────────────────────────────────────────────────

    function init(key, laneEl, bottomEl, options) {
        // Dispose any existing instance for this key.
        if (_instances.has(key)) disposeInstance(_instances.get(key));

        const s = createInstance(laneEl, bottomEl, options);
        _instances.set(key, s);
        onResize(s);
        requestAnimationFrame(() => { if (!s.disposed) onResize(s); });
    }

    function update(key, payload) {
        const s = _instances.get(key);
        if (!s) return;
        hideTooltip(s);
        s.hoveredName  = null;
        s.hoveredBatchIdx = -1;
        s.data = payload;

        // Enrich each event with computed fields used by the filter engine.
        // Done here once per update so computeLayout / evaluate stay generic.
        const todayMs = new Date().setUTCHours(0, 0, 0, 0);
        for (const batch of (payload.batches || [])) {
            const bse = batch.batchStartEpochMs || 0;
            for (const e of (batch.events || [])) {
                e.startUtc  = new Date(bse + e.startMs).toISOString();
                e.finishUtc = e.finishMs != null ? new Date(bse + e.finishMs).toISOString() : null;
                // duration stored as today-midnight + elapsed so hh:mm filter syntax works directly.
                e.duration  = e.finishMs != null
                    ? new Date(todayMs + (e.finishMs - e.startMs)).toISOString()
                    : null;
            }
        }

        const isStack  = !!(payload.stackView);
        const wasStack = s.isStack ?? false;
        s.isStack = isStack;

        if (isStack) {
            // Domain = max(sum of durations per name) — represents the longest
            // accumulated processing time across all chunks in view.
            const chunkDurs = new Map();
            for (const b of (payload.batches || []))
                for (const e of (b.events || [])) {
                    const dur = Math.max(0, (e.finishMs ?? e.startMs) - e.startMs);
                    chunkDurs.set(e.name, (chunkDurs.get(e.name) || 0) + dur);
                }
            const stackMax = chunkDurs.size > 0
                ? Math.max(1000, ...chunkDurs.values())
                : 1000;

            if (!wasStack) {
                // Entering stack view: set domain and reset zoom to fit.
                s.frozenDomainMax = stackMax;
                s.globalMax       = stackMax;
                s.xScale.domain([0, stackMax]);
                s.xZoom = d3.zoomIdentity;
                applyZoom(s, d3.zoomIdentity);
                s.zoom.scaleExtent([1, 5000]);
            } else {
                // Already in stack view: grow domain if new chunks are longer.
                if (stackMax > s.frozenDomainMax) {
                    s.frozenDomainMax = stackMax;
                    s.globalMax       = stackMax;
                }
            }
        } else {
            if (wasStack) {
                // Leaving stack view: clear frozen domain so normal mode re-freezes
                // on the wall-clock max of the current data.
                s.frozenDomainMax = null;
            }

            let newMax = 0;
            for (const b of (payload.batches || []))
                for (const e of (b.events || []))
                    newMax = Math.max(newMax, e.finishMs ?? e.startMs ?? 0);
            if (newMax < 1000) newMax = 1000;
            s.globalMax = newMax;

            if (s.frozenDomainMax) {
                const minK = Math.min(1, s.frozenDomainMax / newMax);
                s.zoom.scaleExtent([minK, 5000]);
            }

            const nEvents = (payload.batches || []).reduce((a, b) => a + (b.events?.length ?? 0), 0);
            if (s.frozenDomainMax === null && nEvents > 0) {
                s.frozenDomainMax = newMax;
                s.xScale.domain([0, newMax]);
                s.xZoom = d3.zoomIdentity;
                applyZoom(s, d3.zoomIdentity);
            }
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
            isStack: false,
            showTooltip: true,
            cursorX: null, cursorTimer: null,
            hoveredBatchIdx: -1,
            hoveredName: null,
            hoveredBlockData: null,
            subrowHOverride: null,
            selFn: null,
            isVisible: true,
        };

        // Wire D3 zoom — wheel events are handled separately on the bottom panel;
        // the lane SVG uses D3 zoom only for programmatic transforms (applyZoom).
        const zoom = d3.zoom()
            .scaleExtent([1, 5000])
            .filter(event => event.type !== 'wheel' && !event.button)
            .on('zoom', event => {
                if (!s.frozenDomainMax) return;
                s.xZoom = clampTransform(s, event.transform);
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

        // Wheel on bottom panel zooms toward mouse position, but only when the
        // mouse is over the brush selection rectangle (the blue handle).
        const wh = e => {
            if (!s.isVisible || !s.frozenDomainMax) return;
            const bx      = e.clientX - s.bottomEl.getBoundingClientRect().left;
            const brushSel = d3.brushSelection(s.selL.node());
            if (!brushSel || bx < brushSel[0] || bx > brushSel[1]) return;
            e.preventDefault();
            const factor = e.deltaY > 0 ? 1 / 1.15 : 1.15;
            const t    = s.xZoom;
            const W    = s.svgW;
            const gMax = s.globalMax || s.frozenDomainMax;
            // Map bottom-panel mouse X → time → lane pixel, zoom toward that pixel.
            const tMouse = Math.max(0, Math.min(gMax,
                d3.scaleLinear().domain([PAD, W - PAD]).range([0, gMax])(bx)));
            const mx = s.xZoom.rescaleX(s.xScale)(tMouse);
            applyZoom(s, d3.zoomIdentity.translate(mx * (1 - factor) + t.x * factor, 0).scale(t.k * factor));
        };
        bottomEl.addEventListener('wheel', wh, { passive: false });
        s.wheelFn = wh;

        // Resize observer.
        const ro = new ResizeObserver(() => { if (!s.disposed) onResize(s); });
        ro.observe(laneEl.parentElement || laneEl);
        s.resizeObserver = ro;

        return s;
    }

    function disposeInstance(s) {
        s.disposed = true;
        document.removeEventListener('keydown', s.keydownFn);
        s.bottomEl?.removeEventListener('wheel', s.wheelFn);
        s.resizeObserver?.disconnect();
        clearTimeout(s.cursorTimer);
        try { s.laneSvg.remove(); } catch {}
        try { s.botSvg.remove(); } catch {}
        try { s.tooltip.remove(); } catch {}
    }

    // Clamp a proposed transform: keeps k ≥ minK and tx in [rightLimit, 0].
    // minK ensures the viewport never exceeds globalMax width.
    // tx ≤ 0 ensures t=0 stays at or to the left of pixel 0 (no negative time).
    function clampTransform(s, t) {
        const W    = s.svgW || 800;
        const fMax = s.frozenDomainMax || 1;
        const gMax = s.globalMax || fMax;
        const minK = Math.min(1, fMax / gMax);
        const k    = Math.max(minK, t.k);
        const rightLimit = -(k * (gMax / fMax) * W - W);
        const tx = Math.max(rightLimit, Math.min(0, t.x));
        return d3.zoomIdentity.translate(tx, 0).scale(k);
    }

    // Apply a zoom transform: clamps first so D3's internal state stays clean,
    // then fires the zoom handler which updates s.xZoom and renders.
    function applyZoom(s, t) {
        s.laneSvg.call(s.zoom.transform, clampTransform(s, t));
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
        const filterAst = parse(s.data.filter || '', TIMELINE_SEARCHABLE_FIELDS, TIMELINE_ALIASES);
        const stack    = s.data.stackView || false;

        let y = 0;
        const sections = [];
        for (const batch of (s.data.batches || [])) {
            const groups  = stack ? buildStackGroups(s, batch, filterAst) : buildGroups(s, batch, groupBy, filterAst);
            const sectionH = HEADER_H + groups.reduce((a, g) => a + g.rowH, 0) + SECTION_GAP;
            sections.push({ batch, groups, y, sectionH });
            y += sectionH;
        }
        s.layout = { sections, totalH: y };
    }

    function buildGroups(s, batch, groupBy, filterAst) {
        const map = new Map();
        for (const e of (batch.events || [])) {
            if (e.finishMs == null) continue;
            if (!evaluate(filterAst, e)) continue;
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

    function buildStackGroups(s, batch, filterAst) {
        const events = (batch.events || []).filter(e =>
            e.finishMs != null && evaluate(filterAst, e));
        const chunks = new Map();
        for (const e of events) {
            if (!chunks.has(e.name)) chunks.set(e.name, []);
            chunks.get(e.name).push(e);
        }
        for (const [, ev] of chunks) ev.sort((a, b) => a.startMs - b.startMs);
        const groups = [];
        for (const [name, evts] of chunks) {
            let cursor = 0;
            const subrow = [];
            for (const e of evts) {
                const dur = (e.finishMs ?? e.startMs) - e.startMs;
                subrow.push({ event: e, xStart: cursor, xEnd: cursor + dur, stack: true });
                cursor += dur;
            }
            const SH  = s.subrowHOverride ?? SUBROW_H;
            const rowH = ROW_PAD * 2 + SH + BLOCK_GAP;
            groups.push({ key: name, events: evts, subrows: [subrow], rowH });
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
        const h    = Math.max(s.viewH, s.layout.totalH + 16);
        s.laneSvg.attr('height', h);
        // Clamp scroll so removing a batch doesn't leave the viewport in empty space.
        const wrap = s.laneEl.parentElement;
        if (wrap) wrap.scrollTop = Math.min(wrap.scrollTop, Math.max(0, h - wrap.clientHeight));
    }

    // ── Render ────────────────────────────────────────────────────────────

    function renderAll(s) {
        refreshColors();
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
                    const x = xS(item.xStart);
                    const w = Math.max(0, xS(item.xEnd) - x);
                    if (x > W || x + w < 0) continue;
                    const ck   = colourKey(e, colourBy);
                    blockData.push({
                        e, ck,
                        x,
                        w: Math.max(1, w),
                        colour: colourForKey(ck, colourBy),
                        bse:    sec.batch.batchStartEpochMs || 0,
                        stableKey: `${sec.batch.runId}:${e.id}`,
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
                if (s.hoveredName == null) return d.e.status === 'inprogress' ? 0.6 : 1;
                return d.e.name === s.hoveredName ? 1 : 0.12;
            })
            .style('stroke',       d => d3.color(d.colour)?.darker(.5) ?? d.colour)
            .style('stroke-width', d => d.e.name === s.hoveredName ? '1.5px' : '0.5px')
            .on('mouseenter', (ev, d) => onBlockEnter(s, ev, d))
            .on('mouseleave', (ev, d) => onBlockLeave(s, ev, d))
            .on('click',      (ev, d) => { if (ev.ctrlKey) copyBlockDetails(s, d); });
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
        const GAP  = 10;
        const name = sec.batch.batchName;

        // Background rect is sized/positioned after measuring actual rendered text
        // widths (getBBox) rather than estimated char widths, since a proportional
        // font otherwise lets the runId text overlap the tail of a wide name.
        const bg = sel.append('rect')
            .attr('y', 2).attr('height', HEADER_H - 4)
            .attr('rx', 3).style('fill', BG_COLOR).style('opacity', 0.88);
        const nameText = sel.append('text').attr('class', 'bm-tl-batch-name')
            .attr('x', tx).attr('y', cy).attr('dy', '0.32em').text(name);
        const nameW = nameText.node().getBBox().width;
        const runIdX = tx + nameW + GAP;
        const runIdText = sel.append('text').attr('class', 'bm-tl-batch-runid')
            .attr('x', runIdX).attr('y', cy).attr('dy', '0.32em')
            .text(sec.batch.runId);
        const runIdW = runIdText.node().getBBox().width;
        bg.attr('x', tx - 4).attr('width', (runIdX - tx) + runIdW + 8);
        nameText.raise();
        runIdText.raise();
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
                .text(fmtRelHMS(t));
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

        // When popup is hidden and hovering a block, show name above the time.
        const lines = [];
        if (!s.showTooltip && s.hoveredName) lines.push(s.hoveredName);
        const relL = fmtRelHMS(timeMs);
        const absL = bse ? ` (${fmtAbsHMS(bse + timeMs)})` : '';
        lines.push(relL + absL);

        const lh  = lines.length > 1 ? 11 : 15;
        const lw  = Math.max(...lines.map(l => l.length * 6.2 + 10));
        let   lx  = mx - lw / 2;
        lx = Math.max(2, Math.min((s.svgW || 800) - lw - 2, lx));

        let y = 2;
        for (const line of lines) {
            s.curBotL.append('rect').attr('x', lx).attr('y', y)
                .attr('width', lw).attr('height', lh).attr('rx', 2)
                .style('fill', CURSOR_COLOR);
            s.curBotL.append('text').attr('x', lx + lw / 2).attr('y', y + lh / 2)
                .attr('text-anchor', 'middle').attr('dy', '0.35em')
                .style('fill', '#fff').style('font-size', lines.length > 1 ? '0.55rem' : '0.6rem')
                .style('font-weight', '700')
                .style('font-family', 'var(--bm-font-mono,monospace)')
                .text(line);
            y += lh + 1;
        }
    }

    function onMouseLeave(s) {
        s.cursorX = null;
        s.cursorLine.style('opacity', 0);
        s.curBotL.selectAll('*').remove();
    }

    // ── Block hover ───────────────────────────────────────────────────────

    function onBlockEnter(s, event, d) {
        s.hoveredName  = d.e.name;
        s.hoveredBlockData = d;
        s.blockL.selectAll('rect.bm-tl-block')
            .style('fill-opacity', b => b.e.name === s.hoveredName ? 1 : 0.12)
            .style('stroke-width', b => b.e.name === s.hoveredName ? '1.5px' : '0.5px');
        if (s.showTooltip) showTooltip(s, event, d);
    }

    function onBlockLeave(s, event, d) {
        s.hoveredName   = null;
        s.hoveredBlockData = null;
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
                <span class="bm-tl-tt-id">${esc(e.name)}</span>
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
        `).style('opacity', 1).style('pointer-events', 'none');

        positionTooltip(s, event);
    }

    function positionTooltip(s, event) {
        const node = s.tooltip.node();
        const tw = node.offsetWidth  || 280;
        const th = node.offsetHeight || 230;

        const laneRect = s.laneEl.getBoundingClientRect();
        const wrap     = s.laneEl.parentElement;

        // Position in laneEl coordinate space (accounts for scroll automatically
        // because laneRect.top moves as the content scrolls).
        const mx = event.clientX - laneRect.left;
        const my = event.clientY - laneRect.top;

        // Visible vertical window inside the scroll container, minus the bottom panel.
        const scrollTop   = wrap ? wrap.scrollTop   : 0;
        const visBottom   = wrap
            ? scrollTop + wrap.clientHeight - BOTTOM_H - 6
            : my + 400;
        const visTop = scrollTop + 4;

        let x = mx + 14;
        let y = my - 8;

        // Flip right→left if tooltip would overflow the lane width.
        if (x + tw > laneRect.width - 4) x = mx - tw - 14;
        x = Math.max(4, x);

        // Flip down→up if tooltip would go under the range selector or off screen.
        if (y + th > visBottom) y = my - th - 12;
        // Don't let it go above the visible scroll top.
        if (y < visTop) y = visTop;

        s.tooltip.style('left', `${x}px`).style('top', `${y}px`);
    }

    function hideTooltip(s) {
        s.tooltip.style('opacity', 0).style('pointer-events', 'none');
    }

    function copyBlockDetails(s, d) {
        const e   = d.e;
        const bse = d.bse;
        const dur = e.finishMs != null ? Math.round(e.finishMs - e.startMs) : null;
        const durStr   = dur != null ? `${dur.toLocaleString()}ms` : '(in progress)';
        const stLabel  = e.status === 'done' ? 'Done' : e.status === 'inprogress' ? 'In Progress' : 'Error';
        const sRel     = fmtRelMs(e.startMs);
        const sAbs     = bse ? fmtAbsMs(bse + e.startMs) : '';
        const startStr = sAbs ? `${sRel} (${sAbs})` : sRel;
        const fRel     = e.finishMs != null ? fmtRelMs(e.finishMs) : `${fmtRelMs(e.startMs)}…`;
        const fAbs     = bse && e.finishMs != null ? fmtAbsMs(bse + e.finishMs) : '';
        const finStr   = fAbs ? `${fRel} (${fAbs})` : fRel;
        const lines    = [
            `Name:  ${e.name}`,
            `Status:   ${stLabel}`,
            `Source:   ${e.source}`,
            `Pipeline: ${e.pipeline}`,
            `Service:  ${e.service}`,
            `PID:      ${e.processId}`,
            `Server:   ${e.server}`,
            `Start:    ${startStr}`,
            `Finish:   ${finStr}`,
            `Duration: ${durStr}`,
        ];
        if (e.error) lines.push(`Error:    ${e.error}`);
        window.copyText(lines.join('\n'), 'Copied to clipboard');
    }

    // ── Keyboard ──────────────────────────────────────────────────────────

    function handleKey(s, e) {
        if (!s || s.disposed || !s.isVisible) return;
        const tag = document.activeElement?.tagName;
        if (tag === 'INPUT' || tag === 'TEXTAREA') return;
        const W   = s.svgW || 800;
        const t   = s.xZoom;

        // Ctrl+arrows: zoom (Left/Right) or bar-height (Up/Down).
        if (e.ctrlKey) {
            if (e.key === 'ArrowLeft' || e.key === 'ArrowRight') {
                e.preventDefault();
                const factor = e.key === 'ArrowLeft' ? 1.25 : 1 / 1.25;
                const mx = s.cursorX ?? 0;  // use cursor position if over lane, else left edge
                applyZoom(s, d3.zoomIdentity.translate(mx * (1 - factor) + t.x * factor, 0).scale(t.k * factor));
            } else if (e.key === 'ArrowUp' || e.key === 'ArrowDown') {
                e.preventDefault();
                const sh = s.subrowHOverride ?? SUBROW_H;
                s.subrowHOverride = Math.max(1, sh + (e.key === 'ArrowDown' ? 1 : -1));
                computeLayout(s);
                updateLaneSvgHeight(s);
                renderAll(s);
            }
            return;
        }

        // Plain arrows: pan. Up/Down fall through to browser (native vertical scroll).
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
                const dx  = e.shiftKey ? W * 0.5 : W * 0.1;
                const gMx = s.globalMax || s.frozenDomainMax || 1;
                const fMx = s.frozenDomainMax || 1;
                const rl  = -(t.k * (gMx / fMx) * W - W);
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
        const firstName = (s.data?.batches?.[0]?.batchName ?? 'export')
            .replace(/[^a-zA-Z0-9_-]/g, '_');
        const ts    = new Date().toISOString().slice(0, 19).replace(/[:T]/g, '-');
        const fname = `${firstName}_${ts}.csv`;

        const rows = ['RunId,BatchName,Name,Source,Pipeline,Service,PID,Server,Start,Finish,Error'];
        for (const b of (s.data.batches || [])) {
            const bse = b.batchStartEpochMs || 0;
            for (const e of (b.events || [])) {
                const startIso  = new Date(bse + e.startMs).toISOString();
                const finishIso = e.finishMs != null ? new Date(bse + e.finishMs).toISOString() : '';
                rows.push([
                    b.runId, b.batchName, e.name,
                    e.source, e.pipeline, e.service,
                    e.processId, e.server,
                    startIso, finishIso,
                    csv(e.error ?? ''),
                ].join(','));
            }
        }
        const content = rows.join('\r\n');
        if (window.showSaveFilePicker) {
            try {
                const fh = await window.showSaveFilePicker({ suggestedName: fname,
                    types: [{ description: 'CSV', accept: { 'text/csv': ['.csv'] } }] });
                const w = await fh.createWritable();
                await w.write(content); await w.close(); return;
            } catch (ex) {
                if (ex.name === 'AbortError') return; // user cancelled the picker
                // Anything else (most commonly NotAllowedError: the "user activation"
                // from the toolbar click doesn't reliably survive the Blazor Server
                // interop round trip) falls through to the plain download below —
                // logged so it's not a silent, unexplained behavior change.
                console.warn('[Timeline] showSaveFilePicker failed, falling back to direct download:', ex.name, ex.message);
            }
        } else {
            console.info('[Timeline] showSaveFilePicker not supported by this browser — using direct download.');
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
            case 'PidServicePipeline': return `${e.service} / ${e.pipeline} / ${e.server}:${e.processId}`;
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
    function esc(s) {
        return String(s ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
    }
    function csv(v) { return `"${String(v ?? '').replace(/"/g, '""')}"`; }

    function setVisible(key, visible) {
        const s = _instances.get(key);
        if (!s) return;
        s.isVisible = visible;
        if (visible) requestAnimationFrame(() => { if (!s.disposed) onResize(s); });
    }

    function setTooltipVisible(key, show) {
        const s = _instances.get(key);
        if (!s) return;
        s.showTooltip = show;
        if (!show) hideTooltip(s);
    }

export { init, update, resetView, exportCsv, dispose, setVisible, setTooltipVisible };
