```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.8037)
Intel Core i9-10900KF CPU 3.70GHz, 1 CPU, 20 logical and 10 physical cores
.NET SDK 9.0.304
  [Host]     : .NET 9.0.8 (9.0.825.36511), X64 RyuJIT AVX2
  Job-ICAGAI : .NET 9.0.8 (9.0.825.36511), X64 RyuJIT AVX2

IterationCount=20  WarmupCount=5  

```
| Method                                       | PlayerCount | NpcCount | Mean             | Error          | StdDev          | Gen0    | Gen1   | Allocated |
|--------------------------------------------- |------------ |--------- |-----------------:|---------------:|----------------:|--------:|-------:|----------:|
| **&#39;Visibility Tick (steady state, no changes)&#39;** | **10**          | **100**      |   **153,381.639 ns** |  **1,584.3260 ns** |   **1,556.0200 ns** |  **3.1738** | **0.7324** |   **35168 B** |
| &#39;GetVisibleEntities lookup&#39;                  | 10          | 100      |         9.606 ns |      0.0240 ns |       0.0247 ns |       - |      - |         - |
| &#39;GetObserversOf (reverse lookup)&#39;            | 10          | 100      |       229.086 ns |      1.8532 ns |       2.0599 ns |  0.0122 |      - |     128 B |
| **&#39;Visibility Tick (steady state, no changes)&#39;** | **10**          | **1000**     | **1,125,271.365 ns** |  **3,648.7689 ns** |   **3,904.1418 ns** | **19.5313** | **1.9531** |  **204593 B** |
| &#39;GetVisibleEntities lookup&#39;                  | 10          | 1000     |         9.769 ns |      0.0585 ns |       0.0674 ns |       - |      - |         - |
| &#39;GetObserversOf (reverse lookup)&#39;            | 10          | 1000     |       257.799 ns |      1.8866 ns |       2.0970 ns |  0.0119 |      - |     128 B |
| **&#39;Visibility Tick (steady state, no changes)&#39;** | **50**          | **100**      |   **736,082.558 ns** |  **3,102.9334 ns** |   **3,320.1040 ns** |  **4.8828** |      **-** |   **54744 B** |
| &#39;GetVisibleEntities lookup&#39;                  | 50          | 100      |         9.745 ns |      0.1013 ns |       0.1084 ns |       - |      - |         - |
| &#39;GetObserversOf (reverse lookup)&#39;            | 50          | 100      |     1,218.135 ns |      8.4444 ns |       9.0354 ns |  0.0114 |      - |     128 B |
| **&#39;Visibility Tick (steady state, no changes)&#39;** | **50**          | **1000**     | **5,227,778.750 ns** | **90,117.8606 ns** | **103,779.8623 ns** | **15.6250** |      **-** |  **198475 B** |
| &#39;GetVisibleEntities lookup&#39;                  | 50          | 1000     |         9.839 ns |      0.0280 ns |       0.0288 ns |       - |      - |         - |
| &#39;GetObserversOf (reverse lookup)&#39;            | 50          | 1000     |     1,222.626 ns |      6.9506 ns |       8.0044 ns |  0.0114 |      - |     128 B |
