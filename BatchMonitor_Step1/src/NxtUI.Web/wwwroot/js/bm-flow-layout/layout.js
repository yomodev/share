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
// median ordering + dummy-node routing + coordinate assignment.
//
// Stage 2 scope: hard constraints (role pinning, groups as contiguous macro-blocks) and
// soft constraints (directional left/right/above/below placement hints, child-overrides-
// parent precedence, order/priority tiebreak, warnings for anything unsatisfiable — see
// docs/12 §3 "Constraint precedence").
//
// Stage 2b scope (this revision): the RECURSIVE BOX (docs/12 §2) — a node may declare a
// `subGraph`, laid out with its own (possibly different) direction BEFORE the parent, so
// its resulting size becomes that node's box size in the parent layout. After the parent
// positions the box, the sub-layout's own node/edge geometry is translated into the
// parent's coordinate space and attached to the parent node's output record. This is the
// single primitive behind three different asks in docs/12: a group as a rigid macro-block,
// a per-service orientation change ("go vertical here"), and a nested run's expanded view.
// external/arriveFrom and per-edge port hints are still not implemented.

/**
 * @typedef {Object} LayoutNode
 * @property {string} id
 * @property {number} width
 * @property {number} height
 * @property {'source'|'sink'} [role] - hard: pin to the first/last layer (only when the
 *        node genuinely has no incoming/outgoing structural edges — otherwise ignored
 *        with a warning, since forcing it would violate the DAG's own edges).
 * @property {string} [group] - hard: nodes sharing a group are kept as a contiguous run
 *        within each layer they appear in — no non-member node can be ordered between them.
 * @property {number} [order] - tiebreak priority (lower = earlier/first); also used to
 *        resolve conflicting directional hints from multiple predecessors.
 * @property {{side:'left'|'right'|'above'|'below'}} [placement] - soft: this node's own
 *        preferred placement relative to its predecessor. Authored by the node itself;
 *        wins over any `placeSuccessor` hint offered by a predecessor (child-overrides-
 *        parent — see docs/12 §3).
 * @property {{side:'left'|'right'|'above'|'below'}} [placeSuccessor] - soft: where this
 *        node would like its successor(s) placed. Loses to the successor's own
 *        `placement` if it declares one.
 * @property {boolean} [external] - soft, Stage 5: pull this node to the extreme end (not
 *        just biased) of its layer's cross axis, per `arriveFrom`, rather than letting the
 *        ordinary median heuristic interleave it among its peers — "place me outside the
 *        cluster of siblings converging on the same target." Ignored (with a warning) if
 *        set without `arriveFrom`, or if `arriveFrom` isn't valid for the flow direction
 *        (same left/right/above/below <-> horizontal/vertical rule as `placement`).
 * @property {'left'|'right'|'above'|'below'} [arriveFrom] - which side of the layer this
 *        `external` node is pinned to; its incoming edge visually approaches from that
 *        side as a natural side effect of the elbow routing (see chainToPoints), not a
 *        separate routing rule. Meaningless without `external: true`.
 * @property {LayoutInput} [subGraph] - Stage 2b: makes this node a recursive box. Its own
 *        `width`/`height` are ignored (ADDED to, actually never set) — the box is sized to
 *        fit the sub-layout plus `subGraph.padding` (default DEFAULT_SUBGRAPH_PADDING) on
 *        every side. `subGraph.direction` defaults to the PARENT's direction (same-
 *        direction flow embedded in a box) but may differ (a vertical sub-flow inside a
 *        horizontal parent, etc.).
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
const DEFAULT_SUBGRAPH_PADDING = 24;
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
 *   nodes: Map<string, {x:number, y:number, width:number, height:number, layer:number,
 *     subGraph?: {nodes: Map<string,object>, edges: Array<object>, width:number, height:number}}>,
 *   edges: Array<{id:string, source:string, target:string, points:{x:number,y:number}[], isBackEdge:boolean}>,
 *   width: number, height: number,
 *   warnings: string[],
 * }}
 * A node's `subGraph` (Stage 2b), when present, is already translated into THIS layout's
 * coordinate space — render it directly, no further offsetting needed.
 */
export function layout(input, opts = {}) {
    const direction = input.direction === 'vertical' ? 'vertical' : 'horizontal';
    const layerSpacing = input.layerSpacing ?? DEFAULT_LAYER_SPACING;
    const nodeSpacing = input.nodeSpacing ?? DEFAULT_NODE_SPACING;
    const warnings = [];

    // Stage 2b recursive boxes: resolve every node's subGraph FIRST (bottom-up — a box's
    // size must be known before ITS parent can be laid out), synthesizing width/height for
    // that node from the sub-layout's own result. The real input.nodes objects are never
    // mutated; a shallow-cloned "effective" node (with width/height filled in) is what the
    // rest of this function actually lays out.
    const subResults = new Map(); // node id -> { result, padding }
    const effectiveNodes = input.nodes.map(n => {
        if (!n.subGraph) return n;
        const padding = n.subGraph.padding ?? DEFAULT_SUBGRAPH_PADDING;
        const subDirection = n.subGraph.direction ?? direction;
        const subResult = layout(
            { ...n.subGraph, direction: subDirection },
            { seedOrder: opts.subSeedOrder?.get(n.id) });
        subResults.set(n.id, { result: subResult, padding });
        for (const w of subResult.warnings) warnings.push(`[${n.id}] ${w}`);
        return {
            ...n,
            width: Math.max(n.width || 0, subResult.width + padding * 2),
            height: Math.max(n.height || 0, subResult.height + padding * 2),
        };
    });

    const nodesById = new Map(effectiveNodes.map(n => [n.id, n]));

    const { structuralEdges, backEdges } = splitBackEdges(input.edges, nodesById);

    const layerOf = assignLayers(effectiveNodes, structuralEdges);
    pinRoles(effectiveNodes, layerOf, structuralEdges, warnings);

    const { layers, dummies, edgeChains } = buildDummyChains(
        effectiveNodes, structuralEdges, layerOf, layerSpacing, nodeSpacing);

    orderLayers(layers, edgeChains, nodesById, direction, opts.seedOrder, warnings);

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

    // The cross axis is centered around 0 independently PER LAYER (see assignCoordinates),
    // so nodes routinely have negative coordinates — the true bounding box needs both the
    // min and max extent, not just the positive side, or it silently underestimates whenever
    // a layer isn't one-sided (this is what recursive boxes' "stay within bounds" invariant
    // depends on: box.width/height must be the box's REAL full extent).
    let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
    for (const p of positioned.values()) {
        minX = Math.min(minX, p.x - p.width / 2);
        maxX = Math.max(maxX, p.x + p.width / 2);
        minY = Math.min(minY, p.y - p.height / 2);
        maxY = Math.max(maxY, p.y + p.height / 2);
    }
    if (!Number.isFinite(minX)) { minX = maxX = minY = maxY = 0; }

    // Only expose real (non-dummy) nodes to callers.
    const realNodes = new Map();
    for (const n of effectiveNodes) realNodes.set(n.id, positioned.get(n.id));

    // Stage 2b: translate each resolved subGraph's own geometry into this box's final
    // position and attach it — the sub-layout was computed in its own (0,0)-centered
    // coordinate space, so the offset is simply this box's final center minus its own
    // half-size plus the padding.
    for (const [id, { result: sub, padding }] of subResults) {
        const box = realNodes.get(id);

        // The sub-layout's own coordinate system isn't necessarily centered/symmetric (the
        // cross axis centers PER LAYER independently — see assignCoordinates), so the
        // translation offset must anchor on its actual min bound, not assume symmetry with
        // width/2. This is exactly the bug the "stays within bounds" test caught.
        let subMinX = Infinity, subMinY = Infinity;
        for (const [, sp] of sub.nodes) {
            subMinX = Math.min(subMinX, sp.x - sp.width / 2);
            subMinY = Math.min(subMinY, sp.y - sp.height / 2);
        }
        if (!Number.isFinite(subMinX)) { subMinX = subMinY = 0; }

        const offsetX = box.x - box.width / 2 + padding - subMinX;
        const offsetY = box.y - box.height / 2 + padding - subMinY;

        const subNodes = new Map();
        for (const [sid, sp] of sub.nodes) {
            subNodes.set(sid, { ...sp, x: sp.x + offsetX, y: sp.y + offsetY });
        }
        const subEdges = sub.edges.map(e => ({
            ...e, points: e.points.map(p => ({ x: p.x + offsetX, y: p.y + offsetY })),
        }));
        box.subGraph = { nodes: subNodes, edges: subEdges, width: sub.width, height: sub.height };
    }

    for (const w of warnings) console.warn(`bm-flow-layout: ${w}`);

    return {
        nodes: realNodes, edges: edgesOut,
        width: (maxX - minX) + layerSpacing / 2, height: (maxY - minY) + nodeSpacing,
        warnings,
    };
}

// ── Hard constraint: role pinning ──────────────────────────────────────────────────────
// Only applied when it can't violate the DAG's own edges: a "source" is pinned to layer 0
// only if it truly has no incoming structural edge; a "sink" to the last layer only if it
// truly has no outgoing one. Otherwise the hint is dropped with a warning rather than
// silently producing an inconsistent layer assignment.
function pinRoles(nodes, layerOf, structuralEdges, warnings) {
    const hasIncoming = new Set(structuralEdges.map(e => e.target));
    const hasOutgoing = new Set(structuralEdges.map(e => e.source));
    const maxLayer = Math.max(0, ...[...layerOf.values()]);

    for (const n of nodes) {
        if (n.role === 'source') {
            if (hasIncoming.has(n.id)) {
                warnings.push(`Node "${n.id}" has role "source" but also has incoming edges — role hint ignored.`);
                continue;
            }
            layerOf.set(n.id, 0);
        } else if (n.role === 'sink') {
            if (hasOutgoing.has(n.id)) {
                warnings.push(`Node "${n.id}" has role "sink" but also has outgoing edges — role hint ignored.`);
                continue;
            }
            layerOf.set(n.id, maxLayer);
        }
    }
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
//
// Stage 2 additions, applied on top of the same median pass:
//   - HARD groups: nodes sharing a `group` are sorted as one clustered unit (by the
//     group's average median) so no non-member can land between them within a layer.
//   - SOFT directional hints: a node with an effective placement hint relative to a
//     predecessor in the adjacent layer gets its median pulled strongly toward that side,
//     rather than the plain neighbor median — see collectDirectionalHints().
function orderLayers(layers, edgeChains, nodesById, direction, seedOrder, warnings) {
    // Initial order: seedOrder bias (stability, Stage 3) falls back to declaration order.
    for (const layer of layers) {
        if (seedOrder) {
            layer.sort((a, b) => (seedOrder.get(a.id) ?? 1e9) - (seedOrder.get(b.id) ?? 1e9));
        }
    }

    const neighborsOf = buildNeighborIndex(edgeChains);
    const hints = collectDirectionalHints(edgeChains, nodesById, direction, warnings);
    const externalSides = collectExternalSides(nodesById, direction, warnings);
    const indexInLayer = () => {
        const idx = new Map();
        layers.forEach((layer, li) => layer.forEach((n, i) => idx.set(n.id, { li, i })));
        return idx;
    };

    const HINT_BIAS = 1000; // large enough to dominate same-layer neighbor medians
    // Bigger than HINT_BIAS: `external` is an absolute "stick to this end of the layer"
    // pin, not a relative nudge toward a predecessor — it must win even when a hint or
    // neighbor median would otherwise pull the node back toward the middle.
    const EXTERNAL_BIAS = 1e6;

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
                const side = externalSides.get(n.id);
                if (side) {
                    const towardHigher = side === 'below' || side === 'right';
                    return { node: n, median: towardHigher ? EXTERNAL_BIAS : -EXTERNAL_BIAS };
                }
                const hint = hints.get(n.id);
                if (hint && positions.get(hint.pred)?.li === adjLi) {
                    const predPos = positions.get(hint.pred).i;
                    const towardHigher = hint.side === 'below' || hint.side === 'right';
                    return { node: n, median: predPos + (towardHigher ? HINT_BIAS : -HINT_BIAS) };
                }
                const neigh = (neighborsOf.get(n.id) ?? []).filter(id => positions.get(id)?.li === adjLi);
                if (neigh.length === 0) return { node: n, median: positions.get(n.id).i };
                const idxs = neigh.map(id => positions.get(id).i).sort((a, b) => a - b);
                const mid = Math.floor(idxs.length / 2);
                const median = idxs.length % 2 === 1
                    ? idxs[mid]
                    : (idxs[mid - 1] + idxs[mid]) / 2;
                return { node: n, median };
            });

            layers[li] = clusterByGroup(medians, nodesById);
            layers[li].forEach((n, i) => positions.set(n.id, { li, i }));
        }
    }
}

// HARD constraint: sorts a layer's (node, median) entries so that every group's members
// end up contiguous — nothing outside the group can be ordered between them. A group's
// position is driven by the average median of its members; members within the group are
// then ordered among themselves by their own median (falls back to `order`/declaration).
function clusterByGroup(medianEntries, nodesById) {
    const groupOf = id => nodesById.get(id)?.group;

    const clusters = new Map(); // group name -> entries[]
    const singles = [];
    for (const entry of medianEntries) {
        const g = groupOf(entry.node.id);
        if (!g) { singles.push({ key: entry.median, items: [entry] }); continue; }
        if (!clusters.has(g)) clusters.set(g, []);
        clusters.get(g).push(entry);
    }
    const grouped = [...clusters.entries()].map(([g, items]) => ({
        key: items.reduce((sum, e) => sum + e.median, 0) / items.length,
        items,
    }));

    const units = [...singles, ...grouped].sort((a, b) => a.key - b.key);

    const result = [];
    for (const unit of units) {
        const ordered = unit.items.slice().sort((a, b) =>
            a.median - b.median || (nodesById.get(a.node.id)?.order ?? 0) - (nodesById.get(b.node.id)?.order ?? 0));
        for (const e of ordered) result.push(e.node);
    }
    return result;
}

// ── Soft constraint: directional placement hints ───────────────────────────────────────
// Resolves, for every direct (non-dummy) structural edge pred->succ, the "effective" hint
// governing succ's placement relative to pred: succ's own `placement` always wins over
// pred's `placeSuccessor` (child-overrides-parent, docs/12 §3). When multiple predecessors
// offer conflicting placeSuccessor hints for the same succ (and it has no self hint), the
// predecessor with the lower `order` wins; the conflict is logged.
//
// Only applies to edges that span exactly one layer (no intervening dummy nodes) — a hint
// about "place my successor to the left" is a direct, local relationship; long edges have
// no single adjacent-layer predecessor to bias against, so they're left to the neighbor
// median alone (documented Stage 2 limitation, not silently wrong).
function collectDirectionalHints(edgeChains, nodesById, direction, warnings) {
    const chosen = new Map(); // succ id -> { side, pred, authoredBySelf }

    for (const chain of edgeChains.values()) {
        if (chain.length !== 2) continue;
        const [pred, succ] = chain;
        const predNode = nodesById.get(pred);
        const succNode = nodesById.get(succ);
        if (!predNode || !succNode) continue;

        const selfHint = succNode.placement;
        const parentHint = predNode.placeSuccessor;
        const rawHint = selfHint ?? parentHint;
        if (!rawHint) continue;

        const side = normalizeSide(rawHint.side, direction, warnings, succ);
        if (side == null) continue;

        const existing = chosen.get(succ);
        if (!existing) {
            chosen.set(succ, { side, pred, authoredBySelf: !!selfHint });
        } else if (selfHint && !existing.authoredBySelf) {
            chosen.set(succ, { side, pred, authoredBySelf: true });
        } else if (!selfHint && !existing.authoredBySelf && existing.side !== side) {
            const orderExisting = nodesById.get(existing.pred)?.order ?? 0;
            const orderNew = predNode.order ?? 0;
            const winner = orderNew < orderExisting ? { side, pred } : existing;
            warnings.push(
                `Conflicting placement hints for "${succ}" from predecessors "${existing.pred}" ` +
                `(${existing.side}) and "${pred}" (${side}) — using "${winner.pred}" (lower order wins).`);
            chosen.set(succ, { ...winner, authoredBySelf: false });
        }
        // else: two self hints on the same node is structurally impossible (one `placement`
        // per node), and a later parent hint never overrides an already-chosen self hint.
    }
    return chosen;
}

// ── Soft constraint: external/arriveFrom (docs/12 §6) ──────────────────────────────────
// "Place me outside the cluster of peers on the same target; my incoming arrow should come
// from this side." Unlike `placement`/`placeSuccessor` (a nudge relative to ONE specific
// predecessor, only usable on adjacent-layer edges), `external` is an absolute pin to
// whichever end of the node's OWN layer `arriveFrom` names — applies regardless of how many
// predecessors the node has, how many layers away they are, or whether it has any
// predecessor at all. Resolved once per layout (not per ordering pass, since it depends
// only on the node's own declared hint, never on neighbor positions).
function collectExternalSides(nodesById, direction, warnings) {
    const sides = new Map();
    for (const [id, node] of nodesById) {
        if (!node.external) continue;
        if (!node.arriveFrom) {
            warnings.push(`Node "${id}" has "external" set but no "arriveFrom" — hint ignored.`);
            continue;
        }
        const side = normalizeSide(node.arriveFrom, direction, warnings, id);
        if (side != null) sides.set(id, side);
    }
    return sides;
}

// A hint is only meaningful on the CROSS axis: "above/below" bias order within a
// horizontal flow's layers; "left/right" bias order within a vertical flow's layers. The
// orthogonal pair has no meaning (the successor's position along the FLOW axis is already
// fixed by its layer) — dropped with a warning rather than silently ignored.
function normalizeSide(side, direction, warnings, nodeId) {
    const validForHorizontal = side === 'above' || side === 'below';
    const validForVertical = side === 'left' || side === 'right';
    if (direction === 'horizontal' && !validForHorizontal) {
        warnings.push(`Directional hint "${side}" on "${nodeId}" is not applicable to a horizontal flow — ignored.`);
        return null;
    }
    if (direction === 'vertical' && !validForVertical) {
        warnings.push(`Directional hint "${side}" on "${nodeId}" is not applicable to a vertical flow — ignored.`);
        return null;
    }
    return side;
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

// Real orthogonal routing (Stage 5): earlier revisions returned raw node/dummy CENTERS,
// which is only axis-aligned when every waypoint in the chain happens to share the same
// cross-axis coordinate — anything else (the overwhelmingly common case) drew a diagonal
// line straight through node cards. Two things fix that, matching what ELK's own
// ORTHOGONAL edge router produces:
//   1. clip the first/last point to the actual node BORDER (not center) on the flow axis,
//      so the edge visibly starts/ends at the card edge instead of poking through it.
//   2. insert an elbow (H-then-V, or V-then-H) between every consecutive pair of waypoints
//      whose cross-axis coordinate differs, bending at the horizontal (resp. vertical)
//      midpoint of that hop — see orthogonalizeChain.
// No per-pipeline ports yet (docs/12 §6/"port hints" — still unbuilt): every edge enters/
// exits at its node's cross-axis CENTER, not a specific pipeline row's y. That's the one
// remaining visual gap vs. ELK's FIXED_POS mode.
function chainToPoints(chain, positioned, direction) {
    const raw = chain.map(id => {
        const p = positioned.get(id);
        return { x: p.x, y: p.y };
    });
    if (raw.length >= 2) {
        const first = positioned.get(chain[0]);
        const last = positioned.get(chain[chain.length - 1]);
        if (direction === 'horizontal') {
            raw[0] = { x: first.x + first.width / 2, y: first.y };
            raw[raw.length - 1] = { x: last.x - last.width / 2, y: last.y };
        } else {
            raw[0] = { x: first.x, y: first.y + first.height / 2 };
            raw[raw.length - 1] = { x: last.x, y: last.y - last.height / 2 };
        }
    }
    return orthogonalizeChain(raw, direction);
}

// Inserts an elbow between each consecutive pair of points whose cross-axis coordinate
// differs, so every segment of the resulting polyline is axis-aligned (pure horizontal or
// pure vertical) — required for roundedOrthPath's corner-rounding to look right, and for
// the edge to read as "orthogonal" at all. The bend sits at the midpoint of the FLOW axis
// between the two points (e.g. halfway between a horizontal hop's two x values), which is
// also where ELK conventionally bends multi-segment orthogonal edges.
function orthogonalizeChain(points, direction) {
    if (points.length < 2) return points;
    const out = [points[0]];
    for (let i = 1; i < points.length; i++) {
        const a = out[out.length - 1];
        const b = points[i];
        if (direction === 'horizontal') {
            if (a.y !== b.y) {
                const midX = (a.x + b.x) / 2;
                out.push({ x: midX, y: a.y }, { x: midX, y: b.y });
            }
        } else {
            if (a.x !== b.x) {
                const midY = (a.y + b.y) / 2;
                out.push({ x: a.x, y: midY }, { x: b.x, y: midY });
            }
        }
        out.push(b);
    }
    return out;
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
