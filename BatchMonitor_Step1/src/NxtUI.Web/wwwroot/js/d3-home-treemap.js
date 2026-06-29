window.homeMemoryTreemap = (function () {

    const _observers = new WeakMap(); // container → ResizeObserver

    function color(ram) {
        if (ram == null) return 'rgba(128,128,128,0.35)';
        if (ram > 500)   return '#E24B4A';
        if (ram > 200)   return '#BA7517';
        return '#1D9E75';
    }

    function paint(container, data, forcedW) {
        d3.select(container).selectAll('*').remove();
        if (!data || !data.children || data.children.length === 0) return;

        const W = forcedW || container.getBoundingClientRect().width || container.offsetWidth || 600;
        const H = container.getBoundingClientRect().height || container.offsetHeight || 180;

        // Small fixed floor so no-data services get a visible pixel without
        // dominating the layout when many processes lack a RAM sample.
        const minVal = 4;

        const root = d3.hierarchy(data)
            .sum(d => d.children ? 0 : Math.max(d.ram || 0, minVal))
            .sort((a, b) => b.value - a.value);

        // Two-level tiling: hosts are diced into vertical columns (side-by-side),
        // services within each host are squarified — this handles hundreds of
        // small processes per host without collapsing them into sub-pixel rows.
        d3.treemap()
            .size([W, H])
            .paddingOuter(1)
            .paddingTop(12)
            .paddingInner(0.5)
            .tile((node, x0, y0, x1, y1) => {
                if (node.depth === 0) d3.treemapDice(node, x0, y0, x1, y1);
                else                  d3.treemapSquarify(node, x0, y0, x1, y1);
            })(root);

        const svg = d3.select(container).append('svg')
            .attr('width', '100%')
            .attr('height', H)
            .attr('viewBox', `0 0 ${W} ${H}`)
            .attr('preserveAspectRatio', 'none')
            .style('display', 'block');

        const tip = d3.select(container).append('div')
            .style('position', 'absolute')
            .style('pointer-events', 'none')
            .style('background', 'rgba(18,18,18,0.9)')
            .style('color', '#e8e8e8')
            .style('padding', '4px 9px')
            .style('border-radius', '4px')
            .style('font-size', '11px')
            .style('line-height', '1.6')
            .style('font-family', "'JetBrains Mono','Cascadia Code',Consolas,monospace")
            .style('display', 'none')
            .style('z-index', '9999')
            .style('white-space', 'nowrap');

        // Host backgrounds + labels
        (root.children || []).forEach(host => {
            svg.append('rect')
                .attr('x', host.x0).attr('y', host.y0)
                .attr('width', host.x1 - host.x0).attr('height', host.y1 - host.y0)
                .attr('fill', 'rgba(128,128,128,0.06)')
                .attr('stroke', 'rgba(128,128,128,0.2)')
                .attr('stroke-width', 0.5)
                .attr('rx', 3);

            const labelW = host.x1 - host.x0 - 8;
            if (labelW > 20) {
                svg.append('text')
                    .attr('x', host.x0 + 5).attr('y', host.y0 + 11)
                    .attr('font-size', '10px')
                    .attr('fill', 'rgba(180,180,180,0.65)')
                    .attr('pointer-events', 'none')
                    .text(host.data.name.length * 6 > labelW
                        ? host.data.name.slice(0, Math.floor(labelW / 6) - 1) + '…'
                        : host.data.name);
            }
        });

        // Service leaf rectangles
        root.leaves().forEach(leaf => {
            const w = Math.max(2, leaf.x1 - leaf.x0 - 2);
            const h = Math.max(2, leaf.y1 - leaf.y0 - 2);

            svg.append('rect')
                .attr('x', leaf.x0 + 1).attr('y', leaf.y0 + 1)
                .attr('width', w).attr('height', h)
                .attr('fill', color(leaf.data.ram))
                .attr('opacity', 0.82)
                .attr('rx', 2)
                .style('cursor', 'pointer')
                .on('mousemove', event => {
                    const ram = leaf.data.ram != null
                        ? Math.round(leaf.data.ram)
                        : 'no data';
                    tip.style('display', 'block')
                       .html(`<strong>${leaf.data.name}</strong><br>PID ${leaf.data.pid} &nbsp;·&nbsp; ${ram}`);
                    const cr = container.getBoundingClientRect();
                    let lx = event.clientX - cr.left + 12;
                    let ly = event.clientY - cr.top  - 40;
                    if (lx + 180 > W) lx = event.clientX - cr.left - 185;
                    tip.style('left', lx + 'px').style('top', ly + 'px');
                })
                .on('mouseleave', () => tip.style('display', 'none'));

            if (w >= 34 && h >= 14 && leaf.data.ram != null) {
                svg.append('text')
                    .attr('x', leaf.x0 + 1 + w / 2)
                    .attr('y', leaf.y0 + 1 + h / 2 + 4)
                    .attr('text-anchor', 'middle')
                    .attr('font-size', '10px')
                    .attr('fill', 'rgba(255,255,255,0.88)')
                    .attr('pointer-events', 'none')
                    .text(Math.round(leaf.data.ram));
            }
        });
    }

    // ── client-side polling (no SignalR for data) ─────────────────────────────

    const _state = new WeakMap(); // container → { aborted, data, ro }

    function start(container, env, intervalMs) {
        stop(container);

        const s = { aborted: false, data: null, ro: null };
        _state.set(container, s);

        // Show a spinner while the first fetch is in flight.
        container.innerHTML =
            '<div class="bm-treemap-loading">' +
              '<div class="bm-treemap-spinner"></div>' +
              '<span class="bm-treemap-loading-label">Loading memory data…</span>' +
            '</div>';

        // ResizeObserver repaints on resize using the last fetched data.
        s.ro = new ResizeObserver(entries => {
            const w = entries[0]?.contentRect.width;
            if (w > 0 && s.data) paint(container, s.data, w);
        });
        s.ro.observe(container);

        const interval = intervalMs || 10000;
        const url      = `/api/treemap/${encodeURIComponent(env)}`;

        async function poll() {
            while (!s.aborted) {
                try {
                    const resp = await fetch(url);
                    if (!s.aborted && resp.ok) {
                        s.data = await resp.json();
                        paint(container, s.data);
                    }
                } catch (_) { /* network error — retry next interval */ }
                // Wait for the interval, but allow abort to cut it short.
                await new Promise(resolve => { s._wake = resolve; setTimeout(resolve, interval); });
            }
        }

        poll();
    }

    function stop(container) {
        if (!_state.has(container)) return;
        const s = _state.get(container);
        s.aborted = true;
        if (s._wake) s._wake();
        if (s.ro) { s.ro.disconnect(); s.ro = null; }
        _state.delete(container);
        d3.select(container).selectAll('*').remove();
    }

    return { start, stop };
})();
