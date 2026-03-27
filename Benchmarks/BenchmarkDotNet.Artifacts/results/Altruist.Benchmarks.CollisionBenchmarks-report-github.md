```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.8037)
Intel Core i9-10900KF CPU 3.70GHz, 1 CPU, 20 logical and 10 physical cores
.NET SDK 9.0.304
  [Host]     : .NET 9.0.8 (9.0.825.36511), X64 RyuJIT AVX2
  Job-OQYKXP : .NET 9.0.8 (9.0.825.36511), X64 RyuJIT AVX2

IterationCount=20  WarmupCount=5  

```
| Method                                          | EntityCount | Mean      | Error    | StdDev   | Gen0    | Gen1   | Allocated |
|------------------------------------------------ |------------ |----------:|---------:|---------:|--------:|-------:|----------:|
| **&#39;Collision Tick (full O(n²) overlap detection)&#39;** | **100**         |  **13.05 μs** | **0.425 μs** | **0.472 μs** |  **1.2817** | **0.3052** |  **13.27 KB** |
| **&#39;Collision Tick (full O(n²) overlap detection)&#39;** | **500**         | **190.91 μs** | **1.929 μs** | **2.144 μs** | **11.4746** | **0.9766** | **118.99 KB** |
