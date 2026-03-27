```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.8037)
Intel Core i9-10900KF CPU 3.70GHz, 1 CPU, 20 logical and 10 physical cores
.NET SDK 9.0.304
  [Host]     : .NET 9.0.8 (9.0.825.36511), X64 RyuJIT AVX2
  Job-ESUBKE : .NET 9.0.8 (9.0.825.36511), X64 RyuJIT AVX2

IterationCount=20  WarmupCount=5  

```
| Method                               | EntityCount | Mean           | Error       | StdDev      | Gen0   | Gen1   | Allocated |
|------------------------------------- |------------ |---------------:|------------:|------------:|-------:|-------:|----------:|
| **&#39;Single Attack (calc + apply)&#39;**       | **100**         |     **14.8152 ns** |   **0.4309 ns** |   **0.4789 ns** | **0.0076** |      **-** |      **80 B** |
| DefaultDamageCalculator.Calculate    | 100         |      0.6746 ns |   0.0059 ns |   0.0061 ns |      - |      - |         - |
| &#39;Sweep Sphere r=500 (spatial query)&#39; | 100         |  2,133.4295 ns |  91.3567 ns | 105.2065 ns | 0.1106 | 0.0381 |    1160 B |
| &#39;Sweep Sphere r=2000 (large AoE)&#39;    | 100         |  2,571.7252 ns | 145.1636 ns | 161.3489 ns | 0.1984 | 0.0496 |    2080 B |
| &#39;Sweep Cone 90° r=1000&#39;              | 100         |  2,259.0943 ns | 100.4093 ns | 111.6046 ns | 0.1183 | 0.0305 |    1248 B |
| &#39;Sweep Line r=2000&#39;                  | 100         |  2,920.7982 ns | 156.4247 ns | 180.1390 ns | 0.1221 | 0.0343 |    1280 B |
| **&#39;Single Attack (calc + apply)&#39;**       | **1000**        |     **14.3890 ns** |   **0.1802 ns** |   **0.1851 ns** | **0.0076** |      **-** |      **80 B** |
| DefaultDamageCalculator.Calculate    | 1000        |      0.8006 ns |   0.0208 ns |   0.0223 ns |      - |      - |         - |
| &#39;Sweep Sphere r=500 (spatial query)&#39; | 1000        |  9,325.0096 ns | 127.0056 ns | 141.1664 ns | 0.1678 | 0.0458 |    1800 B |
| &#39;Sweep Sphere r=2000 (large AoE)&#39;    | 1000        | 13,044.2132 ns | 184.3837 ns | 197.2885 ns | 0.8850 | 0.1831 |    9360 B |
| &#39;Sweep Cone 90° r=1000&#39;              | 1000        | 10,187.5541 ns | 142.9143 ns | 152.9167 ns | 0.1831 | 0.0458 |    2016 B |
| &#39;Sweep Line r=2000&#39;                  | 1000        | 16,996.1706 ns | 187.7375 ns | 200.8770 ns | 0.1831 | 0.0610 |    2048 B |
