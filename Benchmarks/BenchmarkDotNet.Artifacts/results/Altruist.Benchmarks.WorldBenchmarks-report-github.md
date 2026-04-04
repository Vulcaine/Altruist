```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.8037)
Intel Core i9-10900KF CPU 3.70GHz, 1 CPU, 20 logical and 10 physical cores
.NET SDK 9.0.304
  [Host]     : .NET 9.0.8 (9.0.825.36511), X64 RyuJIT AVX2
  Job-JGADJO : .NET 9.0.8 (9.0.825.36511), X64 RyuJIT AVX2

IterationCount=20  WarmupCount=5  

```
| Method                                     | ObjectCount | Mean          | Error       | StdDev      | Allocated |
|------------------------------------------- |------------ |--------------:|------------:|------------:|----------:|
| **&#39;WorldSnapshot creation&#39;**                   | **1000**        |      **2.463 ns** |   **0.0048 ns** |   **0.0049 ns** |         **-** |
| &#39;Iterate all + filter ISynchronizedEntity&#39; | 1000        |  1,092.686 ns |   5.5303 ns |   5.6792 ns |         - |
| &#39;Iterate all + filter IAIBehaviorEntity&#39;   | 1000        |  1,130.297 ns |  10.0894 ns |  11.2144 ns |         - |
| &#39;Dictionary lookup by InstanceId&#39;          | 1000        |     13.003 ns |   0.0468 ns |   0.0501 ns |         - |
| &#39;Iterate all + distance check (r=2000)&#39;    | 1000        |  3,000.750 ns |  35.3309 ns |  40.6871 ns |         - |
| **&#39;WorldSnapshot creation&#39;**                   | **5000**        |      **2.467 ns** |   **0.0110 ns** |   **0.0122 ns** |         **-** |
| &#39;Iterate all + filter ISynchronizedEntity&#39; | 5000        | 12,338.651 ns | 601.8451 ns | 693.0858 ns |         - |
| &#39;Iterate all + filter IAIBehaviorEntity&#39;   | 5000        | 13,535.022 ns | 740.5986 ns | 852.8745 ns |         - |
| &#39;Dictionary lookup by InstanceId&#39;          | 5000        |     13.230 ns |   0.1783 ns |   0.2053 ns |         - |
| &#39;Iterate all + distance check (r=2000)&#39;    | 5000        | 23,326.043 ns | 696.6971 ns | 774.3769 ns |         - |
