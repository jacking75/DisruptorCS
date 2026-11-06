# DISR003: Don't pass capturing lambdas to `PublishEvent`

**Severity:** Info · **Category:** Disruptor.Performance

## Cause

A lambda passed to `RingBuffer<T>.PublishEvent` (or the `ValueRingBuffer<T>` equivalent) captures
a variable from the enclosing scope.

## Why this matters

The ring can publish without allocating event objects, but a capturing lambda can add closure and
delegate allocations. The allocation frequency depends on capture scope, delegate reuse, and
compiler/JIT optimizations; it is not necessarily one closure plus one delegate per event.

A noncapturing static lambda allows delegate caching and prevents accidental capture. It still has
invocation cost; this rule targets avoidable capture rather than lambdas in general.

## Example

```csharp
// Violation — captures `i`; repeated delegate creation may allocate in the loop
for (int i = 0; i < count; i++)
{
    ring.PublishEvent((evt, seq) => evt.OrderId = i);
}
```

## How to fix

**Option 1 — `static` lambda plus the argument overload.** Marking the lambda `static` makes
capture a compile error, so the fix cannot silently regress:

```csharp
for (int i = 0; i < count; i++)
{
    ring.PublishEvent(static (evt, seq, id) => evt.OrderId = id, (long)i);
}
```

**Option 2 — a struct translator**, which removes the delegate as well:

```csharp
readonly struct FillOrder : IEventTranslator<OrderEvent, long>
{
    public void Translate(OrderEvent evt, long sequence, long id) => evt.OrderId = id;
}

ring.PublishEvent(default(FillOrder), orderId);
```

The struct overload avoids a delegate and closure and lets the JIT specialize the call. Inlining
still depends on the method and runtime; measure the actual hot path.

**Option 3 — claim and fill directly**, the most explicit form:

```csharp
long seq = ring.Next();
try
{
    ring[seq].OrderId = i;
}
finally
{
    ring.Publish(seq);
}
```

## When to suppress

This is `Info` because it is a real cost, not a correctness problem. Suppress it freely on cold
paths — startup, admin commands, tests:

```csharp
#pragma warning disable DISR003 // startup path, runs once
ring.PublishEvent((evt, seq) => evt.OrderId = seedId);
#pragma warning restore DISR003
```
