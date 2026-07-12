// bm-flow-layout — a small, framework-agnostic layered graph layout engine.
//
// Deliberately NOT a general graph-drawing library: it implements exactly what
// BatchMonitor's run-detail flow graph needs (see docs/12_Custom_Layout_And_Nested_Runs.md),
// under three facts that make a from-scratch layered (Sugiyama-lite) layout tractable:
//   - at most ~50 nodes ever
//   - the only cycle shape is a trivial A<->B pair (no general cycle-breaking needed)
//   - most requirements are about MANUAL placement control and STABILITY, which automatic
//     engines (ELK, dagre, ...) actively resist
//
// This module knows nothing about SVG/D3/Blazor. Input is a plain graph description;
// output is pure geometry (positioned nodes, edge polylines). See layout() below.
//
// Stage 1 scope (see docs/12): leaves only, single global direction, layer assignment +
// median ordering + dummy-node routing + coordinate assignment. Hints/groups/recursion
// are Stage 2+.

/**
 * @typedef {Object} LayoutNode
 * @property {string} id
 * @property {number} width
 * @property {number} height
 *
 * @typedef {Object} LayoutEdge
 * @property {string} source
 * @property {string} target
 * @property {string} [id]
 *
 * @typedef {Object} LayoutInput
 * @property {LayoutNode[]} nodes
 * @property {LayoutEdge[]} edges
 * @property {'horizontal'|'vertical'} [direction] - flow direction; default 'horizontal'
 * @property {number} [layerSpacing] - gap between layers along the flow axis
 * @property {number} [nodeSpacing] - gap between nodes within a layer (cross axis)
 */

const DEFAULT_LAYER_SPACING = 110;
const DEFAULT_NODE_SPACING = 56;
const ORDERING_PASSES = 4;

/**
 * Lays out a graph. Pure function: same input -> same output (stability requirement —
 * see docs/12 §5). Callers wanting update-to-update stability should pass the previous
 * result's node order back in via `seedOrder` (Stage 3 hook; optional today).
 *
 * @param {LayoutInput} input
 * @param {Object} [opts]
 * @param {Map<string, number>} [opts.seedOrder] - node id -> previous within-layer index,
 *        used to bias ordering toward minimal movement across relayouts (Stage 3).
 * @returns {{
 *   nodes: Map<string, {x:number, y:number, width:number, height:number, layer:number}>,
 *   edges: Array<{id:string, source:string, target:string, points:{x:number,y:number}[], isBackEdge:boolean}>,
 *   width: number, height: number,
 * }}
 */
export function layout(input, opts = {}) {
    const direction = input.direction === 'vertical' ? 'vertical' : 'horizontal';
    const layerSpacing = input.layerSpacing ?? DEFAULT_LAYER_SPACING;
    const nodeSpacing = input.nodeSpacing ?? DEFAULT_NODE_SPACING;

    const nodesById = new Map(input.nodes.map(n => [n.id, n]));

    const { structuralEdges, backEdges } = splitBackEdges(input.edges, nodesById);

    const layerOf = assignLayers(input.nodes, structuralEdges);

    const { layers, dummies, edgeChains } = buildDummyChains(
        input.nodes, structuralEdges, layerOf, layerSpacing, nodeSpacing);

    orderLayers(layers, edgeChains, opts.seedOrder);

    const positioned = assignCoordinates(
        layers, nodesById, dummies, layerOf, direction, layerSpacing, nodeSpacing);

    const edgesOut = [];
    for (const e of structuralEdges) {
        const chain = edgeChains.get(edgeKey(e));
        edgesOut.push({
            id: e.id ?? edgeKey(e),
            source: e.source,
            target: e.target,
            points: chainToPoints(chain, positioned, direction),
            isBackEdge: false,
        });
    }
    for (const e of backEdges) {
        edgesOut.push({
            id: e.id ?? edgeKey(e),
            source: e.source,
            target: e.target,
            points: routeBackEdge(e, positioned, direction),
            isBackEdge: true,
        });
    }

    let maxX = 0, maxY = 0;
    for (const p of positioned.values()) {
        maxX = Math.max(maxX, p.x + p.width / 2);
        maxY = Math.max(maxY, p.y + p.height / 2);
    }

    // Only expose real (non-dummy) nodes to callers.
    const realNodes = new Map();
    for (const n of input.nodes) realNodes.set(n.id, positioned.get(n.id));

    return { nodes: realNodes, edges: edgesOut, width: maxX + layerSpacing / 2, height: maxY + nodeSpacing };
}

function edgeKey(e) { return `${e.source}->${e.target}`; }

// ── Cycle handling: only A<->B is supported (see docs/12 §3.1). The first-seen direction
// of each pair becomes the structural edge used for layering; its reverse is a back-edge,
// routed separately as a curve around the pair rather than participating in layering.
function splitBackEdges(edges, nodesById) {
    const seenPairs = new Set();
    const structuralEdges = [];
    const backEdges = [];
    for (const e of edges) {
        if (!nodesById.has(e.source) || !nodesById.has(e.target) || e.source === e.target) continue;
        const reverseKey = `${e.target}->${e.source}`;
        if (seenPairs.has(reverseKey)) {
            backEdges.push(e);
        } else {
            seenPairs.add(edgeKey(e));
            structuralEdges.push(e);
        }
    }
    return { structuralEdges, backEdges };
}

// Longest-path layer assignment over the structural (acyclic, by construction) edge set.
function assignLayers(nodes, structuralEdges) {
    const layerOf = new Map(nodes.map(n => [n.id, 0]));
    const outgoing = new Map(nodes.map(n => [n.id, []]));
    const indegree = new Map(nodes.map(n => [n.id, 0]));
    for (const e of structuralEdges) {
        outgoing.get(e.source)?.push(e.target);
        indegree.set(e.target, (indegree.get(e.target) ?? 0) + 1);
    }

    // Kahn's algorithm topological order, propagating layer = max(predecessor layers) + 1.
    const queue = nodes.filter(n => (indegree.get(n.id) ?? 0) === 0).map(n => n.id);
    const remaining = new Map(indegree);
    const visited = new Set();
    let head = 0;
    while (head < queue.length) {
        const id = queue[head++];
        visited.add(id);
        for (const next of outgoing.get(id) ?? []) {
            layerOf.set(next, Math.max(layerOf.get(next) ?? 0, (layerOf.get(id) ?? 0) + 1));
            remaining.set(next, remaining.get(next) - 1);
            if (remaining.get(next) === 0) queue.push(next);
        }
    }
    // Any node not reached (shouldn't happen once cycles are split, but guards a stray
    // higher-order cycle we didn't anticipate) keeps its default layer 0 rather than crashing.
    return layerOf;
}

// Inserts a dummy node in every intermediate layer for edges spanning >1 layer, so routing
// has explicit waypoints instead of cutting through intervening layers/boxes.
function buildDummyChains(nodes, structuralEdges, layerOf, layerSpacing, nodeSpacing) {
    const maxLayer = Math.max(0, ...[...layerOf.values()]);
    const layers = Array.from({ length: maxLayer + 1 }, () => []);
    for (const n of nodes) layers[layerOf.get(n.id) ?? 0].push({ id: n.id, isDummy: false });

    const dummies = [];
    const edgeChains = new Map();
    let dummyCounter = 0;

    for (const e of structuralEdges) {
        const l1 = layerOf.get(e.source), l2 = layerOf.get(e.target);
        const chain = [e.source];
        if (l2 > l1 + 1) {
            for (let l = l1 + 1; l < l2; l++) {
                const dummyId = `__dummy_${dummyCounter++}`;
                dummies.push(dummyId);
                layers[l].push({ id: dummyId, isDummy: true });
                chain.push(dummyId);
            }
        }
        chain.push(e.target);
        edgeChains.set(edgeKey(e), chain);
    }
    return { layers, dummies, edgeChains };
}

// Median/barycenter ordering heuristic: a few down-sweeps and up-sweeps, each time
// reordering a layer by the average cross-axis position of its neighbors in the
// already-ordered adjacent layer. Cheap and good enough at our graph sizes (<=50 nodes plus
// dummies) — see docs/12 §1 for why full crossing-minimization quality doesn't matter here.
function orderLayers(layers, edgeChains, seedOrder) {
    // Initial order: seedOrder bias (stability, Stage 3) falls back to declaration order.
    for (const layer of layers) {
        if (seedOrder) {
            layer.sort((a, b) => (seedOrder.get(a.id) ?? 1e9) - (seedOrder.get(b.id) ?? 1e9));
        }
    }

    const neighborsOf = buildNeighborIndex(edgeChains);
    const indexInLayer = () => {
        const idx = new Map();
        layers.forEach((layer, li) => layer.forEach((n, i) => idx.set(n.id, { li, i })));
        return idx;
    };

    for (let pass = 0; pass < ORDERING_PASSES; pass++) {
        const forward = pass % 2 === 0;
        const range = forward
            ? [...layers.keys()]
            : [...layers.keys()].reverse();
        const positions = indexInLayer();

        for (const li of range) {
            const adjLi = forward ? li - 1 : li + 1;
            if (adjLi < 0 || adjLi >= layers.length) continue;
            const layer = layers[li];
            const medians = layer.map(n => {
                const neigh = (neighborsOf.get(n.id) ?? []).filter(id => positions.get(id)?.li === adjLi);
                if (neigh.length === 0) return { node: n, median: positions.get(n.id).i };
                const idxs = neigh.map(id => positions.get(id).i).sort((a, b) => a - b);
                const mid = Math.floor(idxs.length / 2);
                const median = idxs.length % 2 === 1
                    ? idxs[mid]
                    : (idxs[mid - 1] + idxs[mid]) / 2;
                return { node: n, median };
            });
            medians.sort((a, b) => a.median - b.median);
            layers[li] = medians.map(m => m.node);
            medians.forEach((m, i) => positions.set(m.node.id, { li, i }));
        }
    }
}

function buildNeighborIndex(edgeChains) {
    const neighbors = new Map();
    const add = (a, b) => {
        if (!neighbors.has(a)) neighbors.set(a, []);
        neighbors.get(a).push(b);
    };
    for (const chain of edgeChains.values()) {
        for (let i = 0; i < chain.length - 1; i++) {
            add(chain[i], chain[i + 1]);
            add(chain[i + 1], chain[i]);
        }
    }
    return neighbors;
}

function assignCoordinates(layers, nodesById, dummies, layerOf, direction, layerSpacing, nodeSpacing) {
    const dummySet = new Set(dummies);
    const positioned = new Map();

    // Layer axis size = the widest node in any layer (so all layers align on a grid),
    // measured along the flow axis.
    const layerAxisSize = layers.map(layer =>
        Math.max(1, ...layer.filter(n => !n.isDummy).map(n => {
            const node = nodesById.get(n.id);
            return direction === 'horizontal' ? node.width : node.height;
        })));

    let layerOffset = 0;
    const layerOffsets = layerAxisSize.map(size => {
        const offset = layerOffset;
        layerOffset += size + layerSpacing;
        return offset;
    });

    layers.forEach((layer, li) => {
        // Cross-axis extent for this layer, to center the whole layer around 0.
        const sizes = layer.map(n => dummySet.has(n.id) ? 1 : (direction === 'horizontal'
            ? nodesById.get(n.id).height
            : nodesById.get(n.id).width));
        const total = sizes.reduce((a, b) => a + b, 0) + nodeSpacing * Math.max(0, layer.length - 1);
        let cursor = -total / 2;

        layer.forEach((n, i) => {
            const size = sizes[i];
            const center = cursor + size / 2;
            cursor += size + nodeSpacing;

            const node = dummySet.has(n.id) ? { width: 1, height: 1 } : nodesById.get(n.id);
            const along = layerOffsets[li] + layerAxisSize[li] / 2;
            const x = direction === 'horizontal' ? along : center;
            const y = direction === 'horizontal' ? center : along;
            positioned.set(n.id, { x, y, width: node.width, height: node.height, layer: li });
        });
    });

    return positioned;
}

function chainToPoints(chain, positioned, _direction) {
    return chain.map(id => {
        const p = positioned.get(id);
        return { x: p.x, y: p.y };
    });
}

// Back-edges (the reverse of an A<->B pair) aren't part of the layered structure — route a
// simple curve that bows out to one side of the straight line between the two nodes, so it
// visually distinguishes itself from the structural edge instead of overlapping it.
function routeBackEdge(e, positioned, direction) {
    const a = positioned.get(e.source);
    const b = positioned.get(e.target);
    if (!a || !b) return [];
    const mid = { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 };
    const bow = direction === 'horizontal' ? { x: 0, y: 28 } : { x: 28, y: 0 };
    return [
        { x: a.x, y: a.y },
        { x: mid.x + bow.x, y: mid.y + bow.y },
        { x: b.x, y: b.y },
    ];
}
