```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.8037)
Intel Core i9-10900KF CPU 3.70GHz, 1 CPU, 20 logical and 10 physical cores
.NET SDK 9.0.304
  [Host]     : .NET 9.0.8 (9.0.825.36511), X64 RyuJIT AVX2
  Job-ESUBKE : .NET 9.0.8 (9.0.825.36511), X64 RyuJIT AVX2

IterationCount=20  WarmupCount=5  

```
| Method                                          | Mean      | Error    | StdDev   | Gen0   | Allocated |
|------------------------------------------------ |----------:|---------:|---------:|-------:|----------:|
| &#39;GetChangedData - no changes (SyncAlways only)&#39; | 196.83 ns | 3.835 ns | 4.416 ns | 0.0305 |     320 B |
| &#39;GetChangedData - position changed (X+Y)&#39;       | 263.26 ns | 3.442 ns | 3.826 ns | 0.0305 |     320 B |
| &#39;GetChangedData - all properties changed&#39;       | 360.48 ns | 5.222 ns | 5.804 ns | 0.0305 |     320 B |
| &#39;GetChangedData - forceAll (full resync)&#39;       | 363.45 ns | 3.388 ns | 3.766 ns | 0.0305 |     320 B |
| &#39;SyncMetadata lookup (cached)&#39;                  |  16.13 ns | 0.417 ns | 0.463 ns | 0.0099 |     104 B |
