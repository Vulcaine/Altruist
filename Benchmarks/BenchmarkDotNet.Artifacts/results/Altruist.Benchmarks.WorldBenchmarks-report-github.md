```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.8037)
Intel Core i9-10900KF CPU 3.70GHz, 1 CPU, 20 logical and 10 physical cores
.NET SDK 9.0.304
  [Host]     : .NET 9.0.8 (9.0.825.36511), X64 RyuJIT AVX2
  Job-ESUBKE : .NET 9.0.8 (9.0.825.36511), X64 RyuJIT AVX2

IterationCount=20  WarmupCount=5  

```
| Method                                     | ObjectCount | Mean          | Error         | StdDev        | Allocated |
|------------------------------------------- |------------ |--------------:|--------------:|--------------:|----------:|
| **&#39;WorldSnapshot creation&#39;**                   | **1000**        |      **2.453 ns** |     **0.0082 ns** |     **0.0094 ns** |         **-** |
| &#39;Iterate all + filter ISynchronizedEntity&#39; | 1000        |  1,103.039 ns |     9.0919 ns |     8.9294 ns |         - |
| &#39;Iterate all + filter IAIBehaviorEntity&#39;   | 1000        |  1,116.894 ns |    10.5035 ns |    11.2386 ns |         - |
| &#39;Dictionary lookup by InstanceId&#39;          | 1000        |     14.082 ns |     0.0860 ns |     0.0956 ns |         - |
| &#39;Iterate all + distance check (r=2000)&#39;    | 1000        |  3,008.694 ns |    46.7912 ns |    53.8848 ns |         - |
| **&#39;WorldSnapshot creation&#39;**                   | **5000**        |      **2.463 ns** |     **0.0057 ns** |     **0.0066 ns** |         **-** |
| &#39;Iterate all + filter ISynchronizedEntity&#39; | 5000        | 12,615.095 ns | 1,039.4541 ns | 1,197.0369 ns |         - |
| &#39;Iterate all + filter IAIBehaviorEntity&#39;   | 5000        | 12,840.186 ns | 1,063.8978 ns | 1,225.1863 ns |         - |
| &#39;Dictionary lookup by InstanceId&#39;          | 5000        |     13.247 ns |     0.1187 ns |     0.1367 ns |         - |
| &#39;Iterate all + distance check (r=2000)&#39;    | 5000        | 22,969.670 ns |   666.6464 ns |   767.7110 ns |         - |
