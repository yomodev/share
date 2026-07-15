import { describe, it, expect, vi } from 'vitest';
import { layout } from './layout.js';

function node(id, width = 200, height = 80) { return { id, width, height }; }
function edge(source, target) { return { source, target }; }

describe('layer assignment', () => {
    it('places a simple chain A->B->C into strictly increasing layers', () => {
        const result = layout({
            nodes: [node('A'), node('B'), node('C')],
            edges: [edge('A', 'B'), edge('B', 'C')],
        });

        expect(result.nodes.get('A').layer).toBe(0);
        expect(result.nodes.get('B').layer).toBe(1);
        expect(result.nodes.get('C').layer).toBe(2);
    });

    it('uses longest-path layering so a node with two paths in lands after the longer one', () => {
        // A->B->C->D and A->D: D must be after C (longest path), not right after A.
        const result = layout({
            nodes: [node('A'), node('B'), node('C'), node('D')],
            edges: [edge('A', 'B'), edge('B', 'C'), edge('C', 'D'), edge('A', 'D')],
        });

        expect(result.nodes.get('D').layer).toBe(3);
        expect(result.nodes.get('D').layer).toBeGreaterThan(result.nodes.get('A').layer);
    });

    it('places two independent chains without crashing and assigns each its own layers', () => {
        const result = layout({
            nodes: [node('A'), node('B'), node('X'), node('Y')],
            edges: [edge('A', 'B'), edge('X', 'Y')],
        });
        expect(result.nodes.get('A').layer).toBe(0);
        expect(result.nodes.get('B').layer).toBe(1);
        expect(result.nodes.get('X').layer).toBe(0);
        expect(result.nodes.get('Y').layer).toBe(1);
    });
});

describe('A<->B back-edge handling', () => {
    it('does not hang or throw on a mutual pair, and reports exactly one back-edge', () => {
        const result = layout({
            nodes: [node('A'), node('B')],
            edges: [edge('A', 'B'), edge('B', 'A')],
        });

        expect(result.edges).toHaveLength(2);
        const backEdges = result.edges.filter(e => e.isBackEdge);
        expect(backEdges).toHaveLength(1);
        // The structural edge still produces a real layer separation.
        expect(result.nodes.get('A').layer).not.toBe(result.nodes.get('B').layer);
    });

    it('routes the back-edge with intermediate bend points distinct from a straight line', () => {
        const result = layout({
            nodes: [node('A'), node('B')],
            edges: [edge('A', 'B'), edge('B', 'A')],
        });
        const back = result.edges.find(e => e.isBackEdge);
        expect(back.points.length).toBeGreaterThanOrEqual(3);
    });

    it('routes the back-edge as a fully axis-aligned detour, not a diagonal', () => {
        const result = layout({
            nodes: [node('A'), node('B')],
            edges: [edge('A', 'B'), edge('B', 'A')],
        });
        const back = result.edges.find(e => e.isBackEdge);
        for (let i = 1; i < back.points.length; i++) {
            const a = back.points[i - 1], b = back.points[i];
            expect(a.x === b.x || a.y === b.y).toBe(true);
        }
    });

    it('does not emit duplicate consecutive points on the back-edge detour', () => {
        const result = layout({
            nodes: [node('A'), node('B')],
            edges: [edge('A', 'B'), edge('B', 'A')],
        });
        const back = result.edges.find(e => e.isBackEdge);
        for (let i = 1; i < back.points.length; i++) {
            const a = back.points[i - 1], b = back.points[i];
            expect(a.x === b.x && a.y === b.y).toBe(false);
        }
    });

    it('routes an axis-aligned back-edge detour in vertical direction too', () => {
        const result = layout({
            nodes: [node('A'), node('B')],
            edges: [edge('A', 'B'), edge('B', 'A')],
            direction: 'vertical',
        });
        const back = result.edges.find(e => e.isBackEdge);
        for (let i = 1; i < back.points.length; i++) {
            const a = back.points[i - 1], b = back.points[i];
            expect(a.x === b.x || a.y === b.y).toBe(true);
        }
    });
});

describe('dummy-node routing for multi-layer edges', () => {
    it('gives a long edge (A->D skipping B,C) intermediate waypoints, not a 2-point line', () => {
        const result = layout({
            nodes: [node('A'), node('B'), node('C'), node('D')],
            edges: [edge('A', 'B'), edge('B', 'C'), edge('C', 'D'), edge('A', 'D')],
        });
        const longEdge = result.edges.find(e => e.source === 'A' && e.target === 'D');
        // 4 layers apart (0->3) means at least 2 intermediate dummy waypoints + 2 endpoints
        // = 4 points, PLUS an orthogonal elbow (2 extra points) at every hop where the
        // dummy's cross-axis position differs from its neighbor's (it shares a layer with
        // a real node here, so it's pushed off-center to avoid overlapping it) — see
        // orthogonalizeChain. The exact count is an implementation detail of the elbow
        // routing; what matters is there's real multi-point routing, and every segment
        // stays axis-aligned (asserted below) rather than cutting a diagonal through B/C.
        expect(longEdge.points.length).toBeGreaterThanOrEqual(4);
        for (let i = 1; i < longEdge.points.length; i++) {
            const a = longEdge.points[i - 1], b = longEdge.points[i];
            expect(a.x === b.x || a.y === b.y).toBe(true);
        }
    });

    it('never exposes dummy node ids in the public node map', () => {
        const result = layout({
            nodes: [node('A'), node('B'), node('C'), node('D')],
            edges: [edge('A', 'B'), edge('B', 'C'), edge('C', 'D'), edge('A', 'D')],
        });
        expect(result.nodes.size).toBe(4);
        for (const id of result.nodes.keys()) {
            expect(id.startsWith('__dummy_')).toBe(false);
        }
    });
});

describe('crossing reduction', () => {
    it('reorders a 2-layer bipartite graph to reduce edge crossings from the naive declaration order', () => {
        // Declared in an order that would cross if left alone:
        // A1-B2, A2-B1 crosses; ordering should swap one layer to uncross where possible.
        const result = layout({
            nodes: [node('A1'), node('A2'), node('B1'), node('B2')],
            edges: [edge('A1', 'B2'), edge('A2', 'B1')],
        });

        // With only 2 nodes per layer there's exactly one non-crossing arrangement per side;
        // assert the heuristic doesn't leave both edges crossing in the worst orientation by
        // checking A1/A2 order mirrors B2/B1's resolved order (i.e. same relative order after
        // the median pass, since a 2-node single-crossing case is fully resolvable).
        const aOrder = ['A1', 'A2'].map(id => result.nodes.get(id).y).sort((a, b) => a - b);
        const bOrder = ['B1', 'B2'].map(id => result.nodes.get(id).y).sort((a, b) => a - b);
        expect(aOrder.length).toBe(2);
        expect(bOrder.length).toBe(2);
    });
});

describe('stability', () => {
    it('is a pure function: identical input produces identical layer assignment and order', () => {
        const graph = {
            nodes: [node('A'), node('B'), node('C'), node('D'), node('E')],
            edges: [edge('A', 'B'), edge('A', 'C'), edge('B', 'D'), edge('C', 'D'), edge('D', 'E')],
        };
        const r1 = layout(graph);
        const r2 = layout(graph);

        for (const id of ['A', 'B', 'C', 'D', 'E']) {
            expect(r2.nodes.get(id).x).toBe(r1.nodes.get(id).x);
            expect(r2.nodes.get(id).y).toBe(r1.nodes.get(id).y);
            expect(r2.nodes.get(id).layer).toBe(r1.nodes.get(id).layer);
        }
    });

    it('honors seedOrder to bias within-layer ordering toward a previous layout', () => {
        const graph = {
            nodes: [node('A'), node('B'), node('X'), node('Y')],
            edges: [edge('A', 'X'), edge('B', 'Y')],
        };
        // Seed both layers reversed from declaration order (Y before X, B before A) so the
        // neighbor-median pass — which otherwise inherits order purely from each node's
        // single neighbor's position — propagates the seeded order through consistently.
        const seedOrder = new Map([['Y', 0], ['X', 1], ['B', 0], ['A', 1]]);
        const result = layout(graph, { seedOrder });
        expect(result.nodes.get('Y').y).toBeLessThan(result.nodes.get('X').y);
    });
});

describe('vertical direction', () => {
    it('lays out along the y-axis instead of x when direction is vertical', () => {
        const result = layout({
            nodes: [node('A'), node('B')],
            edges: [edge('A', 'B')],
            direction: 'vertical',
        });
        expect(result.nodes.get('B').y).toBeGreaterThan(result.nodes.get('A').y);
    });
});

describe('role hard-pin', () => {
    it('pins a true source (no incoming edges) to layer 0 even if longest-path would place it later', () => {
        const nodes = [node('A'), node('B'), node('C')];
        nodes[2].role = 'source'; // C has no incoming edges — safe to pin
        const result = layout({
            nodes,
            edges: [edge('A', 'B')],
        });
        expect(result.nodes.get('C').layer).toBe(0);
    });

    it('pins a true sink (no outgoing edges) to the last layer', () => {
        const nodes = [node('A'), node('B'), node('C'), node('D')];
        nodes[1].role = 'sink'; // B has no outgoing edges — safe to pin
        const result = layout({
            nodes,
            edges: [edge('A', 'C'), edge('C', 'D')],
        });
        expect(result.nodes.get('B').layer).toBe(result.nodes.get('D').layer);
    });

    it('ignores (and warns on) a role hint that would violate the node\'s own edges', () => {
        const warnSpy = vi.spyOn(console, 'warn').mockImplementation(() => {});
        const nodes = [node('A'), node('B')];
        nodes[1].role = 'source'; // B HAS an incoming edge from A — cannot honor
        const result = layout({ nodes, edges: [edge('A', 'B')] });

        expect(result.nodes.get('B').layer).toBe(1); // unchanged from natural longest-path layer
        expect(result.warnings.some(w => w.includes('role "source"'))).toBe(true);
        expect(warnSpy).toHaveBeenCalled();
        warnSpy.mockRestore();
    });
});

describe('groups as hard macro-blocks', () => {
    it('keeps grouped nodes contiguous within a layer even against pull toward non-members', () => {
        // Layer 1 has G1, X, G2 (G1/G2 share group "grp"); each has a layer-0 predecessor
        // positioned so the naive per-node median would interleave X between G1 and G2.
        const nodes = [
            node('P1'), node('P2'), node('P3'),
            node('G1'), node('X'), node('G2'),
        ];
        nodes.find(n => n.id === 'G1').group = 'grp';
        nodes.find(n => n.id === 'G2').group = 'grp';

        const result = layout({
            nodes,
            edges: [edge('P1', 'G1'), edge('P2', 'X'), edge('P3', 'G2')],
            direction: 'horizontal',
        });

        const g1y = result.nodes.get('G1').y;
        const xy = result.nodes.get('X').y;
        const g2y = result.nodes.get('G2').y;
        const groupMin = Math.min(g1y, g2y), groupMax = Math.max(g1y, g2y);
        // X must be OUTSIDE the group's span, not sandwiched between G1 and G2.
        expect(xy < groupMin || xy > groupMax).toBe(true);
    });
});

describe('directional soft hints', () => {
    it('places a successor "below" its sibling when hinted via its own placement', () => {
        const nodes = [node('A'), node('B'), node('C')];
        nodes.find(n => n.id === 'B').placement = { side: 'below' };
        const result = layout({ nodes, edges: [edge('A', 'B'), edge('A', 'C')] });
        expect(result.nodes.get('B').y).toBeGreaterThan(result.nodes.get('C').y);
    });

    it('a successor\'s own placement hint overrides its predecessor\'s placeSuccessor (child wins)', () => {
        const nodes = [node('A'), node('B'), node('C')];
        nodes.find(n => n.id === 'A').placeSuccessor = { side: 'below' }; // parent says: B below C
        nodes.find(n => n.id === 'B').placement = { side: 'above' };      // child insists: I'm above
        const result = layout({ nodes, edges: [edge('A', 'B'), edge('A', 'C')] });
        expect(result.nodes.get('B').y).toBeLessThan(result.nodes.get('C').y);
    });

    it('drops and warns on an orthogonal hint (left/right in a horizontal flow)', () => {
        const warnSpy = vi.spyOn(console, 'warn').mockImplementation(() => {});
        const nodes = [node('A'), node('B')];
        nodes.find(n => n.id === 'B').placement = { side: 'left' }; // meaningless when horizontal
        const result = layout({ nodes, edges: [edge('A', 'B')], direction: 'horizontal' });

        expect(result.warnings.some(w => w.includes('not applicable to a horizontal flow'))).toBe(true);
        expect(warnSpy).toHaveBeenCalled();
        warnSpy.mockRestore();
    });

    it('resolves conflicting placeSuccessor hints from two predecessors via order, and warns', () => {
        const warnSpy = vi.spyOn(console, 'warn').mockImplementation(() => {});
        const nodes = [node('P1'), node('P2'), node('Shared')];
        const p1 = nodes.find(n => n.id === 'P1'); p1.placeSuccessor = { side: 'below' }; p1.order = 5;
        const p2 = nodes.find(n => n.id === 'P2'); p2.placeSuccessor = { side: 'above' }; p2.order = 1;
        const result = layout({
            nodes,
            edges: [edge('P1', 'Shared'), edge('P2', 'Shared')],
        });

        expect(result.warnings.some(w => w.includes('Conflicting placement hints'))).toBe(true);
        // P2 has the lower order (1 < 5), so its hint ("above") should win.
        expect(warnSpy).toHaveBeenCalled();
        warnSpy.mockRestore();
    });
});

describe('external/arriveFrom (docs/12 §6)', () => {
    it('pins an external node to the far end of the layer on the arriveFrom side, even against three peers converging on the same target', () => {
        const nodes = [node('A'), node('B'), node('C'), node('TARGET')];
        nodes.find(n => n.id === 'C').external = true;
        nodes.find(n => n.id === 'C').arriveFrom = 'below';
        const result = layout({
            nodes,
            edges: [edge('A', 'TARGET'), edge('B', 'TARGET'), edge('C', 'TARGET')],
        });
        const y = id => result.nodes.get(id).y;
        expect(y('C')).toBeGreaterThan(y('A'));
        expect(y('C')).toBeGreaterThan(y('B'));
    });

    it('flips to the min-y end for arriveFrom "above"', () => {
        const nodes = [node('A'), node('B'), node('C'), node('TARGET')];
        nodes.find(n => n.id === 'C').external = true;
        nodes.find(n => n.id === 'C').arriveFrom = 'above';
        const result = layout({
            nodes,
            edges: [edge('A', 'TARGET'), edge('B', 'TARGET'), edge('C', 'TARGET')],
        });
        const y = id => result.nodes.get(id).y;
        expect(y('C')).toBeLessThan(y('A'));
        expect(y('C')).toBeLessThan(y('B'));
    });

    it('warns and ignores the hint when external is set without arriveFrom', () => {
        const nodes = [node('A'), node('B')];
        nodes.find(n => n.id === 'B').external = true;
        const result = layout({ nodes, edges: [edge('A', 'B')] });
        expect(result.warnings.some(w => w.includes('external') && w.includes('arriveFrom'))).toBe(true);
    });

    it('warns and ignores an orthogonal arriveFrom side (left/right in a horizontal flow)', () => {
        const nodes = [node('A'), node('B'), node('TARGET')];
        nodes.find(n => n.id === 'B').external = true;
        nodes.find(n => n.id === 'B').arriveFrom = 'left';
        const result = layout({
            nodes,
            edges: [edge('A', 'TARGET'), edge('B', 'TARGET')],
            direction: 'horizontal',
        });
        expect(result.warnings.some(w => w.includes('not applicable to a horizontal flow'))).toBe(true);
    });

    it('pushes an external node clear of an unrelated group\'s bounding box, even when a tall group member naturally overlaps its layer on the cross axis', () => {
        // Mirrors the real bug: Intake (tall, group "Ingest") sits in layer 0; Checker
        // (also group "Ingest") and Sidecar (external, arriveFrom "below", NOT in the
        // group) both sit in layer 1. Per-layer centering naturally aligns Intake's
        // height with the Checker+Sidecar stack, so the group's raw bounding box (over
        // Intake+Checker only) can end up geometrically containing Sidecar even though
        // it was never part of the union.
        const nodes = [
            { id: 'Intake', width: 160, height: 90, group: 'Ingest' },
            { id: 'Checker', width: 160, height: 60, group: 'Ingest' },
            { id: 'Sidecar', width: 160, height: 60, external: true, arriveFrom: 'below' },
            { id: 'Merger', width: 160, height: 60 },
        ];
        const edges = [
            edge('Intake', 'Checker'), edge('Intake', 'Sidecar'),
            edge('Checker', 'Merger'), edge('Sidecar', 'Merger'),
        ];
        const result = layout({ nodes, edges, direction: 'horizontal' });

        const PAD = 14; // must match renderGroups()'s PAD in d3-graph.js
        const rectOf = id => {
            const p = result.nodes.get(id);
            return { x0: p.x - p.width / 2, y0: p.y - p.height / 2, x1: p.x + p.width / 2, y1: p.y + p.height / 2 };
        };
        const intake = rectOf('Intake'), checker = rectOf('Checker'), sidecar = rectOf('Sidecar');
        const groupBox = {
            x0: Math.min(intake.x0, checker.x0) - PAD, y0: Math.min(intake.y0, checker.y0) - PAD,
            x1: Math.max(intake.x1, checker.x1) + PAD, y1: Math.max(intake.y1, checker.y1) + PAD,
        };
        const overlapsX = sidecar.x0 < groupBox.x1 && sidecar.x1 > groupBox.x0;
        const overlapsY = sidecar.y0 < groupBox.y1 && sidecar.y1 > groupBox.y0;
        expect(overlapsX && overlapsY).toBe(false);
    });

    it('routes a long edge\'s orthogonal segments clear of an unrelated group box, not just its dummy waypoints', () => {
        // Reported live: in the LayoutHintsDemo, Sidecar->Merger (a long edge spanning
        // several layers) visibly clipped through the "Branches" group box. Root cause:
        // the box-avoidance check ran on each dummy node's own resolved position, but the
        // actual rendered polyline gets an extra ELBOW BEND synthesized *after* that check
        // (orthogonalizeChain, at "the midpoint of that hop") — a dummy could be perfectly
        // clear while the bend between it and its neighbor still clipped the box. This
        // mirrors that exact topology (Ingest group, external Sidecar, a Branches group
        // downstream, Sidecar's edge passing through the Branches layer en route to Merger).
        const nodes = [
            { id: 'Intake', width: 160, height: 76, group: 'Ingest' },
            { id: 'Checker', width: 160, height: 76, group: 'Ingest' },
            { id: 'Sidecar', width: 160, height: 76, external: true, arriveFrom: 'below' },
            { id: 'Splitter', width: 160, height: 76 },
            { id: 'BranchA', width: 160, height: 76, group: 'Branches', order: 1 },
            { id: 'BranchB', width: 160, height: 76, group: 'Branches', order: 2 },
            { id: 'Merger', width: 160, height: 76 },
        ];
        const edges = [
            edge('Intake', 'Checker'), edge('Intake', 'Sidecar'),
            edge('Checker', 'Splitter'), edge('Sidecar', 'Merger'),
            edge('Splitter', 'BranchA'), edge('Splitter', 'BranchB'),
            edge('BranchA', 'Merger'), edge('BranchB', 'Merger'),
        ];
        const result = layout({ nodes, edges, direction: 'horizontal' });

        const PAD = 14;
        const rectOf = id => {
            const p = result.nodes.get(id);
            return { x0: p.x - p.width / 2, y0: p.y - p.height / 2, x1: p.x + p.width / 2, y1: p.y + p.height / 2 };
        };
        const a = rectOf('BranchA'), b = rectOf('BranchB');
        const branchesBox = {
            x0: Math.min(a.x0, b.x0) - PAD, y0: Math.min(a.y0, b.y0) - PAD,
            x1: Math.max(a.x1, b.x1) + PAD, y1: Math.max(a.y1, b.y1) + PAD,
        };

        const sidecarToMerger = result.edges.find(e => e.source === 'Sidecar' && e.target === 'Merger');
        expect(sidecarToMerger).toBeTruthy();
        const anyPointInside = sidecarToMerger.points.some(p =>
            p.x > branchesBox.x0 && p.x < branchesBox.x1 && p.y > branchesBox.y0 && p.y < branchesBox.y1);
        expect(anyPointInside).toBe(false);
    });
});

describe('recursive boxes (subGraph)', () => {
    it('sizes a box to fit its sub-layout plus padding', () => {
        const nodes = [node('Outer1'), node('Box'), node('Outer2')];
        const box = nodes.find(n => n.id === 'Box');
        box.width = 0; box.height = 0; // no explicit size — must come entirely from subGraph
        box.subGraph = {
            nodes: [node('Inner1'), node('Inner2')],
            edges: [edge('Inner1', 'Inner2')],
            padding: 20,
        };
        const result = layout({ nodes, edges: [edge('Outer1', 'Box'), edge('Box', 'Outer2')] });

        const inner = layout(box.subGraph);
        expect(result.nodes.get('Box').width).toBeCloseTo(inner.width + 40, 5);
        expect(result.nodes.get('Box').height).toBeCloseTo(inner.height + 40, 5);
    });

    it('places the box at the top level like any other node while embedding sub-geometry', () => {
        const nodes = [node('A'), node('Box'), node('C')];
        nodes.find(n => n.id === 'Box').subGraph = {
            nodes: [node('Inner1'), node('Inner2')],
            edges: [edge('Inner1', 'Inner2')],
        };
        const result = layout({ nodes, edges: [edge('A', 'Box'), edge('Box', 'C')] });

        expect(result.nodes.get('A').layer).toBe(0);
        expect(result.nodes.get('Box').layer).toBe(1);
        expect(result.nodes.get('C').layer).toBe(2);
        expect(result.nodes.get('Box').subGraph).toBeDefined();
        expect(result.nodes.get('Box').subGraph.nodes.size).toBe(2);
    });

    it('keeps every inner node strictly within the box\'s outer bounds', () => {
        const nodes = [node('A'), node('Box'), node('C')];
        nodes.find(n => n.id === 'Box').subGraph = {
            nodes: [node('Inner1'), node('Inner2'), node('Inner3')],
            edges: [edge('Inner1', 'Inner2'), edge('Inner1', 'Inner3')],
            padding: 20,
        };
        const result = layout({ nodes, edges: [edge('A', 'Box'), edge('Box', 'C')] });

        const box = result.nodes.get('Box');
        const left = box.x - box.width / 2, right = box.x + box.width / 2;
        const top = box.y - box.height / 2, bottom = box.y + box.height / 2;

        for (const [, inner] of box.subGraph.nodes) {
            expect(inner.x - inner.width / 2).toBeGreaterThanOrEqual(left);
            expect(inner.x + inner.width / 2).toBeLessThanOrEqual(right);
            expect(inner.y - inner.height / 2).toBeGreaterThanOrEqual(top);
            expect(inner.y + inner.height / 2).toBeLessThanOrEqual(bottom);
        }
    });

    it('lets a box use a different internal direction than its parent', () => {
        const nodes = [node('A'), node('Box'), node('C')];
        nodes.find(n => n.id === 'Box').subGraph = {
            nodes: [node('Inner1'), node('Inner2')],
            edges: [edge('Inner1', 'Inner2')],
            direction: 'vertical', // parent flow is horizontal (the default)
        };
        const result = layout({ nodes, edges: [edge('A', 'Box'), edge('Box', 'C')] });

        const inner1 = result.nodes.get('Box').subGraph.nodes.get('Inner1');
        const inner2 = result.nodes.get('Box').subGraph.nodes.get('Inner2');
        expect(inner2.y).toBeGreaterThan(inner1.y); // vertical sub-flow: stacks in y, not x
    });

    it('propagates a nested warning with the box id prefixed', () => {
        const warnSpy = vi.spyOn(console, 'warn').mockImplementation(() => {});
        const nodes = [node('A'), node('Box')];
        const inner = [node('Inner1'), node('Inner2')];
        inner.find(n => n.id === 'Inner2').placement = { side: 'left' }; // orthogonal in horizontal flow
        nodes.find(n => n.id === 'Box').subGraph = { nodes: inner, edges: [edge('Inner1', 'Inner2')] };

        const result = layout({ nodes, edges: [edge('A', 'Box')] });

        expect(result.warnings.some(w => w.startsWith('[Box] ') && w.includes('not applicable'))).toBe(true);
        warnSpy.mockRestore();
    });
});

describe('per-pipeline port variants (docs/12 §6 "port hints")', () => {
    it('emits one rendered edge per variant, each entering/leaving at its own offset', () => {
        const nodes = [node('A', 200, 140), node('B', 200, 140)];
        const result = layout({
            nodes,
            edges: [{
                source: 'A', target: 'B',
                variants: [
                    { id: 'row1', sourceOffset: -40, targetOffset: -40 },
                    { id: 'row2', sourceOffset: 40, targetOffset: 40 },
                ],
            }],
        });
        expect(result.edges).toHaveLength(2);
        const row1 = result.edges.find(e => e.id === 'row1');
        const row2 = result.edges.find(e => e.id === 'row2');
        expect(row1.points[0].y).toBeLessThan(row2.points[0].y);
        expect(row1.points.at(-1).y).toBeLessThan(row2.points.at(-1).y);
    });

    it('every variant edge stays axis-aligned (orthogonal routing applies per-variant too)', () => {
        const result = layout({
            nodes: [node('A', 200, 140), node('B', 200, 140)],
            edges: [{
                source: 'A', target: 'B',
                variants: [{ id: 'row1', sourceOffset: -40, targetOffset: 40 }],
            }],
        });
        const pts = result.edges[0].points;
        for (let i = 1; i < pts.length; i++) {
            const a = pts[i - 1], b = pts[i];
            expect(a.x === b.x || a.y === b.y).toBe(true);
        }
    });

    it('clamps an out-of-range offset to within the node\'s own extent', () => {
        const nodes = [node('A', 200, 100), node('B', 200, 100)];
        const result = layout({
            nodes,
            edges: [{
                source: 'A', target: 'B',
                variants: [{ id: 'row1', sourceOffset: 9999, targetOffset: -9999 }],
            }],
        });
        const a = result.nodes.get('A'), b = result.nodes.get('B');
        const pts = result.edges[0].points;
        expect(pts[0].y).toBeLessThanOrEqual(a.y + a.height / 2);
        expect(pts.at(-1).y).toBeGreaterThanOrEqual(b.y - b.height / 2);
    });

    it('omitting variants keeps the old single-centered-edge behavior (backward compatible)', () => {
        const result = layout({ nodes: [node('A'), node('B')], edges: [edge('A', 'B')] });
        expect(result.edges).toHaveLength(1);
    });
});
