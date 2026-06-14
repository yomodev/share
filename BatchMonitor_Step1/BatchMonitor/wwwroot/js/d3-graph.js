// D3 Flow Graph Renderer for Batch Monitor
// Requires: d3@7+, dagre-d3@0.6+

(function() {
  'use strict';

  // Ensure namespace exists
  window.bm_d3Graph = window.bm_d3Graph || {};

  // Store for current render state
  let currentGraph = null;
  let currentSvg = null;
  let zoom = null;

  /**
   * Renders a topology (nodes + edges) to the given SVG element.
   * @param {Element} svgElement - The SVG element to render to.
   * @param {Object} topology - Topology object with .Nodes and .Edges arrays.
   */
  window.bm_d3Graph.render = function(svgElement, topology) {
    if (!svgElement || !topology) {
      console.warn('[D3] Missing svgElement or topology');
      return;
    }

    currentSvg = svgElement;

    // Cold-start: empty topology
    if (!topology.Nodes || topology.Nodes.length === 0) {
      renderEmptyState(svgElement);
      return;
    }

    // Create a new directed graph using dagre
    const g = new dagreD3.graphlib.Graph({ compound: true })
      .setGraph({ rankdir: 'LR', ranksep: 100, nodesep: 50 })
      .setDefaultEdgeLabel(function() { return {}; });

    // Add nodes
    topology.Nodes.forEach(node => {
      const label = `${node.Label}\n(${node.InstanceCount} inst)`;
      g.setNode(node.Id, {
        label: label,
        id: node.Id,
        class: 'bm-d3-node',
        rx: 5,
        ry: 5,
        padding: 10,
        width: 140,
        height: 60
      });
    });

    // Add edges
    topology.Edges.forEach(edge => {
      const label = `${edge.MessageCount}`;
      g.setEdge(edge.Source, edge.Target, {
        label: label,
        class: 'bm-d3-edge',
        arrowheadClass: 'bm-d3-arrowhead'
      });
    });

    // Clear and render
    d3.select(svgElement).selectAll('*').remove();
    const svg = d3.select(svgElement);
    const svgGroup = svg.append('g');

    // Use dagre-d3 to render the graph
    const render = new dagreD3.render();
    render(svgGroup, g);

    // Apply D3 zoom behavior
    setupZoom(svg, svgGroup);

    // Style nodes with colors based on success/failure
    setupNodeStyles(svg, topology);

    // Store reference for FitToView
    currentGraph = { graph: g, svgGroup: svgGroup, svg: svg };

    // Fit to initial view
    fitToViewInternal();
  };

  /**
   * Fits the graph to the viewport.
   */
  window.bm_d3Graph.fitToView = function() {
    fitToViewInternal();
  };

  // ── Private helpers ──────────────────────────────────────────────────

  function renderEmptyState(svgElement) {
    d3.select(svgElement).selectAll('*').remove();
    const svg = d3.select(svgElement)
      .attr('width', '100%')
      .attr('height', '100%');

    const group = svg.append('g')
      .attr('transform', 'translate(50,50)');

    group.append('text')
      .attr('x', '50%')
      .attr('y', '50%')
      .attr('text-anchor', 'middle')
      .attr('dominant-baseline', 'middle')
      .attr('class', 'bm-graph-empty')
      .text('Waiting for first events…');
  }

  function setupZoom(svg, svgGroup) {
    // Remove existing zoom behavior if any
    svg.on('.zoom', null);

    const zoomBehavior = d3.zoom()
      .on('zoom', function(event) {
        svgGroup.attr('transform', event.transform);
      });

    svg.call(zoomBehavior);

    // Store for later
    zoom = zoomBehavior;
  }

  function setupNodeStyles(svg, topology) {
    const nodeMap = new Map(topology.Nodes.map(n => [n.Id, n]));

    svg.selectAll('.bm-d3-node').each(function(nodeId) {
      const node = nodeMap.get(nodeId);
      if (!node) return;

      const element = d3.select(this);
      const rect = element.select('rect');

      // Color based on success/failure ratio
      const total = node.SuccessCount + node.FailedCount + node.SkippedCount;
      let color = '#4CAF50'; // green (success)
      if (total > 0) {
        const failRate = node.FailedCount / total;
        if (failRate > 0.5) {
          color = '#f44336'; // red (failure)
        } else if (failRate > 0.1) {
          color = '#ff9800'; // orange (mixed)
        }
      }

      if (rect) {
        rect.style('fill', color)
          .style('opacity', 0.8)
          .style('stroke', '#333')
          .style('stroke-width', '2px');
      }

      // Add title for hover tooltip
      element.append('title')
        .text(`${node.Label}\nInstances: ${node.InstanceCount}\nProcessed: ${node.ProcessedCount}\nSuccess: ${node.SuccessCount}\nFailed: ${node.FailedCount}`);
    });

    svg.selectAll('.bm-d3-edge').each(function() {
      const element = d3.select(this);
      const path = element.select('path');
      if (path) {
        path.style('stroke', '#666')
          .style('stroke-width', '2px');
      }
    });
  }

  function fitToViewInternal() {
    if (!currentGraph || !currentSvg) return;

    const svg = currentGraph.svg;
    const svgGroup = currentGraph.svgGroup;

    try {
      // Get the bounds of the content
      const bounds = svgGroup.node().getBBox();
      const fullWidth = currentSvg.clientWidth;
      const fullHeight = currentSvg.clientHeight;

      const midX = bounds.x + bounds.width / 2;
      const midY = bounds.y + bounds.height / 2;

      const scale = 0.8 / Math.max(bounds.width / fullWidth, bounds.height / fullHeight);
      const translate = [
        fullWidth / 2 - scale * midX,
        fullHeight / 2 - scale * midY
      ];

      const transform = d3.zoomIdentity
        .translate(translate[0], translate[1])
        .scale(scale);

      svg.transition()
        .duration(750)
        .call(zoom.transform, transform);
    } catch (e) {
      console.warn('[D3] Error fitting to view:', e);
    }
  }
})();
