// D3 Animation System for Batch Monitor Flow Graph

(function() {
  'use strict';

  console.log('[Animations] Initializing animation module');

  window.bm_animations = window.bm_animations || {};

  let animationState = {
    nodeAnimationIds: new Map(),
    edgeAnimationIds: new Map(),
    throughputThreshold: 0,
    isRunning: false
  };

  /**
   * Initialize animations for the given SVG and topology.
   */
  window.bm_animations.initializeAnimations = function(svg, topology) {
    console.log('[Animations] initializeAnimations called with', 
      (topology.nodes || topology.Nodes || []).length, 'nodes');

    if (!svg || !topology) {
      console.warn('[Animations] Missing svg or topology');
      return;
    }

    window.bm_animations.stopAnimations();

    const nodes = topology.nodes || topology.Nodes || [];
    const edges = topology.edges || topology.Edges || [];

    updateThroughputThreshold(edges);
    animateNodePulse(svg, nodes);
    animateEdges(svg, nodes, edges);

    animationState.isRunning = true;
    console.log('[Animations] Animations initialized ✓');
  };

  /**
   * Stop all active animations.
   */
  window.bm_animations.stopAnimations = function() {
    console.log('[Animations] Stopping all animations');
    
    animationState.nodeAnimationIds.forEach(id => clearInterval(id));
    animationState.nodeAnimationIds.clear();

    animationState.edgeAnimationIds.forEach(id => clearInterval(id));
    animationState.edgeAnimationIds.clear();

    animationState.isRunning = false;
  };

  /**
   * Update animations with new topology (useful for re-renders).
   * @param {Object} svg - D3 selection of SVG element
   * @param {Object} topology - Updated topology object
   */
  window.bm_animations.updateAnimations = function(svg, topology) {
    if (animationState.isRunning) {
      window.bm_animations.stopAnimations();
    }
    window.bm_animations.initializeAnimations(svg, topology);
  };

  // ── Private helpers ──

  /**
   * Adaptive threshold: calculate median message count as baseline.
   */
  function updateThroughputThreshold(edges) {
    if (!edges || edges.length === 0) {
      animationState.throughputThreshold = 1;
      return;
    }

    const counts = edges
      .map(e => (e.messageCount || e.MessageCount || 0))
      .sort((a, b) => a - b);
    
    const mid = Math.floor(counts.length / 2);
    animationState.throughputThreshold = Math.max(1, counts[mid] || 1);
    
    console.log('[Animations] Throughput threshold:', animationState.throughputThreshold);
  }

  /**
   * Animate nodes with a running status pulse (brightness + saturation).
   * Pulse intensity reflects recent throughput.
   */
  function animateNodePulse(svg, nodes) {
    console.log('[Animations] Starting node pulse animations for', nodes.length, 'nodes');

    const nodeMap = new Map(nodes.map(n => [n.id || n.Id, n]));

    svg.selectAll('.bm-d3-node').each(function(nodeId) {
      const node = nodeMap.get(nodeId);
      if (!node) return;

      const nodeElement = d3.select(this);
      const rect = nodeElement.select('rect');

      if (!rect.node()) return;

      // Calculate throughput score
      const processed = node.processedCount || node.ProcessedCount || 0;
      const throughputScore = Math.min(1, processed / (animationState.throughputThreshold * 10));
      const pulseFrequency = 800 + (throughputScore * 400); // 800-1200ms
      const pulseIntensity = 0.3 + (throughputScore * 0.4); // 0.3-0.7

      const animationId = setInterval(() => {
        if (!rect.node()) return;

        const currentOpacity = parseFloat(rect.style('opacity')) || 0.85;
        const targetOpacity = currentOpacity < 0.87 ? 0.85 + pulseIntensity : 0.85;

        rect.transition()
          .duration(pulseFrequency / 2)
          .style('opacity', targetOpacity);
      }, pulseFrequency);

      animationState.nodeAnimationIds.set(nodeId, animationId);
    });

    console.log('[Animations] Node pulse animations started');
  }

  /**
   * Animate edges with two strategies:
   * - Low throughput: particle dots flowing along the path
   * - High throughput: edge pulse (thickness variation)
   */
  function animateEdges(svg, nodes, edges) {
    if (!edges || edges.length === 0) {
      console.log('[Animations] No edges to animate');
      return;
    }

    console.log('[Animations] Starting edge animations for', edges.length, 'edges');

    edges.forEach((edge, idx) => {
      const edgeElements = svg.selectAll('.bm-d3-edge');
      
      edgeElements.each(function() {
        const element = d3.select(this);
        const path = element.select('path');
        
        if (!path.empty()) {
          const msgCount = edge.messageCount || edge.MessageCount || 0;
          const isHighThroughput = msgCount > animationState.throughputThreshold;

          if (isHighThroughput) {
            // Animate edge thickness
            animateEdgePulse(path, edge);
          }
        }
      });
    });

    console.log('[Animations] Edge animations started');
  }

  /**
   * Edge animation: pulse + thickness variation at high throughput.
   * Creates a visual "stress" effect on busy edges.
   */
  function animateEdgePulse(path, edge) {
    const msgCount = edge.messageCount || edge.MessageCount || 1;
    const baseStrokeWidth = 2.5;
    const maxStrokeWidth = 2.5 + (Math.min(msgCount / (animationState.throughputThreshold * 2), 1) * 3); // 2.5-5.5px
    const pulseFrequency = 1000 - (Math.min(msgCount / (animationState.throughputThreshold * 3), 1) * 400); // 600-1000ms

    const animationId = setInterval(() => {
      const currentWidth = parseFloat(path.style('stroke-width')) || baseStrokeWidth;
      const targetWidth = currentWidth <= (baseStrokeWidth + maxStrokeWidth) / 2 
        ? maxStrokeWidth 
        : baseStrokeWidth;

      path.transition()
        .duration(pulseFrequency / 2)
        .style('stroke-width', targetWidth + 'px')
        .style('opacity', targetWidth === maxStrokeWidth ? 0.95 : 0.8);
    }, pulseFrequency);

    animationState.edgeAnimationIds.set(`edge-${edge.source}-${edge.target}`, animationId);
  }

  console.log('[Animations] Module initialization complete ✓');
})();
