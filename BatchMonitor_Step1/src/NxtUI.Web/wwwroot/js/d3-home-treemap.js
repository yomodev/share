window.homeMemoryTreemap = (function () {

    // ── colour + label helpers ─────────────────────────────────────────────────

    // Gradient stops (RAM MB → RGB), blue (idle) through deep pink (very high).
    // Piecewise-linear RGB interpolation between adjacent stops — no external
    // color-space dependency, just enough steps to look smooth on a treemap tile.
    const RAM_COLOR_STOPS = [
        { ram: 0,    rgb: [59, 130, 246] },  // blue      #3B82F6
        { ram: 128,  rgb: [34, 197, 94]  },  // green     #22C55E
        { ram: 512,  rgb: [234, 179, 8]  },  // yellow    #EAB308
        { ram: 2048, rgb: [249, 115, 22] },  // orange    #F97316
        { ram: 4096, rgb: [239, 68, 68]  },  // red       #EF4444
        { ram: 8196, rgb: [255, 20, 147] },  // deeppink  #FF1493
    ];

    function ramColor(ram) {
        const stops = RAM_COLOR_STOPS;
        if (ram <= stops[0].ram) return `rgb(${stops[0].rgb.join(',')})`;
        const last = stops[stops.length - 1];
        if (ram >= last.ram) return `rgb(${last.rgb.join(',')})`;
        for (let i = 0; i < stops.length - 1; i++) {
            const s0 = stops[i], s1 = stops[i + 1];
            if (ram >= s0.ram && ram <= s1.ram) {
                const t = (ram - s0.ram) / (s1.ram - s0.ram);
                const r = Math.round(s0.rgb[0] + (s1.rgb[0] - s0.rgb[0]) * t);
                const g = Math.round(s0.rgb[1] + (s1.rgb[1] - s0.rgb[1]) * t);
                const b = Math.round(s0.rgb[2] + (s1.rgb[2] - s0.rgb[2]) * t);
                return `rgb(${r},${g},${b})`;
            }
        }
        return `rgb(${last.rgb.join(',')})`;
    }

    function leafColor(d) {
        if (!d.online) return 'rgba(180,50,50,0.25)';   // offline — dim red
        if (d.ram == null) return 'rgba(128,128,128,0.28)'; // online, no RAM yet — gray
        return ramColor(d.ram);
    }

    function leafOpacity(d) { return d.online ? 0.82 : 0.45; }

    // ── paint ──────────────────────────────────────────────────────────────────

    function paint(container, data, forcedW, forcedH) {
        // Only clear the container (which may hold the spinner) when we actually
        // have data to show. Returning before clearing keeps the spinner alive.
        if (!data || !data.children || data.children.length === 0) return;

        const rect = container.getBoundingClientRect();
        const W = forcedW || rect.width  || container.offsetWidth  || 600;
        const H = forcedH || rect.height || container.offsetHeight || 0;

        // The container hasn't been laid out yet — e.g. paint() runs synchronously
        // from start() right as a hidden tab regains visibility, before the browser
        // has committed this frame's flex layout, so getBoundingClientRect() still
        // reports a stale/collapsed box. Painting into that gave the "sometimes very
        // small" treemap; retry next frame instead of drawing at a bogus height.
        if (H < 20) {
            requestAnimationFrame(() => paint(container, data, forcedW, forcedH));
            return;
        }

        d3.select(container).selectAll('*').remove();

        // Each service gets at least 4 units so offline/no-data services are
        // still visible as small rectangles rather than collapsed to nothing.
        const minVal = 4;

        const root = d3.hierarchy(data)
            .sum(d => d.children ? 0 : Math.max(d.ram || 0, minVal))
            .sort((a, b) => b.value - a.value);

        // Give every host column equal width regardless of their RAM totals.
        // We must also rescale leaf values within each host by the same factor so
        // they still sum to the new host value — otherwise squarify advances rows by
        // sumValue/parentValue which overflows the allocated rectangle.
        if (root.children && root.children.length > 1) {
            const eq = root.value / root.children.length;
            root.children.forEach(h => {
                if (h.value > 0 && h.children) {
                    const scale = eq / h.value;
                    h.children.forEach(c => { c.value *= scale; });
                }
                h.value = eq;
            });
        }

        // Two-level tiling: hosts are diced into vertical columns (side-by-side),
        // services within each host are squarified.
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
            const d = leaf.data;

            svg.append('rect')
                .attr('x', leaf.x0 + 1).attr('y', leaf.y0 + 1)
                .attr('width', w).attr('height', h)
                .attr('fill', leafColor(d))
                .attr('opacity', leafOpacity(d))
                .attr('rx', 2)
                .style('cursor', 'pointer')
                .on('click', event => {
                    event.stopPropagation();
                    tip.style('display', 'none');
                    const st = _state.get(container);
                    // Host is included because PIDs are only unique per host — two
                    // different hosts can report the same service name + PID.
                    if (st?.leafClickRef && d.pid != null)
                        st.leafClickRef.invokeMethodAsync('OnLeafClicked', leaf.parent.data.name + '|' + d.name + '|' + d.pid).catch(() => {});
                })
                .on('mousemove', event => {
                    const ram    = d.ram != null ? Math.round(d.ram) + ' MB' : 'no data';
                    const status = d.online ? '' : ' <span style="color:#f87171">offline</span>';
                    tip.style('display', 'block')
                       .html(`<strong>${d.name}</strong>${status}<br>PID ${d.pid} &nbsp;·&nbsp; ${ram}`);
                    const cr = container.getBoundingClientRect();
                    let lx = event.clientX - cr.left + 12;
                    let ly = event.clientY - cr.top  - 40;
                    if (lx + 180 > W) lx = event.clientX - cr.left - 185;
                    tip.style('left', lx + 'px').style('top', ly + 'px');
                })
                .on('mouseleave', () => tip.style('display', 'none'));

            // Show label inside box when large enough.
            // RAM number if available; service name if no RAM yet.
            if (w >= 34 && h >= 14) {
                const label = d.ram != null
                    ? String(Math.round(d.ram))
                    : (w >= 60 && h >= 18 ? d.name.split(/[-_]/)[0] : '');
                if (label) {
                    svg.append('text')
                        .attr('x', leaf.x0 + 1 + w / 2)
                        .attr('y', leaf.y0 + 1 + h / 2 + 4)
                        .attr('text-anchor', 'middle')
                        .attr('font-size', '10px')
                        .attr('fill', d.online ? 'rgba(255,255,255,0.88)' : 'rgba(255,180,180,0.7)')
                        .attr('pointer-events', 'none')
                        .text(label);
                }
            }
        });
    }

    // ── "no heartbeat data" placeholder ───────────────────────────────────────

    function showNoData(container, env) {
        d3.select(container).selectAll('*').remove();
        const el = document.createElement('div');
        el.className = 'bm-treemap-loading';
        el.innerHTML =
            '<span class="bm-treemap-loading-label" style="opacity:0.5">' +
            'No services found for <strong>' + env + '</strong>' +
            '</span>';
        container.appendChild(el);
    }

    // ── client-side polling ────────────────────────────────────────────────────

    const _state = new WeakMap(); // container → { aborted, data, ro, _wake }

    // Last good payload per env, kept across stop()/start() cycles (i.e. across Home
    // being hidden/shown again) so resuming repaints instantly instead of blanking
    // the treemap and re-showing the loading spinner every time a tab is focused.
    const _lastData = new Map();

    function allOnlineLeavesHaveRam(node) {
        if (!node.children || node.children.length === 0)
            return !node.online || node.ram != null;  // offline leaves don't need RAM
        return node.children.every(allOnlineLeavesHaveRam);
    }

    // Notifies .NET at two distinct points: as soon as SOMETHING is on screen
    // (NotifyFirstPaint — this is what "the map is ready" should mean for a
    // loading spinner, since real infra may have services that never report a
    // RAM sample at all, so waiting for allOnlineLeavesHaveRam can wait forever),
    // and again once every online leaf has a RAM value (NotifyDataLoaded, kept
    // for any caller that specifically wants full data, not just first paint).
    function notify(s, hasFullData) {
        if (!s.dotnetRef) return;
        if (!s.notifiedFirstPaint) {
            s.notifiedFirstPaint = true;
            s.dotnetRef.invokeMethodAsync('NotifyFirstPaint').catch(() => {});
        }
        if (hasFullData && !s.notifiedFullyLoaded) {
            s.notifiedFullyLoaded = true;
            s.dotnetRef.invokeMethodAsync('NotifyDataLoaded').catch(() => {});
        }
    }

    function start(container, env, intervalMs, dotnetRef, leafClickRef) {
        stop(container);

        const cached = _lastData.get(env) || null;
        const s = {
            aborted: false, data: cached, ro: null, _wake: null,
            dotnetRef: dotnetRef || null, leafClickRef: leafClickRef || null,
            notifiedFirstPaint: false, notifiedFullyLoaded: false,
        };
        _state.set(container, s);

        if (cached) {
            // Resuming with data we've already shown before — repaint immediately,
            // no spinner, no empty gap.
            paint(container, cached);
            notify(s, allOnlineLeavesHaveRam(cached));
        } else {
            // First time ever for this env — show the big spinner until data arrives.
            container.innerHTML =
                '<div class="bm-treemap-loading bm-treemap-loading-big">' +
                  '<div class="bm-treemap-spinner bm-treemap-spinner-big"></div>' +
                  '<span class="bm-treemap-loading-label">Loading services information…</span>' +
                '</div>';
        }

        // ResizeObserver repaints on resize using the last fetched data. Reacts to
        // height changes too (not just width) — a container that settles to its
        // final height without a width change (e.g. late flex layout) still needs
        // a repaint, otherwise it stays stuck at whatever height paint() saw last.
        s.ro = new ResizeObserver(entries => {
            const cr = entries[0]?.contentRect;
            if (cr && (cr.width > 0 || cr.height > 0) && s.data) paint(container, s.data, cr.width, cr.height);
        });
        s.ro.observe(container);

        const steadyInterval = intervalMs || 10000;
        const retryInterval  = 2000;   // aggressive retry while waiting for heartbeat
        const maxRetries     = 15;     // give up after 30 s showing "no data"
        const url            = `/api/treemap/${encodeURIComponent(env)}`;

        async function poll() {
            let emptyCount = 0;
            while (!s.aborted) {
                let hasData = false;
                try {
                    const resp = await fetch(url);
                    if (!s.aborted && resp.ok) {
                        const json = await resp.json();
                        hasData = json && json.children && json.children.length > 0;
                        if (hasData) {
                            // First real data: clear spinner, render, reset retry counter.
                            s.data     = json;
                            emptyCount = 0;
                            _lastData.set(env, json);
                            paint(container, s.data);
                            notify(s, allOnlineLeavesHaveRam(json));
                        }
                    }
                } catch (_) { /* network error — retry */ }

                if (!hasData) {
                    emptyCount++;
                    if (emptyCount >= maxRetries) {
                        // Heartbeat gave us nothing for 30 s — show a gentle message.
                        if (!s.aborted) showNoData(container, env);
                    }
                }

                // Use short retry interval until we have data; then switch to steady poll.
                // Already-cached env skips the retry burst — go straight to steady polling.
                const delay = (s.data ? steadyInterval : retryInterval);
                await new Promise(resolve => { s._wake = resolve; setTimeout(resolve, delay); });
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
