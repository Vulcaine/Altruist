using BenchmarkDotNet.Running;

// Run all benchmarks: dotnet run -c Release
// Run specific:       dotnet run -c Release --filter *Sync*
// Quick test:         dotnet run -c Release --filter *Sync* --job short
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
