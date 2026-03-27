```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.8037)
Intel Core i9-10900KF CPU 3.70GHz, 1 CPU, 20 logical and 10 physical cores
.NET SDK 9.0.304
  [Host]     : .NET 9.0.8 (9.0.825.36511), X64 RyuJIT AVX2
  Job-RQLSLI : .NET 9.0.8 (9.0.825.36511), X64 RyuJIT AVX2

IterationCount=20  WarmupCount=5  

```
| Method                                     | ObjectCount | Mean          | Error         | StdDev        | Allocated |
|------------------------------------------- |------------ |--------------:|--------------:|--------------:|----------:|
| **&#39;WorldSnapshot creation&#39;**                   | **1000**        |      **2.467 ns** |     **0.0116 ns** |     **0.0114 ns** |         **-** |
| &#39;Iterate all + filter ISynchronizedEntity&#39; | 1000        |  1,105.700 ns |    12.6864 ns |    14.1009 ns |         - |
| &#39;Iterate all + filter IAIBehaviorEntity&#39;   | 1000        |  1,158.274 ns |    18.5064 ns |    21.3120 ns |         - |
| &#39;Dictionary lookup by InstanceId&#39;          | 1000        |     14.487 ns |     0.1333 ns |     0.1427 ns |         - |
| &#39;Iterate all + distance check (r=2000)&#39;    | 1000        |  2,887.454 ns |    48.4156 ns |    55.7555 ns |         - |
| **&#39;WorldSnapshot creation&#39;**                   | **5000**        |      **2.454 ns** |     **0.0071 ns** |     **0.0076 ns** |         **-** |
| &#39;Iterate all + filter ISynchronizedEntity&#39; | 5000        | 12,509.111 ns |   929.4533 ns | 1,070.3598 ns |         - |
| &#39;Iterate all + filter IAIBehaviorEntity&#39;   | 5000        | 13,393.645 ns |   545.4761 ns |   628.1711 ns |         - |
| &#39;Dictionary lookup by InstanceId&#39;          | 5000        |     14.565 ns |     0.1436 ns |     0.1597 ns |         - |
| &#39;Iterate all + distance check (r=2000)&#39;    | 5000        | 22,462.250 ns | 1,425.0955 ns | 1,641.1421 ns |         - |
