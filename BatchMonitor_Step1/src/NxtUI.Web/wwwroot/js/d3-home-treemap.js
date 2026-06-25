window.homeMemoryTreemap = (function () {

    function color(ram) {
        if (ram == null) return 'rgba(128,128,128,0.35)';
        if (ram > 500)   return '#E24B4A';
        if (ram > 200)   return '#BA7517';
        return '#1D9E75';
    }

    function render(container, data) {
        d3.select(container).selectAll('*').remove();
        if (!data || !data.children || data.children.length === 0) return;

        const rect = container.getBoundingClientRect();
        const W = rect.width  || container.offsetWidth  || 600;
        const H = rect.height || container.offsetHeight || 180;

        const root = d3.hierarchy(data)
            .sum(d => Math.max(d.ram || 0, 1))
            .sort((a, b) => b.value - a.value);

        d3.treemap()
            .size([W, H])
            .paddingOuter(3)
            .paddingTop(16)
            .paddingInner(2)
            .tile(d3.treemapSquarify)(root);

        const svg = d3.select(container).append('svg')
            .attr('width', W).attr('height', H)
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
            const w = leaf.x1 - leaf.x0 - 2;
            const h = leaf.y1 - leaf.y0 - 2;
            if (w <= 0 || h <= 0) return;

            const rect = svg.append('rect')
                .attr('x', leaf.x0 + 1).attr('y', leaf.y0 + 1)
                .attr('width', w).attr('height', h)
                .attr('fill', color(leaf.data.ram))
                .attr('opacity', 0.82)
                .attr('rx', 2)
                .style('cursor', 'pointer')
                .on('mousemove', event => {
                    const ram = leaf.data.ram != null
                        ? Math.round(leaf.data.ram) + ' MB'
                        : 'no data';
                    tip.style('display', 'block')
                       .html(`<strong>${leaf.data.name}</strong> &nbsp;PID ${leaf.data.pid}<br>${ram}`);
                    const cr = container.getBoundingClientRect();
                    let lx = event.clientX - cr.left + 12;
                    let ly = event.clientY - cr.top  - 40;
                    if (lx + 180 > W) lx = event.clientX - cr.left - 185;
                    tip.style('left', lx + 'px').style('top', ly + 'px');
                })
                .on('mouseleave', () => tip.style('display', 'none'));

            // RAM label — only if rectangle is wide enough
            if (w >= 34 && h >= 14 && leaf.data.ram != null) {
                svg.append('text')
                    .attr('x', leaf.x0 + 1 + w / 2)
                    .attr('y', leaf.y0 + 1 + h / 2 + 4)
                    .attr('text-anchor', 'middle')
                    .attr('font-size', '10px')
                    .attr('fill', 'rgba(255,255,255,0.88)')
                    .attr('pointer-events', 'none')
                    .text(Math.round(leaf.data.ram) + ' MB');
            }
        });
    }

    function destroy(container) {
        d3.select(container).selectAll('*').remove();
    }

    return { render, destroy };
})();
