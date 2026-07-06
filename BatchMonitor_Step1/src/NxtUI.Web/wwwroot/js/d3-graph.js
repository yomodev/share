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
        const HEADER_PAD = 14 + 40 + 16; // left pad + instance badge + right pad
        let needed = measureText(handle, n.label, 'bm-node-label') + HEADER_PAD;

        for (const p of (n.pipelines || [])) {
            const nameW   = measureText(handle, p.displayName || p.name, 'bm-row-name');
            const countsW = measureText(handle, `✓${p.doneCount ?? 0} ⟳${p.inProgressCount ?? 0}`, 'bm-row-counts');
            needed = Math.max(needed, 10 /*left pad*/ + nameW + 16 /*gap*/ + countsW + 10 /*right pad*/);
        }

        return Math.max(NODE_WIDTH, Math.min(MAX_NODE_WIDTH, Math.ceil(needed)));
    }

    // ── Layout ───────────────────────────────────────────────────────────

    function chooseDir(containerEl) {
        const r = containerEl.getBoundingClientRect();
        return r.width >= r.height ? 'LR' : 'TB';
    }

    function runLayout(handle, topo) {
        const dir = chooseDir(handle.containerEl);
        const g = new dagre.graphlib.Graph({ multigraph: true });
        g.setGraph({
            rankdir: dir,
            // More breathing room than the old fixed spacing — wider node boxes
            // (dynamic width) and more separation both reduce edges cutting
            // through unrelated nodes.
            nodesep: dir === 'LR' ? 90  : 110,
            ranksep: dir === 'LR' ? 200 : 130,
            marginx: 48, marginy: 48,
        });
        g.setDefaultEdgeLabel(() => ({}));

        for (const n of topo.nodes) {
            // Cached on the node so updateClipPaths (called right after this) uses the
            // exact same width without re-measuring text a second time.
            n._layoutWidth = computeNodeWidth(handle, n);
            g.setNode(n.id, { width: n._layoutWidth, height: nh(n), data: n });
        }

        const ids = new Set(topo.nodes.map(n => n.id));
        for (const e of topo.edges) {
            if (!ids.has(e.source) || !ids.has(e.target)) continue;
            g.setEdge(e.source, e.target, { data: e }, `${e.sourcePipeline}::${e.targetPipeline}`);
        }
        dagre.layout(g);
        return g;
    }

    // ── Edge path: horizontal entry/exit, following dagre's own waypoints ────
    // dagre.layout() already routes multi-rank edges through intermediate points
    // that account for the nodes sitting between source and target. The previous
    // version discarded all but the first/last point and drew a single S-curve
    // between them, which could cut straight through unrelated node boxes on
    // edges spanning more than one rank. Following every waypoint (still via a
    // smooth curve, not sharp corners) keeps the routing dagre already computed.
    const edgeLine = d3.line().x(p => p.x).y(p => p.y).curve(d3.curveCatmullRom.alpha(0.5));

    function edgePath(pts) {
        if (!pts || pts.length < 2) return '';
        if (pts.length === 2) {
            const s = pts[0], t = pts[1];
            const dx = Math.max(Math.abs(t.x - s.x) * 0.45, 70);
            return `M ${s.x} ${s.y} C ${s.x + dx} ${s.y}, ${t.x - dx} ${t.y}, ${t.x} ${t.y}`;
        }
        return edgeLine(pts);
    }

    function snapPorts(pts, g, edge) {
        if (!pts || pts.length < 2) return pts;
        const sp = [...pts.map(p => ({ ...p }))];
        const sn = g.node(edge.source), tn = g.node(edge.target);
        if (sn) {
            const ri = pipelineIdx(sn.data, edge.sourcePipeline);
            sp[0] = { x: sn.x + sn.width / 2, y: sn.y + (ri >= 0 ? rowCY(sn.data, ri) : 0) };
        }
        if (tn) {
            const ri = pipelineIdx(tn.data, edge.targetPipeline);
            const last = sp.length - 1;
            sp[last] = { x: tn.x - tn.width / 2, y: tn.y + (ri >= 0 ? rowCY(tn.data, ri) : 0) };
        }
        return sp;
    }

    // ── init ─────────────────────────────────────────────────────────────

    function init(containerEl, dotNetRef) {
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
            animState: createState(),
            currentGraph: null, pendingTopology: null,
            layoutTimer: null, rafId: null, lastFrameTime: null,
            userZoomed: false, isVisible: true, disposed: false,
            popoverNodeId: null, popoverPipeline: null, popoverHideTimer: null,
        };

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

    function commitLayout(handle) {
        if (handle.disposed) return;
        const topo = handle.pendingTopology;
        if (!topo) return;

        // runLayout computes each node's dynamic width (and caches it on the node as
        // _layoutWidth) — clip paths are refreshed afterward so they reuse that same
        // width instead of re-measuring text a second time.
        const g = runLayout(handle, topo);
        handle.currentGraph = g;
        updateClipPaths(handle, topo);
        renderEdges(handle, g);  // edges first — drawn below nodes
        renderNodes(handle, g);
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

    function renderNodes(handle, g) {
        const data = g.nodes().map(id => ({ id, ...g.node(id) }));

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

        // Clip group — all row content clipped to card shape.
        enter.append('g').attr('class', 'bm-node-clip-group');

        // Header accent bar (left edge strip — inside clip group in updateNode).
        enter.append('rect').attr('class', 'bm-node-accent').attr('width', 4).attr('rx', 2);

        // Service label.
        enter.append('text').attr('class', 'bm-node-label').attr('dy', '0.32em');

        // Header divider.
        enter.append('line').attr('class', 'bm-node-hdr-div');

        // Instance badge.
        const badge = enter.append('g').attr('class', 'bm-node-badge');
        badge.append('circle').attr('r', 11);
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

            // Clip group reference.
            el.select('.bm-node-clip-group')
                .attr('clip-path', `url(#bm-clip-${d.id})`);

            el.select('.bm-node-bg')
                .attr('x', -hw).attr('y', -hh)
                .attr('width', d.width).attr('height', d.height)
                .attr('fill', bg);

            el.select('.bm-node-rect')
                .attr('x', -hw).attr('y', -hh)
                .attr('width', d.width).attr('height', d.height)
                .attr('class', `bm-node-rect bm-hdr-${st}`);

            // Accent bar — placed *after* bg so it's on top, but inside clip.
            el.select('.bm-node-accent')
                .attr('x', -hw).attr('y', -hh)
                .attr('height', HEADER_HEIGHT)
                .attr('fill', col);

            el.select('.bm-node-label')
                .attr('x', -hw + 14).attr('y', -hh + HEADER_HEIGHT / 2)
                .text(d.data.label);

            el.select('.bm-node-hdr-div')
                .attr('x1', -hw).attr('x2', hw)
                .attr('y1', -hh + HEADER_HEIGHT).attr('y2', -hh + HEADER_HEIGHT)
                .attr('stroke', div);

            const ic = d.data.instanceCount ?? 0;
            el.select('.bm-node-badge').style('display', ic > 0 ? null : 'none');
            el.select('.bm-node-badge circle')
                .attr('cx', hw - 34).attr('cy', -hh + HEADER_HEIGHT / 2);
            el.select('.bm-node-badge-text')
                .attr('x', hw - 34).attr('y', -hh + HEADER_HEIGHT / 2)
                .text(`×${ic}`);

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
        // Left state-colour bar (3 px wide, inset 1px from card edge so it
        // doesn't touch the border rect).
        enter.append('rect').attr('class', 'bm-row-bar').attr('width', 3);
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
        const pX = -hw + 10;
        const pW = nd.width - 10 - 68;   // track width; right gap for counts
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

            // Bar inset 1px from left edge; clip-path on parent handles overflow.
            row.select('.bm-row-bar')
                .attr('x', -hw + 1)
                .attr('y', top + 6)
                .attr('height', ROW_HEIGHT - 12)
                .attr('fill', col);

            row.select('.bm-row-name')
                .attr('x', -hw + 10).attr('y', r._cy - 9)
                .attr('class', 'bm-row-name');

            row.select('.bm-row-name').text(r.displayName || r.name);

            row.select('.bm-row-track')
                .attr('x', pX).attr('y', r._cy + 2)
                .attr('width', pW).attr('height', pH);

            row.select('.bm-row-fill')
                .attr('x', pX).attr('y', r._cy + 2).attr('height', pH)
                .attr('fill', col)
                .transition().duration(T)
                .attr('width', pW * Math.max(0, Math.min(1, r.progress ?? 0)));

            row.select('.bm-row-counts')
                .attr('x', hw - 6).attr('y', r._cy - 9)
                .attr('text-anchor', 'end')
                .text(`✓${r.doneCount ?? 0} ⟳${r.inProgressCount ?? 0}`);
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
        const n = g.node(nd.id);
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

    function renderEdges(handle, g) {
        const data = g.edges().map(e => {
            const ed  = g.edge(e);
            const pts = snapPorts(ed.points, g, ed.data);
            return { id: `${e.v}::${e.name}::${e.w}`, pts, data: ed.data };
        });

        const sel = handle.eLayer.selectAll('g.bm-edge').data(data, d => d.id);

        sel.exit().transition().duration(T).style('opacity', 0).remove();

        const enter = sel.enter().append('g').attr('class', 'bm-edge').style('opacity', 0);

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
            path.attr('d', edgePath(d.pts))
                .attr('stroke', col)
                .attr('stroke-width', w);

            const mid = d.pts[Math.floor(d.pts.length / 2)] || { x:0, y:0 };
            d3.select(this).select('.bm-edge-label')
                .attr('x', mid.x).attr('y', mid.y - 8)
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
        return w > 0 ? `✓${d} ~${w}` : `✓${d}`;
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

    function fitToView(handle, animate) {
        const g  = handle.currentGraph;
        if (!g) return;
        const gi = g.graph();
        const cw = handle.containerEl.clientWidth  || 1;
        const ch = handle.containerEl.clientHeight || 1;
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

/** @param {Element} el @param {string} key @param {object} dotNetRef */
export function init(el, key, dotNetRef) { if (_handles.has(key)) D3Graph.dispose(_handles.get(key)); _handles.set(key, D3Graph.init(el, dotNetRef)); }
/** @param {string} key @param {object} topo */
export function update(key, topo)        { const h = _handles.get(key); if (h) D3Graph.update(h, topo); }
/** @param {string} key */
export function fitToView(key)           { const h = _handles.get(key); if (h) { D3Graph.resetZoom(h); D3Graph.fitToView(h, true); } }
/** @param {string} key @param {boolean} visible */
export function setVisible(key, visible) { const h = _handles.get(key); if (h) D3Graph.setVisible(h, visible); }
/** @param {string} key */
export function dispose(key)             { const h = _handles.get(key); if (h) { D3Graph.dispose(h); _handles.delete(key); } }
