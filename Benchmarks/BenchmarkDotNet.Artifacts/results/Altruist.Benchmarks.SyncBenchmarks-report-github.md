```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.8037)
Intel Core i9-10900KF CPU 3.70GHz, 1 CPU, 20 logical and 10 physical cores
.NET SDK 9.0.304
  [Host]     : .NET 9.0.8 (9.0.825.36511), X64 RyuJIT AVX2
  Job-RQLSLI : .NET 9.0.8 (9.0.825.36511), X64 RyuJIT AVX2

IterationCount=20  WarmupCount=5  

```
| Method                                          | Mean      | Error     | StdDev    | Gen0   | Allocated |
|------------------------------------------------ |----------:|----------:|----------:|-------:|----------:|
| &#39;GetChangedData - no changes (SyncAlways only)&#39; | 202.68 ns |  3.897 ns |  4.002 ns | 0.0305 |     320 B |
| &#39;GetChangedData - position changed (X+Y)&#39;       | 264.15 ns |  5.219 ns |  6.010 ns | 0.0305 |     320 B |
| &#39;GetChangedData - all properties changed&#39;       | 377.66 ns | 41.855 ns | 42.982 ns | 0.0305 |     320 B |
| &#39;GetChangedData - forceAll (full resync)&#39;       | 379.92 ns | 11.220 ns | 12.006 ns | 0.0305 |     320 B |
| &#39;SyncMetadata lookup (cached)&#39;                  |  17.47 ns |  0.994 ns |  1.105 ns | 0.0099 |     104 B |
