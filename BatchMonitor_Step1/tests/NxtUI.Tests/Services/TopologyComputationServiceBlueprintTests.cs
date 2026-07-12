using AwesomeAssertions;
using NxtUI.Core.Models;
using NxtUI.Core.Services;

namespace NxtUI.Tests.Services;

/// <summary>
/// Covers the topology-hint merge (ApplyBlueprint) — declared vs observed reconciliation,
/// skeleton nodes/edges, and the empty-store path. Uses Mock-shaped data (same service
/// names/pipelines as MockRunService) so this doubles as the "does the sample hint actually
/// do something against the mock" check requested alongside config/topology/fullload.json.
/// </summary>
public class TopologyComputationServiceBlueprintTests
{
    private static TopologyVariant SampleVariant() => new()
    {
        Layout = new LayoutHint { Direction = "vertical", Shape = "tree" },
        Services =
        [
            new ServiceHint { Name = "Ingester", Role = "source", Group = "Ingest", Publishes = ["staged"] },
            new ServiceHint { Name = "Validator", Role = "middle", Group = "Validate", Subscribes = ["staged"], Publishes = ["validated"] },
            new ServiceHint { Name = "Loader", Role = "sink", Group = "Persist", Subscribes = ["validated"], Color = "#A371F7" },
        ],
    };

    private static PerformanceEvent Evt(string name, string service, string pipeline, DateTime start, DateTime? finish = null) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = name,
        Service = service,
        Pipeline = pipeline,
        Server = "srv-01",
        ProcessId = 1234,
        Start = start,
        Finish = finish,
    };

    [Fact]
    public void Observed_service_matching_a_hint_is_decorated()
    {
        var t0 = DateTime.UtcNow;
        var events = new Dictionary<string, PerformanceEvent>
        {
            ["e1"] = Evt("chunk1", "Ingester", "csv-ingest", t0, t0.AddSeconds(1)),
        };

        var blueprint = TopologyBlueprint.Compile(SampleVariant());
        var svc = new TopologyComputationService();
        var topo = svc.ComputeTopology(events, blueprint: blueprint);

        var node = topo.Nodes.Should().ContainSingle(n => n.Id == "Ingester").Subject;
        node.IsDeclared.Should().BeTrue();
        node.IsObserved.Should().BeTrue();
        node.Role.Should().Be("source");
        node.Group.Should().Be("Ingest");
    }

    [Fact]
    public void Declared_service_never_observed_renders_as_greyed_skeleton()
    {
        var t0 = DateTime.UtcNow;
        var events = new Dictionary<string, PerformanceEvent>
        {
            // Only Ingester appears — Validator/Loader are declared but never seen.
            ["e1"] = Evt("chunk1", "Ingester", "csv-ingest", t0, t0.AddSeconds(1)),
        };

        var blueprint = TopologyBlueprint.Compile(SampleVariant());
        var svc = new TopologyComputationService();
        var topo = svc.ComputeTopology(events, blueprint: blueprint);

        var loader = topo.Nodes.Should().ContainSingle(n => n.Id == "Loader").Subject;
        loader.IsDeclared.Should().BeTrue();
        loader.IsObserved.Should().BeFalse();
        loader.HeaderState.Should().Be(PipelineState.NotStarted);
        loader.Color.Should().Be("#A371F7");
    }

    [Fact]
    public void Observed_service_not_in_blueprint_is_flagged_undeclared()
    {
        var t0 = DateTime.UtcNow;
        var events = new Dictionary<string, PerformanceEvent>
        {
            // "Enricher" isn't in SampleVariant's services at all.
            ["e1"] = Evt("chunk1", "Enricher", "field-map", t0, t0.AddSeconds(1)),
        };

        var blueprint = TopologyBlueprint.Compile(SampleVariant());
        var svc = new TopologyComputationService();
        var topo = svc.ComputeTopology(events, blueprint: blueprint);

        var node = topo.Nodes.Should().ContainSingle(n => n.Id == "Enricher").Subject;
        node.IsDeclared.Should().BeFalse();
        node.IsObserved.Should().BeTrue();
    }

    [Fact]
    public void Declared_edge_between_two_concrete_nodes_is_added_as_skeleton_when_not_observed()
    {
        var t0 = DateTime.UtcNow;
        var events = new Dictionary<string, PerformanceEvent>
        {
            // Ingester and Validator both appear, but never on the SAME chunk (Name), so no
            // real chunk-flow edge is inferred — the declared edge should still show, faint.
            ["e1"] = Evt("chunkA", "Ingester", "csv-ingest", t0, t0.AddSeconds(1)),
            ["e2"] = Evt("chunkB", "Validator", "schema-check", t0, t0.AddSeconds(1)),
        };

        var blueprint = TopologyBlueprint.Compile(SampleVariant());
        var svc = new TopologyComputationService();
        var topo = svc.ComputeTopology(events, blueprint: blueprint);

        var edge = topo.Edges.Should().ContainSingle(e => e.Source == "Ingester" && e.Target == "Validator").Subject;
        edge.IsDeclared.Should().BeTrue();
        edge.IsObserved.Should().BeFalse();
    }

    [Fact]
    public void Declared_edge_actually_observed_is_marked_both_declared_and_observed()
    {
        var t0 = DateTime.UtcNow;
        var events = new Dictionary<string, PerformanceEvent>
        {
            // Same chunk "chunk1" hops Ingester -> Validator -> a real chunk-flow edge.
            ["e1"] = Evt("chunk1", "Ingester", "csv-ingest", t0, t0.AddSeconds(1)),
            ["e2"] = Evt("chunk1", "Validator", "schema-check", t0.AddSeconds(2), t0.AddSeconds(3)),
        };

        var blueprint = TopologyBlueprint.Compile(SampleVariant());
        var svc = new TopologyComputationService();
        var topo = svc.ComputeTopology(events, blueprint: blueprint);

        var edge = topo.Edges.Should().ContainSingle(e => e.Source == "Ingester" && e.Target == "Validator").Subject;
        edge.IsDeclared.Should().BeTrue();
        edge.IsObserved.Should().BeTrue();
    }

    [Fact]
    public void Empty_event_store_with_blueprint_still_renders_the_full_skeleton()
    {
        var blueprint = TopologyBlueprint.Compile(SampleVariant());
        var svc = new TopologyComputationService();
        var topo = svc.ComputeTopology(new Dictionary<string, PerformanceEvent>(), blueprint: blueprint);

        topo.Nodes.Select(n => n.Id).Should().BeEquivalentTo(["Ingester", "Validator", "Loader"]);
        topo.Nodes.Should().OnlyContain(n => n.HeaderState == PipelineState.NotStarted && !n.IsObserved);
        topo.Layout.Should().NotBeNull();
        topo.Layout!.Shape.Should().Be("tree");
        topo.HasBlueprint.Should().BeTrue();
    }

    [Fact]
    public void No_blueprint_behaves_exactly_as_before()
    {
        var t0 = DateTime.UtcNow;
        var events = new Dictionary<string, PerformanceEvent>
        {
            ["e1"] = Evt("chunk1", "Ingester", "csv-ingest", t0, t0.AddSeconds(1)),
        };

        var svc = new TopologyComputationService();
        var topo = svc.ComputeTopology(events);

        topo.Layout.Should().BeNull();
        topo.Nodes.Should().ContainSingle(n => n.Id == "Ingester" && !n.IsDeclared);
    }

    [Fact]
    public void No_blueprint_sets_HasBlueprint_false_even_though_IsDeclared_defaults_false_on_every_node()
    {
        // Regression test: the UI's "undeclared" (dashed border) styling used to be computed
        // purely from IsObserved && !IsDeclared — which is true for EVERY node when no
        // blueprint applies at all (IsDeclared just defaults to false), making every service
        // render dashed with no hint file present. HasBlueprint is the explicit signal the UI
        // gates on instead, so it must be false here regardless of the nodes' IsDeclared value.
        var t0 = DateTime.UtcNow;
        var events = new Dictionary<string, PerformanceEvent>
        {
            ["e1"] = Evt("chunk1", "Ingester", "csv-ingest", t0, t0.AddSeconds(1)),
            ["e2"] = Evt("chunk2", "Enricher", "field-map", t0, t0.AddSeconds(1)),
        };

        var svc = new TopologyComputationService();
        var topo = svc.ComputeTopology(events); // no blueprint passed

        topo.HasBlueprint.Should().BeFalse();
        topo.Nodes.Should().OnlyContain(n => !n.IsDeclared); // would look "undeclared" if not gated
    }

    [Fact]
    public void Blueprint_sets_HasBlueprint_true()
    {
        var blueprint = TopologyBlueprint.Compile(SampleVariant());
        var svc = new TopologyComputationService();
        var topo = svc.ComputeTopology(new Dictionary<string, PerformanceEvent>(), blueprint: blueprint);

        topo.HasBlueprint.Should().BeTrue();
    }

    [Fact]
    public void Declared_group_colour_flows_through_to_Topology_GroupColors()
    {
        var variant = SampleVariant();
        variant.Groups = [new GroupHint { Name = "Ingest", Color = "#3FB950" }];
        var blueprint = TopologyBlueprint.Compile(variant);

        var svc = new TopologyComputationService();
        var topo = svc.ComputeTopology(new Dictionary<string, PerformanceEvent>(), blueprint: blueprint);

        topo.GroupColors.Should().ContainKey("Ingest").WhoseValue.Should().Be("#3FB950");
        // "Validate"/"Persist" are real groups on other services in SampleVariant but were
        // never listed in variant.Groups — they keep the default cosmetic band (no entry).
        topo.GroupColors.Should().NotContainKey("Validate");
    }

    [Fact]
    public void Group_colour_lookup_is_case_insensitive()
    {
        var variant = SampleVariant();
        variant.Groups = [new GroupHint { Name = "INGEST", Color = "#3FB950" }];
        var blueprint = TopologyBlueprint.Compile(variant);

        blueprint.GroupColors.Should().ContainKey("Ingest"); // declared as "INGEST"
    }

    [Fact]
    public void No_groups_declared_means_empty_GroupColors_not_null()
    {
        var blueprint = TopologyBlueprint.Compile(SampleVariant()); // SampleVariant declares no Groups
        var svc = new TopologyComputationService();
        var topo = svc.ComputeTopology(new Dictionary<string, PerformanceEvent>(), blueprint: blueprint);

        topo.GroupColors.Should().NotBeNull();
        topo.GroupColors.Should().BeEmpty();
    }
}
