```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.8037)
Intel Core i9-10900KF CPU 3.70GHz, 1 CPU, 20 logical and 10 physical cores
.NET SDK 9.0.304
  [Host]     : .NET 9.0.8 (9.0.825.36511), X64 RyuJIT AVX2
  Job-PNTTJH : .NET 9.0.8 (9.0.825.36511), X64 RyuJIT AVX2
  ShortRun   : .NET 9.0.8 (9.0.825.36511), X64 RyuJIT AVX2

WarmupCount=3  

```
| Method                                       | Job        | IterationCount | LaunchCount | count | Mean         | Error        | StdDev     | Gen0   | Allocated |
|--------------------------------------------- |----------- |--------------- |------------ |------ |-------------:|-------------:|-----------:|-------:|----------:|
| **&#39;FSM.Update - stay in state (no transition)&#39;** | **Job-PNTTJH** | **5**              | **Default**     | **?**     |     **14.29 ns** |     **0.146 ns** |   **0.038 ns** |      **-** |         **-** |
| &#39;FSM.Update - transition (exit+enter hooks)&#39; | Job-PNTTJH | 5              | Default     | ?     |     75.81 ns |     5.903 ns |   0.914 ns |      - |         - |
| &#39;CreateStateMachine (from cached template)&#39;  | Job-PNTTJH | 5              | Default     | ?     |    197.70 ns |    11.216 ns |   2.913 ns | 0.0763 |     800 B |
| &#39;FSM.Update - stay in state (no transition)&#39; | ShortRun   | 3              | 1           | ?     |     14.33 ns |     1.868 ns |   0.102 ns |      - |         - |
| &#39;FSM.Update - transition (exit+enter hooks)&#39; | ShortRun   | 3              | 1           | ?     |     75.28 ns |    20.100 ns |   1.102 ns |      - |         - |
| &#39;CreateStateMachine (from cached template)&#39;  | ShortRun   | 3              | 1           | ?     |    193.40 ns |    57.458 ns |   3.149 ns | 0.0763 |     800 B |
| **&#39;FSM.Update x1000 entities (simulate tick)&#39;**  | **Job-PNTTJH** | **5**              | **Default**     | **1000**  | **13,609.51 ns** |   **376.534 ns** |  **58.269 ns** |      **-** |         **-** |
| &#39;FSM.Update x1000 entities (simulate tick)&#39;  | ShortRun   | 3              | 1           | 1000  | 13,583.31 ns |   437.385 ns |  23.975 ns |      - |         - |
| **&#39;FSM.Update x1000 entities (simulate tick)&#39;**  | **Job-PNTTJH** | **5**              | **Default**     | **5000**  | **68,164.10 ns** | **2,150.616 ns** | **332.810 ns** |      **-** |         **-** |
| &#39;FSM.Update x1000 entities (simulate tick)&#39;  | ShortRun   | 3              | 1           | 5000  | 66,589.53 ns | 6,656.430 ns | 364.861 ns |      - |         - |
