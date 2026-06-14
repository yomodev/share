// D3 Animation System for Batch Monitor Flow Graph
// Handles node pulse effects, edge animations, and throughput indicators

(function() {
  'use strict';

  window.bm_animations = window.bm_animations || {};

  // Animation state
  let animationState = {
    nodeAnimationIds: new Map(),
    edgeAnimationIds: new Map(),
    throughputThreshold: 0,
    lastThroughputUpdate: 0,
    isRunning: false
  };

  /**
   * Initialize animations for the given SVG and topology.
   * @param {Object} svg - D3 selection of SVG element
   * @param {Object} topology - Topology object with nodes and edges
   */
  window.bm_animations.initializeAnimations = function(svg, topology) {
    if (!svg || !topology) return;

    // Stop existing animations
    window.bm_animations.stopAnimations();

    // Calculate adaptive throughput threshold
    updateThroughputThreshold(topology);

    // Start node pulse animations
    animateNodePulse(svg, topology);

    // Start edge animations (particles + pulse)
    animateEdges(svg, topology);

    animationState.isRunning = true;
  };

  /**
   * Stop all active animations.
   */
  window.bm_animations.stopAnimations = function() {
    // Clear node intervals
    animationState.nodeAnimationIds.forEach(id => clearInterval(id));
    animationState.nodeAnimationIds.clear();

    // Clear edge intervals
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

  // ── Private helpers ──────────────────────────────────────────────────

  /**
   * Adaptive threshold: calculate median message count as baseline.
   */
  function updateThroughputThreshold(topology) {
    if (!topology.Edges || topology.Edges.length === 0) {
      animationState.throughputThreshold = 1;
      return;
    }

    const counts = topology.Edges.map(e => e.MessageCount).sort((a, b) => a - b);
    const mid = Math.floor(counts.length / 2);
    animationState.throughputThreshold = counts[mid] || 1;
    animationState.lastThroughputUpdate = Date.now();

    console.log(`[Animations] Throughput threshold set to: ${animationState.throughputThreshold}`);
  }

  /**
   * Animate nodes with a running status pulse (brightness + saturation).
   * Pulse intensity reflects recent throughput.
   */
  function animateNodePulse(svg, topology) {
    const nodeMap = new Map(topology.Nodes.map(n => [n.Id, n]));

    svg.selectAll('.bm-d3-node').each(function(nodeId) {
      const node = nodeMap.get(nodeId);
      if (!node) return;

      const nodeElement = d3.select(this);
      const rect = nodeElement.select('rect');

      // Calculate recent throughput score (0-1 scale)
      const throughputScore = Math.min(1, node.ProcessedCount / (animationState.throughputThreshold * 10));

      // Pulse frequency and intensity scale with throughput
      const pulseFrequency = 800 + (throughputScore * 400); // 800-1200ms
      const pulseIntensity = 0.3 + (throughputScore * 0.4); // 0.3-0.7

      const animationId = setInterval(() => {
        if (!rect.node()) return;

        const currentOpacity = parseFloat(rect.style('opacity')) || 0.8;
        const targetOpacity = currentOpacity < 0.85 ? 0.8 + pulseIntensity : 0.8;

        rect.transition()
          .duration(pulseFrequency / 2)
          .style('opacity', targetOpacity);
      }, pulseFrequency);

      animationState.nodeAnimationIds.set(nodeId, animationId);
    });
  }

  /**
   * Animate edges with two strategies:
   * - Low throughput: particle dots flowing along the path
   * - High throughput: edge pulse (thickness variation)
   */
  function animateEdges(svg, topology) {
    if (!topology.Edges || topology.Edges.length === 0) return;

    topology.Edges.forEach((edge, idx) => {
      const edgeElement = svg.select(`g.bm-d3-edge:nth-child(${idx + 1})`);
      if (!edgeElement.empty()) {
        const path = edgeElement.select('path');
        if (!path.empty()) {
          // Determine animation type based on throughput
          const isHighThroughput = edge.MessageCount > animationState.throughputThreshold;

          if (isHighThroughput) {
            animateEdgePulse(path, edge);
          } else {
            animateEdgeParticles(edgeElement, path, edge);
          }
        }
      }
    });
  }

  /**
   * Edge animation: particle dots at low throughput.
   * Creates a visual flow of particles from source to target.
   */
  function animateEdgeParticles(edgeElement, path, edge) {
    const pathElement = path.node();
    if (!pathElement || pathElement.getTotalLength === undefined) return;

    const pathLength = pathElement.getTotalLength();
    const particleCount = Math.max(1, Math.floor(edge.MessageCount / 5));
    const animationDuration = 3000 + (edge.MessageCount * 100); // Slower at low throughput

    // Create particle container if not exists
    let particleContainer = edgeElement.select('.bm-particles');
    if (particleContainer.empty()) {
      particleContainer = edgeElement.append('g').attr('class', 'bm-particles');
    }

    // Add particles
    for (let i = 0; i < particleCount; i++) {
      const delay = (i / particleCount) * animationDuration;

      const particle = particleContainer.append('circle')
        .attr('r', 3)
        .attr('fill', '#4CAF50')
        .attr('opacity', 0.7);

      // Animate particle along path
      animateParticleAlongPath(particle, path, animationDuration, delay);
    }
  }

  /**
   * Animate a single particle along a path using SMIL animation.
   */
  function animateParticleAlongPath(particle, pathElement, duration, delay) {
    const path = pathElement.node();
    if (!path || !path.getTotalLength) return;

    const pathLength = path.getTotalLength();

    // Use requestAnimationFrame for smooth animation
    let lastTime = Date.now() + delay;

    const animateParticle = () => {
      const now = Date.now();
      const elapsed = (now - lastTime) % duration;
      const progress = elapsed / duration;

      const point = path.getPointAtLength(progress * pathLength);
      particle.attr('cx', point.x).attr('cy', point.y);

      if (animationState.isRunning) {
        requestAnimationFrame(animateParticle);
      }
    };

    // Start animation after delay
    setTimeout(() => {
      if (animationState.isRunning) {
        animateParticle();
      }
    }, delay);
  }

  /**
   * Edge animation: pulse + thickness variation at high throughput.
   * Creates a visual "stress" effect on busy edges.
   */
  function animateEdgePulse(path, edge) {
    const baseStrokeWidth = 2;
    const maxStrokeWidth = 2 + (Math.min(edge.MessageCount / (animationState.throughputThreshold * 2), 1) * 4); // 2-6px
    const pulseFrequency = 1000 - (Math.min(edge.MessageCount / (animationState.throughputThreshold * 3), 1) * 500); // 500-1000ms

    const animationId = setInterval(() => {
      const currentWidth = parseFloat(path.style('stroke-width')) || baseStrokeWidth;
      const targetWidth = currentWidth <= (baseStrokeWidth + maxStrokeWidth) / 2 
        ? maxStrokeWidth 
        : baseStrokeWidth;

      path.transition()
        .duration(pulseFrequency / 2)
        .style('stroke-width', targetWidth + 'px')
        .style('opacity', targetWidth === maxStrokeWidth ? 1 : 0.8);
    }, pulseFrequency);

    animationState.edgeAnimationIds.set(`edge-${edge.Source}-${edge.Target}`, animationId);
  }

})();
