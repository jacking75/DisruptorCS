# DISR004: Ring buffer size must be a power of two

**Severity:** Error · **Category:** Disruptor.Correctness

## Cause

A compile-time constant that is not a power of two was passed as a ring buffer size
(`bufferSize`, `ringBufferSize`, or `ringSizePerShard`).

## Why this matters

Slot lookup is `sequence & (bufferSize - 1)`. That single AND instruction replaces a modulo and is
one of the reasons the ring buffer is fast — but it is only equivalent to `sequence % bufferSize`
when `bufferSize` is a power of two.

The constructor validates this at runtime, but by then your server is already starting up. Catching
it at build time is strictly better.

## Example

```csharp
// Violation
var ring = RingBuffer<OrderEvent>.CreateSingleProducer(
    () => new OrderEvent(), 1000, new YieldingWaitStrategy());

var disruptor = new Disruptor<OrderEvent>(() => new OrderEvent(), 100);
```

## How to fix

Round to a power of two. The diagnostic message suggests the next one up:

```csharp
var ring = RingBuffer<OrderEvent>.CreateSingleProducer(
    () => new OrderEvent(), 1024, new YieldingWaitStrategy());
```

Shift expressions read well for large sizes and stay obviously correct:

```csharp
const int RingSize = 1 << 16;   // 65,536
```

## Choosing a size

| Ring size | Slot memory (64 B events) | Typical use |
|---:|---:|---|
| 256 | ~18 KB | Per-shard room queues |
| 1,024 | ~74 KB | General pipelines |
| 65,536 | ~4.7 MB | Burst absorption |
| 1,048,576 | ~75 MB | Benchmarks; rarely needed in production |

Bigger is not automatically better. A large ring hides backpressure, so a slow consumer shows up as
growing latency rather than as a visible stall. Size it to the burst you actually want to absorb,
then watch `disruptor.utilization` and `disruptor.handler_lag`.

## Notes

Only compile-time constants are checked. A size computed at runtime is left to the constructor's
validation.
