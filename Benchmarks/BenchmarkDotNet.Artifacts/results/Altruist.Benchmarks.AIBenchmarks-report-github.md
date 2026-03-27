```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.8037)
Intel Core i9-10900KF CPU 3.70GHz, 1 CPU, 20 logical and 10 physical cores
.NET SDK 9.0.304
  [Host]     : .NET 9.0.8 (9.0.825.36511), X64 RyuJIT AVX2
  Job-RQLSLI : .NET 9.0.8 (9.0.825.36511), X64 RyuJIT AVX2

IterationCount=20  WarmupCount=5  

```
| Method                                       | count | Mean         | Error      | StdDev     | Median       | Gen0   | Allocated |
|--------------------------------------------- |------ |-------------:|-----------:|-----------:|-------------:|-------:|----------:|
| **&#39;FSM.Update - stay in state (no transition)&#39;** | **?**     |     **20.33 ns** |   **4.231 ns** |   **4.872 ns** |     **23.72 ns** |      **-** |         **-** |
| &#39;FSM.Update - transition (exit+enter hooks)&#39; | ?     |     78.17 ns |   2.005 ns |   2.309 ns |     77.64 ns |      - |         - |
| &#39;CreateStateMachine (from cached template)&#39;  | ?     |    195.52 ns |   8.920 ns |   9.914 ns |    197.29 ns | 0.0763 |     800 B |
| **&#39;FSM.Update x1000 entities (simulate tick)&#39;**  | **1000**  | **13,604.04 ns** | **258.586 ns** | **253.966 ns** | **13,546.40 ns** |      **-** |         **-** |
| **&#39;FSM.Update x1000 entities (simulate tick)&#39;**  | **5000**  | **68,691.24 ns** | **460.110 ns** | **492.313 ns** | **68,753.02 ns** |      **-** |         **-** |
