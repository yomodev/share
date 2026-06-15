// Batch Monitor — D3 flow graph edge animation
//
// Two animation modes per edge, chosen adaptively based on observed
// message throughput (messages/sec over a short rolling window):
//
//  - LOW throughput:  discrete particle dots travel along the edge path,
//                      one roughly per message.
//  - HIGH throughput: the edge itself pulses (stroke width / opacity)
//                      proportionally to recent throughput, avoiding a
//                      "particle blizzard" at scale.
//
// All functions operate on plain DOM/SVG elements and D3 selections;
// no Blazor/.NET interop here.

window.BatchMonitor = window.BatchMonitor || {};
window.BatchMonitor.D3Animation = (function () {

    // Messages/sec at or above this switches an edge to pulse mode.
    const THROUGHPUT_SWITCH_THRESHOLD = 2.0;

    // Rolling window (ms) used to estimate throughput from message-count deltas.
    const THROUGHPUT_WINDOW_MS = 5000;

    // Particle visual tuning.
    const PARTICLE_RADIUS = 3;
    const PARTICLE_DURATION_MS = 1100; // time to travel the full edge path
    const MAX_CONCURRENT_PARTICLES = 6; // cap per edge to avoid blizzard near the threshold

    /**
     * Creates a fresh per-edge animation state map, keyed by edge id
     * ("source→target"). Each entry:
     * {
     *   lastMessageCount, lastSampleTime, throughput,
     *   particles: [{ progress }], pulsePhase
     * }
     */
    function createState() {
        return new Map();
    }

    /**
     * Updates throughput estimate for an edge given its current cumulative
     * message count. Call this whenever fresh topology data arrives.
     */
    function sampleEdge(state, edgeId, messageCount, now) {
        let s = state.get(edgeId);
        if (!s) {
            s = {
                lastMessageCount: messageCount,
                lastSampleTime: now,
                throughput: 0,
                particles: [],
                pulsePhase: 0,
            };
            state.set(edgeId, s);
            return s;
        }

        const dt = now - s.lastSampleTime;
        if (dt > 50) { // avoid div-by-near-zero on rapid successive calls
            const delta = Math.max(0, messageCount - s.lastMessageCount);
            const instantRate = delta / (dt / 1000);
            // Exponential moving average for a stable-but-responsive estimate.
            const alpha = Math.min(1, dt / THROUGHPUT_WINDOW_MS);
            s.throughput = s.throughput * (1 - alpha) + instantRate * alpha;
            s.lastMessageCount = messageCount;
            s.lastSampleTime = now;
        }
        return s;
    }

    /**
     * Returns 'pulse' or 'particles' for the given edge state.
     */
    function modeForEdge(s) {
        return s.throughput >= THROUGHPUT_SWITCH_THRESHOLD ? 'pulse' : 'particles';
    }

    /**
     * Advances animation state by `dtMs` and returns render instructions.
     * Spawns new particles probabilistically based on throughput when in
     * particle mode; advances existing particles and pulse phase.
     *
     * Returns: { mode, particles: [{progress}], pulse: { intensity (0-1) } | null }
     */
    function tick(s, dtMs) {
        const mode = modeForEdge(s);

        if (mode === 'particles') {
            // Spawn probability per tick proportional to throughput.
            const spawnChance = (s.throughput * dtMs) / 1000;
            if (s.particles.length < MAX_CONCURRENT_PARTICLES && Math.random() < spawnChance) {
                s.particles.push({ progress: 0 });
            }

            // Advance & cull.
            const step = dtMs / PARTICLE_DURATION_MS;
            s.particles = s.particles
                .map(p => ({ progress: p.progress + step }))
                .filter(p => p.progress < 1);

            return { mode, particles: s.particles, pulse: null };
        }

        // Pulse mode: phase cycles 0..1, intensity derived from a sine wave
        // whose speed scales with throughput (capped) so busier edges pulse faster.
        const cyclesPerSec = Math.min(3, 0.5 + s.throughput / 20);
        s.pulsePhase = (s.pulsePhase + (dtMs / 1000) * cyclesPerSec) % 1;
        const intensity = 0.5 + 0.5 * Math.sin(s.pulsePhase * Math.PI * 2);

        // Particles already in flight finish their journey even after switching modes.
        const step = dtMs / PARTICLE_DURATION_MS;
        s.particles = s.particles
            .map(p => ({ progress: p.progress + step }))
            .filter(p => p.progress < 1);

        return { mode, particles: s.particles, pulse: { intensity } };
    }

    /**
     * Computes the point at parameter t (0-1) along an SVG path element,
     * using the native getPointAtLength API.
     */
    function pointOnPath(pathEl, t) {
        if (!pathEl || typeof pathEl.getTotalLength !== 'function') return { x: 0, y: 0 };
        const len = pathEl.getTotalLength();
        if (!isFinite(len) || len <= 0) return { x: 0, y: 0 };
        const pt = pathEl.getPointAtLength(len * Math.min(1, Math.max(0, t)));
        return { x: pt.x, y: pt.y };
    }

    return {
        THROUGHPUT_SWITCH_THRESHOLD,
        PARTICLE_RADIUS,
        createState,
        sampleEdge,
        modeForEdge,
        tick,
        pointOnPath,
    };
})();
