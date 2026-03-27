```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.8037)
Intel Core i9-10900KF CPU 3.70GHz, 1 CPU, 20 logical and 10 physical cores
.NET SDK 9.0.304
  [Host]     : .NET 9.0.8 (9.0.825.36511), X64 RyuJIT AVX2
  Job-ESUBKE : .NET 9.0.8 (9.0.825.36511), X64 RyuJIT AVX2

IterationCount=20  WarmupCount=5  

```
| Method                                       | PlayerCount | NpcCount | Mean           | Error          | StdDev         | Gen0    | Gen1   | Allocated |
|--------------------------------------------- |------------ |--------- |---------------:|---------------:|---------------:|--------:|-------:|----------:|
| **&#39;Visibility Tick (steady state, no changes)&#39;** | **10**          | **100**      | **104,172.196 ns** |  **3,137.4533 ns** |  **3,487.2710 ns** |  **3.5400** | **0.8545** |   **33741 B** |
| &#39;GetVisibleEntities lookup&#39;                  | 10          | 100      |       5.600 ns |      0.0232 ns |      0.0238 ns |       - |      - |         - |
| &#39;GetObserversOf (reverse lookup)&#39;            | 10          | 100      |     154.181 ns |      0.6880 ns |      0.7362 ns |  0.0122 |      - |     128 B |
| **&#39;Visibility Tick (steady state, no changes)&#39;** | **10**          | **1000**     | **425,711.945 ns** |  **4,380.0774 ns** |  **4,868.4444 ns** | **19.5313** | **4.3945** |  **197246 B** |
| &#39;GetVisibleEntities lookup&#39;                  | 10          | 1000     |       5.591 ns |      0.0106 ns |      0.0114 ns |       - |      - |         - |
| &#39;GetObserversOf (reverse lookup)&#39;            | 10          | 1000     |     154.701 ns |      0.8349 ns |      0.8933 ns |  0.0122 |      - |     128 B |
| **&#39;Visibility Tick (steady state, no changes)&#39;** | **50**          | **100**      | **192,570.361 ns** |  **9,166.4918 ns** | **10,556.1456 ns** |  **4.3945** | **0.9766** |   **43168 B** |
| &#39;GetVisibleEntities lookup&#39;                  | 50          | 100      |       5.699 ns |      0.0743 ns |      0.0855 ns |       - |      - |         - |
| &#39;GetObserversOf (reverse lookup)&#39;            | 50          | 100      |     684.914 ns |      4.2550 ns |      4.9001 ns |  0.0114 |      - |     128 B |
| **&#39;Visibility Tick (steady state, no changes)&#39;** | **50**          | **1000**     | **908,007.357 ns** | **24,754.3671 ns** | **26,486.8955 ns** | **19.5313** | **3.9063** |  **196269 B** |
| &#39;GetVisibleEntities lookup&#39;                  | 50          | 1000     |       5.601 ns |      0.0335 ns |      0.0344 ns |       - |      - |         - |
| &#39;GetObserversOf (reverse lookup)&#39;            | 50          | 1000     |     712.013 ns |      3.5788 ns |      3.9778 ns |  0.0114 |      - |     128 B |
