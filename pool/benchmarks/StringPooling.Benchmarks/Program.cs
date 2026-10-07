using BenchmarkDotNet.Running;
using StringPooling.Benchmarks;

if (args.Length > 0 && args[0] == "quality")
{
    QualityReport.Run();
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(SingleThreadBenchmarks).Assembly).Run(args);
