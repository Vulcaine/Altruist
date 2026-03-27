```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.8037)
Intel Core i9-10900KF CPU 3.70GHz, 1 CPU, 20 logical and 10 physical cores
.NET SDK 9.0.304
  [Host]     : .NET 9.0.8 (9.0.825.36511), X64 RyuJIT AVX2
  Job-LGNPJR : .NET 9.0.8 (9.0.825.36511), X64 RyuJIT AVX2

IterationCount=20  WarmupCount=5  

```
| Method                                       | count | Mean         | Error      | StdDev     | Gen0   | Allocated |
|--------------------------------------------- |------ |-------------:|-----------:|-----------:|-------:|----------:|
| **&#39;FSM.Update - stay in state (no transition)&#39;** | **?**     |     **13.86 ns** |   **0.062 ns** |   **0.071 ns** |      **-** |         **-** |
| &#39;FSM.Update - transition (exit+enter hooks)&#39; | ?     |     75.56 ns |   1.313 ns |   1.513 ns |      - |         - |
| &#39;CreateStateMachine (from cached template)&#39;  | ?     |    192.79 ns |   9.261 ns |  10.293 ns | 0.0763 |     800 B |
| **&#39;FSM.Update x1000 entities (simulate tick)&#39;**  | **1000**  | **13,632.70 ns** |  **84.650 ns** |  **86.929 ns** |      **-** |         **-** |
| **&#39;FSM.Update x1000 entities (simulate tick)&#39;**  | **5000**  | **67,833.13 ns** | **384.937 ns** | **427.856 ns** |      **-** |         **-** |
