```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.8037)
Intel Core i9-10900KF CPU 3.70GHz, 1 CPU, 20 logical and 10 physical cores
.NET SDK 9.0.304
  [Host]     : .NET 9.0.8 (9.0.825.36511), X64 RyuJIT AVX2
  Job-JGADJO : .NET 9.0.8 (9.0.825.36511), X64 RyuJIT AVX2

IterationCount=20  WarmupCount=5  

```
| Method                                       | PlayerCount | NpcCount | Mean           | Error          | StdDev         | Gen0    | Gen1   | Allocated |
|--------------------------------------------- |------------ |--------- |---------------:|---------------:|---------------:|--------:|-------:|----------:|
| **&#39;Visibility Tick (steady state, no changes)&#39;** | **10**          | **100**      | **117,589.818 ns** |  **5,202.7482 ns** |  **5,991.4926 ns** |  **3.9063** | **0.7324** |   **38524 B** |
| &#39;GetVisibleEntities lookup&#39;                  | 10          | 100      |       5.627 ns |      0.0480 ns |      0.0513 ns |       - |      - |         - |
| &#39;GetObserversOf (reverse lookup)&#39;            | 10          | 100      |     153.712 ns |      1.0275 ns |      1.1833 ns |  0.0122 |      - |     128 B |
| **&#39;Visibility Tick (steady state, no changes)&#39;** | **10**          | **1000**     | **397,407.442 ns** | **18,518.3804 ns** | **20,583.1307 ns** | **14.6484** | **2.9297** |  **151523 B** |
| &#39;GetVisibleEntities lookup&#39;                  | 10          | 1000     |       5.677 ns |      0.0299 ns |      0.0294 ns |       - |      - |         - |
| &#39;GetObserversOf (reverse lookup)&#39;            | 10          | 1000     |     154.146 ns |      0.8775 ns |      0.9389 ns |  0.0122 |      - |     128 B |
| **&#39;Visibility Tick (steady state, no changes)&#39;** | **50**          | **100**      | **200,952.302 ns** |  **6,606.7843 ns** |  **7,608.3826 ns** |  **4.3945** | **0.9766** |   **43061 B** |
| &#39;GetVisibleEntities lookup&#39;                  | 50          | 100      |       5.438 ns |      0.0388 ns |      0.0399 ns |       - |      - |         - |
| &#39;GetObserversOf (reverse lookup)&#39;            | 50          | 100      |     770.962 ns |     20.0748 ns |     21.4798 ns |  0.0114 |      - |     128 B |
| **&#39;Visibility Tick (steady state, no changes)&#39;** | **50**          | **1000**     | **885,734.603 ns** | **18,348.6021 ns** | **19,632.7986 ns** | **15.6250** | **1.9531** |  **153628 B** |
| &#39;GetVisibleEntities lookup&#39;                  | 50          | 1000     |       6.300 ns |      0.1493 ns |      0.1533 ns |       - |      - |         - |
| &#39;GetObserversOf (reverse lookup)&#39;            | 50          | 1000     |     766.726 ns |      7.1451 ns |      7.3375 ns |  0.0114 |      - |     128 B |
