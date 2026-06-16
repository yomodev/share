// Batch Monitor — D3 flow graph edge animation
//
// Dash-flow animation (§8.2 / Step 7):
//  - Every edge has an animated dash pattern; dashes travel in the flow
//    direction (source -> target) via a continuously decreasing
//    stroke-dashoffset.
//  - Animation speed is proportional to recent throughput (done-count
//    delta over a rolling window).
//  - At higher throughput, the dash gap shrinks toward zero (dashes
//    merge into a near-solid line) and the stroke thickens slightly.
//  - On an activity spike (a sudden jump in throughput), the edge
//    brightens briefly (~300ms) via an opacity/glow boost that decays
//    back to baseline.
//
// All functions operate on plain DOM/SVG elements and D3 selections;
// no Blazor/.NET interop here.

window.BatchMonitor = window.BatchMonitor || {};
window.BatchMonitor.D3Animation = (function () {

    // Rolling window (ms) used to estimate throughput from done-count deltas.
    const THROUGHPUT_WINDOW_MS = 5000;

    // Dash pattern tuning.
    const DASH_LENGTH = 6;          // length of each dash segment (px)
    const MAX_GAP = 10;             // gap between dashes at zero throughput (px)
    const MIN_GAP = 1.5;            // gap between dashes at high throughput (px)
    const BASE_SPEED = 14;          // dash travel speed at baseline (px/sec)
    const MAX_SPEED = 160;          // dash travel speed at high throughput (px/sec)

    // Throughput (done/sec) considered "high" for gap-shrink / thickness scaling.
    const HIGH_THROUGHPUT = 2.0;

    // Activity-spike detection & decay.
    const SPIKE_DELTA_THRESHOLD = 1;     // minimum done-count delta to count as a spike
    const SPIKE_DECAY_MS = 300;          // brighten decays back to baseline over this window

    /**
     * Creates a fresh per-edge animation state map, keyed by edge id.
     * Each entry:
     * {
     *   lastDoneCount, lastSampleTime, throughput,
     *   dashOffset, spikeIntensity
     * }
     */
    function createState() {
        return new Map();
    }

    /**
     * Updates throughput estimate (and detects activity spikes) for an edge
     * given its current cumulative done count. Call this whenever fresh
     * topology data arrives.
     */
    function sampleEdge(state, edgeId, doneCount, now) {
        let s = state.get(edgeId);
        if (!s) {
            s = {
                lastDoneCount: doneCount,
                lastSampleTime: now,
                throughput: 0,
                dashOffset: 0,
                spikeIntensity: 0,
            };
            state.set(edgeId, s);
            return s;
        }

        const dt = now - s.lastSampleTime;
        if (dt > 50) { // avoid div-by-near-zero on rapid successive calls
            const delta = doneCount - s.lastDoneCount;

            if (delta >= SPIKE_DELTA_THRESHOLD) {
                s.spikeIntensity = 1; // brighten; decays in tick()
            }

            const instantRate = Math.max(0, delta) / (dt / 1000);
            const alpha = Math.min(1, dt / THROUGHPUT_WINDOW_MS);
            s.throughput = s.throughput * (1 - alpha) + instantRate * alpha;
            s.lastDoneCount = doneCount;
            s.lastSampleTime = now;
        }
        return s;
    }

    /**
     * Advances animation state by `dtMs` and returns render instructions
     * for the dash-flow animation.
     *
     * Returns:
     * {
     *   dashArray: "<dash> <gap>",
     *   dashOffset: number,        // negative, decreasing -> dashes flow toward target
     *   thicknessBoost: 0-1,       // additive thickness factor at high throughput
     *   brighten: 0-1              // activity-spike brighten intensity, decays to 0
     * }
     */
    function tick(s, dtMs) {
        // Normalise throughput into 0-1 for gap/speed/thickness scaling.
        const t = Math.max(0, Math.min(1, s.throughput / HIGH_THROUGHPUT));

        const gap = MAX_GAP - (MAX_GAP - MIN_GAP) * t;
        const speed = BASE_SPEED + (MAX_SPEED - BASE_SPEED) * t;

        // Dashes flow from source to target: decreasing dashoffset moves the
        // pattern in the direction the path was drawn.
        s.dashOffset -= speed * (dtMs / 1000);
        // Keep the offset bounded so it doesn't grow unboundedly over a long session.
        const period = DASH_LENGTH + gap;
        if (s.dashOffset <= -period) {
            s.dashOffset += period;
        }

        // Decay activity-spike brighten back to 0 over SPIKE_DECAY_MS.
        if (s.spikeIntensity > 0) {
            s.spikeIntensity = Math.max(0, s.spikeIntensity - dtMs / SPIKE_DECAY_MS);
        }

        return {
            dashArray: `${DASH_LENGTH} ${gap.toFixed(2)}`,
            dashOffset: s.dashOffset,
            thicknessBoost: t,
            brighten: s.spikeIntensity,
        };
    }

    return {
        HIGH_THROUGHPUT,
        createState,
        sampleEdge,
        tick,
    };
})();
