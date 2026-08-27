```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.9168)
Unknown processor
.NET SDK 10.0.400
  [Host]     : .NET 10.0.11 (10.0.1126.37416), X64 RyuJIT AVX2
  Job-SSIKFM : .NET 10.0.11 (10.0.1126.37416), X64 RyuJIT AVX2

IterationCount=5  RunStrategy=Monitoring  WarmupCount=2  

```
| Method             | Producers | Mean     | Error     | StdDev    | Ratio | RatioSD | Allocated | Alloc Ratio |
|------------------- |---------- |---------:|----------:|----------:|------:|--------:|----------:|------------:|
| **&#39;DisruptorCS MP&#39;**   | **2**         | **28.99 ms** | **16.523 ms** |  **4.291 ms** |  **1.02** |    **0.20** |  **50.28 KB** |        **1.00** |
| &#39;disruptor-net MP&#39; | 2         | 21.78 ms |  2.845 ms |  0.739 ms |  0.76 |    0.11 |  52.75 KB |        1.05 |
|                    |           |          |           |           |       |         |           |             |
| **&#39;DisruptorCS MP&#39;**   | **8**         | **90.52 ms** | **55.919 ms** | **14.522 ms** |  **1.02** |    **0.20** |  **51.15 KB** |        **1.00** |
| &#39;disruptor-net MP&#39; | 8         | 49.13 ms |  9.136 ms |  2.373 ms |  0.55 |    0.07 |  53.64 KB |        1.05 |
