# DISR005: Don't let batch span slot references escape

**Severity:** Warning · **Category:** Disruptor.Correctness

## Cause

An `IBatchEventHandler<T>.OnBatch` (or `IValueBatchEventHandler<T>.OnBatch`) implementation stores an
element of the span into a field, property, array, or collection.

## Why this matters

The span handed to `OnBatch` points **directly at ring buffer slots**. When the call returns, the
processor may advance its sequence, and the producer can reuse slots once all gating consumers
have advanced. The callback gives you no ownership after it returns.

A reference you kept therefore starts pointing at **a different event's data**. Nothing throws — the
values just silently change under you. In a deterministic pipeline that is the worst possible
failure mode, because replaying the journal will produce a different result than the live run did.

The span itself is a `ref struct`, so the compiler already stops you from storing *it*. But the
**elements** are ordinary references, so nothing stops you from storing those. That is the gap this
rule closes.

## Example

```csharp
// Violation — three different ways to leak the same reference
public sealed class BadHandler : IBatchEventHandler<OrderEvent>
{
    private OrderEvent? _last;
    private readonly List<OrderEvent> _seen = new();

    public void OnBatch(ReadOnlySpan<OrderEvent> events, long startSequence, bool endOfBatch)
    {
        _last = events[^1];              // field
        _seen.Add(events[0]);            // collection

        var e = events[0];
        _last = e;                       // via a local — still a leak
    }
}
```

## How to fix

**Option 1 — copy the values you need:**

```csharp
public void OnBatch(ReadOnlySpan<OrderEvent> events, long startSequence, bool endOfBatch)
{
    for (int i = 0; i < events.Length; i++)
        _sum += events[i].Quantity;      // reading a value is fine
}
```

**Option 2 — build an immutable snapshot:**

```csharp
public readonly record struct OrderSnapshot(long OrderId, int Quantity)
{
    public static OrderSnapshot From(OrderEvent e) => new(e.OrderId, e.Quantity);
}

public void OnBatch(ReadOnlySpan<OrderEvent> events, long startSequence, bool endOfBatch)
{
    for (int i = 0; i < events.Length; i++)
        _pending.Add(OrderSnapshot.From(events[i]));   // owns its data
}
```

**Option 3 — serialize inside the call**, which is often the reason you wanted a batch handler:

```csharp
public void OnBatch(ReadOnlySpan<OrderEvent> events, long startSequence, bool endOfBatch)
{
    // Encode the whole segment into a buffer you own, then flush once.
    for (int i = 0; i < events.Length; i++)
        _codec.Write(_buffer, events[i]);

    if (endOfBatch)
        _writer.Flush();
}
```

## What the rule does and doesn't catch

Caught:

- Assignment to a field, property, or array element
- `Add` / `Insert` / `Enqueue` / `Push` / `AddFirst` / `AddLast` / `TryAdd` on any collection
- The above reached through a local variable that was assigned from the span

Not caught (the analyzer cannot follow these):

- Passing a span element to a method that stores it internally
- Storing it inside a lambda or local function that outlives the call
- Reflection or `ref` reassignment

The rule is a `Warning` rather than an `Error` for exactly that reason: it catches the common shapes,
not all of them. Treat "no DISR005" as "no obvious leak", not as proof.

## Note for value-type handlers

`IValueBatchEventHandler<T>` receives a writable `Span<T>` of **structs**. Copying a struct out of
the span is a real value copy. A struct containing mutable references still needs a sufficiently
deep copy. The current analyzer conservatively flags direct span-element escapes for value types
too, and does not trace nested field accesses; absence of a warning does not prove ownership.
