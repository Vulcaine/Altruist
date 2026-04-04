```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.8037)
Intel Core i9-10900KF CPU 3.70GHz, 1 CPU, 20 logical and 10 physical cores
.NET SDK 9.0.304
  [Host]     : .NET 9.0.8 (9.0.825.36511), X64 RyuJIT AVX2
  Job-QUOQZX : .NET 9.0.8 (9.0.825.36511), X64 RyuJIT AVX2

IterationCount=20  WarmupCount=5  

```
| Method                                          | EntityCount | Mean          | Error        | StdDev       | Gen0   | Gen1   | Allocated |
|------------------------------------------------ |------------ |--------------:|-------------:|-------------:|-------:|-------:|----------:|
| **&#39;Collision Tick (full O(n²) overlap detection)&#39;** | **100**         |  **13,342.32 ns** |   **232.603 ns** |   **248.883 ns** | **1.0223** | **0.2594** |   **10736 B** |
| &#39;DispatchHit (single pair, no handlers)&#39;        | 100         |     425.79 ns |     4.706 ns |     5.420 ns | 0.0944 |      - |     992 B |
| RemoveEntity                                    | 100         |      44.80 ns |     0.160 ns |     0.157 ns |      - |      - |         - |
| **&#39;Collision Tick (full O(n²) overlap detection)&#39;** | **500**         | **204,388.44 ns** | **1,314.160 ns** | **1,460.686 ns** | **4.8828** | **0.4883** |   **52336 B** |
| &#39;DispatchHit (single pair, no handlers)&#39;        | 500         |     428.12 ns |     4.860 ns |     5.402 ns | 0.0944 |      - |     992 B |
| RemoveEntity                                    | 500         |      48.49 ns |     3.743 ns |     4.311 ns |      - |      - |         - |
