using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;

// Run everything (reproducible, writes a summary): ./run.sh
// Run all benchmarks:  dotnet run -c Release
// Run specific:        dotnet run -c Release -- --filter '*Sync*'
// Quick test:          dotnet run -c Release -- --filter '*Sync*' --job short
//
// The reference job (5 warmup + 20 measured iterations) is added only when no
// --job/-j argument is given, so a command-line job replaces it instead of
// running in addition to it.
// The generated boilerplate project rebuilds the whole Altruist dependency graph
// single-threaded; BDN's default 2-minute build timeout is too short for that.
IConfig config = ManualConfig.Union(DefaultConfig.Instance, ManualConfig.CreateEmpty().WithBuildTimeout(TimeSpan.FromMinutes(15)));
bool jobOnCommandLine = args.Any(a => a is "--job" or "-j" || a.StartsWith("--job=", StringComparison.Ordinal));
if (!jobOnCommandLine)
{
    config = config.AddJob(Job.Default
        .WithWarmupCount(5)
        .WithIterationCount(20)
        .WithId("Reference"));
}

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
