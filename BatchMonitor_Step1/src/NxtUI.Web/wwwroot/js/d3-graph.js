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
import { layout as flowLayout } from './bm-flow-layout/layout.js';

const D3Graph = (() => {

    const NODE_WIDTH     = 234;   // floor width — nodes grow past this to fit their text
    const MAX_NODE_WIDTH = 460;   // safety cap so one long name can't blow up the whole layout
    const HEADER_HEIGHT  = 32;
    const ROW_HEIGHT     = 46;   // was 40 — the name/counts text sat too close to the row's top divider
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

    // Two-hue palette: green = fully drained (nothing left in flight), blue = still
    // has work in flight. Each hue has a bright and a dim shade — bright means "the
    // state that earned this colour happened recently" (PipelineState.Active),
    // dim means "true, but it's been quiet for a while" (Completed / InProgress).
    // "Active" alone doesn't say which hue applies — DetermineState (see
    // TopologyComputationService) assigns Active purely by recency, so the caller
    // must also check whether the row/node/edge still has work in flight.
    const STATE_COLOR = {
        errored:     '#F85149',
        activeGreen: '#3FB950',
        activeBlue:  '#388BFD',
        completed:   'rgba(63,185,80,0.55)',
        inprogress:  'rgba(56,139,253,0.55)',
        idle:        '#8B949E',
        // notstarted intentionally omitted — stateColor() returns dividerColor() for it
        // instead, so it stays theme-aware (dark vs light) like the header's own style.
    };

    function sk(s) { return String(s || 'notstarted').toLowerCase(); }

    // stillWorking: true if the thing this state belongs to still has work in
    // flight (only matters when state is 'active' — every other state already
    // implies one hue unambiguously).
    function stateColor(s, stillWorking) {
        const st = sk(s);
        if (st === 'active') return stillWorking ? STATE_COLOR.activeBlue : STATE_COLOR.activeGreen;
        // "notstarted" mirrors the header's own theme-aware divider colour (see
        // .bm-hdr-notstarted in d3-graph.css) instead of a fixed dark-theme-tuned
        // translucent gray, which read as almost invisible on the light theme.
        if (st === 'notstarted') return dividerColor();
        return STATE_COLOR[st] || dividerColor();
    }

    // CSS class suffix — same disambiguation as stateColor(), for styles that
    // can't be set as inline SVG attributes (e.g. the header's stroke + glow).
    function stateClass(s, stillWorking) {
        const st = sk(s);
        if (st !== 'active') return st;
        return stillWorking ? 'active-blue' : 'active-green';
    }

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
    // Port constraint mode is configurable (RunsSettings.GraphPortConstraints, passed
    // in via init()'s portConstraints arg — see handle.portConstraints):
    //   FIXED_SIDE (default): ports are pinned to a side only — ELK can reorder them
    //     within that side to minimise crossings, but an arrow no longer lands on the
    //     exact pixel row it represents; see edgeTooltip/edgeLabel to tell them apart.
    //   FIXED_POS: ports are pinned to the exact row they represent — arrows always
    //     connect to the right row, but ELK can't reorder them, so dense diagrams can
    //     show more overlapping edges.

    function portId(nodeId, dir, pipeline) { return `${nodeId}::${dir}::${pipeline}`; }

    // Flow direction: an explicit topology-hint layout always wins; otherwise falls back to
    // handle.defaultDirection (RunsSettings.GraphDirection — 'RIGHT'/'DOWN' fixed, or 'AUTO'
    // for the aspect-ratio auto-pick this used to always do).
    function chooseDir(handle, layout) {
        const d = layout && layout.direction ? String(layout.direction).toLowerCase() : '';
        if (d === 'horizontal') return 'RIGHT';
        if (d === 'vertical')   return 'DOWN';
        if (handle.defaultDirection === 'AUTO') {
            const r = handle.containerEl.getBoundingClientRect();
            return r.width >= r.height ? 'RIGHT' : 'DOWN';
        }
        return handle.defaultDirection;
    }

    // Node/edge spacing multiplier from the hint's density.
    function densityScale(layout) {
        const d = layout && layout.density ? String(layout.density).toLowerCase() : '';
        return d === 'compact' ? 0.65 : d === 'airy' ? 1.6 : 1;
    }

    // "source"/"sink" role → ELK layer constraint (layered layout only).
    function layerConstraint(role) {
        const r = role ? String(role).toLowerCase() : '';
        return r === 'source' ? 'FIRST' : r === 'sink' ? 'LAST' : 'NONE';
    }

    // Same "source"/"sink" role convention, normalised for bm-flow-layout's role hard-pin
    // (see pinRoles() in bm-flow-layout/layout.js) — anything else is left undefined.
    function normalizeRole(role) {
        const r = role ? String(role).toLowerCase() : '';
        return r === 'source' || r === 'sink' ? r : undefined;
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

    // Stage 0 seam (docs/12_Custom_Layout_And_Nested_Runs.md): both engines are called with
    // the same (handle, topo) input and must return the same { nodes, edges, width, height }
    // shape (nodes: Map<id, {x,y,width,height,data}>, edges: [{id, pts, data}]). Selecting
    // handle.layoutEngine = 'custom' (default 'elk') is a comparison-only toggle for the
    // Stage 1 go/no-go evaluation — not exposed in any UI yet, and the custom path is Stage 1
    // scope only (no ports/pipeline-level edges, no groups, no hints beyond role/order).
    // `seedOrder` (Stage 3 stability) is only meaningful to the custom engine — ELK has no
    // such hook and re-solves from scratch regardless.
    async function runLayout(handle, topo, seedOrder) {
        return handle.layoutEngine === 'custom'
            ? runLayoutCustom(handle, topo, seedOrder)
            : runLayoutElk(handle, topo);
    }

    // Derives a Stage 3 seedOrder (docs/12 §5) from the PREVIOUS layout's own cross-axis
    // coordinates, biasing the next relayout toward minimal movement instead of a fresh
    // reshuffle when the graph's structure actually does change. Any consistent numeric
    // ordering works as a seed — the raw previous coordinate is the simplest one.
    function buildSeedOrder(handle) {
        if (!handle.currentGraph) return undefined;
        const dir = chooseDir(handle, handle.pendingTopology?.layout || null);
        const direction = dir === 'RIGHT' ? 'horizontal' : 'vertical';
        const seedOrder = new Map();
        for (const [id, p] of handle.currentGraph.nodes) {
            seedOrder.set(id, direction === 'horizontal' ? p.y : p.x);
        }
        return seedOrder;
    }

    // Refreshes each existing node/edge's `data` reference from the latest topology without
    // touching position — used when structuralKey() finds the graph's shape unchanged, so a
    // pure counts/state/colour update never reshuffles the layout (Stage 3 stability).
    function refreshDataInPlace(handle, topo) {
        const nodeById = new Map(topo.nodes.map(n => [n.id, n]));
        for (const [id, n] of handle.currentGraph.nodes) {
            if (nodeById.has(id)) n.data = nodeById.get(id);
        }
        const edgeByKey = new Map(topo.edges.map(e =>
            [`${e.source}>${e.target}>${e.sourcePipeline || ''}>${e.targetPipeline || ''}`, e]));
        for (const e of handle.currentGraph.edges) {
            const key = `${e.data?.source}>${e.data?.target}>${e.data?.sourcePipeline || ''}>${e.data?.targetPipeline || ''}`;
            if (edgeByKey.has(key)) e.data = edgeByKey.get(key);
        }
        handle.hasBlueprint = topo.hasBlueprint === true;
    }

    // Adapts bm-flow-layout's pure-geometry output into the same render contract as ELK.
    // Edges are service-to-service only here (no per-pipeline ports) — see docs/12 Stage 1.
    // Blueprint hints (role/group/order, decorated onto topo nodes by
    // TopologyComputationService.Decorate) are passed through to the engine's Stage 2
    // hard/soft constraints. `placement`/`placeSuccessor` are not yet in the C# hint schema
    // (docs/12 §6, not yet implemented) so they're always undefined for now — the engine
    // already supports them, they're just unreachable from the app until that lands.
    function runLayoutCustom(handle, topo, seedOrder) {
        const ids  = new Set(topo.nodes.map(n => n.id));
        const edges = topo.edges.filter(e => ids.has(e.source) && ids.has(e.target));
        const nodeById = new Map(topo.nodes.map(n => [n.id, n]));

        const layout = topo.layout || null;
        const dir = chooseDir(handle, layout);
        const direction = dir === 'RIGHT' ? 'horizontal' : 'vertical';

        const flowNodes = topo.nodes.map(n => {
            n._layoutWidth = computeNodeWidth(handle, n);
            return {
                id: n.id, width: n._layoutWidth, height: nh(n),
                role: normalizeRole(n.role), group: n.group || undefined, order: n.order,
            };
        });
        // De-dupe to one edge per (source,target) pair — the custom engine lays out at the
        // service level, not per-pipeline, in Stage 1.
        const seenPairs = new Set();
        const flowEdges = [];
        for (const e of edges) {
            const key = `${e.source}->${e.target}`;
            if (seenPairs.has(key)) continue;
            seenPairs.add(key);
            flowEdges.push({ id: key, source: e.source, target: e.target });
        }
        const edgeDataByKey = new Map(edges.map(e => [`${e.source}->${e.target}`, e]));

        const result = flowLayout({ nodes: flowNodes, edges: flowEdges, direction }, { seedOrder });

        const nodes = new Map();
        for (const [id, p] of result.nodes) {
            nodes.set(id, { x: p.x, y: p.y, width: p.width, height: p.height, data: nodeById.get(id) });
        }
        const outEdges = result.edges.map(e => ({
            id: e.id, pts: e.points, data: edgeDataByKey.get(e.id),
        }));

        return { nodes, edges: outEdges, width: result.width, height: result.height };
    }

    async function runLayoutElk(handle, topo) {
        const ids  = new Set(topo.nodes.map(n => n.id));
        const edges = topo.edges.filter(e => ids.has(e.source) && ids.has(e.target));

        const layout    = topo.layout || null;
        const dir       = chooseDir(handle, layout);
        const outSide   = dir === 'RIGHT' ? 'EAST'  : 'SOUTH';
        const inSide    = dir === 'RIGHT' ? 'WEST'  : 'NORTH';
        const fixedPos  = handle.portConstraints === 'FIXED_POS';
        const tree      = layout && String(layout.shape || '').toLowerCase() === 'tree';
        const dens      = densityScale(layout);

        // Which pipeline rows need an output/input port. Edges with NO pipeline (declared-
        // but-not-yet-observed blueprint edges, which connect two SERVICES rather than two
        // specific pipeline rows) deliberately get no port here — see elkEdges below, where
        // they attach straight to the node instead. Giving them a port keyed by "" would
        // create a phantom connection point distinct from every real pipeline row, which is
        // exactly what made a declared edge look like a stray duplicate arrow alongside the
        // real per-pipeline one between the same two services.
        const outPorts = new Map(); // nodeId -> Set(pipeline)
        const inPorts  = new Map();
        const addPort = (map, id, pipe) => (map.get(id) ?? map.set(id, new Set()).get(id)).add(pipe);
        for (const e of edges) {
            if (e.sourcePipeline) addPort(outPorts, e.source, e.sourcePipeline);
            if (e.targetPipeline) addPort(inPorts,  e.target, e.targetPipeline);
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

            const nodeOpts = { 'elk.portConstraints': fixedPos ? 'FIXED_POS' : 'FIXED_SIDE' };
            // Per-node hints from the run-type blueprint (layered layout only). role pins the
            // node to the first/last layer; order biases in-layer placement (lower = earlier).
            if (!tree) {
                const lc = layerConstraint(n.role);
                if (lc !== 'NONE') nodeOpts['elk.layered.layering.layerConstraint'] = lc;
                if (typeof n.order === 'number') nodeOpts['elk.priority'] = String(-n.order);
            }
            return { id: n.id, width: w, height: h, ports, layoutOptions: nodeOpts };
        });

        const elkEdges = edges.map((e, i) => ({
            id: `e${i}`,
            // No pipeline → attach to the node itself (ELK routes it to a sensible point on
            // the node's boundary automatically), not a per-pipeline port. See the port-
            // building comment above for why.
            sources: [e.sourcePipeline ? portId(e.source, 'out', e.sourcePipeline) : e.source],
            targets: [e.targetPipeline ? portId(e.target, 'in',  e.targetPipeline) : e.target],
        }));
        const edgeDataById = new Map(edges.map((e, i) => [`e${i}`, e]));

        // Nudges ELK's own compaction toward filling the container's actual shape
        // instead of defaulting to a fixed internal ratio — helps address layouts
        // that were sprawling wide/short regardless of the panel's real proportions.
        const cw = handle.containerEl.clientWidth  || 1;
        const ch = handle.containerEl.clientHeight || 1;

        // Blueprint layout hints: "tree" shape → ELK mrtree; density scales spacing;
        // straightenEdges → NETWORK_SIMPLEX node placement (straighter, less tidy packing).
        const px = (base) => String(Math.round(base * dens));
        const graph = {
            id: 'root',
            layoutOptions: {
                'elk.algorithm': tree ? 'mrtree' : 'layered',
                'elk.direction': dir,
                'elk.edgeRouting': 'ORTHOGONAL',
                'elk.aspectRatio': String(cw / ch),
                'elk.layered.spacing.nodeNodeBetweenLayers': px(110),
                'elk.spacing.nodeNode': px(56),
                // These two were the tightest settings relative to how many parallel
                // edges can share a channel between two fixed-position ports — bumped
                // up first (least invasive: doesn't change where any arrow connects)
                // before trading away exact per-row port alignment for FIXED_SIDE.
                'elk.spacing.edgeNode': px(34),
                'elk.spacing.edgeEdge': px(28),
                'elk.layered.spacing.edgeNodeBetweenLayers': px(34),
                'elk.layered.crossingMinimization.strategy': 'LAYER_SWEEP',
                'elk.layered.nodePlacement.strategy':
                    (layout && layout.straightenEdges) ? 'NETWORK_SIMPLEX' : 'BRANDES_KOEPF',
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
    // roundedOrthPath, picked by RunsSettings.GraphEdgeStyle via handle.edgeStyle.
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
        // Extra fixed nudge (left/up) on top of the directional back-offset — the label's
        // bigger font (see .bm-edge-label) made it sit too close to the arrow/edge line,
        // especially once its bounding box grows for multi-digit counts.
        return { x: end.x - (dx / len) * back - 6, y: end.y - (dy / len) * back - 11 };
    }

    // ── init ─────────────────────────────────────────────────────────────

    function init(containerEl, dotNetRef, portConstraints, edgeStyle, defaultDirection) {
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
        const gLayer = root.append('g').attr('class', 'bm-group-layer'); // behind edges + nodes
        const eLayer = root.append('g').attr('class', 'bm-edge-layer');
        const nLayer = root.append('g').attr('class', 'bm-node-layer');
        // Child-run blocks (docs/12 §7.4) render in their own layer, below the main service
        // flow — they're triggered at the RUN level, not by a specific service/pipeline, so
        // they're never wired into gLayer/eLayer/nLayer's edges.
        const crLayer = root.append('g').attr('class', 'bm-child-run-layer');

        // Popover div.
        const popover = d3.select(containerEl).append('div')
            .attr('class', 'bm-graph-popover')
            .style('opacity', 0)
            .style('pointer-events', 'none');

        const zoom = d3.zoom()
            .scaleExtent([0.12, 3])
            // ev.sourceEvent is the originating DOM event (wheel/mousedown/dblclick/touch)
            // for a real user gesture, and null for a programmatic .call(zoom.transform, ...)
            // — which is exactly how fitToView applies its computed transform. Without this
            // check, the very first auto-fit (right after the initial topology loads) marked
            // the graph as user-zoomed and permanently blocked every later auto-fit in
            // commitLayout, leaving the view stuck at whatever scale matched that first,
            // possibly-incomplete topology as more data streamed in.
            .on('zoom', ev => {
                root.attr('transform', ev.transform);
                if (ev.sourceEvent) handle.userZoomed = true;
            });
        svg.call(zoom);
        svg.on('click', () => hidePopover(handle));

        const handle = {
            containerEl, svg, root, gLayer, eLayer, nLayer, crLayer, zoom, popover, defs, dotNetRef,
            portConstraints: portConstraints === 'FIXED_POS' ? 'FIXED_POS' : 'FIXED_SIDE',
            edgeStyle: edgeStyle === 'CURVED' ? 'CURVED' : 'ORTHOGONAL',
            // RunsSettings.GraphDirection (Program.cs / D3FlowGraph.razor) — 'RIGHT'/'DOWN'
            // fixes the default; 'AUTO' keeps the old aspect-ratio auto-pick. A run-type
            // hint's own layout.direction (see chooseDir) always wins over this either way.
            defaultDirection: (defaultDirection === 'DOWN' || defaultDirection === 'AUTO') ? defaultDirection : 'RIGHT',
            animState: createState(),
            elk: (typeof ELK !== 'undefined') ? new ELK() : null,
            currentGraph: null, pendingTopology: null,
            layoutTimer: null, layoutSeq: 0, rafId: null, lastFrameTime: null, lastCommitTime: 0,
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

    // Throttle, NOT debounce: during continuous streams (live tailing, and especially
    // replay's ~400ms recompute cadence) update() is called faster than DEBOUNCE_MS apart.
    // A true debounce (clearTimeout + reset on every call) would keep pushing commitLayout
    // further into the future on every call and could starve indefinitely for as long as
    // updates keep arriving under the window — which is exactly what made replay look like
    // it only re-rendered once or twice over a 30s sweep. A throttle instead runs at a fixed
    // cadence: at most one commitLayout every DEBOUNCE_MS, always using whatever the latest
    // pendingTopology is by the time it fires.
    function update(handle, topo) {
        if (!handle || handle.disposed) return;
        handle.pendingTopology = topo;
        if (!handle.currentGraph) { commitLayout(handle); return; }
        if (handle.layoutTimer) return; // a commit is already scheduled — it'll pick up this topology
        const elapsed = performance.now() - handle.lastCommitTime;
        const wait = Math.max(0, DEBOUNCE_MS - elapsed);
        handle.layoutTimer = setTimeout(() => {
            handle.layoutTimer = null;
            commitLayout(handle);
        }, wait);
    }

    // Stage 3 stability (docs/12 §5): a pure data update (counts/state/colour changed, but
    // no node/edge/group actually added or removed) should never reshuffle positions — ELK
    // re-solves from scratch every commit and WILL reshuffle even then, which is exactly the
    // instability the custom engine exists to fix. Comparing a cheap structural signature
    // lets us skip the whole relayout (and the ELK worker round-trip) on the common case of
    // "same graph shape, new numbers," refreshing only each node/edge's `data` reference.
    function structuralKey(topo) {
        const nodeIds = topo.nodes.map(n => n.id).sort();
        const edgeKeys = topo.edges.map(e => `${e.source}>${e.target}>${e.sourcePipeline || ''}>${e.targetPipeline || ''}`).sort();
        const groups = topo.nodes.map(n => `${n.id}:${n.group || ''}`).sort();
        return nodeIds.join(',') + '|' + edgeKeys.join(',') + '|' + groups.join(',');
    }

    async function commitLayout(handle) {
        if (handle.disposed || !handle.elk) return;
        const topo = handle.pendingTopology;
        if (!topo) return;
        handle.lastCommitTime = performance.now();

        const newKey = structuralKey(topo);
        if (handle.currentGraph && newKey === handle.lastStructuralKey) {
            refreshDataInPlace(handle, topo);
            renderGroups(handle);
            renderEdges(handle);
            renderNodes(handle);
            // childRuns isn't part of structuralKey (a new/changed child shouldn't reshuffle
            // the whole service graph), so it's always re-rendered fresh from the latest
            // topology regardless of which branch this commit took. Known gap: if a child
            // appears while the main graph's structure is otherwise unchanged, fitToView
            // (below, main-layout branch only) won't auto-adjust to include the taller view —
            // a manual re-fit (the toolbar button) picks it up.
            renderChildRuns(handle);
            return;
        }

        // ELK layout is async (runs in a worker). Guard against an older layout
        // resolving after a newer one — only the latest sequence wins.
        const seq = ++handle.layoutSeq;
        let g;
        try {
            // runLayout also caches each node's dynamic width as _layoutWidth — clip
            // paths reuse that below without re-measuring text a second time.
            g = await runLayout(handle, topo, buildSeedOrder(handle));
        } catch (err) {
            console.error('[D3Graph] ELK layout failed', err);
            return;
        }
        if (handle.disposed || seq !== handle.layoutSeq) return;

        handle.lastStructuralKey = newKey;
        handle.currentGraph = g;
        handle.hasBlueprint = topo.hasBlueprint === true;
        updateClipPaths(handle, topo);
        renderGroups(handle);  // group bands beneath everything
        renderEdges(handle);   // edges below nodes
        renderNodes(handle);
        renderChildRuns(handle);
        if (!handle.userZoomed) fitToView(handle, true);
    }

    // ── Child-run blocks (docs/12 §7.4) ─────────────────────────────────────
    // Collapsed blocks are a fixed-size status+description card. An expanded one (its id
    // present in topo.expandedChildren — RunDetail.razor's in-place-expand state) is instead
    // a bigger box containing a lightweight mini sub-flow of that child's own services (laid
    // out with bm-flow-layout regardless of which engine — ELK or custom — draws the OUTER
    // graph; see docs/12 §7.4's "always bm-flow-layout for the recursive sub-layout" note)
    // plus, recursively, that child's OWN child-run row if it has one. Rendered as a raw
    // rebuild each commit (no D3 key-based enter/update/exit) rather than an animated join —
    // expand/collapse already changes every box's size, so there's little to gain from
    // diffing, and it keeps the recursion far simpler.
    const CHILD_RUN_WIDTH = 200, CHILD_RUN_HEIGHT = 60, CHILD_RUN_GAP = 16, CHILD_RUN_MARGIN = 48;
    const EXPANDED_HEADER_H = 26, EXPANDED_PAD = 12, EXPANDED_ROW_GAP = 10;
    const MINI_NODE_W = 92, MINI_NODE_H = 30;

    function childRunStatusColor(status) {
        switch (String(status || '').toLowerCase()) {
            case 'running':   return STATE_COLOR.activeBlue;
            case 'completed': return STATE_COLOR.activeGreen;
            case 'failed':
            case 'terminated': return STATE_COLOR.errored;
            default:          return dividerColor(); // Unknown/Purged/NotStarted
        }
    }

    function minBoundsOf(nodesMap) {
        let minX = Infinity, minY = Infinity;
        for (const [, n] of nodesMap) {
            minX = Math.min(minX, n.x - n.width / 2);
            minY = Math.min(minY, n.y - n.height / 2);
        }
        if (!Number.isFinite(minX)) { minX = minY = 0; }
        return { minX, minY };
    }

    // Builds a plain-data description of one child-run box — collapsed size, or (if
    // expandedTopo is given) the mini sub-layout + recursively-built nested child blocks and
    // the resulting bigger box size. Pure/no DOM — renderChildRunBlock draws whatever this
    // returns. Kept separate so the size (needed to LAY OUT siblings before any of them are
    // drawn) is known without touching the DOM first.
    function buildChildRunBlock(entry, expandedTopo) {
        if (!expandedTopo) {
            return { entry, expanded: false, width: CHILD_RUN_WIDTH, height: CHILD_RUN_HEIGHT };
        }

        const ids = new Set(expandedTopo.nodes.map(n => n.id));
        const miniNodes = expandedTopo.nodes.map(n => ({ id: n.id, width: MINI_NODE_W, height: MINI_NODE_H, role: normalizeRole(n.role) }));
        const seenPairs = new Set();
        const miniEdges = [];
        for (const e of (expandedTopo.edges || [])) {
            if (!ids.has(e.source) || !ids.has(e.target)) continue;
            const key = `${e.source}->${e.target}`;
            if (seenPairs.has(key)) continue;
            seenPairs.add(key);
            miniEdges.push({ id: key, source: e.source, target: e.target });
        }
        const subResult = flowLayout({ nodes: miniNodes, edges: miniEdges, direction: 'horizontal', layerSpacing: 40, nodeSpacing: 10 });

        const childBlocks = (expandedTopo.childRuns || []).map(childEntry =>
            buildChildRunBlock(childEntry, expandedTopo.expandedChildren?.[childEntry.runId]));
        const childRowHeight = childBlocks.length ? Math.max(...childBlocks.map(b => b.height)) : 0;
        const childRowWidth = childBlocks.reduce((sum, b) => sum + b.width, 0) + Math.max(0, childBlocks.length - 1) * EXPANDED_ROW_GAP;

        const contentWidth = Math.max(subResult.width, childRowWidth, MINI_NODE_W + EXPANDED_PAD * 2);
        const contentHeight = subResult.height + (childBlocks.length ? EXPANDED_ROW_GAP + childRowHeight : 0);

        return {
            entry, expanded: true, subResult, childBlocks,
            width: contentWidth + EXPANDED_PAD * 2,
            height: EXPANDED_HEADER_H + contentHeight + EXPANDED_PAD * 2,
        };
    }

    // Renders one block (collapsed or expanded, recursing into childBlocks) into parentSel,
    // centered at (cx, cy) in parentSel's own local coordinate space.
    function renderChildRunBlock(handle, parentSel, block, cx, cy) {
        const { entry, width: w, height: h } = block;
        const g = parentSel.append('g')
            .attr('class', `bm-child-run${block.expanded ? ' bm-child-run-expanded' : ''}`)
            .attr('transform', `translate(${cx},${cy})`);

        g.append('rect').attr('class', 'bm-child-run-bg').attr('rx', 10)
            .attr('x', -w / 2).attr('y', -h / 2).attr('width', w).attr('height', h)
            .attr('fill', nodeBg())
            .attr('stroke', childRunStatusColor(entry.status));

        const openThis = ev => {
            ev.stopPropagation(); // don't let svg's own click (hidePopover) swallow this
            handle.dotNetRef?.invokeMethodAsync('RequestOpenChildRun', entry.runId);
        };

        if (!block.expanded) {
            g.append('text').attr('class', 'bm-child-run-desc')
                .attr('text-anchor', 'middle').attr('x', 0).attr('y', -6)
                .text(truncateLabel(entry.description || entry.runId, 22));
            g.append('text').attr('class', 'bm-child-run-status')
                .attr('text-anchor', 'middle').attr('x', 0).attr('y', 14)
                .attr('fill', childRunStatusColor(entry.status))
                .text(entry.status ?? 'Unknown');
            g.on('click', openThis);
            return;
        }

        // Expanded: header bar (click collapses it back — same RequestOpenChildRun toggle)
        // + mini sub-flow + this child's own nested child-run row, if it has one.
        g.append('rect').attr('class', 'bm-child-run-header')
            .attr('x', -w / 2).attr('y', -h / 2).attr('width', w).attr('height', EXPANDED_HEADER_H)
            .style('fill', childRunStatusColor(entry.status)).style('fill-opacity', 0.18)
            .on('click', openThis);
        g.append('text').attr('class', 'bm-child-run-header-label')
            .attr('x', -w / 2 + 10).attr('y', -h / 2 + EXPANDED_HEADER_H / 2)
            .text(`${truncateLabel(entry.description || entry.runId, 26)} — ${entry.status ?? 'Unknown'}`)
            .on('click', openThis);

        const { minX: subMinX, minY: subMinY } = minBoundsOf(block.subResult.nodes);
        const subTop = -h / 2 + EXPANDED_HEADER_H + EXPANDED_PAD;
        const offX = (-w / 2 + EXPANDED_PAD) - subMinX;
        const offY = subTop - subMinY;

        for (const e of block.subResult.edges) {
            const pts = e.points.map(p => ({ x: p.x + offX, y: p.y + offY }));
            g.append('path').attr('class', 'bm-child-mini-edge')
                .attr('d', roundedOrthPath(pts, 5));
        }
        for (const [id, n] of block.subResult.nodes) {
            const mini = g.append('g').attr('class', 'bm-child-mini-node')
                .attr('transform', `translate(${n.x + offX},${n.y + offY})`);
            mini.append('rect').attr('class', 'bm-child-mini-node-bg').attr('rx', 4)
                .attr('x', -n.width / 2).attr('y', -n.height / 2).attr('width', n.width).attr('height', n.height);
            mini.append('text').attr('class', 'bm-child-mini-node-label')
                .attr('text-anchor', 'middle').attr('x', 0).attr('y', 4)
                .text(truncateLabel(id, 13));
        }

        if (block.childBlocks.length) {
            const rowTop = subTop + block.subResult.height + EXPANDED_ROW_GAP;
            let childCursorX = -w / 2 + EXPANDED_PAD;
            for (const childBlock of block.childBlocks) {
                renderChildRunBlock(handle, g, childBlock, childCursorX + childBlock.width / 2, rowTop + childBlock.height / 2);
                childCursorX += childBlock.width + EXPANDED_ROW_GAP;
            }
        }
    }

    function renderChildRuns(handle) {
        const childRuns = handle.pendingTopology?.childRuns || [];
        const expandedChildren = handle.pendingTopology?.expandedChildren || {};
        const g = handle.currentGraph;

        handle.crLayer.selectAll('*').remove();
        if (!g || childRuns.length === 0) return;

        const blocks = childRuns.map(entry => buildChildRunBlock(entry, expandedChildren[entry.runId]));
        const totalWidth = blocks.reduce((sum, b) => sum + b.width, 0) + CHILD_RUN_GAP * Math.max(0, blocks.length - 1);
        const baseY = g.height + CHILD_RUN_MARGIN;
        let cursorX = (g.width - totalWidth) / 2;

        for (const block of blocks) {
            renderChildRunBlock(handle, handle.crLayer, block, cursorX + block.width / 2, baseY + block.height / 2);
            cursorX += block.width + CHILD_RUN_GAP;
        }
    }

    function truncateLabel(text, maxLen) {
        return text.length > maxLen ? text.slice(0, maxLen - 1) + '…' : text;
    }

    // ── Group bands — a subtle labelled backdrop behind same-`group` nodes ────
    // v1: a bounding box around the laid-out members (ELK isn't asked to cluster them, so
    // members can be non-adjacent; the band still communicates the grouping visually).
    // Groups: ANY shared `group` tag always gets the default cosmetic band (soft, theme-
    // coloured, no configuration needed — unchanged from before). A group also listed in
    // the run-type hint's `groups` block (topo.groupColors, keyed by name) is upgraded to a
    // real bordered box in that colour instead — always bordered, no separate on/off flag;
    // declaring a colour IS what turns the box on. Border is the same colour at a much
    // higher opacity than the fill, so it reads as "this box's own colour," not a generic
    // outline. See docs/12_Custom_Layout_And_Nested_Runs.md §6.
    function renderGroups(handle) {
        const PAD = 14, LABEL_H = 16;
        const groupColors = handle.pendingTopology?.groupColors || {};
        const byGroup = new Map();
        for (const [id, n] of handle.currentGraph.nodes) {
            const grp = n.data && n.data.group;
            if (!grp) continue;
            const hw = (n.width || 0) / 2, hh = (n.height || 0) / 2;
            const box = byGroup.get(grp) || { x0: Infinity, y0: Infinity, x1: -Infinity, y1: -Infinity };
            box.x0 = Math.min(box.x0, n.x - hw); box.y0 = Math.min(box.y0, n.y - hh);
            box.x1 = Math.max(box.x1, n.x + hw); box.y1 = Math.max(box.y1, n.y + hh);
            byGroup.set(grp, box);
        }

        const data = [...byGroup.entries()].map(([name, b]) => ({ name, b, color: groupColors[name] || null }));
        const sel = handle.gLayer.selectAll('g.bm-group').data(data, d => d.name);
        sel.exit().remove();
        const enter = sel.enter().append('g').attr('class', 'bm-group');
        enter.append('rect').attr('class', 'bm-group-rect').attr('rx', 12);
        enter.append('text').attr('class', 'bm-group-label');

        const merged = enter.merge(sel);
        merged.classed('bm-group-colored', d => !!d.color);
        merged.select('.bm-group-rect')
            .attr('x', d => d.b.x0 - PAD).attr('y', d => d.b.y0 - PAD - LABEL_H)
            .attr('width', d => (d.b.x1 - d.b.x0) + PAD * 2)
            .attr('height', d => (d.b.y1 - d.b.y0) + PAD * 2 + LABEL_H)
            // .style (not .attr) — a CSS class rule beats an SVG presentation attribute, so
            // .attr('fill', ...) here would be silently overridden by .bm-group-rect's own
            // CSS; inline style wins over a class rule without !important.
            .style('fill', d => d.color || null)
            .style('fill-opacity', d => d.color ? 0.12 : null)
            .style('stroke', d => d.color || null)
            .style('stroke-opacity', d => d.color ? 0.85 : null)
            .style('stroke-dasharray', d => d.color ? 'none' : null);
        merged.select('.bm-group-label')
            .attr('x', d => d.b.x0 - PAD + 18).attr('y', d => d.b.y0 - PAD - LABEL_H / 2 + 1)
            .style('fill', d => d.color || null)
            .text(d => d.name);
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

    // ELK occasionally returns a child with no (or non-finite) x/y — seen for isolated
    // nodes (no edges, e.g. a declared-but-never-observed skeleton service) under some
    // layout/algorithm combinations. translate(NaN, NaN) breaks the whole <g>'s rendering
    // and spams the console; fall back to the graph origin and warn once per node/render
    // so a real occurrence is traceable instead of silently corrupting the layout.
    function safeCoord(d) {
        if (Number.isFinite(d.x) && Number.isFinite(d.y)) return d;
        console.warn(`d3-graph: node "${d.id}" has non-finite position (x=${d.x}, y=${d.y}) — falling back to (0,0)`);
        return { ...d, x: Number.isFinite(d.x) ? d.x : 0, y: Number.isFinite(d.y) ? d.y : 0 };
    }

    function renderNodes(handle) {
        const data = [...handle.currentGraph.nodes].map(([id, n]) => safeCoord({ id, ...n }));

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

        attachHoverZoom(merged, handle);
        updateNode(merged, handle);
    }

    // Hovering a node scales IT UP (via CSS transform, layered on top of its own SVG
    // translate — they compose, scaling around the node's own center) so its text stays
    // readable when the whole graph is fit-to-view at a small scale. The scale factor is
    // inversely proportional to the CURRENT zoom level (read from the live d3-zoom
    // transform, not a fixed bump), so a node hovered while zoomed way out gets a much
    // bigger boost than one hovered at/near 1:1 — capped so it never gets absurd either way.
    const HOVER_ZOOM_TARGET = 1.7; // ~= the on-screen scale a hovered node aims for
    const HOVER_ZOOM_MAX = 3.5;

    function attachHoverZoom(merged, handle) {
        merged
            .on('mouseenter.zoom', function (ev, d) {
                const k = d3.zoomTransform(handle.svg.node()).k || 1;
                const scale = Math.min(HOVER_ZOOM_MAX, Math.max(1, HOVER_ZOOM_TARGET / k));
                if (scale <= 1.01) return; // already big enough at this zoom level — no-op
                // A CSS `transform` on an SVG element REPLACES the `transform` presentation
                // attribute outright rather than composing with it (per spec, both are the
                // same CSS property; the attribute is just the lowest-priority style origin)
                // — this is exactly the "strobo/jump" this file's own comment above .bm-node
                // warns about from an earlier attempt. Including the node's own translate(x,y)
                // in the SAME CSS transform string avoids it: this fully takes over from the
                // attribute instead of fighting it. No transform-origin override needed either
                // — every child shape is already drawn symmetric around this group's own local
                // (0,0), which is the default SVG transform-origin, so `scale()` here already
                // scales around the node's visual center for free.
                //
                // `px` is REQUIRED here even though the SVG transform ATTRIBUTE accepts bare
                // unitless numbers — the CSS transform PROPERTY does not: translate(x, y) with
                // no unit is invalid CSS syntax, and the browser silently drops the entire
                // declaration (no console error, nothing happens). This was the actual bug
                // behind "zoom-on-hover does nothing" — confirmed by hand: `transform:
                // translate(50, 50)` is silently rejected, `transform: translate(50px, 50px)`
                // isn't. Since this SVG has no viewBox scaling (width/height are 100%, 1:1 with
                // the container), `px` here maps directly to the same user-unit coordinate
                // space the attribute-based translate already uses.
                d3.select(this).raise()
                    .classed('bm-node-hover-zoom', true)
                    .style('transform', `translate(${d.x}px, ${d.y}px) scale(${scale})`);
            })
            .on('mouseleave.zoom', function () {
                // Clearing the CSS transform reverts control to the attribute, which the
                // ongoing position transitions keep current — safe even if a relayout moved
                // this node while it was hovered. The class comes off too so it's not left
                // transitioning the NEXT unrelated attribute-driven position change.
                d3.select(this).classed('bm-node-hover-zoom', false).style('transform', null);
            });
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
            // A node's own "still working" is true if ANY of its pipeline rows still
            // has something in flight, regardless of which row is the recently-active one.
            const stillWorking = (d.data.pipelines || []).some(p => (p.inProgressCount ?? 0) > 0);
            const st  = stateClass(d.data.headerState, stillWorking);
            const col = stateColor(d.data.headerState, stillWorking);
            // Blueprint provenance — only meaningful when a blueprint actually applies. With no
            // blueprint at all, IsDeclared defaults to false on every node, which would
            // otherwise make every node read as "undeclared" (dashed) — there's no plan to be
            // off of, so both flags are forced off when handle.hasBlueprint is false.
            const undeclared = handle.hasBlueprint && d.data.isObserved === true  && d.data.isDeclared === false;
            const skeleton   = handle.hasBlueprint && d.data.isObserved === false && d.data.isDeclared === true;
            const accentCol  = d.data.color || col; // per-node colour override from the hint
            el.classed('bm-node-skeleton', skeleton);

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
                .attr('class', `bm-node-rect bm-hdr-${st}${undeclared ? ' bm-node-rect-undeclared' : ''}`);

            // Header tint — full-width band behind the title, same hue as the border
            // but semi-transparent (fill-opacity, not a separate alpha color) so it
            // reads as "this header belongs to that border colour" rather than a
            // solid block. clip-path (the same rounded-card clip as the rows below)
            // keeps its corners from poking out past the card's rounded top corners —
            // previously this was an unclipped 4px-wide bar with square corners.
            el.select('.bm-node-accent')
                .attr('x', -hw).attr('y', -hh)
                .attr('width', d.width).attr('height', HEADER_HEIGHT)
                .attr('fill', accentCol).attr('fill-opacity', 0.4)
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
                .attr('fill', col)
                .classed('bm-pulse-active', st === 'active-green' || st === 'active-blue');

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
            const col  = stateColor(r.state, (r.inProgressCount ?? 0) > 0);

            row.select('.bm-row-hit')
                .attr('x', -hw).attr('y', top)
                .attr('width', nd.width).attr('height', ROW_HEIGHT);

            row.select('.bm-row-div')
                .attr('x1', -hw).attr('x2', hw)
                .attr('y1', top).attr('y2', top)
                .attr('stroke', div).attr('stroke-width', 0.5);

            // Name (top-left) and counts (top-right) share the upper line; the
            // progress bar spans the full width beneath them (matches the mock).
            // textY was top+16 — with the row's top divider right at `top`, that left
            // the text sitting almost flush against it; +20 gives it real breathing room.
            const textY = top + 20;
            const barY  = top + 32;

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

        const instHtml = instances.length > 0
            ? instances.map(i => {
                const done = i.doneCount ?? 0, total = done + (i.inProgressCount ?? 0);
                // Pad the digits (not the "PID " prefix) to a fixed width with non-breaking
                // spaces, so the "PID " label is always the same length and the server
                // column after it still lines up regardless of the PID's digit count.
                const pid = String(i.processId ?? '').padStart(5, ' ');
                const errored = (i.errorCount ?? 0) > 0;
                return `<div class="bm-pop-inst${errored ? ' bm-pop-inst-error' : ''}" title="Open logs">
                    <span class="bm-pop-pid">PID&nbsp;${esc(pid)}</span>
                    <span class="bm-pop-srv" title="Resolving log path…">${esc(i.server)}</span>
                    <span class="bm-pop-cnt">${done} / ${total}</span>
                </div>`;
            }).join('')
            : '<div class="bm-pop-empty">No instances seen yet</div>';

        const topicHtml = topic
            ? `<div class="bm-pop-topic">
                   <button class="bm-pop-kafka" title="${esc(topic)}">↗&nbsp;Browse topic</button>
               </div>`
            : '';

        const errorCount = pipeline.errorCount ?? 0;
        const errHtml = errorCount > 0
            ? `<button class="bm-pop-err" title="Show ${errorCount} error${errorCount === 1 ? '' : 's'}">⚠&nbsp;${errorCount}</button>`
            : '';

        handle.popover.html(`
            <div class="bm-pop-hdr">
                ${errHtml}
                <button class="bm-pop-x">×</button>
            </div>
            ${instHtml}
            ${topicHtml}
        `)
        .style('opacity', 1)
        .style('pointer-events', 'auto');

        handle.popover.select('.bm-pop-x').on('click', () => hidePopover(handle));
        handle.popover.select('.bm-pop-err').on('click', () => {
            if (handle.dotNetRef) handle.dotNetRef.invokeMethodAsync('RequestShowErrors', nodeDatum.id, pipeline.name);
            hidePopover(handle);
        });
        handle.popover.select('.bm-pop-kafka').on('click', () => {
            if (handle.dotNetRef) handle.dotNetRef.invokeMethodAsync('RequestOpenKafkaTopic', topic);
        });
        handle.popover.selectAll('.bm-pop-inst').each(function (_, idx) {
            const inst = instances[idx];
            const row  = d3.select(this);
            row.on('click', () => {
                if (handle.dotNetRef)
                    handle.dotNetRef.invokeMethodAsync('RequestOpenInstanceLog', nodeDatum.id, inst.server, inst.processId ?? 0, inst.lastActivity);
            });
            if (handle.dotNetRef) {
                handle.dotNetRef
                    .invokeMethodAsync('RequestInstanceLogPathHint', nodeDatum.id, inst.server, inst.processId ?? 0)
                    .then(path => row.select('.bm-pop-srv').attr('title', path || 'Log path not discovered yet'))
                    .catch(() => {});
            }
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

        // Wide, invisible "hit" path underneath the visible one — the real edge can be as
        // thin as 1-2px (edgeW), too small a target to reliably hover; this gives it a much
        // bigger interactive strip without changing what's actually drawn.
        enter.append('path').attr('class', 'bm-edge-hit')
            .attr('fill', 'none')
            .attr('stroke', 'transparent')
            .attr('stroke-width', 16);

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
            const col = stateColor(d.data?.state, edgeStillWorking(handle, d.data));
            const w   = edgeW(d.data);
            const el  = d3.select(this);
            const dAttr = edgePath(handle, d.pts);

            el.select('.bm-edge-hit').attr('d', dAttr);

            // Set stroke as attribute so context-stroke on the marker resolves.
            el.select('.bm-edge-path')
                .attr('d', dAttr)
                .attr('stroke', col)
                .attr('stroke-width', w);

            // Declared-but-not-yet-observed edges (from the blueprint) render faint.
            el.classed('bm-edge-skeleton', d.data?.isObserved === false && d.data?.isDeclared === true);

            el.select('title').text(edgeTooltip(d.data));

            // Place the label near the arrowhead (target end) rather than the
            // geometric middle — a multi-bend path's middle can sit far from the
            // arrow, making it ambiguous which edge the number belongs to.
            const lp = edgeLabelPos(d.pts);
            el.select('.bm-edge-label')
                .attr('x', lp.x).attr('y', lp.y)
                .text(edgeLabel(d.data));
        });

        // Hover: bring the hovered edge to the front and highlight it (thicker, glowing),
        // dim every other edge — so a dense graph's crossing arrows can be told apart by
        // tracing one at a time instead of untangling them all at once by eye.
        merged
            .on('mouseenter.hover', function() {
                const hovered = this;
                d3.select(this).raise().classed('bm-edge-hover', true);
                handle.eLayer.selectAll('g.bm-edge').classed('bm-edge-dim', function() { return this !== hovered; });
            })
            .on('mouseleave.hover', function() {
                handle.eLayer.selectAll('g.bm-edge').classed('bm-edge-dim', false);
                d3.select(this).classed('bm-edge-hover', false);
            });
    }

    // An edge's colour should match its SOURCE pipeline row's own colour — not a
    // separately-derived notion of "still working" — so a completed (green) row
    // never feeds a blue arrow. waitingEstimate (chunks handed off but not yet
    // seen at the target) is about the target's backlog, not the source's own
    // state, so it isn't the right signal here.
    function edgeStillWorking(handle, e) {
        const srcNode = handle.currentGraph?.nodes?.get(e?.source);
        const row = srcNode?.data?.pipelines?.find(p => p.name === e?.sourcePipeline);
        return (row?.inProgressCount ?? 0) > 0;
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
            const col = stateColor(d.data?.state, edgeStillWorking(handle, d.data));
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
export function init(el, key, dotNetRef, portConstraints, edgeStyle, defaultDirection) { if (_handles.has(key)) D3Graph.dispose(_handles.get(key)); _handles.set(key, D3Graph.init(el, dotNetRef, portConstraints, edgeStyle, defaultDirection)); }
/** @param {string} key @param {object} topo */
export function update(key, topo)        { const h = _handles.get(key); if (h) D3Graph.update(h, topo); }
/** @param {string} key */
export function fitToView(key)           { const h = _handles.get(key); if (h) { D3Graph.resetZoom(h); D3Graph.fitToView(h, true); } }
/** @param {string} key @param {boolean} visible */
export function setVisible(key, visible) { const h = _handles.get(key); if (h) D3Graph.setVisible(h, visible); }
/** @param {string} key */
export function dispose(key)             { const h = _handles.get(key); if (h) { D3Graph.dispose(h); _handles.delete(key); } }
