using AwesomeAssertions;
using NxtUI.Core.Models;
using NxtUI.Web.Services;

namespace NxtUI.Tests.Services;

/// <summary>
/// Covers MockRunService's demo nested-run simulation (docs/12_Custom_Layout_And_Nested_Runs.md
/// §7) — deterministic child assignment, no self-reference, RunNode summary shape, and the
/// childDepth=0 opt-out.
/// </summary>
public class MockRunServiceNestedRunsTests
{
    [Fact]
    public async Task Same_run_id_gets_the_same_children_across_calls()
    {
        var svc = new MockRunService();
        var runId = FindRunIdWithChildren(svc);

        var first = await svc.GetRunDetailsAsync("dev1", runId);
        var second = await svc.GetRunDetailsAsync("dev1", runId);

        first.Children.Select(c => c.RunId).Should().Equal(second.Children.Select(c => c.RunId));
    }

    [Fact]
    public async Task A_run_never_appears_as_its_own_child()
    {
        var svc = new MockRunService();
        var runId = FindRunIdWithChildren(svc);

        var details = await svc.GetRunDetailsAsync("dev1", runId);

        details.Children.Should().NotContain(c => c.RunId == runId);
    }

    [Fact]
    public async Task childDepth_zero_suppresses_children_even_for_a_run_that_would_otherwise_have_them()
    {
        var svc = new MockRunService();
        var runId = FindRunIdWithChildren(svc);

        var details = await svc.GetRunDetailsAsync("dev1", runId, childDepth: 0);

        details.Children.Should().BeEmpty();
    }

    [Fact]
    public async Task Child_summaries_carry_no_counts_matching_the_optional_progress_decision()
    {
        var svc = new MockRunService();
        var runId = FindRunIdWithChildren(svc);

        var details = await svc.GetRunDetailsAsync("dev1", runId);

        details.Children.Should().NotBeEmpty();
        details.Children.Should().OnlyContain(c => c.DoneCount == null && c.TotalCount == null);
    }

    [Fact]
    public async Task A_run_with_no_children_returns_an_empty_list_not_null()
    {
        var svc = new MockRunService();
        var runId = FindRunIdWithoutChildren(svc);

        var details = await svc.GetRunDetailsAsync("dev1", runId);

        details.Children.Should().NotBeNull();
        details.Children.Should().BeEmpty();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string FindRunIdWithChildren(MockRunService svc)
    {
        // The mock's own RunId scheme is internal, but GetRunsAsync exposes the same
        // deterministic store this test needs to search — walk it looking for a run whose
        // details actually come back with children.
        var runs = svc.GetRunsAsync("dev1", DateTime.UtcNow.AddYears(1), 200).GetAwaiter().GetResult();
        foreach (var r in runs)
        {
            var details = svc.GetRunDetailsAsync("dev1", r.RunId).GetAwaiter().GetResult();
            if (details.Children.Count > 0) return r.RunId;
        }
        throw new InvalidOperationException("No run with children found in mock store — adjust the test or the mock's demo ratio.");
    }

    private static string FindRunIdWithoutChildren(MockRunService svc)
    {
        var runs = svc.GetRunsAsync("dev1", DateTime.UtcNow.AddYears(1), 200).GetAwaiter().GetResult();
        foreach (var r in runs)
        {
            var details = svc.GetRunDetailsAsync("dev1", r.RunId).GetAwaiter().GetResult();
            if (details.Children.Count == 0) return r.RunId;
        }
        throw new InvalidOperationException("Every run in the mock store has children — adjust the test or the mock's demo ratio.");
    }
}
