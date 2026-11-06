# DISR002: `Publish()` must run in a `finally` after `Next()`

**Severity:** Warning · **Category:** Disruptor.Correctness

## Cause

`RingBuffer<T>.Next()` (or `ValueRingBuffer<T>.Next()`) claims a sequence, but the matching
`Publish()` call in the same method body is not inside a `finally` block.

## Why this matters

`Next()` reserves a slot. Until that exact sequence is published, **gating cannot advance past it**.
A single unpublished slot therefore stops every consumer and, once the ring wraps, blocks every
producer. Timing out a later capacity wait does not repair that publication gap.

If the code between `Next()` and `Publish()` can throw, the exception path must still publish.

## Example

```csharp
// Violation — an exception in Fill() jams the ring forever
long seq = ring.Next();
Fill(ring[seq], command);
ring.Publish(seq);
```

## How to fix

**Option 1 — `try`/`finally`:**

```csharp
long seq = ring.Next();
try
{
    Fill(ring[seq], command);
}
finally
{
    ring.Publish(seq);   // publishes even if Fill throws
}
```

**Option 2 — a narrowly scoped publication block:**

```csharp
using (var scope = ring.PublishEventScope())
{
    Fill(scope.Event, command);
}   // publishes on Dispose, including on the exception path
```

**Option 3 — `PublishEvent`**, which wraps the `finally` internally:

```csharp
ring.PublishEvent(static (evt, seq, cmd) => Fill(evt, cmd), command);
```

## Explicitly mark failed fills

A claimed slot must be published to preserve sequence continuity, but a half-written business event
is not automatically safe. Validate before claiming where possible. Otherwise, mark failed fills
and make every downstream stage check that marker:

```csharp
long seq = ring.Next();
try
{
    Fill(ring[seq], command);
    ring[seq].IsValid = true;
}
catch (Exception ex)
{
    ring[seq].IsValid = false;   // handler will skip it
    _log.Error(ex, "failed to fill seq {Seq}", seq);
}
finally
{
    ring.Publish(seq);
}
```

## Notes

The analyzer only reports when a `Publish` call **exists in the same method but outside a
`finally`**. Delegating publication to a helper method is a legitimate pattern that the analyzer
cannot follow, which is why the rule is a warning rather than an error.

`TryNext()` is not covered: it reports failure through its return value and does not claim a
sequence when it fails.
