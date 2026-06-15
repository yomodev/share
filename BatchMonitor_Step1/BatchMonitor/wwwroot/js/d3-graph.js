// Batch Monitor — D3 flow graph
//
// Renders a directed-acyclic flow graph of service topology using:
//  - dagre for hierarchical (left-to-right) layout
//  - D3 v7 for SVG rendering, zoom/pan, transitions
//  - d3-animation.js for live edge particle/pulse animation
//
// Public API (all attached to window.BatchMonitor.D3Graph):
//   init(containerEl)              -> handle
//   update(handle, topologyJson)   -> void   (debounced layout internally)
//   fitToView(handle)              -> void
//   dispose(handle)                -> void

window.BatchMonitor = window.BatchMonitor || {};
window.BatchMonitor.D3Graph = (function () {

    const NODE_WIDTH = 180;
    const NODE_HEIGHT = 64;
    const LAYOUT_DEBOUNCE_MS = 500; // spec: 400-600ms
    const TRANSITION_MS = 300;

    // ── Layout ───────────────────────────────────────────────────────────

    function runDagreLayout(topology) {
        const g = new dagre.graphlib.Graph();
        g.setGraph({ rankdir: 'LR', nodesep: 32, ranksep: 90, marginx: 24, marginy: 24 });
        g.setDefaultEdgeLabel(() => ({}));

        for (const node of topology.nodes) {
            g.setNode(node.id, { width: NODE_WIDTH, height: NODE_HEIGHT, data: node });
        }
        for (const edge of topology.edges) {
            // Skip edges referencing unknown nodes defensively.
            if (!topology.nodes.find(n => n.id === edge.source)) continue;
            if (!topology.nodes.find(n => n.id === edge.target)) continue;
            g.setEdge(edge.source, edge.target, { data: edge });
        }

        dagre.layout(g);
        return g;
    }

    // ── Path generation ──────────────────────────────────────────────────

    function edgePathD(points) {
        // Smooth curve through dagre's control points.
        const line = d3.line()
            .x(p => p.x)
            .y(p => p.y)
            .curve(d3.curveBasis);
        return line(points);
    }

    // ── Public handle factory ───────────────────────────────────────────

    function init(containerEl) {
        if (!containerEl) return null;

        const svg = d3.select(containerEl)
            .append('svg')
            .attr('class', 'bm-d3-svg')
            .attr('width', '100%')
            .attr('height', '100%');

        // Defs for arrow markers / gradients.
        const defs = svg.append('defs');
        defs.append('marker')
            .attr('id', 'bm-arrow')
            .attr('viewBox', '0 0 10 10')
            .attr('refX', 8)
            .attr('refY', 5)
            .attr('markerWidth', 7)
            .attr('markerHeight', 7)
            .attr('orient', 'auto-start-reverse')
            .append('path')
            .attr('d', 'M 0 0 L 10 5 L 0 10 z')
            .attr('class', 'bm-edge-arrow');

        const root = svg.append('g').attr('class', 'bm-d3-root');
        const edgeLayer = root.append('g').attr('class', 'bm-edge-layer');
        const particleLayer = root.append('g').attr('class', 'bm-particle-layer');
        const nodeLayer = root.append('g').attr('class', 'bm-node-layer');

        const zoom = d3.zoom()
            .scaleExtent([0.25, 2.5])
            .on('zoom', (event) => {
                root.attr('transform', event.transform);
                handle.userHasInteracted = true;
            });

        svg.call(zoom);

        const tooltip = d3.select(containerEl)
            .append('div')
            .attr('class', 'bm-graph-tooltip')
            .style('opacity', 0);

        const handle = {
            containerEl,
            svg,
            root,
            edgeLayer,
            particleLayer,
            nodeLayer,
            zoom,
            tooltip,
            animState: window.BatchMonitor.D3Animation.createState(),
            currentGraph: null,     // dagre graphlib graph from the last committed layout
            currentTopology: null,
            layoutTimer: null,
            rafId: null,
            lastFrameTime: null,
            userHasInteracted: false,
            disposed: false,
        };

        // Animation loop.
        const frame = (now) => {
            if (handle.disposed) return;
            if (handle.lastFrameTime == null) handle.lastFrameTime = now;
            const dt = now - handle.lastFrameTime;
            handle.lastFrameTime = now;

            renderAnimationFrame(handle, dt, now);

            handle.rafId = requestAnimationFrame(frame);
        };
        handle.rafId = requestAnimationFrame(frame);

        return handle;
    }

    // ── Update (debounced layout) ──────────────────────────────────────────

    function update(handle, topology) {
        if (!handle || handle.disposed) return;
        handle.pendingTopology = topology;

        if (handle.currentGraph === null) {
            // First render: commit immediately so the user isn't staring at
            // an empty canvas while the debounce timer runs.
            commitLayout(handle);
            return;
        }

        if (handle.layoutTimer) clearTimeout(handle.layoutTimer);
        handle.layoutTimer = setTimeout(() => commitLayout(handle), LAYOUT_DEBOUNCE_MS);
    }

    function commitLayout(handle) {
        if (handle.disposed) return;
        handle.layoutTimer = null;

        const topology = handle.pendingTopology;
        if (!topology) return;

        const g = runDagreLayout(topology);
        handle.currentGraph = g;
        handle.currentTopology = topology;

        renderGraph(handle, g, topology);

        // Auto fit-to-view on first render or whenever the user hasn't
        // manually panned/zoomed yet (graph "grows outward" per spec).
        if (!handle.userHasInteracted) {
            fitToView(handle, /* animate */ true);
        }
    }

    // ── Rendering ───────────────────────────────────────────────────────

    function renderGraph(handle, g, topology) {
        renderNodes(handle, g);
        renderEdges(handle, g, topology);
    }

    function renderNodes(handle, g) {
        const nodeIds = g.nodes();
        const nodeData = nodeIds.map(id => ({ id, ...g.node(id) }));

        const sel = handle.nodeLayer
            .selectAll('g.bm-node')
            .data(nodeData, d => d.id);

        // EXIT: fade + scale out then remove.
        sel.exit()
            .transition().duration(TRANSITION_MS).ease(d3.easeCubicOut)
            .attr('transform', d => `translate(${d.x},${d.y}) scale(0.6)`)
            .style('opacity', 0)
            .remove();

        // ENTER: fade + scale in at computed position.
        const enter = sel.enter()
            .append('g')
            .attr('class', 'bm-node')
            .attr('transform', d => `translate(${d.x},${d.y}) scale(0.6)`)
            .style('opacity', 0)
            .on('mouseenter', (event, d) => showTooltip(handle, event, d))
            .on('mousemove', (event, d) => moveTooltip(handle, event))
            .on('mouseleave', () => hideTooltip(handle));

        buildNodeContent(enter);

        enter.transition().duration(TRANSITION_MS).ease(d3.easeCubicOut)
            .attr('transform', d => `translate(${d.x},${d.y}) scale(1)`)
            .style('opacity', 1);

        // UPDATE: animate to new position; refresh dynamic visuals.
        const merged = enter.merge(sel);

        merged.transition().duration(TRANSITION_MS).ease(d3.easeCubicInOut)
            .attr('transform', d => `translate(${d.x},${d.y}) scale(1)`)
            .style('opacity', 1);

        updateNodeContent(merged);
    }

    function buildNodeContent(enter) {
        const rect = enter.append('rect')
            .attr('class', 'bm-node-rect')
            .attr('x', d => -d.width / 2)
            .attr('y', d => -d.height / 2)
            .attr('width', d => d.width)
            .attr('height', d => d.height)
            .attr('rx', 10);

        enter.append('text')
            .attr('class', 'bm-node-label')
            .attr('x', 0)
            .attr('y', -10)
            .attr('text-anchor', 'middle')
            .text(d => d.data.label);

        enter.append('text')
            .attr('class', 'bm-node-sub')
            .attr('x', 0)
            .attr('y', 10)
            .attr('text-anchor', 'middle');

        // Instance count badge, top-right corner.
        enter.append('g')
            .attr('class', 'bm-node-badge')
            .attr('transform', d => `translate(${d.width / 2 - 16}, ${-d.height / 2 + 14})`)
            .call(g => {
                g.append('circle').attr('r', 11);
                g.append('text').attr('text-anchor', 'middle').attr('dy', '0.32em');
            });
    }

    function updateNodeContent(sel) {
        sel.select('.bm-node-rect')
            .attr('class', d => {
                const t = d.data.recentThroughputScore || 0;
                let activity = 'idle';
                if (t > 0.66) activity = 'hot';
                else if (t > 0.2) activity = 'warm';
                return `bm-node-rect bm-activity-${activity}`;
            });

        sel.select('.bm-node-label').text(d => d.data.label);

        sel.select('.bm-node-sub')
            .text(d => formatNodeSubtext(d.data));

        sel.select('.bm-node-badge text').text(d => `×${d.data.instanceCount ?? 0}`);
        sel.select('.bm-node-badge')
            .style('display', d => (d.data.instanceCount ?? 0) > 0 ? null : 'none');
    }

    function formatNodeSubtext(node) {
        const processed = node.processedCount ?? 0;
        return `${processed.toLocaleString()} processed`;
    }

    function renderEdges(handle, g, topology) {
        const edgeData = g.edges().map(e => {
            const edge = g.edge(e);
            return {
                id: `${e.v}→${e.w}`,
                v: e.v,
                w: e.w,
                points: edge.points,
                data: edge.data,
            };
        });

        const sel = handle.edgeLayer
            .selectAll('g.bm-edge')
            .data(edgeData, d => d.id);

        sel.exit()
            .transition().duration(TRANSITION_MS).style('opacity', 0).remove();

        const enter = sel.enter()
            .append('g')
            .attr('class', 'bm-edge')
            .style('opacity', 0);

        enter.append('path')
            .attr('class', 'bm-edge-path')
            .attr('marker-end', 'url(#bm-arrow)')
            .attr('d', d => edgePathD(d.points));

        enter.append('text')
            .attr('class', 'bm-edge-label')
            .attr('text-anchor', 'middle');

        enter.transition().duration(TRANSITION_MS).style('opacity', 1);

        // UPDATE: redraw paths after node transitions have had a moment to
        // start (edges redraw slightly after nodes move, per spec).
        const merged = enter.merge(sel);

        merged.transition().delay(TRANSITION_MS * 0.5).duration(TRANSITION_MS)
            .style('opacity', 1)
            .select('.bm-edge-path')
            .attr('d', d => edgePathD(d.points));

        merged.select('.bm-edge-label')
            .attr('x', d => midpoint(d.points).x)
            .attr('y', d => midpoint(d.points).y - 6)
            .text(d => formatEdgeLabel(d.data));
    }

    function midpoint(points) {
        if (!points || points.length === 0) return { x: 0, y: 0 };
        const mid = points[Math.floor(points.length / 2)];
        return mid;
    }

    function formatEdgeLabel(edge) {
        if (!edge) return '';
        const count = edge.messageCount ?? 0;
        const pending = edge.pendingEstimate ?? 0;
        return pending > 0 ? `${count} · ~${pending} pending` : `${count}`;
    }

    // ── Animation frame (particles + pulse) ────────────────────────────

    function renderAnimationFrame(handle, dtMs, now) {
        if (!handle.currentGraph || !handle.currentTopology) return;

        const anim = window.BatchMonitor.D3Animation;
        const edgePaths = handle.edgeLayer.selectAll('g.bm-edge');

        const particleData = [];

        edgePaths.each(function (d) {
            const s = anim.sampleEdge(handle.animState, d.id, d.data?.messageCount ?? 0, now);
            const result = anim.tick(s, dtMs);
            const pathEl = this.querySelector('path.bm-edge-path');

            if (result.mode === 'pulse') {
                d3.select(this).select('.bm-edge-path')
                    .classed('bm-edge-pulse', true)
                    .style('--bm-pulse-intensity', result.pulse.intensity.toFixed(3));
            } else {
                d3.select(this).select('.bm-edge-path')
                    .classed('bm-edge-pulse', false)
                    .style('--bm-pulse-intensity', null);
            }

            for (const p of result.particles) {
                const pt = anim.pointOnPath(pathEl, p.progress);
                particleData.push({ id: `${d.id}:${p.progress.toFixed(4)}:${Math.random()}`, x: pt.x, y: pt.y });
            }
        });

        const sel = handle.particleLayer.selectAll('circle.bm-particle')
            .data(particleData, (d, i) => i);

        sel.exit().remove();

        sel.enter()
            .append('circle')
            .attr('class', 'bm-particle')
            .attr('r', anim.PARTICLE_RADIUS);

        handle.particleLayer.selectAll('circle.bm-particle')
            .attr('cx', d => d.x)
            .attr('cy', d => d.y);
    }

    // ── Tooltip ─────────────────────────────────────────────────────────

    function showTooltip(handle, event, d) {
        const node = d.data;
        const breakdown = node.instanceBreakdown || {};
        const rows = Object.entries(breakdown)
            .map(([pid, stats]) => {
                const s = Array.isArray(stats) ? stats : [stats.eventCount, stats.successCount, stats.failureCount];
                return `<div class="bm-tooltip-row"><span>PID ${pid}</span><span>${s[0]} events · ${s[1]} ok · ${s[2]} failed</span></div>`;
            })
            .join('');

        handle.tooltip
            .html(`
                <div class="bm-tooltip-title">${escapeHtml(node.label)}</div>
                <div class="bm-tooltip-row"><span>Processed</span><span>${(node.processedCount ?? 0).toLocaleString()}</span></div>
                <div class="bm-tooltip-row"><span>Success / Failed / Skipped</span><span>${node.successCount ?? 0} / ${node.failedCount ?? 0} / ${node.skippedCount ?? 0}</span></div>
                ${rows ? `<div class="bm-tooltip-divider"></div>${rows}` : ''}
            `)
            .style('opacity', 1);

        moveTooltip(handle, event);
    }

    function moveTooltip(handle, event) {
        const rect = handle.containerEl.getBoundingClientRect();
        const x = event.clientX - rect.left;
        const y = event.clientY - rect.top;
        handle.tooltip
            .style('left', `${x + 14}px`)
            .style('top', `${y + 14}px`);
    }

    function hideTooltip(handle) {
        handle.tooltip.style('opacity', 0);
    }

    function escapeHtml(s) {
        return String(s).replace(/[&<>"']/g, c => ({
            '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
        }[c]));
    }

    // ── Fit to view ─────────────────────────────────────────────────────

    function fitToView(handle, animate) {
        if (!handle || !handle.currentGraph) return;

        const g = handle.currentGraph;
        const graphInfo = g.graph();
        const w = graphInfo.width || 1;
        const h = graphInfo.height || 1;

        const containerRect = handle.containerEl.getBoundingClientRect();
        const cw = containerRect.width || 1;
        const ch = containerRect.height || 1;

        const padding = 40;
        const scale = Math.min(
            (cw - padding * 2) / w,
            (ch - padding * 2) / h,
            2.5
        );
        const clampedScale = Math.max(0.25, isFinite(scale) ? scale : 1);

        const tx = (cw - w * clampedScale) / 2;
        const ty = (ch - h * clampedScale) / 2;

        const transform = d3.zoomIdentity.translate(tx, ty).scale(clampedScale);

        if (animate) {
            handle.svg.transition().duration(TRANSITION_MS).call(handle.zoom.transform, transform);
        } else {
            handle.svg.call(handle.zoom.transform, transform);
        }
    }

    function resetUserInteraction(handle) {
        if (handle) handle.userHasInteracted = false;
    }

    // ── Dispose ─────────────────────────────────────────────────────────

    function dispose(handle) {
        if (!handle) return;
        handle.disposed = true;
        if (handle.rafId) cancelAnimationFrame(handle.rafId);
        if (handle.layoutTimer) clearTimeout(handle.layoutTimer);
        handle.svg.remove();
        handle.tooltip.remove();
    }

    return {
        init,
        update,
        fitToView,
        resetUserInteraction,
        dispose,
    };
})();

// ── Blazor interop layer ────────────────────────────────────────────────
// Keeps a registry of graph handles keyed by a string id supplied by the
// D3FlowGraph component, since IJSObjectReference round-trips are awkward
// for an object holding D3 selections / RAF state.
window.BatchMonitor.D3FlowGraphInterop = (function () {
    const handles = new Map();
    const G = window.BatchMonitor.D3Graph;

    function init(containerEl, key) {
        if (handles.has(key)) {
            G.dispose(handles.get(key));
        }
        handles.set(key, G.init(containerEl));
    }

    function update(key, topology) {
        const h = handles.get(key);
        if (h) G.update(h, topology);
    }

    function fitToView(key) {
        const h = handles.get(key);
        if (h) {
            G.resetUserInteraction(h);
            G.fitToView(h, true);
        }
    }

    function dispose(key) {
        const h = handles.get(key);
        if (h) {
            G.dispose(h);
            handles.delete(key);
        }
    }

    return { init, update, fitToView, dispose };
})();
