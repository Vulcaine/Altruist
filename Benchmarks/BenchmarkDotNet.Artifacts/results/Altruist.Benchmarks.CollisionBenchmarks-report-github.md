```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.8037)
Intel Core i9-10900KF CPU 3.70GHz, 1 CPU, 20 logical and 10 physical cores
.NET SDK 9.0.304
  [Host]     : .NET 9.0.8 (9.0.825.36511), X64 RyuJIT AVX2
  Job-KFQXNI : .NET 9.0.8 (9.0.825.36511), X64 RyuJIT AVX2
  ShortRun   : .NET 9.0.8 (9.0.825.36511), X64 RyuJIT AVX2


```
| Method                                          | Job        | IterationCount | LaunchCount | WarmupCount | EntityCount | Mean      | Error     | StdDev   | Gen0   | Gen1   | Allocated |
|------------------------------------------------ |----------- |--------------- |------------ |------------ |------------ |----------:|----------:|---------:|-------:|-------:|----------:|
| **&#39;Collision Tick (full O(n²) overlap detection)&#39;** | **Job-KFQXNI** | **20**             | **Default**     | **5**           | **100**         |  **13.29 μs** |  **0.163 μs** | **0.187 μs** | **1.0223** | **0.2594** |  **10.48 KB** |
| &#39;Collision Tick (full O(n²) overlap detection)&#39; | ShortRun   | 3              | 1           | 3           | 100         |  12.87 μs |  0.709 μs | 0.039 μs | 1.0223 | 0.2594 |  10.48 KB |
| **&#39;Collision Tick (full O(n²) overlap detection)&#39;** | **Job-KFQXNI** | **20**             | **Default**     | **5**           | **500**         | **209.41 μs** |  **1.898 μs** | **2.110 μs** | **4.8828** | **0.7324** |  **51.11 KB** |
| &#39;Collision Tick (full O(n²) overlap detection)&#39; | ShortRun   | 3              | 1           | 3           | 500         | 212.17 μs | 48.422 μs | 2.654 μs | 4.8828 | 0.7324 |  51.11 KB |
