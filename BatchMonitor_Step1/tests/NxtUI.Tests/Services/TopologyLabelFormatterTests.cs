using AwesomeAssertions;
using NxtUI.Core.Services;

namespace NxtUI.Tests.Services;

public class TopologyLabelFormatterTests
{
    private static readonly string[] DefaultWords = ["Pipeline", "ABC", "{EnvID}"];

    [Fact]
    public void Removes_trailing_pipeline_word()
    {
        TopologyLabelFormatter.Strip("CbLegacyDiscoveryPipeline", DefaultWords, "AXIS-DEV1")
            .Should().Be("CbLegacyDiscovery");
    }

    [Fact]
    public void Removes_env_id_and_trims_trailing_separator()
    {
        TopologyLabelFormatter.Strip("CcrBatchConf_AXIS-DEV1", DefaultWords, "AXIS-DEV1")
            .Should().Be("CcrBatchConf");
    }

    [Fact]
    public void Removes_env_id_from_middle_and_collapses_doubled_separator()
    {
        // "AXIS-DEV1" cut out of the middle leaves "CcrBatchConf__Incremental" -> collapse.
        TopologyLabelFormatter.Strip("CcrBatchConf_AXIS-DEV1_Incremental", DefaultWords, "AXIS-DEV1")
            .Should().Be("CcrBatchConf_Incremental");
    }

    [Fact]
    public void Removes_env_id_and_pipeline_word_together()
    {
        TopologyLabelFormatter.Strip("CcrBatchConf_AXIS-DEV1_Incremental_S_PRA_Pipeline", DefaultWords, "AXIS-DEV1")
            .Should().Be("CcrBatchConf_Incremental_S_PRA");
    }

    [Fact]
    public void Matching_is_case_insensitive()
    {
        TopologyLabelFormatter.Strip("SomethingPIPELINE", DefaultWords, "DEV1")
            .Should().Be("Something");
    }

    [Fact]
    public void Keeps_original_when_stripping_would_empty_it()
    {
        TopologyLabelFormatter.Strip("Pipeline", DefaultWords, "DEV1")
            .Should().Be("Pipeline");
    }

    [Fact]
    public void Null_or_empty_word_list_returns_input_unchanged()
    {
        TopologyLabelFormatter.Strip("CcrBatchConf_AXIS-DEV1", null, "AXIS-DEV1")
            .Should().Be("CcrBatchConf_AXIS-DEV1");
        TopologyLabelFormatter.Strip("CcrBatchConf_AXIS-DEV1", [], "AXIS-DEV1")
            .Should().Be("CcrBatchConf_AXIS-DEV1");
    }

    [Fact]
    public void Empty_env_id_skips_the_env_token_only()
    {
        // {EnvID} resolves to empty -> skipped; other words still applied.
        TopologyLabelFormatter.Strip("FooPipeline_AXIS-DEV1", DefaultWords, "")
            .Should().Be("Foo_AXIS-DEV1");
    }

    [Fact]
    public void Removes_literal_abc_word()
    {
        TopologyLabelFormatter.Strip("ABC_WcrFacilities", DefaultWords, "DEV1")
            .Should().Be("WcrFacilities");
    }
}
