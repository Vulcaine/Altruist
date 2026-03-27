```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.8037)
Intel Core i9-10900KF CPU 3.70GHz, 1 CPU, 20 logical and 10 physical cores
.NET SDK 9.0.304
  [Host]     : .NET 9.0.8 (9.0.825.36511), X64 RyuJIT AVX2
  Job-ESUBKE : .NET 9.0.8 (9.0.825.36511), X64 RyuJIT AVX2

IterationCount=20  WarmupCount=5  

```
| Method                                          | EntityCount | Mean         | Error       | StdDev      | Gen0    | Gen1   | Allocated |
|------------------------------------------------ |------------ |-------------:|------------:|------------:|--------:|-------:|----------:|
| **&#39;Collision Tick (full O(n²) overlap detection)&#39;** | **100**         |  **12,351.0 ns** |   **125.60 ns** |   **139.61 ns** |  **1.2970** | **0.3204** |   **13592 B** |
| &#39;DispatchHit (single pair, no handlers)&#39;        | 100         |     418.6 ns |     7.57 ns |     8.72 ns |  0.1001 |      - |    1048 B |
| RemoveEntity                                    | 100         |     276.8 ns |     1.10 ns |     1.26 ns |       - |      - |         - |
| **&#39;Collision Tick (full O(n²) overlap detection)&#39;** | **500**         | **191,589.2 ns** | **2,200.34 ns** | **2,354.34 ns** | **11.4746** | **0.9766** |  **121848 B** |
| &#39;DispatchHit (single pair, no handlers)&#39;        | 500         |     423.4 ns |     6.08 ns |     7.00 ns |  0.1001 |      - |    1048 B |
| RemoveEntity                                    | 500         |     279.6 ns |     0.17 ns |     0.18 ns |       - |      - |         - |
