```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.8037)
Intel Core i9-10900KF CPU 3.70GHz, 1 CPU, 20 logical and 10 physical cores
.NET SDK 9.0.304
  [Host]     : .NET 9.0.8 (9.0.825.36511), X64 RyuJIT AVX2
  Job-ICAGAI : .NET 9.0.8 (9.0.825.36511), X64 RyuJIT AVX2

IterationCount=20  WarmupCount=5  

```
| Method                                          | EntityCount | Mean           | Error        | StdDev       | Gen0     | Gen1   | Allocated |
|------------------------------------------------ |------------ |---------------:|-------------:|-------------:|---------:|-------:|----------:|
| **&#39;Collision Tick (full O(n²) overlap detection)&#39;** | **100**         |    **88,537.9 ns** |  **1,266.53 ns** |  **1,300.63 ns** |  **16.1133** | **1.2207** |  **169144 B** |
| &#39;DispatchHit (single pair, no handlers)&#39;        | 100         |       421.2 ns |      7.51 ns |      8.35 ns |   0.1001 |      - |    1048 B |
| RemoveEntity                                    | 100         |       275.2 ns |      0.52 ns |      0.60 ns |        - |      - |         - |
| **&#39;Collision Tick (full O(n²) overlap detection)&#39;** | **500**         | **2,118,415.4 ns** | **34,727.59 ns** | **34,107.14 ns** | **386.7188** |      **-** | **4044346 B** |
| &#39;DispatchHit (single pair, no handlers)&#39;        | 500         |       422.7 ns |      6.97 ns |      8.03 ns |   0.1001 |      - |    1048 B |
| RemoveEntity                                    | 500         |       276.6 ns |      1.37 ns |      1.58 ns |        - |      - |         - |
