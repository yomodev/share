// Batch Monitor — D3 flow graph  (rev 4)
//
// Fixes in this revision:
//  - Arrow marker: orient="auto" + fill="context-stroke" with inline stroke
//    attribute so context-stroke resolves correctly; no more flipped arrows.
//  - Node hover: CSS transform removed (caused strobo/jump). Hover is now
//    a subtle border/glow brightening via CSS filter only — no positional change.
//  - Node background: read --mud-palette-surface at runtime so it works with
//    both dark and light MudBlazor themes.
//  - Row border bar: clipped to node interior via SVG clipPath, never overflows.
//  - Edges: paint-order ensures labels are readable on both themes.

import { createState, sampleEdge, tick } from './d3-animation.js';

const D3Graph = (() => {

    const NODE_WIDTH     = 234;   // floor width — nodes grow past this to fit their text
    const MAX_NODE_WIDTH = 460;   // safety cap so one long name can't blow up the whole layout
    const HEADER_HEIGHT  = 32;
    const ROW_HEIGHT     = 40;
    const MIN_ROWS       = 1;
    const DEBOUNCE_MS    = 500;
    const T              = 280;   // transition duration ms

    // Read page surface colour at runtime so light/dark themes both work.
    function nodeBg() {
        return getComputedStyle(document.documentElement)
            .getPropertyValue('--mud-palette-surface').trim() || '#1e1e2e';
    }

    function dividerColor() {
        return getComputedStyle(document.documentElement)
            .getPropertyValue('--mud-palette-divider').trim() || 'rgba(139,148,158,0.2)';
    }

    const STATE_COLOR = {
        errored:    '#F85149',
        active:     '#3FB950',
        inprogress: '#388BFD',
        idle:       '#8B949E',
        completed:  '#8B949E',
        notstarted: 'rgba(139,148,158,0.22)',
    };

    function sk(s) { return String(s || 'notstarted').toLowerCase(); }
    function sc(s) { return STATE_COLOR[sk(s)] || STATE_COLOR.notstarted; }

    // Counts are shown as "done / total" so the fraction visually matches the
    // progress bar. Row total = done + in-progress (all chunks seen on the row);
    // edge total = done + waiting (handed off + still in flight to the target).
    function rowCountsText(p) {
        const d = p.doneCount ?? 0, ip = p.inProgressCount ?? 0;
        return `${d} / ${d + ip}`;
    }

    // ── Geometry ─────────────────────────────────────────────────────────

    function nh(node) {
        return HEADER_HEIGHT + Math.max((node.pipelines || []).length, MIN_ROWS) * ROW_HEIGHT;
    }

    function rowCY(node, i) {
        return -nh(node) / 2 + HEADER_HEIGHT + i * ROW_HEIGHT + ROW_HEIGHT / 2;
    }

    function pipelineIdx(node, name) {
        return (node?.pipelines || []).findIndex(p => p.name === name);
    }

    // Measures rendered text width using a throwaway SVG <text> in defs (never painted,
    // but real font metrics — unlike guessing from character count). defs content isn't
    // rendered but is still laid out, so getComputedTextLength() is accurate here.
    function measureText(handle, text, className) {
        const t = handle.defs.append('text').attr('class', className).text(text || '');
        const w = t.node().getComputedTextLength();
        t.remove();
        return w;
    }

    // Node width grows to fit its longest text (header label, or a pipeline row's
    // name + done/in-progress counts) instead of a fixed width that clips long
    // service/pipeline names. Floored at NODE_WIDTH, capped at MAX_NODE_WIDTH so one
    // very long name can't blow out the whole layout.
    function computeNodeWidth(handle, n) {
        const HEADER_PAD = 14 + 46 + 16; // left pad + instance badge pill + right pad
        let needed = measureText(handle, n.label, 'bm-node-label') + HEADER_PAD;

        for (const p of (n.pipelines || [])) {
            const nameW   = measureText(handle, p.displayName || p.name, 'bm-row-name');
            const countsW = measureText(handle, rowCountsText(p), 'bm-row-counts');
            needed = Math.max(needed, 10 /*left pad*/ + nameW + 16 /*gap*/ + countsW + 10 /*right pad*/);
        }

        return Math.max(NODE_WIDTH, Math.min(MAX_NODE_WIDTH, Math.ceil(needed)));
    }

    // ── Layout (ELK: layered + orthogonal routing) ─────────────────────────────
    //
    // Direction adapts to the container's aspect ratio (wide container → flow left-
    // to-right/EAST-WEST ports; tall/narrow container → flow top-to-bottom/SOUTH-
    // NORTH ports) — same idea as the old dagre-based chooseDir(), which this ELK
    // port carried forward but had temporarily hardcoded to 'RIGHT' only. ELK routes
    // edges orthogonally in the channels *between* blocks — they never cross a node
    // interior.
    //
    // Port constraint mode is configurable (UiSettings.GraphPortConstraints, passed
    // in via init()'s portConstraints arg — see handle.portConstraints):
    //   FIXED_SIDE (default): ports are pinned to a side only — ELK can reorder them
    //     within that side to minimise crossings, but an arrow no longer lands on the
    //     exact pixel row it represents; see edgeTooltip/edgeLabel to tell them apart.
    //   FIXED_POS: ports are pinned to the exact row they represent — arrows always
    //     connect to the right row, but ELK can't reorder them, so dense diagrams can
    //     show more overlapping edges.

    function portId(nodeId, dir, pipeline) { return `${nodeId}::${dir}::${pipeline}`; }

    function chooseDir(containerEl) {
        const r = containerEl.getBoundingClientRect();
        return r.width >= r.height ? 'RIGHT' : 'DOWN';
    }

    // Port position along the node's EAST/WEST edge, aligned to the centre of the
    // owning pipeline row (used when flow direction is RIGHT, FIXED_POS only).
    function portYFromTop(node, pipeline) {
        const i = pipelineIdx(node, pipeline);
        return i >= 0
            ? HEADER_HEIGHT + i * ROW_HEIGHT + ROW_HEIGHT / 2
            : nh(node) / 2;
    }

    // Port position along the node's SOUTH/NORTH edge (used when flow direction is
    // DOWN, FIXED_POS only) — rows stack vertically inside the node regardless of
    // graph direction, so there's no natural per-row position on a horizontal edge;
    // spread evenly instead.
    function portXEvenSpread(node, pipeline, width) {
        const rows = node.pipelines || [];
        const i = pipelineIdx(node, pipeline);
        const n = Math.max(rows.length, 1);
        return i >= 0 ? (i + 1) * width / (n + 1) : width / 2;
    }

    async function runLayout(handle, topo) {
        const ids  = new Set(topo.nodes.map(n => n.id));
        const edges = topo.edges.filter(e => ids.has(e.source) && ids.has(e.target));

        const dir       = chooseDir(handle.containerEl);
        const outSide   = dir === 'RIGHT' ? 'EAST'  : 'SOUTH';
        const inSide    = dir === 'RIGHT' ? 'WEST'  : 'NORTH';
        const fixedPos  = handle.portConstraints === 'FIXED_POS';

        // Which pipeline rows need an output/input port.
        const outPorts = new Map(); // nodeId -> Set(pipeline)
        const inPorts  = new Map();
        const addPort = (map, id, pipe) => (map.get(id) ?? map.set(id, new Set()).get(id)).add(pipe);
        for (const e of edges) {
            addPort(outPorts, e.source, e.sourcePipeline);
            addPort(inPorts,  e.target, e.targetPipeline);
        }

        const nodeById = new Map(topo.nodes.map(n => [n.id, n]));

        const children = topo.nodes.map(n => {
            // Cached on the node so updateClipPaths reuses the width without re-measuring.
            n._layoutWidth = computeNodeWidth(handle, n);
            const w = n._layoutWidth, h = nh(n);

            const ports = [];
            for (const pipe of (outPorts.get(n.id) || [])) {
                const pos = fixedPos
                    ? (dir === 'RIGHT' ? { x: w, y: portYFromTop(n, pipe) } : { x: portXEvenSpread(n, pipe, w), y: h })
                    : {};
                ports.push({ id: portId(n.id, 'out', pipe), ...pos, layoutOptions: { 'elk.port.side': outSide } });
            }
            for (const pipe of (inPorts.get(n.id) || [])) {
                const pos = fixedPos
                    ? (dir === 'RIGHT' ? { x: 0, y: portYFromTop(n, pipe) } : { x: portXEvenSpread(n, pipe, w), y: 0 })
                    : {};
                ports.push({ id: portId(n.id, 'in', pipe), ...pos, layoutOptions: { 'elk.port.side': inSide } });
            }

            return { id: n.id, width: w, height: h, ports,
                     layoutOptions: { 'elk.portConstraints': fixedPos ? 'FIXED_POS' : 'FIXED_SIDE' } };
        });

        const elkEdges = edges.map((e, i) => ({
            id: `e${i}`,
            sources: [portId(e.source, 'out', e.sourcePipeline)],
            targets: [portId(e.target, 'in',  e.targetPipeline)],
        }));
        const edgeDataById = new Map(edges.map((e, i) => [`e${i}`, e]));

        // Nudges ELK's own compaction toward filling the container's actual shape
        // instead of defaulting to a fixed internal ratio — helps address layouts
        // that were sprawling wide/short regardless of the panel's real proportions.
        const cw = handle.containerEl.clientWidth  || 1;
        const ch = handle.containerEl.clientHeight || 1;

        const graph = {
            id: 'root',
            layoutOptions: {
                'elk.algorithm': 'layered',
                'elk.direction': dir,
                'elk.edgeRouting': 'ORTHOGONAL',
                'elk.aspectRatio': String(cw / ch),
                'elk.layered.spacing.nodeNodeBetweenLayers': '110',
                'elk.spacing.nodeNode': '56',
                // These two were the tightest settings relative to how many parallel
                // edges can share a channel between two fixed-position ports — bumped
                // up first (least invasive: doesn't change where any arrow connects)
                // before trading away exact per-row port alignment for FIXED_SIDE.
                'elk.spacing.edgeNode': '34',
                'elk.spacing.edgeEdge': '28',
                'elk.layered.spacing.edgeNodeBetweenLayers': '34',
                'elk.layered.crossingMinimization.strategy': 'LAYER_SWEEP',
                'elk.padding': '[top=48,left=48,bottom=48,right=48]',
            },
            children,
            edges: elkEdges,
        };

        const res = await handle.elk.layout(graph);

        // Normalise into our render structure. ELK coords have a top-left origin;
        // we store node CENTRE (renderNodes translates to centre, draws from -w/2)
        // and keep edge points in ELK-absolute space (which equals our render space).
        const nodes = new Map();
        for (const c of (res.children || [])) {
            nodes.set(c.id, {
                x: c.x + c.width / 2, y: c.y + c.height / 2,
                width: c.width, height: c.height,
                data: nodeById.get(c.id),
            });
        }

        const outEdges = (res.edges || []).map(re => {
            const s = re.sections?.[0];
            const pts = s ? [s.startPoint, ...(s.bendPoints || []), s.endPoint] : [];
            return { id: re.id, pts, data: edgeDataById.get(re.id) };
        });

        return { nodes, edges: outEdges, width: res.width || 0, height: res.height || 0 };
    }

    // Orthogonal polyline with rounded corners: line to a point `r` before each
    // bend, then a quadratic through the bend to `r` after it. Works for the axis-
    // aligned segments ELK emits (and degrades gracefully for any diagonal).
    function roundedOrthPath(pts, r = 9) {
        if (!pts || pts.length < 2) return '';
        if (pts.length === 2)
            return `M ${pts[0].x} ${pts[0].y} L ${pts[1].x} ${pts[1].y}`;

        let d = `M ${pts[0].x} ${pts[0].y}`;
        for (let i = 1; i < pts.length - 1; i++) {
            const p0 = pts[i - 1], p1 = pts[i], p2 = pts[i + 1];
            const d1 = Math.hypot(p1.x - p0.x, p1.y - p0.y) || 1;
            const d2 = Math.hypot(p2.x - p1.x, p2.y - p1.y) || 1;
            const rr = Math.min(r, d1 / 2, d2 / 2);
            const a = { x: p1.x - (p1.x - p0.x) / d1 * rr, y: p1.y - (p1.y - p0.y) / d1 * rr };
            const b = { x: p1.x + (p2.x - p1.x) / d2 * rr, y: p1.y + (p2.y - p1.y) / d2 * rr };
            d += ` L ${a.x} ${a.y} Q ${p1.x} ${p1.y} ${b.x} ${b.y}`;
        }
        const last = pts[pts.length - 1];
        return d + ` L ${last.x} ${last.y}`;
    }

    // Smooth spline through the same ELK waypoints (start/bends/end) instead of
    // straight orthogonal segments — looser, organic look; the alternative to
    // roundedOrthPath, picked by UiSettings.GraphEdgeStyle via handle.edgeStyle.
    // Applies regardless of port-constraint mode (FIXED_SIDE or FIXED_POS) — this is
    // purely how the path between ELK's computed points is drawn, not how those
    // points/ports were chosen.
    const curveLine = d3.line().x(p => p.x).y(p => p.y).curve(d3.curveCatmullRom.alpha(0.5));

    function curvedPath(pts) {
        if (!pts || pts.length < 2) return '';
        return curveLine(pts);
    }

    function edgePath(handle, pts) {
        return handle.edgeStyle === 'CURVED' ? curvedPath(pts) : roundedOrthPath(pts);
    }

    // A point a short distance back from the arrowhead (last point), offset above
    // the line, so the label reads clearly next to the arrow it belongs to.
    function edgeLabelPos(pts) {
        if (!pts || pts.length < 2) return { x: 0, y: 0 };
        const end  = pts[pts.length - 1];
        const prev = pts[pts.length - 2];
        const dx = end.x - prev.x, dy = end.y - prev.y;
        const len = Math.hypot(dx, dy) || 1;
        const back = Math.min(len * 0.5, 40);
        return { x: end.x - (dx / len) * back, y: end.y - (dy / len) * back - 7 };
    }

    // ── init ─────────────────────────────────────────────────────────────

    function init(containerEl, dotNetRef, portConstraints, edgeStyle) {
        if (!containerEl) return null;

        const svg = d3.select(containerEl).append('svg')
            .attr('class', 'bm-d3-svg')
            .attr('width', '100%').attr('height', '100%');

        const defs = svg.append('defs');

        // Arrow marker — orient="auto" (not auto-start-reverse) so it always
        // points toward the target. fill="context-stroke" inherits the path's
        // stroke colour; the stroke must be set as an SVG attribute (not just
        // CSS) for context-stroke to resolve in all browsers.
        defs.append('marker')
            .attr('id', 'bm-arrow')
            .attr('viewBox', '0 0 10 10')
            .attr('refX', 8).attr('refY', 5)
            .attr('markerWidth', 5).attr('markerHeight', 5)
            .attr('orient', 'auto')
            .attr('markerUnits', 'strokeWidth')
            .append('path')
            .attr('d', 'M 0 1 L 9 5 L 0 9 z')
            .attr('fill', 'context-stroke')
            .attr('stroke', 'none');

        const root  = svg.append('g').attr('class', 'bm-d3-root');
        const eLayer = root.append('g').attr('class', 'bm-edge-layer');
        const nLayer = root.append('g').attr('class', 'bm-node-layer');

        // Popover div.
        const popover = d3.select(containerEl).append('div')
            .attr('class', 'bm-graph-popover')
            .style('opacity', 0)
            .style('pointer-events', 'none');

        const zoom = d3.zoom()
            .scaleExtent([0.12, 3])
            .on('zoom', ev => { root.attr('transform', ev.transform); handle.userZoomed = true; });
        svg.call(zoom);
        svg.on('click', () => hidePopover(handle));

        const handle = {
            containerEl, svg, root, eLayer, nLayer, zoom, popover, defs, dotNetRef,
            portConstraints: portConstraints === 'FIXED_POS' ? 'FIXED_POS' : 'FIXED_SIDE',
            edgeStyle: edgeStyle === 'CURVED' ? 'CURVED' : 'ORTHOGONAL',
            animState: createState(),
            elk: (typeof ELK !== 'undefined') ? new ELK() : null,
            currentGraph: null, pendingTopology: null,
            layoutTimer: null, layoutSeq: 0, rafId: null, lastFrameTime: null,
            userZoomed: false, isVisible: true, disposed: false,
            popoverNodeId: null, popoverPipeline: null, popoverHideTimer: null,
        };
        if (!handle.elk) console.error('[D3Graph] ELK not loaded — flow graph cannot lay out.');

        const frame = now => {
            if (handle.disposed) return;
            if (handle.isVisible) {
                if (!handle.lastFrameTime) handle.lastFrameTime = now;
                renderAnimationFrame(handle, now - handle.lastFrameTime, now);
                handle.lastFrameTime = now;
            } else {
                handle.lastFrameTime = null;
            }
            handle.rafId = requestAnimationFrame(frame);
        };
        handle.rafId = requestAnimationFrame(frame);
        return handle;
    }

    // ── update / layout ───────────────────────────────────────────────────

    function update(handle, topo) {
        if (!handle || handle.disposed) return;
        handle.pendingTopology = topo;
        if (!handle.currentGraph) { commitLayout(handle); return; }
        clearTimeout(handle.layoutTimer);
        handle.layoutTimer = setTimeout(() => commitLayout(handle), DEBOUNCE_MS);
    }

    async function commitLayout(handle) {
        if (handle.disposed || !handle.elk) return;
        const topo = handle.pendingTopology;
        if (!topo) return;

        // ELK layout is async (runs in a worker). Guard against an older layout
        // resolving after a newer one — only the latest sequence wins.
        const seq = ++handle.layoutSeq;
        let g;
        try {
            // runLayout also caches each node's dynamic width as _layoutWidth — clip
            // paths reuse that below without re-measuring text a second time.
            g = await runLayout(handle, topo);
        } catch (err) {
            console.error('[D3Graph] ELK layout failed', err);
            return;
        }
        if (handle.disposed || seq !== handle.layoutSeq) return;

        handle.currentGraph = g;
        updateClipPaths(handle, topo);
        renderEdges(handle);  // edges first — drawn below nodes
        renderNodes(handle);
        if (!handle.userZoomed) fitToView(handle, true);
    }

    // ── SVG clipPaths — one per node, clips row content to card bounds ────

    function updateClipPaths(handle, topo) {
        const clips = handle.defs.selectAll('clipPath.bm-node-clip')
            .data(topo.nodes, n => n.id);

        clips.exit().remove();

        const enter = clips.enter().append('clipPath')
            .attr('class', 'bm-node-clip')
            .attr('id', n => `bm-clip-${n.id}`);
        enter.append('rect').attr('rx', 10);

        // Update dimensions for both enter + update.
        handle.defs.selectAll('clipPath.bm-node-clip').each(function(n) {
            const h = nh(n), w = n._layoutWidth || NODE_WIDTH;
            d3.select(this).select('rect')
                .attr('x', -w / 2).attr('y', -h / 2)
                .attr('width', w).attr('height', h);
        });
    }

    // ── Nodes ─────────────────────────────────────────────────────────────

    function renderNodes(handle) {
        const data = [...handle.currentGraph.nodes].map(([id, n]) => ({ id, ...n }));

        const sel = handle.nLayer.selectAll('g.bm-node').data(data, d => d.id);

        sel.exit().transition().duration(T)
            .style('opacity', 0)
            .attr('transform', d => `translate(${d.x},${d.y}) scale(0.75)`)
            .remove();

        const enter = sel.enter().append('g').attr('class', 'bm-node')
            .attr('transform', d => `translate(${d.x},${d.y}) scale(0.8)`)
            .style('opacity', 0);

        buildNode(enter, handle);

        enter.transition().duration(T).ease(d3.easeCubicOut)
            .attr('transform', d => `translate(${d.x},${d.y})`)
            .style('opacity', 1);

        const merged = enter.merge(sel);
        merged.transition().duration(T).ease(d3.easeCubicInOut)
            .attr('transform', d => `translate(${d.x},${d.y})`)
            .style('opacity', 1);

        updateNode(merged, handle);
    }

    function buildNode(enter, handle) {
        // Opaque background (theme-aware, set in updateNode).
        enter.append('rect').attr('class', 'bm-node-bg').attr('rx', 10);

        // Visible border.
        enter.append('rect').attr('class', 'bm-node-rect').attr('rx', 10)
            .attr('fill', 'none');

        // Header tint band (sized/positioned in updateNode).
        enter.append('rect').attr('class', 'bm-node-accent');

        // Service label.
        enter.append('text').attr('class', 'bm-node-label').attr('dy', '0.32em');

        // Header divider.
        enter.append('line').attr('class', 'bm-node-hdr-div');

        // Instance badge — rounded-rect pill (not a circle: circles clip 2-digit
        // counts too tightly to read).
        const badge = enter.append('g').attr('class', 'bm-node-badge');
        badge.append('rect').attr('class', 'bm-node-badge-bg');
        badge.append('text').attr('class', 'bm-node-badge-text')
            .attr('text-anchor', 'middle').attr('dy', '0.32em');

        // Active-state header pulse.
        enter.append('rect').attr('class', 'bm-node-pulse').attr('rx', 10)
            .style('opacity', 0).attr('fill', '#3FB950');

        // Pipeline rows container (clipped).
        enter.append('g').attr('class', 'bm-node-rows');
    }

    function updateNode(sel, handle) {
        const bg = nodeBg();
        const div = dividerColor();

        sel.each(function(d) {
            const el  = d3.select(this);
            const hw  = d.width / 2, hh = d.height / 2;
            const st  = sk(d.data.headerState);
            const col = sc(d.data.headerState);

            // Clip the pipeline-rows group to the card shape so long row names /
            // counts can never spill outside the rounded rectangle. (Previously the
            // clip-path was on an empty sibling group and the rows were unclipped.)
            el.select('.bm-node-rows')
                .attr('clip-path', `url(#bm-clip-${d.id})`);

            el.select('.bm-node-bg')
                .attr('x', -hw).attr('y', -hh)
                .attr('width', d.width).attr('height', d.height)
                .attr('fill', bg);

            el.select('.bm-node-rect')
                .attr('x', -hw).attr('y', -hh)
                .attr('width', d.width).attr('height', d.height)
                .attr('class', `bm-node-rect bm-hdr-${st}`);

            // Header tint — full-width band behind the title, same hue as the border
            // but semi-transparent (fill-opacity, not a separate alpha color) so it
            // reads as "this header belongs to that border colour" rather than a
            // solid block. clip-path (the same rounded-card clip as the rows below)
            // keeps its corners from poking out past the card's rounded top corners —
            // previously this was an unclipped 4px-wide bar with square corners.
            el.select('.bm-node-accent')
                .attr('x', -hw).attr('y', -hh)
                .attr('width', d.width).attr('height', HEADER_HEIGHT)
                .attr('fill', col).attr('fill-opacity', 0.14)
                .attr('clip-path', `url(#bm-clip-${d.id})`);

            el.select('.bm-node-label')
                .attr('x', -hw + 14).attr('y', -hh + HEADER_HEIGHT / 2)
                .text(d.data.label);

            el.select('.bm-node-hdr-div')
                .attr('x1', -hw).attr('x2', hw)
                .attr('y1', -hh + HEADER_HEIGHT).attr('y2', -hh + HEADER_HEIGHT)
                .attr('stroke', div);

            // Instance-count pill — rounded rect sized to its text (not a fixed-radius
            // circle, which clips 2-digit counts too tightly to read).
            const ic = d.data.instanceCount ?? 0;
            el.select('.bm-node-badge').style('display', ic > 0 ? null : 'none');
            const badgeText = `×${ic}`;
            const bh = 20;
            const bw = Math.max(30, measureText(handle, badgeText, 'bm-node-badge-text') + 16);
            const bx = hw - 10 - bw; // 10px pad from the card's right edge
            const by = -hh + HEADER_HEIGHT / 2 - bh / 2;
            el.select('.bm-node-badge-bg')
                .attr('x', bx).attr('y', by)
                .attr('width', bw).attr('height', bh)
                .attr('rx', bh / 2);
            el.select('.bm-node-badge-text')
                .attr('x', bx + bw / 2).attr('y', -hh + HEADER_HEIGHT / 2)
                .text(badgeText);

            el.select('.bm-node-pulse')
                .attr('x', -hw).attr('y', -hh)
                .attr('width', d.width).attr('height', HEADER_HEIGHT)
                .classed('bm-pulse-active', st === 'active');

            renderRows(el, d, handle, bg);
        });
    }

    // ── Pipeline rows ─────────────────────────────────────────────────────

    function renderRows(nodeEl, nd, handle, bg) {
        const pipelines = nd.data.pipelines || [];
        const rows = pipelines.map((p, i) => ({
            ...p, _hw: nd.width / 2, _i: i, _cy: rowCY(nd.data, i),
            _nodeId: nd.id, _nodeData: nd.data,
        }));

        // Rows live inside the clip group.
        const container = nodeEl.select('.bm-node-rows');

        const sel = container.selectAll('g.bm-row').data(rows, r => r.name);

        sel.exit().transition().duration(T).style('opacity', 0).remove();

        const enter = sel.enter().append('g').attr('class', 'bm-row')
            .style('opacity', 0)
            .style('cursor', 'pointer');

        // Hit area — full row width.
        enter.append('rect').attr('class', 'bm-row-hit').attr('fill', 'transparent');
        // Top divider line.
        enter.append('line').attr('class', 'bm-row-div');
        // Name text.
        enter.append('text').attr('class', 'bm-row-name');
        // Progress track + fill.
        enter.append('rect').attr('class', 'bm-row-track').attr('rx', 2);
        enter.append('rect').attr('class', 'bm-row-fill').attr('rx', 2);
        // Done / in-progress counts.
        enter.append('text').attr('class', 'bm-row-counts');

        enter
            .on('click', (event, r) => {
                event.stopPropagation();
                togglePopover(handle, nd, r);
            })
            .on('mouseenter', () => cancelHidePopover(handle))
            .on('mouseleave', () => scheduleHidePopover(handle));

        enter.transition().duration(T).style('opacity', 1);

        const merged = enter.merge(sel);
        merged.transition().duration(T).style('opacity', 1);

        const hw = nd.width / 2;
        const PAD = 10;
        const pX = -hw + PAD;
        const pW = nd.width - PAD * 2;   // full-width progress bar (both sides padded)
        const pH = 3;
        const div = dividerColor();

        merged.each(function(r) {
            const row  = d3.select(this);
            const top  = r._cy - ROW_HEIGHT / 2;
            const col  = sc(r.state);

            row.select('.bm-row-hit')
                .attr('x', -hw).attr('y', top)
                .attr('width', nd.width).attr('height', ROW_HEIGHT);

            row.select('.bm-row-div')
                .attr('x1', -hw).attr('x2', hw)
                .attr('y1', top).attr('y2', top)
                .attr('stroke', div).attr('stroke-width', 0.5);

            // Name (top-left) and counts (top-right) share the upper line; the
            // progress bar spans the full width beneath them (matches the mock).
            const textY = top + 16;
            const barY  = top + 27;

            row.select('.bm-row-name')
                .attr('x', -hw + PAD).attr('y', textY)
                .text(r.displayName || r.name);

            row.select('.bm-row-counts')
                .attr('x', hw - PAD).attr('y', textY)
                .attr('text-anchor', 'end')
                .text(rowCountsText(r));

            row.select('.bm-row-track')
                .attr('x', pX).attr('y', barY)
                .attr('width', pW).attr('height', pH);

            row.select('.bm-row-fill')
                .attr('x', pX).attr('y', barY).attr('height', pH)
                .attr('fill', col)
                .transition().duration(T)
                .attr('width', pW * Math.max(0, Math.min(1, r.progress ?? 0)));
        });
    }

    // ── Popover ───────────────────────────────────────────────────────────

    function togglePopover(handle, nodeDatum, pipeline) {
        if (handle.popoverNodeId === nodeDatum.id &&
            handle.popoverPipeline === pipeline.name) {
            hidePopover(handle);
        } else {
            showPopover(handle, nodeDatum, pipeline);
        }
    }

    function showPopover(handle, nodeDatum, pipeline) {
        cancelHidePopover(handle);
        handle.popoverNodeId   = nodeDatum.id;
        handle.popoverPipeline = pipeline.name;

        const instances = pipeline.instances || [];
        const topic     = pipeline.topic || '';
        const progress  = Math.round((pipeline.progress ?? 0) * 100);

        const instHtml = instances.length > 0
            ? instances.map(i =>
                `<div class="bm-pop-inst">
                    <span class="bm-pop-srv">${esc(i.server)}</span>
                    <span class="bm-pop-pid">PID&nbsp;${esc(i.processId)}</span>
                    <span class="bm-pop-cnt">✓${i.doneCount ?? 0}&nbsp;⟳${i.inProgressCount ?? 0}</span>
                </div>`).join('')
            : '<div class="bm-pop-empty">No instances seen yet</div>';

        const topicHtml = topic
            ? `<div class="bm-pop-topic">
                   <span class="bm-pop-topic-name" title="${esc(topic)}">Topic:&nbsp;<code>${esc(topic)}</code></span>
                   <button class="bm-pop-kafka">↗&nbsp;Kafka</button>
               </div>`
            : '';

        handle.popover.html(`
            <div class="bm-pop-hdr">
                <span class="bm-pop-title">${esc(pipeline.displayName || pipeline.name)}</span>
                <span class="bm-pop-badge bm-pop-${esc(sk(pipeline.state))}">${esc(sk(pipeline.state))}</span>
                <button class="bm-pop-x">×</button>
            </div>
            <div class="bm-pop-prog-row">
                <div class="bm-pop-track"><div class="bm-pop-fill" style="width:${progress}%;background:${sc(pipeline.state)}"></div></div>
                <span class="bm-pop-pct">${progress}%</span>
            </div>
            ${topicHtml}
            <div class="bm-pop-sep"></div>
            <div class="bm-pop-inst-hdr">Instances (${instances.length})</div>
            ${instHtml}
        `)
        .style('opacity', 1)
        .style('pointer-events', 'auto');

        handle.popover.select('.bm-pop-x').on('click', () => hidePopover(handle));
        handle.popover.select('.bm-pop-kafka').on('click', () => {
            if (handle.dotNetRef) handle.dotNetRef.invokeMethodAsync('RequestOpenKafkaTopic', topic);
        });
        handle.popover
            .on('mouseenter', () => cancelHidePopover(handle))
            .on('mouseleave', () => scheduleHidePopover(handle));

        positionPopover(handle, nodeDatum);
    }

    function positionPopover(handle, nd) {
        const g = handle.currentGraph;
        if (!g) return;
        const n = g.nodes.get(nd.id);
        if (!n) return;

        const t   = d3.zoomTransform(handle.svg.node());
        const sx  = t.x + n.x * t.k;
        const sy  = t.y + n.y * t.k;
        const nw  = n.width  * t.k;
        const nh2 = n.height * t.k;
        const cw  = handle.containerEl.clientWidth;
        const ch  = handle.containerEl.clientHeight;
        const pw  = 258, ph = 240;

        let left = sx + nw / 2 + 10;
        if (left + pw > cw - 8) left = sx - nw / 2 - pw - 10;
        left = Math.max(8, Math.min(left, cw - pw - 8));

        let top = sy - nh2 / 2;
        top = Math.max(8, Math.min(top, ch - ph - 8));

        handle.popover.style('left', `${left}px`).style('top', `${top}px`);
    }

    function scheduleHidePopover(h) {
        h.popoverHideTimer = setTimeout(() => hidePopover(h), 220);
    }
    function cancelHidePopover(h)  { clearTimeout(h.popoverHideTimer); }
    function hidePopover(h) {
        cancelHidePopover(h);
        h.popoverNodeId = h.popoverPipeline = null;
        h.popover.style('opacity', 0).style('pointer-events', 'none');
    }

    function esc(s) {
        return String(s ?? '').replace(/[&<>"']/g,
            c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
    }

    // ── Edges ─────────────────────────────────────────────────────────────

    function renderEdges(handle) {
        const data = handle.currentGraph.edges;

        const sel = handle.eLayer.selectAll('g.bm-edge').data(data, d => d.id);

        sel.exit().transition().duration(T).style('opacity', 0).remove();

        const enter = sel.enter().append('g').attr('class', 'bm-edge').style('opacity', 0);

        // Native tooltip on the whole edge (path + label) — hovering either shows
        // what the two numbers mean and which target pipeline the edge feeds.
        enter.append('title');

        // stroke must be set as SVG attribute (not CSS) for context-stroke to
        // work on the arrowhead marker.
        enter.append('path').attr('class', 'bm-edge-path')
            .attr('marker-end', 'url(#bm-arrow)')
            .attr('fill', 'none')
            .style('stroke-dasharray', '5 9');

        enter.append('text').attr('class', 'bm-edge-label').attr('text-anchor', 'middle');
        enter.transition().duration(T).style('opacity', 1);

        const merged = enter.merge(sel);

        merged.each(function(d) {
            const col = sc(d.data?.state);
            const w   = edgeW(d.data);
            const path = d3.select(this).select('.bm-edge-path');
            // Set stroke as attribute so context-stroke on the marker resolves.
            path.attr('d', edgePath(handle, d.pts))
                .attr('stroke', col)
                .attr('stroke-width', w);

            d3.select(this).select('title').text(edgeTooltip(d.data));

            // Place the label near the arrowhead (target end) rather than the
            // geometric middle — a multi-bend path's middle can sit far from the
            // arrow, making it ambiguous which edge the number belongs to.
            const lp = edgeLabelPos(d.pts);
            d3.select(this).select('.bm-edge-label')
                .attr('x', lp.x).attr('y', lp.y)
                .text(edgeLabel(d.data));
        });
    }

    function edgeW(e) {
        const n = e?.doneCount ?? 0;
        return Math.max(1, Math.min(5, 1 + Math.log10(n + 1) * 1.7));
    }

    function edgeLabel(e) {
        if (!e) return '';
        const d = e.doneCount ?? 0, w = e.waitingEstimate ?? 0;
        return `${d} / ${d + w}`;   // handed off / total (handed off + in flight)
    }

    function edgeTooltip(e) {
        if (!e) return '';
        const d = e.doneCount ?? 0, w = e.waitingEstimate ?? 0;
        const target = e.targetPipeline || e.target || '';
        return `${d} processed, ${w} in flight` + (target ? ` → ${target}` : '');
    }

    // ── Animation frame ────────────────────────────────────────────────────

    function renderAnimationFrame(handle, dt, now) {
        if (!handle.currentGraph) return;

        handle.eLayer.selectAll('g.bm-edge').each(function(d) {
            const s = sampleEdge(handle.animState, d.id, d.data?.doneCount ?? 0, now);
            const r = tick(s, dt);
            const col = sc(d.data?.state);
            const w   = edgeW(d.data) + r.thicknessBoost;

            d3.select(this).select('.bm-edge-path')
                .attr('stroke', col)
                .attr('stroke-width', w)
                .style('stroke-dasharray', r.dashArray)
                .style('stroke-dashoffset', r.dashOffset)
                .style('opacity', 0.70 + r.brighten * 0.30)
                .style('filter', r.brighten > 0.05
                    ? `drop-shadow(0 0 ${(r.brighten * 5).toFixed(1)}px ${col})`
                    : null);
        });

        // Header pulse for active-state nodes.
        const phase = 0.5 + 0.5 * Math.sin(now / 700);
        handle.nLayer.selectAll('.bm-node-pulse.bm-pulse-active')
            .style('opacity', 0.08 + phase * 0.14);
    }

    // ── Fit / visible / dispose ────────────────────────────────────────────

    function fitToView(handle, animate, retriesLeft = 6) {
        const g  = handle.currentGraph;
        if (!g) return;
        const cw0 = handle.containerEl.clientWidth;
        const ch0 = handle.containerEl.clientHeight;

        // The container can still be mid-layout (e.g. tab/flex sizing not settled yet)
        // the moment ELK's async layout resolves, especially on first load — measuring
        // it then produces a bogus (often tiny) transform that's never corrected
        // afterward, which reads as "the graph opens zoomed into a small corner of
        // itself". Retry a few frames instead of committing to a size that isn't real.
        if ((cw0 < 40 || ch0 < 40) && retriesLeft > 0) {
            requestAnimationFrame(() => fitToView(handle, animate, retriesLeft - 1));
            return;
        }

        const gi = { width: g.width, height: g.height };
        const cw = cw0 || 1;
        const ch = ch0 || 1;
        const pad = 52;
        const scale = Math.max(0.12, Math.min(3,
            Math.min((cw - pad * 2) / (gi.width  || 1),
                     (ch - pad * 2) / (gi.height || 1))));
        const tx = (cw - (gi.width  || 0) * scale) / 2;
        const ty = (ch - (gi.height || 0) * scale) / 2;
        const tr = d3.zoomIdentity.translate(tx, ty).scale(scale);
        if (animate) handle.svg.transition().duration(T).call(handle.zoom.transform, tr);
        else         handle.svg.call(handle.zoom.transform, tr);
    }

    function setVisible(handle, visible) {
        if (!handle) return;
        handle.isVisible = !!visible;
        handle.svg.style('display', visible ? null : 'none');
        if (visible && handle.currentGraph && !handle.userZoomed) fitToView(handle, false);
    }

    function resetZoom(h) { if (h) h.userZoomed = false; }

    function dispose(handle) {
        if (!handle) return;
        handle.disposed = true;
        cancelAnimationFrame(handle.rafId);
        clearTimeout(handle.layoutTimer);
        // Guard against elements already detached by Blazor's DOM patcher
        if (handle.svg?.node()?.parentNode)     handle.svg.remove();
        if (handle.popover?.node()?.parentNode) handle.popover.remove();
    }

    return { init, update, fitToView, resetZoom, setVisible, dispose };
})();

// ── Blazor interop exports ────────────────────────────────────────────────

const _handles = new Map();

/** @param {Element} el @param {string} key @param {object} dotNetRef @param {string} [portConstraints] "FIXED_SIDE" (default) or "FIXED_POS" @param {string} [edgeStyle] "ORTHOGONAL" (default) or "CURVED" */
export function init(el, key, dotNetRef, portConstraints, edgeStyle) { if (_handles.has(key)) D3Graph.dispose(_handles.get(key)); _handles.set(key, D3Graph.init(el, dotNetRef, portConstraints, edgeStyle)); }
/** @param {string} key @param {object} topo */
export function update(key, topo)        { const h = _handles.get(key); if (h) D3Graph.update(h, topo); }
/** @param {string} key */
export function fitToView(key)           { const h = _handles.get(key); if (h) { D3Graph.resetZoom(h); D3Graph.fitToView(h, true); } }
/** @param {string} key @param {boolean} visible */
export function setVisible(key, visible) { const h = _handles.get(key); if (h) D3Graph.setVisible(h, visible); }
/** @param {string} key */
export function dispose(key)             { const h = _handles.get(key); if (h) { D3Graph.dispose(h); _handles.delete(key); } }
