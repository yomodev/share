namespace NxtUI.Core.Models;

using System.Text.Json.Serialization;

// String converter (matching PipelineState's own convention in Topology.cs) so the
// child-run blocks' status reaches d3-graph.js as a readable string ("Running", "Completed",
// ...) instead of a raw enum int — this is the first JS consumer of RunStatus.
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RunStatus
{
    Unknown,
    Running,
    Completed,
    Terminated,
    Failed,
    Purged,
}
