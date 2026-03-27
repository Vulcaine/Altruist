```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.8037)
Intel Core i9-10900KF CPU 3.70GHz, 1 CPU, 20 logical and 10 physical cores
.NET SDK 9.0.304
  [Host]     : .NET 9.0.8 (9.0.825.36511), X64 RyuJIT AVX2
  Job-LGNPJR : .NET 9.0.8 (9.0.825.36511), X64 RyuJIT AVX2

IterationCount=20  WarmupCount=5  

```
| Method                                          | Mean      | Error    | StdDev    | Gen0   | Allocated |
|------------------------------------------------ |----------:|---------:|----------:|-------:|----------:|
| &#39;GetChangedData - no changes (SyncAlways only)&#39; | 197.95 ns | 3.631 ns |  3.885 ns | 0.0305 |     320 B |
| &#39;GetChangedData - position changed (X+Y)&#39;       | 249.15 ns | 7.330 ns |  8.147 ns | 0.0305 |     320 B |
| &#39;GetChangedData - all properties changed&#39;       | 362.96 ns | 9.689 ns | 11.158 ns | 0.0305 |     320 B |
| &#39;GetChangedData - forceAll (full resync)&#39;       | 372.34 ns | 6.105 ns |  7.030 ns | 0.0305 |     320 B |
| &#39;SyncMetadata lookup (cached)&#39;                  |  17.34 ns | 1.073 ns |  1.192 ns | 0.0099 |     104 B |
