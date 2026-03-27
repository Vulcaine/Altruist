```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.8037)
Intel Core i9-10900KF CPU 3.70GHz, 1 CPU, 20 logical and 10 physical cores
.NET SDK 9.0.304
  [Host]     : .NET 9.0.8 (9.0.825.36511), X64 RyuJIT AVX2
  Job-GXTHQJ : .NET 9.0.8 (9.0.825.36511), X64 RyuJIT AVX2

IterationCount=20  WarmupCount=5  

```
| Method                                       | PlayerCount | NpcCount | Mean     | Error   | StdDev  | Gen0    | Gen1   | Allocated |
|--------------------------------------------- |------------ |--------- |---------:|--------:|--------:|--------:|-------:|----------:|
| **&#39;Visibility Tick (steady state, no changes)&#39;** | **10**          | **100**      | **107.3 μs** | **0.76 μs** | **0.84 μs** |  **3.9063** | **0.8545** |  **36.22 KB** |
| **&#39;Visibility Tick (steady state, no changes)&#39;** | **10**          | **1000**     | **429.4 μs** | **7.14 μs** | **7.94 μs** | **17.5781** | **3.4180** | **170.48 KB** |
| **&#39;Visibility Tick (steady state, no changes)&#39;** | **50**          | **100**      | **186.8 μs** | **1.37 μs** | **1.41 μs** |  **4.3945** | **0.9766** |  **42.58 KB** |
| **&#39;Visibility Tick (steady state, no changes)&#39;** | **50**          | **1000**     | **855.0 μs** | **3.84 μs** | **3.77 μs** | **13.6719** | **1.9531** | **133.31 KB** |
