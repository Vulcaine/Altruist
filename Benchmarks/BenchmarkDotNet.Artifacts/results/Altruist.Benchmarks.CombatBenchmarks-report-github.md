```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.8037)
Intel Core i9-10900KF CPU 3.70GHz, 1 CPU, 20 logical and 10 physical cores
.NET SDK 9.0.304
  [Host]     : .NET 9.0.8 (9.0.825.36511), X64 RyuJIT AVX2
  Job-ICAGAI : .NET 9.0.8 (9.0.825.36511), X64 RyuJIT AVX2

IterationCount=20  WarmupCount=5  

```
| Method                               | EntityCount | Mean           | Error       | StdDev      | Gen0   | Gen1   | Allocated |
|------------------------------------- |------------ |---------------:|------------:|------------:|-------:|-------:|----------:|
| **&#39;Single Attack (calc + apply)&#39;**       | **100**         |      **8.1024 ns** |   **0.1380 ns** |   **0.1417 ns** | **0.0031** |      **-** |      **32 B** |
| DefaultDamageCalculator.Calculate    | 100         |      0.6894 ns |   0.0109 ns |   0.0112 ns |      - |      - |         - |
| &#39;Sweep Sphere r=500 (spatial query)&#39; | 100         |  2,264.9202 ns |  79.1560 ns |  81.2874 ns | 0.1030 | 0.0381 |    1104 B |
| &#39;Sweep Sphere r=2000 (large AoE)&#39;    | 100         |  2,694.4110 ns |  74.8482 ns |  80.0868 ns | 0.1907 | 0.0496 |    2024 B |
| &#39;Sweep Cone 90° r=1000&#39;              | 100         |  2,338.0286 ns | 110.3610 ns | 118.0851 ns | 0.1106 | 0.0381 |    1192 B |
| &#39;Sweep Line r=2000&#39;                  | 100         |  2,978.2170 ns | 162.5176 ns | 180.6379 ns | 0.1144 | 0.0305 |    1224 B |
| **&#39;Single Attack (calc + apply)&#39;**       | **1000**        |      **8.0815 ns** |   **0.1310 ns** |   **0.1402 ns** | **0.0031** |      **-** |      **32 B** |
| DefaultDamageCalculator.Calculate    | 1000        |      0.6754 ns |   0.0049 ns |   0.0053 ns |      - |      - |         - |
| &#39;Sweep Sphere r=500 (spatial query)&#39; | 1000        | 10,921.7793 ns | 207.9763 ns | 231.1651 ns | 0.1526 | 0.0458 |    1744 B |
| &#39;Sweep Sphere r=2000 (large AoE)&#39;    | 1000        | 14,597.0820 ns | 253.3220 ns | 281.5667 ns | 0.8850 | 0.1831 |    9304 B |
| &#39;Sweep Cone 90° r=1000&#39;              | 1000        | 10,699.4520 ns | 221.9455 ns | 246.6918 ns | 0.1831 | 0.0458 |    1960 B |
| &#39;Sweep Line r=2000&#39;                  | 1000        | 16,986.1427 ns | 347.0010 ns | 385.6907 ns | 0.1831 | 0.0610 |    1992 B |
