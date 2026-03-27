```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.8037)
Intel Core i9-10900KF CPU 3.70GHz, 1 CPU, 20 logical and 10 physical cores
.NET SDK 9.0.304
  [Host]     : .NET 9.0.8 (9.0.825.36511), X64 RyuJIT AVX2
  Job-PSVTUE : .NET 9.0.8 (9.0.825.36511), X64 RyuJIT AVX2
  ShortRun   : .NET 9.0.8 (9.0.825.36511), X64 RyuJIT AVX2

WarmupCount=3  

```
| Method                                          | Job        | IterationCount | LaunchCount | Mean      | Error      | StdDev   | Gen0   | Allocated |
|------------------------------------------------ |----------- |--------------- |------------ |----------:|-----------:|---------:|-------:|----------:|
| &#39;GetChangedData - no changes (SyncAlways only)&#39; | Job-PSVTUE | 5              | Default     | 208.23 ns |  27.834 ns | 7.228 ns | 0.0305 |     320 B |
| &#39;GetChangedData - position changed (X+Y)&#39;       | Job-PSVTUE | 5              | Default     | 253.70 ns |  12.711 ns | 3.301 ns | 0.0305 |     320 B |
| &#39;GetChangedData - all properties changed&#39;       | Job-PSVTUE | 5              | Default     | 363.37 ns |  38.972 ns | 6.031 ns | 0.0305 |     320 B |
| &#39;GetChangedData - forceAll (full resync)&#39;       | Job-PSVTUE | 5              | Default     | 361.88 ns |  12.288 ns | 1.902 ns | 0.0305 |     320 B |
| &#39;SyncMetadata lookup (cached)&#39;                  | Job-PSVTUE | 5              | Default     |  18.57 ns |   7.092 ns | 1.842 ns | 0.0099 |     104 B |
| &#39;GetChangedData - no changes (SyncAlways only)&#39; | ShortRun   | 3              | 1           | 201.96 ns |  17.744 ns | 0.973 ns | 0.0305 |     320 B |
| &#39;GetChangedData - position changed (X+Y)&#39;       | ShortRun   | 3              | 1           | 260.63 ns |  66.520 ns | 3.646 ns | 0.0305 |     320 B |
| &#39;GetChangedData - all properties changed&#39;       | ShortRun   | 3              | 1           | 370.76 ns | 125.504 ns | 6.879 ns | 0.0305 |     320 B |
| &#39;GetChangedData - forceAll (full resync)&#39;       | ShortRun   | 3              | 1           | 359.85 ns |  69.755 ns | 3.824 ns | 0.0305 |     320 B |
| &#39;SyncMetadata lookup (cached)&#39;                  | ShortRun   | 3              | 1           |  15.91 ns |  17.074 ns | 0.936 ns | 0.0099 |     104 B |
