```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.8037)
Intel Core i9-10900KF CPU 3.70GHz, 1 CPU, 20 logical and 10 physical cores
.NET SDK 9.0.304
  [Host]     : .NET 9.0.8 (9.0.825.36511), X64 RyuJIT AVX2
  Job-DZODIU : .NET 9.0.8 (9.0.825.36511), X64 RyuJIT AVX2

IterationCount=20  WarmupCount=5  

```
| Method                                       | PlayerCount | NpcCount | Mean             | Error          | StdDev         | Gen0    | Gen1   | Allocated |
|--------------------------------------------- |------------ |--------- |-----------------:|---------------:|---------------:|--------:|-------:|----------:|
| **&#39;Visibility Tick (steady state, no changes)&#39;** | **10**          | **100**      |   **198,359.034 ns** |  **3,361.0976 ns** |  **3,735.8511 ns** |  **3.4180** | **0.7324** |   **35768 B** |
| &#39;GetVisibleEntities lookup&#39;                  | 10          | 100      |        10.046 ns |      0.2269 ns |      0.2613 ns |       - |      - |         - |
| &#39;GetObserversOf (reverse lookup)&#39;            | 10          | 100      |       226.111 ns |      0.8706 ns |      0.9315 ns |  0.0122 |      - |     128 B |
| **&#39;Visibility Tick (steady state, no changes)&#39;** | **10**          | **1000**     | **1,374,102.740 ns** |  **7,838.2053 ns** |  **8,712.1445 ns** | **11.7188** | **1.9531** |  **131016 B** |
| &#39;GetVisibleEntities lookup&#39;                  | 10          | 1000     |        10.045 ns |      0.0695 ns |      0.0773 ns |       - |      - |         - |
| &#39;GetObserversOf (reverse lookup)&#39;            | 10          | 1000     |       232.452 ns |      3.6714 ns |      4.0808 ns |  0.0119 |      - |     128 B |
| **&#39;Visibility Tick (steady state, no changes)&#39;** | **50**          | **100**      |   **857,236.393 ns** |  **5,078.2074 ns** |  **5,433.6250 ns** |  **1.9531** |      **-** |   **26616 B** |
| &#39;GetVisibleEntities lookup&#39;                  | 50          | 100      |         9.845 ns |      0.1855 ns |      0.2137 ns |       - |      - |         - |
| &#39;GetObserversOf (reverse lookup)&#39;            | 50          | 100      |     1,226.554 ns |      4.7102 ns |      5.2353 ns |  0.0114 |      - |     128 B |
| **&#39;Visibility Tick (steady state, no changes)&#39;** | **50**          | **1000**     | **6,634,090.061 ns** | **64,489.2688 ns** | **69,002.7951 ns** | **15.6250** |      **-** |  **189592 B** |
| &#39;GetVisibleEntities lookup&#39;                  | 50          | 1000     |        10.113 ns |      0.1903 ns |      0.2037 ns |       - |      - |         - |
| &#39;GetObserversOf (reverse lookup)&#39;            | 50          | 1000     |     1,576.020 ns |     19.9919 ns |     20.5302 ns |  0.0114 |      - |     128 B |
