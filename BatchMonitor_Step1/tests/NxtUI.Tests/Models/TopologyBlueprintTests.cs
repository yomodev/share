using AwesomeAssertions;
using NxtUI.Core.Models;

namespace NxtUI.Tests.Models;

public class TopologyBlueprintTests
{
    private static TopologyHintFile SampleFile() => new()
    {
        RunType = "FullLoad",
        Variants =
        [
            new TopologyVariant
            {
                Match = new VariantMatch { AnyService = "Ingester*" },
                Layout = new LayoutHint { Direction = "vertical", Shape = "tree" },
                Services =
                [
                    new ServiceHint { Name = "Ingester", Role = "source", Publishes = ["staged"] },
                    new ServiceHint { Name = "Validator", Role = "middle", Subscribes = ["staged"], Publishes = ["validated"] },
                    new ServiceHint { Name = "Loader", Role = "sink", Subscribes = ["validated"] },
                ],
            },
        ],
        Default = new TopologyVariant
        {
            Layout = new LayoutHint { Direction = "horizontal" },
            Services = [new ServiceHint { Name = "Ingester" }],
        },
    };

    [Fact]
    public void SelectVariant_matches_anyService_glob()
    {
        var file = SampleFile();
        var chosen = TopologyBlueprint.SelectVariant(file, ["IngesterA", "Loader"]);
        chosen.Should().BeSameAs(file.Variants[0]);
    }

    [Fact]
    public void SelectVariant_falls_back_to_default_when_nothing_matches()
    {
        var file = SampleFile();
        var chosen = TopologyBlueprint.SelectVariant(file, ["Enricher", "Router"]);
        chosen.Should().BeSameAs(file.Default);
    }

    [Fact]
    public void SelectVariant_returns_null_when_no_match_and_no_default()
    {
        var file = new TopologyHintFile { Variants = [new TopologyVariant { Match = new VariantMatch { AnyService = "X*" } }] };
        TopologyBlueprint.SelectVariant(file, ["Y"]).Should().BeNull();
    }

    [Fact]
    public void Compile_derives_edge_from_shared_publish_subscribe_token()
    {
        var variant = SampleFile().Variants[0];
        var bp = TopologyBlueprint.Compile(variant);

        bp.DeclaredEdges.Should().Contain(("Ingester", "Validator"));
        bp.DeclaredEdges.Should().Contain(("Validator", "Loader"));
        bp.DeclaredEdges.Should().NotContain(("Ingester", "Loader")); // no shared token
    }

    [Fact]
    public void Compile_includes_explicit_edges_and_dedups()
    {
        var variant = new TopologyVariant
        {
            Services =
            [
                new ServiceHint { Name = "A", Publishes = ["t"] },
                new ServiceHint { Name = "B", Subscribes = ["t"] },
            ],
            Edges = [new EdgeHint { From = "A", To = "B" }], // same edge as the derived one
        };

        var bp = TopologyBlueprint.Compile(variant);
        bp.DeclaredEdges.Should().ContainSingle(e => e.From == "A" && e.To == "B");
    }

    [Fact]
    public void Glob_IsMatch_is_case_insensitive_and_supports_wildcards()
    {
        Glob.IsMatch("IngesterA", "ingester*").Should().BeTrue();
        Glob.IsMatch("Loader", "Ingester*").Should().BeFalse();
        Glob.IsMatch("A1", "A?").Should().BeTrue();
    }
}
