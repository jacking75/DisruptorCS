```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.9168)
Unknown processor
.NET SDK 10.0.400
  [Host]     : .NET 10.0.11 (10.0.1126.37416), X64 RyuJIT AVX2
  Job-ZHZRWW : .NET 10.0.11 (10.0.1126.37416), X64 RyuJIT AVX2

IterationCount=5  RunStrategy=Monitoring  WarmupCount=2  

```
| Method                      | RingSize | Mean       | Error      | StdDev     | Ratio | RatioSD | Gen0      | Allocated   | Alloc Ratio |
|---------------------------- |--------- |-----------:|-----------:|-----------:|------:|--------:|----------:|------------:|------------:|
| **&#39;DisruptorCS (Yielding)&#39;**    | **1024**     |  **36.177 ms** |  **12.258 ms** |  **3.1835 ms** |  **1.01** |    **0.12** |         **-** |    **45.94 KB** |        **1.00** |
| &#39;disruptor-net (Yielding)&#39;  | 1024     |  12.323 ms |  12.002 ms |  3.1168 ms |  0.34 |    0.08 |         - |    48.46 KB |        1.05 |
| &#39;Channel&lt;T&gt; (bounded SPSC)&#39; | 1024     | 339.749 ms | 112.282 ms | 29.1592 ms |  9.45 |    1.08 | 1000.0000 | 62518.71 KB |    1,360.95 |
|                             |          |            |            |            |       |         |           |             |             |
| **&#39;DisruptorCS (Yielding)&#39;**    | **65536**    |   **6.290 ms** |   **3.526 ms** |  **0.9157 ms** |  **1.02** |    **0.19** |         **-** |  **2565.91 KB** |        **1.00** |
| &#39;disruptor-net (Yielding)&#39;  | 65536    |   9.611 ms |   2.711 ms |  0.7041 ms |  1.55 |    0.23 |         - |  2568.81 KB |        1.00 |
| &#39;Channel&lt;T&gt; (bounded SPSC)&#39; | 65536    | 234.811 ms |  27.137 ms |  7.0475 ms | 37.96 |    5.04 | 1000.0000 | 63525.78 KB |       24.76 |
