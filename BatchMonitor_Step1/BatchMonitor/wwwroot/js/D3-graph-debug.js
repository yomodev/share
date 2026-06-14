// Debug version of d3-graph with proper sizing

(function() {
  'use strict';

  console.log('[D3] Initializing D3 graph module');

  window.bm_d3Graph = window.bm_d3Graph || {};

  let currentGraph = null;
  let currentSvg = null;
  let zoom = null;

  /**
   * Renders a topology to SVG
   */
  window.bm_d3Graph.render = function(svgElement, topology) {
    console.log('[D3] render() called', { 
      svgElement, 
      topologyType: typeof topology,
      topologyKeys: Object.keys(topology || {})
    });

    if (!svgElement) {
      console.error('[D3] Missing svgElement');
      return;
    }

    if (!topology) {
      console.error('[D3] Missing topology');
      return;
    }

    currentSvg = svgElement;

    // Blazor JS interop: property names are camelCase
    const nodes = topology.nodes || topology.Nodes || [];
    const edges = topology.edges || topology.Edges || [];

    console.log('[D3] Topology:', {
      nodesLength: nodes.length,
      edgesLength: edges.length,
      totalChunks: topology.totalChunks,
      totalEvents: topology.totalEvents
    });

    // Check if topology is empty
    if (nodes.length === 0) {
      console.log('[D3] Topology is empty, rendering empty state');
      renderEmptyState(svgElement);
      return;
    }

    try {
      console.log('[D3] Creating dagre graph with', nodes.length, 'nodes and', edges.length, 'edges');

      // Create dagre graph with larger spacing
      const g = new dagreD3.graphlib.Graph({ compound: true })
        .setGraph({ 
          rankdir: 'LR', 
          ranksep: 150,    // Increased from 100
          nodesep: 80,     // Increased from 50
          marginx: 20,
          marginy: 20
        })
        .setDefaultEdgeLabel(function() { return {}; });

      // Add nodes with larger dimensions
      nodes.forEach(node => {
        const label = `${node.label || node.Label}\n(${node.instanceCount || node.InstanceCount} inst)`;
        g.setNode(node.id || node.Id, {
          label: label,
          id: node.id || node.Id,
          class: 'bm-d3-node',
          rx: 8,
          ry: 8,
          padding: 15,
          width: 180,      // Increased from 140
          height: 80       // Increased from 60
        });
      });
      console.log(`[D3] Added ${nodes.length} nodes`);

      // Add edges
      if (edges && edges.length > 0) {
        edges.forEach(edge => {
          const msgCount = edge.messageCount || edge.MessageCount || 0;
          const pending = edge.pendingEstimate || edge.PendingEstimate || 0;
          const label = `${msgCount}${pending > 0 ? `\n(~${pending})` : ''}`;
          g.setEdge(edge.source || edge.Source, edge.target || edge.Target, {
            label: label,
            class: 'bm-d3-edge',
            arrowheadClass: 'bm-d3-arrowhead',
            messageCount: msgCount,
            pendingEstimate: pending
          });
        });
        console.log(`[D3] Added ${edges.length} edges`);
      }

      // Render
      console.log('[D3] Rendering with dagre-d3...');
      d3.select(svgElement).selectAll('*').remove();
      
      const svg = d3.select(svgElement);
      
      // Set SVG dimensions to fill container
      const container = svgElement.parentElement;
      const width = container ? container.clientWidth : 1000;
      const height = container ? container.clientHeight : 600;
      
      console.log('[D3] SVG dimensions:', { width, height });
      
      svg.attr('width', width)
         .attr('height', height);

      const svgGroup = svg.append('g');

      // Render the graph
      const render = new dagreD3.render();
      render(svgGroup, g);
      
      console.log('[D3] Dagre render complete');

      // Get actual bounds after rendering
      const bounds = svgGroup.node().getBBox();
      console.log('[D3] Graph bounds:', bounds);

      // Setup interactivity
      setupZoom(svg, svgGroup);
      setupNodeStyles(svg, nodes);
      setupEdgeStyles(svg, edges);

      currentGraph = { graph: g, svgGroup: svgGroup, svg: svg, bounds: bounds };

      console.log('[D3] Fitting to view...');
      fitToViewInternal();

      // Start animations
      if (window.bm_animations && window.bm_animations.initializeAnimations) {
        console.log('[D3] Initializing animations with', nodes.length, 'nodes');
        window.bm_animations.initializeAnimations(svg, topology);
      } else {
        console.warn('[D3] Animation module not found');
      }

      console.log('[D3] ✓ Render complete');
    } catch (error) {
      console.error('[D3] Error during render:', error);
      console.error(error.stack);
    }
  };

  window.bm_d3Graph.fitToView = function() {
    console.log('[D3] fitToView() called');
    fitToViewInternal();
  };

  // ─── Private helpers ───

  function renderEmptyState(svgElement) {
    console.log('[D3] renderEmptyState()');
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
    svg.on('.zoom', null);
    const zoomBehavior = d3.zoom()
      .on('zoom', function(event) {
        svgGroup.attr('transform', event.transform);
      });
    svg.call(zoomBehavior);
    zoom = zoomBehavior;
  }

  function setupNodeStyles(svg, nodes) {
    const nodeMap = new Map(nodes.map(n => [n.id || n.Id, n]));

    svg.selectAll('.bm-d3-node').each(function(nodeId) {
      const node = nodeMap.get(nodeId);
      if (!node) return;

      const element = d3.select(this);
      const rect = element.select('rect');

      const successCount = node.successCount || node.SuccessCount || 0;
      const failedCount = node.failedCount || node.FailedCount || 0;
      const skippedCount = node.skippedCount || node.SkippedCount || 0;
      const total = successCount + failedCount + skippedCount;

      let color = '#4CAF50'; // green
      if (total > 0) {
        const failRate = failedCount / total;
        if (failRate > 0.5) {
          color = '#f44336'; // red
        } else if (failRate > 0.1) {
          color = '#ff9800'; // orange
        }
      }

      if (rect) {
        rect.style('fill', color)
          .style('opacity', 0.85)
          .style('stroke', '#333')
          .style('stroke-width', '2px');
      }

      // Enhanced tooltip
      const label = node.label || node.Label || 'Unknown';
      const tooltip = `${label}\nInstances: ${node.instanceCount || node.InstanceCount}\nProcessed: ${node.processedCount || node.ProcessedCount}\nSuccess: ${successCount}\nFailed: ${failedCount}`;
      element.append('title').text(tooltip);
    });
  }

  function setupEdgeStyles(svg, edges) {
    svg.selectAll('.bm-d3-edge').each(function() {
      const element = d3.select(this);
      const path = element.select('path');
      if (path) {
        path.style('stroke', '#666')
          .style('stroke-width', '2.5px')
          .style('opacity', 0.8);
      }

      // Style edge text
      const text = element.select('text');
      if (text) {
        text.style('font-size', '11px')
          .style('fill', '#333')
          .style('font-weight', '600');
      }
    });
  }

  function fitToViewInternal() {
    if (!currentGraph || !currentSvg) {
      console.warn('[D3] fitToViewInternal: no graph state');
      return;
    }

    const svg = currentGraph.svg;
    const svgGroup = currentGraph.svgGroup;

    try {
      const bounds = svgGroup.node().getBBox();
      const fullWidth = currentSvg.clientWidth;
      const fullHeight = currentSvg.clientHeight;

      console.log('[D3] fitToView:', { 
        graphBounds: { x: bounds.x, y: bounds.y, width: bounds.width, height: bounds.height },
        viewSize: { width: fullWidth, height: fullHeight }
      });

      const midX = bounds.x + bounds.width / 2;
      const midY = bounds.y + bounds.height / 2;

      // Leave 10% margin
      const scale = 0.85 / Math.max(bounds.width / fullWidth, bounds.height / fullHeight);
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
        
      console.log('[D3] fitToView complete - scale:', scale.toFixed(2));
    } catch (e) {
      console.warn('[D3] fitToView error:', e);
    }
  }

  console.log('[D3] Module initialization complete ✓');
})();
