```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.8037)
Intel Core i9-10900KF CPU 3.70GHz, 1 CPU, 20 logical and 10 physical cores
.NET SDK 9.0.304
  [Host]     : .NET 9.0.8 (9.0.825.36511), X64 RyuJIT AVX2
  Job-VJJISO : .NET 9.0.8 (9.0.825.36511), X64 RyuJIT AVX2
  ShortRun   : .NET 9.0.8 (9.0.825.36511), X64 RyuJIT AVX2


```
| Method                                          | Job        | IterationCount | LaunchCount | WarmupCount | Mean      | Error     | StdDev   | Gen0   | Allocated |
|------------------------------------------------ |----------- |--------------- |------------ |------------ |----------:|----------:|---------:|-------:|----------:|
| &#39;GetChangedData - no changes (SyncAlways only)&#39; | Job-VJJISO | 20             | Default     | 5           | 198.36 ns |  5.466 ns | 6.295 ns | 0.0305 |     320 B |
| &#39;GetChangedData - position changed (X+Y)&#39;       | Job-VJJISO | 20             | Default     | 5           | 237.22 ns |  3.052 ns | 3.392 ns | 0.0305 |     320 B |
| &#39;GetChangedData - all properties changed&#39;       | Job-VJJISO | 20             | Default     | 5           | 347.13 ns |  2.034 ns | 2.260 ns | 0.0305 |     320 B |
| &#39;GetChangedData - forceAll (full resync)&#39;       | Job-VJJISO | 20             | Default     | 5           | 370.84 ns |  2.496 ns | 2.775 ns | 0.0305 |     320 B |
| &#39;SyncMetadata lookup (cached)&#39;                  | Job-VJJISO | 20             | Default     | 5           |  15.87 ns |  0.250 ns | 0.246 ns | 0.0099 |     104 B |
| &#39;GetChangedData - no changes (SyncAlways only)&#39; | ShortRun   | 3              | 1           | 3           | 201.71 ns |  9.727 ns | 0.533 ns | 0.0305 |     320 B |
| &#39;GetChangedData - position changed (X+Y)&#39;       | ShortRun   | 3              | 1           | 3           | 237.35 ns | 53.418 ns | 2.928 ns | 0.0305 |     320 B |
| &#39;GetChangedData - all properties changed&#39;       | ShortRun   | 3              | 1           | 3           | 353.99 ns | 13.082 ns | 0.717 ns | 0.0305 |     320 B |
| &#39;GetChangedData - forceAll (full resync)&#39;       | ShortRun   | 3              | 1           | 3           | 370.61 ns |  7.212 ns | 0.395 ns | 0.0305 |     320 B |
| &#39;SyncMetadata lookup (cached)&#39;                  | ShortRun   | 3              | 1           | 3           |  16.31 ns | 13.618 ns | 0.746 ns | 0.0099 |     104 B |
