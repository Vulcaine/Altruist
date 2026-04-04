```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.8037)
Intel Core i9-10900KF CPU 3.70GHz, 1 CPU, 20 logical and 10 physical cores
.NET SDK 9.0.304
  [Host]     : .NET 9.0.8 (9.0.825.36511), X64 RyuJIT AVX2
  Job-LIIQXQ : .NET 9.0.8 (9.0.825.36511), X64 RyuJIT AVX2
  ShortRun   : .NET 9.0.8 (9.0.825.36511), X64 RyuJIT AVX2


```
| Method                                          | Job        | IterationCount | LaunchCount | WarmupCount | Mean      | Error     | StdDev   | Gen0   | Allocated |
|------------------------------------------------ |----------- |--------------- |------------ |------------ |----------:|----------:|---------:|-------:|----------:|
| &#39;GetChangedData - no changes (SyncAlways only)&#39; | Job-LIIQXQ | 20             | Default     | 5           | 197.90 ns |  1.718 ns | 1.838 ns | 0.0305 |     320 B |
| &#39;GetChangedData - position changed (X+Y)&#39;       | Job-LIIQXQ | 20             | Default     | 5           | 237.88 ns |  3.387 ns | 3.764 ns | 0.0305 |     320 B |
| &#39;GetChangedData - all properties changed&#39;       | Job-LIIQXQ | 20             | Default     | 5           | 348.68 ns |  2.141 ns | 2.466 ns | 0.0305 |     320 B |
| &#39;GetChangedData - forceAll (full resync)&#39;       | Job-LIIQXQ | 20             | Default     | 5           | 363.69 ns |  2.241 ns | 2.580 ns | 0.0305 |     320 B |
| &#39;GetSyncChanges - struct API (no changes)&#39;      | Job-LIIQXQ | 20             | Default     | 5           | 196.66 ns |  2.837 ns | 3.268 ns | 0.0305 |     320 B |
| &#39;GetSyncChanges - position changed&#39;             | Job-LIIQXQ | 20             | Default     | 5           | 249.02 ns |  2.613 ns | 2.904 ns | 0.0305 |     320 B |
| &#39;SyncMetadata lookup (cached)&#39;                  | Job-LIIQXQ | 20             | Default     | 5           |  16.63 ns |  0.924 ns | 1.064 ns | 0.0099 |     104 B |
| &#39;GetChangedData - no changes (SyncAlways only)&#39; | ShortRun   | 3              | 1           | 3           | 201.73 ns | 71.098 ns | 3.897 ns | 0.0305 |     320 B |
| &#39;GetChangedData - position changed (X+Y)&#39;       | ShortRun   | 3              | 1           | 3           | 238.72 ns | 31.644 ns | 1.735 ns | 0.0305 |     320 B |
| &#39;GetChangedData - all properties changed&#39;       | ShortRun   | 3              | 1           | 3           | 361.48 ns | 53.357 ns | 2.925 ns | 0.0305 |     320 B |
| &#39;GetChangedData - forceAll (full resync)&#39;       | ShortRun   | 3              | 1           | 3           | 362.40 ns | 27.986 ns | 1.534 ns | 0.0305 |     320 B |
| &#39;GetSyncChanges - struct API (no changes)&#39;      | ShortRun   | 3              | 1           | 3           | 199.22 ns | 48.908 ns | 2.681 ns | 0.0305 |     320 B |
| &#39;GetSyncChanges - position changed&#39;             | ShortRun   | 3              | 1           | 3           | 235.37 ns | 37.792 ns | 2.072 ns | 0.0305 |     320 B |
| &#39;SyncMetadata lookup (cached)&#39;                  | ShortRun   | 3              | 1           | 3           |  15.36 ns |  4.490 ns | 0.246 ns | 0.0099 |     104 B |
