using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NxtUI.Core.Services;

namespace NxtUI.Tests.Services;

/// <summary>Uses the actual shipped sample (config/topology/fullload.json) as its own fixture —
/// this doubles as a regression test that the sample stays valid JSON matching the model.</summary>
public class TopologyHintLoaderTests : IDisposable
{
    private readonly string _basePath = Path.Combine(Path.GetTempPath(), "nxtui-topology-tests-" + Guid.NewGuid());

    public TopologyHintLoaderTests()
    {
        Directory.CreateDirectory(Path.Combine(_basePath, "topology"));
    }

    public void Dispose() => Directory.Delete(_basePath, recursive: true);

    private TopologyHintLoader CreateLoader() =>
        new(new EnvironmentConfigOptions { BasePath = _basePath }, NullLogger<TopologyHintLoader>.Instance);

    [Fact]
    public void Missing_file_returns_null()
    {
        CreateLoader().Get("NoSuchType").Should().BeNull();
    }

    [Fact]
    public void Blank_runType_returns_null_without_touching_disk()
    {
        CreateLoader().Get(null).Should().BeNull();
        CreateLoader().Get("  ").Should().BeNull();
    }

    [Fact]
    public void Loads_the_shipped_sample_and_lowercases_the_lookup()
    {
        var sample = FindShippedSample();
        File.Copy(sample, Path.Combine(_basePath, "topology", "fullload.json"));

        var loader = CreateLoader();
        var hint = loader.Get("FullLoad"); // mixed case — file is fullload.json

        hint.Should().NotBeNull();
        hint!.RunType.Should().Be("FullLoad");
        hint.Variants.Should().ContainSingle();
        hint.Variants[0].Match!.AnyService.Should().Be("Ingester*");
        hint.Variants[0].Services.Should().HaveCount(5);
        hint.Default.Should().NotBeNull();
    }

    [Fact]
    public void Invalid_json_logs_and_returns_null_instead_of_throwing()
    {
        File.WriteAllText(Path.Combine(_basePath, "topology", "broken.json"), "{ not valid json");
        CreateLoader().Get("broken").Should().BeNull();
    }

    [Fact]
    public void Result_is_cached_after_first_load()
    {
        var sample = FindShippedSample();
        File.Copy(sample, Path.Combine(_basePath, "topology", "fullload.json"));
        var loader = CreateLoader();

        var first = loader.Get("FullLoad");
        File.Delete(Path.Combine(_basePath, "topology", "fullload.json")); // prove the second call doesn't re-read
        var second = loader.Get("FullLoad");

        second.Should().BeSameAs(first);
    }

    private static string FindShippedSample()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++, dir = Path.GetDirectoryName(dir))
        {
            var candidate = Path.Combine(dir, "src", "NxtUI.Web", "config", "topology", "fullload.json");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Could not locate config/topology/fullload.json relative to the test binary.");
    }
}
