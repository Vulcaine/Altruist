```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.8037)
Intel Core i9-10900KF CPU 3.70GHz, 1 CPU, 20 logical and 10 physical cores
.NET SDK 9.0.304
  [Host]     : .NET 9.0.8 (9.0.825.36511), X64 RyuJIT AVX2
  Job-ESUBKE : .NET 9.0.8 (9.0.825.36511), X64 RyuJIT AVX2

IterationCount=20  WarmupCount=5  

```
| Method                                       | count | Mean         | Error      | StdDev     | Gen0   | Allocated |
|--------------------------------------------- |------ |-------------:|-----------:|-----------:|-------:|----------:|
| **&#39;FSM.Update - stay in state (no transition)&#39;** | **?**     |     **14.20 ns** |   **0.158 ns** |   **0.169 ns** |      **-** |         **-** |
| &#39;FSM.Update - transition (exit+enter hooks)&#39; | ?     |     74.50 ns |   0.312 ns |   0.347 ns |      - |         - |
| &#39;CreateStateMachine (from cached template)&#39;  | ?     |    188.89 ns |   2.460 ns |   2.735 ns | 0.0763 |     800 B |
| **&#39;FSM.Update x1000 entities (simulate tick)&#39;**  | **1000**  | **13,830.50 ns** |  **80.373 ns** |  **89.334 ns** |      **-** |         **-** |
| **&#39;FSM.Update x1000 entities (simulate tick)&#39;**  | **5000**  | **67,827.43 ns** | **192.119 ns** | **205.565 ns** |      **-** |         **-** |
