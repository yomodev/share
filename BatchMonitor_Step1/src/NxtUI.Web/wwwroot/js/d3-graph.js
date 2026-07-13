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

    // Same idea as computeNodeWidth, sized for a child-run card's header (label + the
    // expand/collapse badge instead of an instance-count pill) — grows to fit a long run
    // description instead of clipping it, matching how a real service node's card grows.
    function computeChildCardWidth(handle, label) {
        const HEADER_PAD = 14 + CHILD_BADGE_W + 16;
        const needed = measureText(handle, label, 'bm-node-label') + HEADER_PAD;
        return Math.max(CHILD_RUN_MIN_WIDTH, Math.min(MAX_NODE_WIDTH, Math.ceil(needed)));
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

    // ── Expanded child-run boxes: full-fidelity nested nodes ────────────────────────────
    // An expanded child run (docs/12 §7.4, "in-place expand") is NOT drawn as a lightweight
    // simplified representation — its real service nodes are merged into the SAME
    // nodes/edges the main graph renders, at the SAME size, through the SAME buildNode/
    // updateNode/renderEdges/animation/popover code paths as any top-level service. This is
    // what makes an expanded child look and behave identically to a top-level one: same
    // card size, same pipeline rows/colors/animations, same click-to-see-errors popover.
    //
    // The one thing that's genuinely different is WHERE that content sits: it's boxed inside
    // a background rect (renderChildRunBoxes) representing "these nodes belong to run X."
    // Sizing that box requires knowing its contents' laid-out size BEFORE the outer engine
    // (ELK or custom) places it — so each expanded child's own sub-layout is always computed
    // with bm-flow-layout (buildChildRunBoxSpecs), independent of which engine draws the
    // OUTER graph, then handed to that outer engine as one opaque, pre-sized leaf. After the
    // outer engine resolves that leaf's position, flattenChildRunBoxes walks back into the
    // pre-computed sub-layout and merges its real nodes/edges into the final result,
    // translated into place — recursively, so a grandchild expanded inside an expanded
    // child works the same way one level deeper.
    // Matches HEADER_HEIGHT (the real service-node header height) — renderChildCardChrome
    // draws an expanded box's header at that same height, so the reserved space here must
    // agree or the merged real content would start either overlapping the header or with an
    // unexplained gap beneath it.
    const EXPANDED_HEADER_H = HEADER_HEIGHT, EXPANDED_PAD = 12;

    // Builds the plain node/edge list bm-flow-layout needs for ONE topology level (the main
    // graph, or one expanded child's own topology), plus a side metadata map (bm-flow-layout
    // itself carries no `data` — see its own module for why) recording, per node id, either
    // the real TopologyNode or (for a nested expanded child) that child's own recursively-
    // built box spec.
    function buildLevelForLayout(handle, topoNodes, topoEdges, childRunEntries, expandedChildren, direction, boxColor) {
        const ids = new Set(topoNodes.map(n => n.id));
        const flowNodes = [];
        const metaById = new Map();
        for (const n of topoNodes) {
            n._layoutWidth = computeNodeWidth(handle, n);
            flowNodes.push({
                id: n.id, width: n._layoutWidth, height: nh(n),
                role: normalizeRole(n.role), group: n.group || undefined, order: n.order,
            });
            metaById.set(n.id, { kind: 'node', data: n });
        }

        for (const entry of (childRunEntries || [])) {
            const childTopo = expandedChildren ? expandedChildren[entry.runId] : null;
            if (!childTopo) continue; // collapsed — handled separately by renderChildRuns's row
            // `boxColor` is THIS level's own resolved color (RunsSettings.ChildRunBoxColor,
            // topology-hint-overridable) — the box drawn for `entry` belongs to and is
            // colored by ITS OWN parent (this level), not by the child's own topology.
            const spec = buildChildRunBoxSpec(handle, childTopo, entry, direction, boxColor);
            const boxId = `__box_${entry.runId}`;
            flowNodes.push({ id: boxId, width: spec.width, height: spec.height });
            metaById.set(boxId, { kind: 'box', spec });
        }

        const edges = (topoEdges || []).filter(e => ids.has(e.source) && ids.has(e.target));
        const seenPairs = new Set();
        const flowEdges = [];
        for (const e of edges) {
            const key = `${e.source}->${e.target}`;
            if (seenPairs.has(key)) continue;
            seenPairs.add(key);
            flowEdges.push({ id: key, source: e.source, target: e.target });
        }
        const edgeDataByKey = new Map(edges.map(e => [`${e.source}->${e.target}`, e]));

        return { flowNodes, flowEdges, metaById, edgeDataByKey };
    }

    // Companion to the top-level refreshDataInPlace, scoped to one cached child sub-layout:
    // swaps each metaById node entry's `data` (and each edgeDataByKey entry) for the
    // matching object in the freshly-computed childTopo, leaving the cached geometry alone.
    function refreshChildBuiltDataInPlace(built, childTopo) {
        const nodeById = new Map(childTopo.nodes.map(n => [n.id, n]));
        for (const meta of built.metaById.values()) {
            if (meta.kind === 'node' && nodeById.has(meta.data.id)) meta.data = nodeById.get(meta.data.id);
        }
        const edgeByKey = new Map((childTopo.edges || []).map(e => [`${e.source}->${e.target}`, e]));
        for (const key of built.edgeDataByKey.keys()) {
            if (edgeByKey.has(key)) built.edgeDataByKey.set(key, edgeByKey.get(key));
        }
    }

    // Recursively computes one expanded child's own sub-layout (bottom-up — a box's size
    // must be known before its own parent can be placed) and returns a spec describing its
    // box size plus everything flattenChildRunBoxes needs to merge its contents in later.
    function buildChildRunBoxSpec(handle, childTopo, entry, parentDirection, boxColor) {
        const direction = parentDirection; // sub-boxes keep the same flow direction as their parent

        // Reuse the previous commit's sub-layout for this child if ITS OWN subtree (structure
        // recursively down through ITS OWN expanded grandchildren) hasn't changed — a full
        // outer relayout can be triggered by the ROOT graph gaining an edge, or by a totally
        // different sibling child changing, in which case redoing THIS child's bm-flow-layout
        // call from scratch is pure waste. Keyed by runId (not childTopo identity, which is a
        // fresh object every recompute even when nothing in it actually changed) + a
        // structural signature; the cache lives on the handle so it survives across commits
        // for the life of the graph instance.
        const childKey = structuralKey(childTopo);
        if (!handle.childSpecCache) handle.childSpecCache = new Map();
        handle._touchedChildRunIds?.add(entry.runId);
        const cached = handle.childSpecCache.get(entry.runId);
        // Reuse is only safe when this child has no expanded grandchildren of its own: a
        // cache hit skips the recursive buildLevelForLayout call entirely, which is also
        // what would normally (a) mark a grandchild's own runId as touched this pass (so
        // pruneChildSpecCache doesn't evict its still-valid cache entry out from under it)
        // and (b) refresh ITS data in place. Leaf children (the common case — see docs/12
        // §7's default non-recursive expand) have neither concern.
        const hasExpandedGrandchildren = Object.keys(childTopo.expandedChildren || {}).length > 0;
        let built, result;
        if (cached && cached.key === childKey && cached.direction === direction && !hasExpandedGrandchildren) {
            ({ built, result } = cached);
            // Geometry (node positions, edge points, box size) is unchanged, but the counts/
            // colours on each node/edge are NOT part of structuralKey and DO change every
            // tick — refresh those in place so a reused layout doesn't freeze this child's
            // displayed data at whatever it was when the cache entry was built.
            refreshChildBuiltDataInPlace(built, childTopo);
        } else {
            // One level deeper: THIS child's own boxes (for ITS children, i.e. grandchildren
            // of the outer level) are colored by childTopo's own resolved color, not
            // `boxColor` (which belongs to the level that owns `entry`, i.e. this box itself).
            built = buildLevelForLayout(
                handle, childTopo.nodes, childTopo.edges, childTopo.childRuns, childTopo.expandedChildren,
                direction, childTopo.childRunBoxColor);
            result = flowLayout({ nodes: built.flowNodes, edges: built.flowEdges, direction }, {});
            handle.childSpecCache.set(entry.runId, { key: childKey, direction, built, result });
        }

        // The box must be at least wide enough for its own header title (a long run
        // description shouldn't get clipped just because its content happens to be narrow) —
        // same "grow to fit text" rule a real service node's card follows.
        const titleWidth = computeChildCardWidth(handle, entry.description || entry.runId);
        return {
            runId: entry.runId, entry,
            width: Math.max(result.width + EXPANDED_PAD * 2, titleWidth),
            height: result.height + EXPANDED_HEADER_H + EXPANDED_PAD * 2,
            result, metaById: built.metaById, edgeDataByKey: built.edgeDataByKey,
            groupColors: childTopo.groupColors || {},
            boxColor,
        };
    }

    // After the outer engine (ELK or custom) has resolved a position for every node
    // including the synthetic `__box_<runId>` leaves, walks the tree merging each box's
    // pre-computed sub-layout into the final nodes/edges/boxes output, recursively.
    function flattenChildRunBoxes(rawPositions, metaById, edgeDataByKey, direction, offX, offY, runIdPrefix, outNodes, outEdges, outBoxes, outGroupColors) {
        for (const [id, pos] of rawPositions) {
            const meta = metaById.get(id);
            if (!meta) continue;
            if (meta.kind === 'node') {
                const outId = runIdPrefix ? `${runIdPrefix}::${id}` : id;
                outNodes.set(outId, {
                    x: pos.x + offX, y: pos.y + offY, width: pos.width, height: pos.height,
                    data: runIdPrefix ? { ...meta.data, _runId: runIdPrefix } : meta.data,
                });
            } else {
                const spec = meta.spec;
                const boxX = pos.x + offX, boxY = pos.y + offY;
                outBoxes.push({ runId: spec.runId, entry: spec.entry, x: boxX, y: boxY, width: pos.width, height: pos.height, boxColor: spec.boxColor });
                if (outGroupColors) outGroupColors.set(spec.runId, spec.groupColors || {});

                const { minX: subMinX, minY: subMinY } = minBoundsOf(spec.result.nodes);
                const contentLeft = boxX - pos.width / 2 + EXPANDED_PAD;
                const contentTop  = boxY - pos.height / 2 + EXPANDED_HEADER_H + EXPANDED_PAD;
                const nOffX = contentLeft - subMinX;
                const nOffY = contentTop - subMinY;

                flattenChildRunBoxes(
                    spec.result.nodes, spec.metaById, spec.edgeDataByKey, direction,
                    nOffX, nOffY, spec.runId, outNodes, outEdges, outBoxes, outGroupColors);

                for (const e of spec.result.edges) {
                    const outId = `${spec.runId}::${e.id}`;
                    outEdges.push({
                        id: outId,
                        pts: e.points.map(p => ({ x: p.x + nOffX, y: p.y + nOffY })),
                        data: spec.edgeDataByKey.get(e.id),
                    });
                }
            }
        }
    }

    function pruneChildSpecCache(handle) {
        if (!handle.childSpecCache || !handle._touchedChildRunIds) return;
        for (const runId of handle.childSpecCache.keys()) {
            if (!handle._touchedChildRunIds.has(runId)) handle.childSpecCache.delete(runId);
        }
    }

    // Adapts bm-flow-layout's pure-geometry output into the same render contract as ELK.
    // Edges are service-to-service only here (no per-pipeline ports) — see docs/12 Stage 1.
    // Blueprint hints (role/group/order, decorated onto topo nodes by
    // TopologyComputationService.Decorate) are passed through to the engine's Stage 2
    // hard/soft constraints. `placement`/`placeSuccessor` are not yet in the C# hint schema
    // (docs/12 §6, not yet implemented) so they're always undefined for now — the engine
    // already supports them, they're just unreachable from the app until that lands.
    function runLayoutCustom(handle, topo, seedOrder) {
        const layout = topo.layout || null;
        const dir = chooseDir(handle, layout);
        const direction = dir === 'RIGHT' ? 'horizontal' : 'vertical';

        // Tracks which runIds buildChildRunBoxSpec actually touched this layout pass, so
        // stale entries (a child that got collapsed and never re-expanded) don't sit in
        // childSpecCache forever — pruned below once the whole tree's been walked.
        handle._touchedChildRunIds = new Set();

        const built = buildLevelForLayout(handle, topo.nodes, topo.edges, topo.childRuns, topo.expandedChildren, direction, topo.childRunBoxColor);
        const result = flowLayout({ nodes: built.flowNodes, edges: built.flowEdges, direction }, { seedOrder });
        pruneChildSpecCache(handle);

        const nodes = new Map();
        const outEdges = [];
        const boxes = [];
        const groupColorsByRun = new Map([['', topo.groupColors || {}]]);
        flattenChildRunBoxes(result.nodes, built.metaById, built.edgeDataByKey, direction, 0, 0, null, nodes, outEdges, boxes, groupColorsByRun);
        for (const e of result.edges) {
            outEdges.push({ id: e.id, pts: e.points, data: built.edgeDataByKey.get(e.id) });
        }

        return { nodes, edges: outEdges, width: result.width, height: result.height, childRunBoxes: boxes, groupColorsByRun };
    }

    async function runLayoutElk(handle, topo) {
        // See runLayoutCustom's own copy of this comment — same childSpecCache pruning need.
        handle._touchedChildRunIds = new Set();

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
        const direction = dir === 'RIGHT' ? 'horizontal' : 'vertical';

        // Expanded child-run boxes (docs/12 §7.4): each one's own sub-layout is always
        // computed with bm-flow-layout regardless of ELK being the outer engine here — ELK
        // only needs to know its opaque size (a plain leaf, no ports: a child-run box has no
        // structural edges to anything). See buildChildRunBoxSpec/flattenChildRunBoxes above.
        const boxMetaById = new Map();
        const boxChildren = [];
        for (const entry of (topo.childRuns || [])) {
            const childTopo = topo.expandedChildren ? topo.expandedChildren[entry.runId] : null;
            if (!childTopo) continue;
            const spec = buildChildRunBoxSpec(handle, childTopo, entry, direction, topo.childRunBoxColor);
            const boxId = `__box_${entry.runId}`;
            boxChildren.push({ id: boxId, width: spec.width, height: spec.height, ports: [], layoutOptions: {} });
            boxMetaById.set(boxId, { kind: 'box', spec });
        }
        pruneChildSpecCache(handle);

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

        // Content-based id (source/target service+pipeline), NOT an array index — the
        // `edges` array's order can shift between successive topology recomputes (a new
        // pipeline appearing shifts everything after it), and renderEdges' D3 join keys on
        // this id (`.data(data, d => d.id)`) to track element identity across updates. An
        // index-based id would silently rebind "e5"'s DOM element (with its stale computed
        // path/colour) onto a completely different logical edge whenever the order shifted,
        // which read as edges briefly showing the wrong colour or appearing to vanish/jump.
        const edgeId = e => `${e.source}::${e.sourcePipeline || ''}->${e.target}::${e.targetPipeline || ''}`;
        const elkEdges = edges.map(e => ({
            id: edgeId(e),
            // No pipeline → attach to the node itself (ELK routes it to a sensible point on
            // the node's boundary automatically), not a per-pipeline port. See the port-
            // building comment above for why.
            sources: [e.sourcePipeline ? portId(e.source, 'out', e.sourcePipeline) : e.source],
            targets: [e.targetPipeline ? portId(e.target, 'in',  e.targetPipeline) : e.target],
        }));
        const edgeDataById = new Map(edges.map(e => [edgeId(e), e]));

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
            children: [...children, ...boxChildren],
            edges: elkEdges,
        };

        const res = await handle.elk.layout(graph);

        // Normalise into our render structure. ELK coords have a top-left origin;
        // we store node CENTRE (renderNodes translates to centre, draws from -w/2)
        // and keep edge points in ELK-absolute space (which equals our render space).
        // res.children includes both real nodes and the synthetic __box_<runId> leaves —
        // flattenChildRunBoxes tells them apart via metaById and merges each box's own
        // pre-computed sub-layout in, recursively.
        const rawPositions = new Map();
        for (const c of (res.children || [])) {
            rawPositions.set(c.id, { x: c.x + c.width / 2, y: c.y + c.height / 2, width: c.width, height: c.height });
        }
        const metaById = new Map();
        for (const n of topo.nodes) metaById.set(n.id, { kind: 'node', data: n });
        for (const [id, meta] of boxMetaById) metaById.set(id, meta);

        const nodes = new Map();
        const outEdges = [];
        const boxes = [];
        const groupColorsByRun = new Map([['', topo.groupColors || {}]]);
        flattenChildRunBoxes(rawPositions, metaById, edgeDataById, direction, 0, 0, null, nodes, outEdges, boxes, groupColorsByRun);

        for (const re of (res.edges || [])) {
            const s = re.sections?.[0];
            const pts = s ? [s.startPoint, ...(s.bendPoints || []), s.endPoint] : [];
            outEdges.push({ id: re.id, pts, data: edgeDataById.get(re.id) });
        }

        return { nodes, edges: outEdges, width: res.width || 0, height: res.height || 0, childRunBoxes: boxes, groupColorsByRun };
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

    // A point a short distance forward from the source end, offset above the line, so the
    // label reads clearly next to the edge it belongs to. Anchored near the SOURCE (not the
    // target/arrowhead) because several edges converging on one target would otherwise all
    // sit near the same point and overlap — each edge's own source is where it's most
    // distinct from its siblings. `siblingIndex`/`siblingCount` (edges sharing this edge's
    // target) add a small perpendicular stagger for the case where the sources themselves
    // are also close together (e.g. several sibling pipelines on the same source node).
    // Walks a (possibly multi-bend) polyline to the point `t` (0-1) of the way along its
    // total length, plus the unit tangent direction there — used to try several candidate
    // label positions along an edge rather than being pinned to one fixed spot.
    function pointAlongPath(pts, t) {
        if (!pts || pts.length < 2) return { x: 0, y: 0, ux: 1, uy: 0 };
        const segLens = [];
        let total = 0;
        for (let i = 1; i < pts.length; i++) {
            const l = Math.hypot(pts[i].x - pts[i - 1].x, pts[i].y - pts[i - 1].y);
            segLens.push(l);
            total += l;
        }
        let target = total * Math.max(0, Math.min(1, t));
        for (let i = 0; i < segLens.length; i++) {
            const l = segLens[i] || 1;
            if (target <= l || i === segLens.length - 1) {
                const f = Math.max(0, Math.min(1, target / l));
                const p0 = pts[i], p1 = pts[i + 1];
                return { x: p0.x + (p1.x - p0.x) * f, y: p0.y + (p1.y - p0.y) * f, ux: (p1.x - p0.x) / l, uy: (p1.y - p0.y) / l };
            }
            target -= l;
        }
        const last = pts[pts.length - 1];
        return { x: last.x, y: last.y, ux: 1, uy: 0 };
    }

    function rectsOverlap(a, b) {
        return a.x0 < b.x1 && a.x1 > b.x0 && a.y0 < b.y1 && a.y1 > b.y0;
    }

    // Finds a spot for an edge's label that doesn't sit on top of a node card or another
    // already-placed label — NOT pinned close to either endpoint. Tries a handful of
    // positions spread along the path (avoiding the very ends, which are cluttered by the
    // node card and arrowhead) at a few perpendicular offsets/distances from the line, and
    // takes the first one that clears every node bbox and every label placed so far this
    // render pass (`placedRects`, shared across all edges — the caller resets it once per
    // renderEdges call). Falls back to the path midpoint if every candidate collides (dense
    // graphs can run out of clean spots; still better than not drawing a label at all).
    function placeEdgeLabel(pts, text, nodeBoxes, placedRects) {
        const w = Math.max(24, String(text || '').length * 6.4 + 10), h = 16;
        const tCandidates = [0.5, 0.35, 0.65, 0.22, 0.78];
        const distances = [12, 22, 32];
        for (const t of tCandidates) {
            const p = pointAlongPath(pts, t);
            const px = -p.uy, py = p.ux; // perpendicular unit vector
            for (const side of [-1, 1]) {
                for (const dist of distances) {
                    const cx = p.x + px * dist * side, cy = p.y + py * dist * side;
                    const rect = { x0: cx - w / 2, y0: cy - h / 2, x1: cx + w / 2, y1: cy + h / 2 };
                    const hitsNode  = nodeBoxes.some(b => rectsOverlap(rect, b));
                    const hitsLabel = placedRects.some(r => rectsOverlap(rect, r));
                    if (!hitsNode && !hitsLabel) {
                        placedRects.push(rect);
                        return { x: cx, y: cy };
                    }
                }
            }
        }
        const p = pointAlongPath(pts, 0.5);
        const cx = p.x - p.uy * 12, cy = p.y + p.ux * 12;
        placedRects.push({ x0: cx - w / 2, y0: cy - h / 2, x1: cx + w / 2, y1: cy + h / 2 });
        return { x: cx, y: cy };
    }

    // ── init ─────────────────────────────────────────────────────────────

    function init(containerEl, dotNetRef, portConstraints, edgeStyle, defaultDirection, layoutEngine) {
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
        // Background+header for EXPANDED child-run boxes (docs/12 §7.4) — behind eLayer/nLayer
        // so the merged real nodes/edges from an expanded child render on top of it.
        const cbLayer = root.append('g').attr('class', 'bm-child-box-layer');
        const eLayer = root.append('g').attr('class', 'bm-edge-layer');
        const nLayer = root.append('g').attr('class', 'bm-node-layer');
        // COLLAPSED child-run blocks (docs/12 §7.4) render in their own layer, below the main
        // service flow — they're triggered at the RUN level, not by a specific service/
        // pipeline, so they're never wired into gLayer/eLayer/nLayer's edges. (An EXPANDED
        // child's content lives in eLayer/nLayer instead, merged with the main graph — see
        // cbLayer above and buildLevelForLayout/flattenChildRunBoxes.)
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
            containerEl, svg, root, gLayer, cbLayer, eLayer, nLayer, crLayer, zoom, popover, defs, dotNetRef,
            portConstraints: portConstraints === 'FIXED_POS' ? 'FIXED_POS' : 'FIXED_SIDE',
            edgeStyle: edgeStyle === 'CURVED' ? 'CURVED' : 'ORTHOGONAL',
            // RunsSettings.GraphDirection (Program.cs / D3FlowGraph.razor) — 'RIGHT'/'DOWN'
            // fixes the default; 'AUTO' keeps the old aspect-ratio auto-pick. A run-type
            // hint's own layout.direction (see chooseDir) always wins over this either way.
            defaultDirection: (defaultDirection === 'DOWN' || defaultDirection === 'AUTO') ? defaultDirection : 'RIGHT',
            // RunsSettings.GraphLayoutEngine — 'elk' (default) or 'custom' (bm-flow-layout).
            // See runLayout()'s own comment for what each engine actually supports.
            layoutEngine: layoutEngine === 'custom' ? 'custom' : 'elk',
            animState: createState(),
            elk: (typeof ELK !== 'undefined') ? new ELK() : null,
            currentGraph: null, pendingTopology: null,
            layoutTimer: null, layoutSeq: 0, rafId: null, lastFrameTime: null, lastCommitTime: 0,
            userZoomed: false, isVisible: true, disposed: false,
            popoverNodeId: null, popoverPipeline: null, popoverHideTimer: null,
        };
        if (!handle.elk && handle.layoutEngine !== 'custom') console.error('[D3Graph] ELK not loaded — flow graph cannot lay out.');

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
    // Recursive: an expanded child's real nodes/edges are merged into the main layout (see
    // buildLevelForLayout/flattenChildRunBoxes), so toggling expand/collapse — or a
    // structural change inside an already-expanded child — MUST force a full relayout, not
    // the stability short-circuit below. Without including expandedChildren here, expanding
    // a child would never actually compute/position its merged nodes at all.
    function structuralKey(topo) {
        const nodeIds = topo.nodes.map(n => n.id).sort();
        const edgeKeys = topo.edges.map(e => `${e.source}>${e.target}>${e.sourcePipeline || ''}>${e.targetPipeline || ''}`).sort();
        const groups = topo.nodes.map(n => `${n.id}:${n.group || ''}`).sort();
        const expandedIds = Object.keys(topo.expandedChildren || {}).sort();
        const expandedNested = expandedIds
            .map(id => `${id}[${structuralKey(topo.expandedChildren[id])}]`)
            .join(',');
        return nodeIds.join(',') + '|' + edgeKeys.join(',') + '|' + groups.join(',') + '|' + expandedNested;
    }

    async function commitLayout(handle) {
        // The custom engine (RunsSettings.GraphLayoutEngine = "Custom") doesn't touch ELK at
        // all, so it shouldn't be blocked by ELK failing to load (e.g. the CDN script being
        // unreachable) — only the default 'elk' engine actually needs handle.elk.
        if (handle.disposed || (handle.layoutEngine !== 'custom' && !handle.elk)) return;
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
            // Unlike renderChildRuns' collapsed cards (whose fake pipeline row shows live
            // counts that genuinely change every tick), an EXPANDED box's chrome — the only
            // thing renderChildRunBoxes draws, its real merged content is drawn separately by
            // renderNodes/renderEdges above — is derived entirely from handle.currentGraph,
            // which this (structurally-stable) branch just proved is byte-identical to last
            // commit. Profiling a live demo run showed this full clear-and-rebuild (including
            // discarding and re-registering every box's clip-path def) costing a ~50ms main-
            // thread long task on every ~2s live-tail tick for zero visual change — skip it
            // unless something that actually affects the chrome (status color/label) changed.
            const boxSig = childRunBoxesSignature(handle);
            if (boxSig !== handle.lastChildRunBoxesSig) {
                renderChildRunBoxes(handle);
                handle.lastChildRunBoxesSig = boxSig;
            }
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
        renderChildRunBoxes(handle);
        handle.lastChildRunBoxesSig = childRunBoxesSignature(handle);
        if (!handle.userZoomed) fitToView(handle, true);
    }

    // ── Child-run blocks (docs/12 §7.4) ─────────────────────────────────────
    // Both the collapsed and the expanded representation are styled as a real service
    // node's card (header with left-aligned, grow-to-fit title + a right-side expand/
    // collapse badge) so a child run doesn't read as a visually distinct "lesser" thing —
    // see renderChildCardChrome. Collapsed additionally draws ONE fake pipeline row (name
    // replaced by the run's own status, same progress-bar styling) purely to keep the same
    // silhouette as a real node; an EXPANDED child has no fake row because its real
    // nodes/edges are merged into the main graph's own nodes/edges (buildLevelForLayout/
    // flattenChildRunBoxes above) and rendered through the exact same buildNode/updateNode/
    // renderEdges/animation/popover path as any top-level service, at full size.
    // renderChildRunBoxes (below) only draws the chrome behind that merged content.
    const CHILD_RUN_GAP = 16, CHILD_RUN_MARGIN = 48;
    const CHILD_RUN_MIN_WIDTH = NODE_WIDTH;
    const CHILD_RUN_HEIGHT = HEADER_HEIGHT + ROW_HEIGHT; // header + one fake pipeline row
    const CHILD_BADGE_W = 34, CHILD_BADGE_H = 20;

    // Cheap fingerprint of everything renderChildRunBoxes' chrome actually depends on
    // (position/size come from currentGraph, which the caller already knows is unchanged on
    // the stable-path branch — this only needs to catch a status flip or box-color change).
    function childRunBoxesSignature(handle) {
        const boxes = handle.currentGraph?.childRunBoxes || [];
        return boxes.map(b => `${b.runId}:${b.entry?.status}:${b.boxColor || ''}`).join('|');
    }

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

    // Draws the shared "looks like a real service node" chrome — background/border card,
    // header accent band, left-aligned title, header divider, and an expand/collapse badge
    // on the right (in place of a real node's instance-count pill). Returns nothing; callers
    // add whatever goes below the header (a fake row for collapsed, nothing for expanded —
    // the real merged nodes sit there instead).
    function renderChildCardChrome(g, handle, { id, width, height, status, label, expanded, onToggle, boxColor }) {
        const hw = width / 2, hh = height / 2;
        // RunsSettings.ChildRunBoxColor (topology-hint-overridable) gives every child-run
        // box/card the SAME chrome color regardless of status — a deliberate visual cue that
        // "this is a nested run," distinct from a same-colored top-level service — instead of
        // the original behavior of deriving it from the child's own Running/Completed/Failed
        // status. Falls back to the status color when unset (default, unchanged behavior).
        // The fake pipeline row's own progress bar (renderFakePipelineRow) still always uses
        // the real status color — this only affects the card's identity chrome.
        const chromeCol = boxColor || childRunStatusColor(status);

        g.append('rect').attr('class', 'bm-child-run-bg').attr('rx', 10)
            .attr('x', -hw).attr('y', -hh).attr('width', width).attr('height', height)
            .attr('fill', nodeBg()).attr('stroke', chromeCol);

        // Real nodes clip their header accent band to the card's own rounded-corner shape
        // (updateClipPaths/bm-clip-<id>) so it never pokes square corners out past the
        // rounded card underneath it — this reuses that same mechanism for a child card's
        // header (registerChildCardClip, called by each caller before this).
        g.append('rect').attr('class', 'bm-node-accent')
            .attr('x', -hw).attr('y', -hh).attr('width', width).attr('height', HEADER_HEIGHT)
            .attr('fill', chromeCol).attr('fill-opacity', 0.4)
            .attr('clip-path', `url(#bm-clip-${id})`)
            .style('cursor', 'pointer').on('click', onToggle);

        // dy centers the text on its own line (matches buildNode's real header label,
        // which sets the same dy at enter time) — without it the glyph sits on the SVG
        // text baseline instead of vertically centered in the header band.
        g.append('text').attr('class', 'bm-node-label').attr('dy', '0.32em')
            .attr('x', -hw + 14).attr('y', -hh + HEADER_HEIGHT / 2)
            .style('cursor', 'pointer').on('click', onToggle)
            .text(label);

        g.append('line').attr('class', 'bm-node-hdr-div')
            .attr('x1', -hw).attr('x2', hw)
            .attr('y1', -hh + HEADER_HEIGHT).attr('y2', -hh + HEADER_HEIGHT)
            .attr('stroke', dividerColor());

        // Expand/collapse badge — same rounded-pill shape as a real node's instance count,
        // showing an up/down chevron (expanding/collapsing this card vertically opens/
        // closes content beneath it, same as any disclosure control) instead of a count.
        const bx = hw - 10 - CHILD_BADGE_W, by = -hh + HEADER_HEIGHT / 2 - CHILD_BADGE_H / 2;
        const badge = g.append('g').attr('class', 'bm-node-badge bm-child-card-badge')
            .style('cursor', 'pointer').on('click', onToggle);
        badge.append('title').text(expanded ? 'Collapse this run' : 'Expand this run');
        badge.append('rect').attr('class', 'bm-node-badge-bg')
            .attr('x', bx).attr('y', by).attr('width', CHILD_BADGE_W).attr('height', CHILD_BADGE_H)
            .attr('rx', CHILD_BADGE_H / 2);
        badge.append('text').attr('class', 'bm-node-badge-text')
            .attr('text-anchor', 'middle').attr('dy', '0.32em')
            .attr('x', bx + CHILD_BADGE_W / 2).attr('y', -hh + HEADER_HEIGHT / 2)
            .text(expanded ? '▾' : '▴');

        // Hover-zoom exists to make a small, hard-to-read card legible when the graph is
        // zoomed way out — an EXPANDED box is already a big container full of its own
        // full-size real nodes (not a compact card needing magnification), so scaling the
        // whole thing on hover is just disorienting. Collapsed cards are the same size as a
        // real node and get the same benefit a real node does.
        if (!expanded) attachHoverZoom(g, handle);
    }

    // Registers (or updates) the rounded-rect clipPath a child card's header accent band
    // clips to — see the comment on renderChildCardChrome. Callers own a class name so two
    // independent full-clear-then-rebuild passes (collapsed cards vs expanded boxes) don't
    // remove each other's entries mid-commit.
    function resetChildCardClips(handle, cssClass) {
        handle.defs.selectAll(`clipPath.${cssClass}`).remove();
    }
    function registerChildCardClip(handle, cssClass, id, width, height) {
        const clip = handle.defs.append('clipPath').attr('class', cssClass).attr('id', `bm-clip-${id}`);
        clip.append('rect').attr('rx', 10)
            .attr('x', -width / 2).attr('y', -height / 2).attr('width', width).attr('height', height);
    }

    // Draws the one fake pipeline row a COLLAPSED card shows, reusing the exact same
    // .bm-row-* classes/geometry a real node's pipeline row uses (see renderRows) so a
    // collapsed child run has the same silhouette as any other service card. The row's
    // "name" slot shows the run's status instead of a pipeline name; its progress bar
    // reflects DoneCount/TotalCount when known, or is fully filled once Completed — always
    // drawn (never hidden) so the card never looks broken/incomplete while Running/Failed.
    function renderFakePipelineRow(g, width, cardHeight, entry) {
        const hw = width / 2;
        const top = -cardHeight / 2 + HEADER_HEIGHT;
        const textY = top + 20, barY = top + 32;
        const PAD = 10;
        const pX = -hw + PAD, pW = width - PAD * 2, pH = 3;
        const statusCol = childRunStatusColor(entry.status);
        const done = entry.doneCount ?? 0, total = entry.totalCount ?? 0;
        const isCompleted = String(entry.status || '').toLowerCase() === 'completed';
        const frac = isCompleted ? 1 : (total > 0 ? Math.max(0, Math.min(1, done / total)) : 0);

        g.append('line').attr('class', 'bm-row-div')
            .attr('x1', -hw).attr('x2', hw).attr('y1', top).attr('y2', top)
            .attr('stroke', dividerColor()).attr('stroke-width', 0.5);
        g.append('text').attr('class', 'bm-row-name')
            .attr('x', -hw + PAD).attr('y', textY)
            .text(entry.status ?? 'Unknown');
        if (total > 0) {
            g.append('text').attr('class', 'bm-row-counts')
                .attr('x', hw - PAD).attr('y', textY).attr('text-anchor', 'end')
                .text(`${done} / ${total}`);
        }
        g.append('rect').attr('class', 'bm-row-track').attr('rx', 2)
            .attr('x', pX).attr('y', barY).attr('width', pW).attr('height', pH);
        g.append('rect').attr('class', 'bm-row-fill').attr('rx', 2)
            .attr('x', pX).attr('y', barY).attr('width', pW * frac).attr('height', pH)
            .attr('fill', statusCol);
    }

    // Only COLLAPSED child runs render here — an expanded one's id is in
    // handle.pendingTopology.expandedChildren and is skipped: its content is part of the
    // main graph now (see comment above), drawn by renderNodes/renderEdges instead.
    function renderChildRuns(handle) {
        const childRuns = handle.pendingTopology?.childRuns || [];
        const expandedChildren = handle.pendingTopology?.expandedChildren || {};
        const g = handle.currentGraph;

        handle.crLayer.selectAll('*').remove();
        if (!g) return;
        const collapsed = childRuns.filter(entry => !expandedChildren[entry.runId]);
        if (collapsed.length === 0) return;

        const widths = collapsed.map(entry => computeChildCardWidth(handle, entry.description || entry.runId));
        const totalWidth = widths.reduce((a, w) => a + w, 0) + CHILD_RUN_GAP * Math.max(0, collapsed.length - 1);
        const baseY = g.height + CHILD_RUN_MARGIN;
        let cursorX = (g.width - totalWidth) / 2;

        resetChildCardClips(handle, 'bm-child-collapsed-clip');

        collapsed.forEach((entry, i) => {
            const width = widths[i];
            const cx = cursorX + width / 2, cy = baseY + CHILD_RUN_HEIGHT / 2;
            const clipId = `child-collapsed-${entry.runId}`;
            registerChildCardClip(handle, 'bm-child-collapsed-clip', clipId, width, CHILD_RUN_HEIGHT);
            const onToggle = ev => {
                ev.stopPropagation(); // don't let svg's own click (hidePopover) swallow this
                handle.dotNetRef?.invokeMethodAsync('RequestOpenChildRun', entry.runId);
            };
            const gEl = handle.crLayer.append('g').attr('class', 'bm-child-run')
                .datum({ x: cx, y: cy })
                .attr('transform', `translate(${cx},${cy})`)
                .on('click', onToggle);

            renderChildCardChrome(gEl, handle, {
                id: clipId, width, height: CHILD_RUN_HEIGHT, status: entry.status,
                label: truncateLabel(entry.description || entry.runId, 34), expanded: false, onToggle,
                boxColor: handle.pendingTopology?.childRunBoxColor,
            });
            renderFakePipelineRow(gEl, width, CHILD_RUN_HEIGHT, entry);

            cursorX += width + CHILD_RUN_GAP;
        });
    }

    // Background + header for each EXPANDED child-run box (handle.currentGraph.childRunBoxes,
    // populated by runLayoutCustom/runLayoutElk's flattenChildRunBoxes — one entry per
    // expanded child at every nesting level, already in final outer coordinates). Drawn in
    // its own layer behind nLayer/eLayer so the merged real nodes/edges render on top of it.
    // Clicking the header (or its badge) collapses that run back (same RequestOpenChildRun
    // toggle a collapsed block's click uses).
    function renderChildRunBoxes(handle) {
        handle.cbLayer.selectAll('*').remove();
        const boxes = handle.currentGraph?.childRunBoxes || [];
        resetChildCardClips(handle, 'bm-child-expanded-clip');
        for (const box of boxes) {
            const clipId = `child-expanded-${box.runId}`;
            registerChildCardClip(handle, 'bm-child-expanded-clip', clipId, box.width, box.height);
            const g = handle.cbLayer.append('g').attr('class', 'bm-child-run bm-child-run-expanded')
                .datum({ x: box.x, y: box.y })
                .attr('transform', `translate(${box.x},${box.y})`);
            const onToggle = ev => {
                ev.stopPropagation();
                handle.dotNetRef?.invokeMethodAsync('RequestOpenChildRun', box.runId);
            };
            renderChildCardChrome(g, handle, {
                id: clipId, width: box.width, height: box.height, status: box.entry.status,
                label: truncateLabel(box.entry.description || box.runId, 40), expanded: true, onToggle,
                boxColor: box.boxColor,
            });
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
    // Gap between the group's border and its label, drawn OUTSIDE (above) the box rather
    // than crammed into a band along its top edge — see fitToView's GROUP_LABEL_MARGIN,
    // which reserves canvas space for whichever group ends up nearest the top so this
    // label is never clipped by the viewport.
    const GROUP_LABEL_GAP = 8;
    const GROUP_LABEL_MARGIN = 14 /* PAD */ + GROUP_LABEL_GAP + 14 /* label line height */;

    function renderGroups(handle) {
        const PAD = 14, LABEL_H = 16;
        const groupColorsByRun = handle.currentGraph.groupColorsByRun || new Map([['', handle.pendingTopology?.groupColors || {}]]);
        // Keyed by (owning run, group name) — a nested child run reusing the same group
        // name as its parent (or another sibling run) must NOT merge into the same box.
        const byGroup = new Map();
        for (const [id, n] of handle.currentGraph.nodes) {
            const grp = n.data && n.data.group;
            if (!grp) continue;
            const runId = n.data._runId || '';
            const key = `${runId} ${grp}`;
            const hw = (n.width || 0) / 2, hh = (n.height || 0) / 2;
            const box = byGroup.get(key) || { x0: Infinity, y0: Infinity, x1: -Infinity, y1: -Infinity, runId, name: grp };
            box.x0 = Math.min(box.x0, n.x - hw); box.y0 = Math.min(box.y0, n.y - hh);
            box.x1 = Math.max(box.x1, n.x + hw); box.y1 = Math.max(box.y1, n.y + hh);
            byGroup.set(key, box);
        }

        const data = [...byGroup.entries()].map(([key, b]) => {
            const groupColors = groupColorsByRun.get(b.runId) || {};
            return { key, name: b.name, b, color: groupColors[b.name] || null };
        });
        const sel = handle.gLayer.selectAll('g.bm-group').data(data, d => d.key);
        sel.exit().remove();
        const enter = sel.enter().append('g').attr('class', 'bm-group');
        enter.append('rect').attr('class', 'bm-group-rect').attr('rx', 12);
        enter.append('text').attr('class', 'bm-group-label');

        const merged = enter.merge(sel);
        merged.classed('bm-group-colored', d => !!d.color);
        merged.select('.bm-group-rect')
            .attr('x', d => d.b.x0 - PAD).attr('y', d => d.b.y0 - PAD)
            .attr('width', d => (d.b.x1 - d.b.x0) + PAD * 2)
            .attr('height', d => (d.b.y1 - d.b.y0) + PAD * 2)
            // .style (not .attr) — a CSS class rule beats an SVG presentation attribute, so
            // .attr('fill', ...) here would be silently overridden by .bm-group-rect's own
            // CSS; inline style wins over a class rule without !important.
            .style('fill', d => d.color || null)
            .style('fill-opacity', d => d.color ? 0.12 : null)
            .style('stroke', d => d.color || null)
            .style('stroke-opacity', d => d.color ? 0.85 : null)
            .style('stroke-dasharray', d => d.color ? 'none' : null);
        // Sits OUTSIDE the box, just above its top border — not competing with the
        // border/rounded corner for the same few pixels of vertical space.
        merged.select('.bm-group-label')
            .attr('x', d => d.b.x0 - PAD + 4).attr('y', d => d.b.y0 - PAD - GROUP_LABEL_GAP)
            .attr('dominant-baseline', 'alphabetic')
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
    const HOVER_ZOOM_TARGET = 1.15; // ~= the on-screen scale a hovered node aims for
    const HOVER_ZOOM_MAX = 1.9; // was 1.7/3.5 — reported as ~2x stronger than expected

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
            <div class="bm-pop-instances">${instHtml}</div>
            ${topicHtml}
        `)
        .style('opacity', 1)
        .style('pointer-events', 'auto');

        // nodeDatum.id is the (possibly `runId::` prefixed, for a merged expanded-child node
        // — see flattenChildRunBoxes) globally-unique map key, used above for popover
        // tracking/positioning. Every call back into Blazor instead needs the RAW service id
        // (nodeDatum.data.id — TopologyNode.Id, never prefixed) since that's what actually
        // matches the run's own event data; a prefixed id would break log-path lookups etc.
        // _runId (undefined for a top-level node) tells RunDetail.razor which run's own event
        // store to use — only the errors dialog needs it (instance-log/Kafka-topic clicks
        // already carry everything they need from that node's own real pipeline data).
        const rawServiceId = nodeDatum.data?.id ?? nodeDatum.id;
        const ownerRunId = nodeDatum.data?._runId ?? '';

        handle.popover.select('.bm-pop-x').on('click', () => hidePopover(handle));
        handle.popover.select('.bm-pop-err').on('click', () => {
            if (handle.dotNetRef) handle.dotNetRef.invokeMethodAsync('RequestShowErrors', rawServiceId, pipeline.name, ownerRunId);
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
                    handle.dotNetRef.invokeMethodAsync('RequestOpenInstanceLog', rawServiceId, inst.server, inst.processId ?? 0, inst.lastActivity);
            });
            if (handle.dotNetRef) {
                handle.dotNetRef
                    .invokeMethodAsync('RequestInstanceLogPathHint', rawServiceId, inst.server, inst.processId ?? 0)
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

        // Shared across every edge in this render pass — placeEdgeLabel consults both so a
        // label never lands on top of a node card or an already-placed sibling label.
        const nodeBoxes = [...handle.currentGraph.nodes.values()].map(n => ({
            x0: n.x - n.width / 2, y0: n.y - n.height / 2, x1: n.x + n.width / 2, y1: n.y + n.height / 2,
        }));
        const placedLabelRects = [];

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

            // Cached so the per-frame animation loop (renderAnimationFrame, up to 60x/sec
            // for every edge in the graph) doesn't redo stateColor/edgeStillWorking's
            // pipeline-array scan or edgeW's log10 on every single frame — these only
            // change when a layout actually commits, not every animation tick.
            d._baseColor = col;
            d._baseWidth = w;

            el.select('.bm-edge-hit').attr('d', dAttr);

            // Set stroke as attribute so context-stroke on the marker resolves.
            el.select('.bm-edge-path')
                .attr('d', dAttr)
                .attr('stroke', col)
                .attr('stroke-width', w);

            // Declared-but-not-yet-observed edges (from the blueprint) render faint.
            el.classed('bm-edge-skeleton', d.data?.isObserved === false && d.data?.isDeclared === true);

            el.select('title').text(edgeTooltip(d.data));

            // Not pinned to either endpoint — placeEdgeLabel searches a handful of spots
            // along the path and picks the first that doesn't land on a node card or a
            // label already placed for an earlier edge this render pass (see nodeBoxes/
            // placedLabelRects above).
            const labelText = edgeLabel(d.data);
            const lp = placeEdgeLabel(d.pts, labelText, nodeBoxes, placedLabelRects);
            el.select('.bm-edge-label')
                .attr('x', lp.x).attr('y', lp.y)
                .text(labelText);
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

        // Hot loop — runs up to 60x/sec for every edge in the graph (which, with expanded
        // child runs merged in, can now be several times what a single top-level run has).
        // Raw DOM access instead of re-wrapping each element in a D3 selection, and the
        // per-edge base colour/width are read from the cache renderEdges populated at
        // layout-commit time rather than recomputed here (see d._baseColor/_baseWidth).
        for (const el of handle.eLayer.node().querySelectorAll('g.bm-edge')) {
            const d = el.__data__; // D3 stores the bound datum here — avoids a d3.select() wrapper per edge per frame
            if (!d) continue;
            const s = sampleEdge(handle.animState, d.id, d.data?.doneCount ?? 0, now);
            const r = tick(s, dt);
            const col = d._baseColor;
            const w   = (d._baseWidth ?? 1) + r.thicknessBoost;

            const path = el.querySelector('.bm-edge-path');
            if (!path) continue;
            path.setAttribute('stroke', col);
            path.setAttribute('stroke-width', w);
            path.style.strokeDasharray = r.dashArray;
            path.style.strokeDashoffset = r.dashOffset;
            path.style.opacity = 0.70 + r.brighten * 0.30;
            path.style.filter = r.brighten > 0.05
                ? `drop-shadow(0 0 ${(r.brighten * 5).toFixed(1)}px ${col})`
                : '';
        }

        // Header pulse for active-state nodes.
        const phase = 0.5 + 0.5 * Math.sin(now / 700);
        const pulseOpacity = 0.08 + phase * 0.14;
        for (const el of handle.nLayer.node().querySelectorAll('.bm-node-pulse.bm-pulse-active')) {
            el.style.opacity = pulseOpacity;
        }
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

        // A group's label is drawn OUTSIDE (above) its box — see renderGroups/
        // GROUP_LABEL_MARGIN — which can poke above y=0 (the topmost node's own edge)
        // in graph coordinates. Reserve that extra height here so the label never
        // gets clipped by the viewport when a group sits in the top row.
        const gi = { width: g.width, height: g.height + GROUP_LABEL_MARGIN };
        const cw = cw0 || 1;
        const ch = ch0 || 1;
        const pad = 52;
        const scale = Math.max(0.12, Math.min(3,
            Math.min((cw - pad * 2) / (gi.width  || 1),
                     (ch - pad * 2) / (gi.height || 1))));
        const tx = (cw - (gi.width  || 0) * scale) / 2;
        const ty = (ch - (gi.height || 0) * scale) / 2 + GROUP_LABEL_MARGIN * scale;
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
export function init(el, key, dotNetRef, portConstraints, edgeStyle, defaultDirection, layoutEngine) { if (_handles.has(key)) D3Graph.dispose(_handles.get(key)); _handles.set(key, D3Graph.init(el, dotNetRef, portConstraints, edgeStyle, defaultDirection, layoutEngine)); }
/** @param {string} key @param {object} topo */
export function update(key, topo)        { const h = _handles.get(key); if (h) D3Graph.update(h, topo); }
/** @param {string} key */
export function fitToView(key)           { const h = _handles.get(key); if (h) { D3Graph.resetZoom(h); D3Graph.fitToView(h, true); } }
/** @param {string} key @param {boolean} visible */
export function setVisible(key, visible) { const h = _handles.get(key); if (h) D3Graph.setVisible(h, visible); }
/** @param {string} key */
export function dispose(key)             { const h = _handles.get(key); if (h) { D3Graph.dispose(h); _handles.delete(key); } }
