```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.8037)
Intel Core i9-10900KF CPU 3.70GHz, 1 CPU, 20 logical and 10 physical cores
.NET SDK 9.0.304
  [Host]     : .NET 9.0.8 (9.0.825.36511), X64 RyuJIT AVX2
  Job-QUOQZX : .NET 9.0.8 (9.0.825.36511), X64 RyuJIT AVX2

IterationCount=20  WarmupCount=5  

```
| Method                               | EntityCount | Mean       | Error     | StdDev    | Gen0   | Allocated |
|------------------------------------- |------------ |-----------:|----------:|----------:|-------:|----------:|
| **&#39;Single Attack (calc + apply)&#39;**       | **100**         | **14.7513 ns** | **0.3468 ns** | **0.3855 ns** | **0.0076** |      **80 B** |
| DefaultDamageCalculator.Calculate    | 100         |  0.7097 ns | 0.0285 ns | 0.0328 ns |      - |         - |
| &#39;Sweep Sphere r=500 (spatial query)&#39; | 100         |         NA |        NA |        NA |     NA |        NA |
| &#39;Sweep Sphere r=2000 (large AoE)&#39;    | 100         |         NA |        NA |        NA |     NA |        NA |
| &#39;Sweep Cone 90° r=1000&#39;              | 100         |         NA |        NA |        NA |     NA |        NA |
| &#39;Sweep Line r=2000&#39;                  | 100         |         NA |        NA |        NA |     NA |        NA |
| **&#39;Single Attack (calc + apply)&#39;**       | **1000**        | **21.1083 ns** | **5.1103 ns** | **5.8851 ns** | **0.0076** |      **80 B** |
| DefaultDamageCalculator.Calculate    | 1000        |  0.7037 ns | 0.0355 ns | 0.0379 ns |      - |         - |
| &#39;Sweep Sphere r=500 (spatial query)&#39; | 1000        |         NA |        NA |        NA |     NA |        NA |
| &#39;Sweep Sphere r=2000 (large AoE)&#39;    | 1000        |         NA |        NA |        NA |     NA |        NA |
| &#39;Sweep Cone 90° r=1000&#39;              | 1000        |         NA |        NA |        NA |     NA |        NA |
| &#39;Sweep Line r=2000&#39;                  | 1000        |         NA |        NA |        NA |     NA |        NA |

Benchmarks with issues:
  CombatBenchmarks.'Sweep Sphere r=500 (spatial query)': Job-QUOQZX(IterationCount=20, WarmupCount=5) [EntityCount=100]
  CombatBenchmarks.'Sweep Sphere r=2000 (large AoE)': Job-QUOQZX(IterationCount=20, WarmupCount=5) [EntityCount=100]
  CombatBenchmarks.'Sweep Cone 90° r=1000': Job-QUOQZX(IterationCount=20, WarmupCount=5) [EntityCount=100]
  CombatBenchmarks.'Sweep Line r=2000': Job-QUOQZX(IterationCount=20, WarmupCount=5) [EntityCount=100]
  CombatBenchmarks.'Sweep Sphere r=500 (spatial query)': Job-QUOQZX(IterationCount=20, WarmupCount=5) [EntityCount=1000]
  CombatBenchmarks.'Sweep Sphere r=2000 (large AoE)': Job-QUOQZX(IterationCount=20, WarmupCount=5) [EntityCount=1000]
  CombatBenchmarks.'Sweep Cone 90° r=1000': Job-QUOQZX(IterationCount=20, WarmupCount=5) [EntityCount=1000]
  CombatBenchmarks.'Sweep Line r=2000': Job-QUOQZX(IterationCount=20, WarmupCount=5) [EntityCount=1000]
