# DISR003: Don't pass capturing lambdas to `PublishEvent`

**Severity:** Info · **Category:** Disruptor.Performance

## Cause

A lambda passed to `RingBuffer<T>.PublishEvent` (or the `ValueRingBuffer<T>` equivalent) captures
a variable from the enclosing scope.

## Why this matters

Publishing is the hottest path in this library, and "zero allocation on the normal path" is one of
its core guarantees. A capturing lambda allocates **a closure object and a delegate on every
publish**, which puts the GC back into a loop that was designed to avoid it entirely.

A *non-capturing* lambda is cached by the compiler and costs nothing, which is why this rule targets
capture specifically rather than lambdas in general.

## Example

```csharp
// Violation — captures `i`, so each iteration allocates
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

Because `TTranslator` is constrained to `struct`, the JIT specializes the call and inlines
`Translate` — no delegate, no closure, no indirection.

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
