using Disruptor;
using Disruptor.Dsl;
using Disruptor.WaitStrategy;

namespace UsageRecipes;

internal static class OrderPipeline
{
    public static void Run()
    {
        var validate = new ValidateHandler();
        var price = new PriceHandler();
        var accept = new AcceptHandler();
        using var pipeline = new Disruptor<Order>(
            () => new(), 16, ProducerType.Single, new BlockingWaitStrategy());

        // Both readers see every order. Each writes a DIFFERENT field.
        // Equivalent short form: pipeline.HandleEventsWith(validate, price).Then(accept).
        var validation = pipeline.HandleEventsWith(validate);
        var pricing = pipeline.HandleEventsWith(price);
        validation.And(pricing).Then(accept);
        var ring = pipeline.Start();
        foreach (int quantity in new[] { 2, -1, 3 })
            ring.PublishEvent(static (evt, _, value) =>
            {
                evt.Quantity = value;
                evt.Valid = false;
                evt.Total = 0;
            }, quantity);

        pipeline.Shutdown(TimeSpan.FromSeconds(5));
        Check.That(validate.Count == 3 && price.Count == 3 && accept.Accepted == 2
                   && accept.Rejected == 1 && accept.Revenue == 500, "Pipeline result differs.");
        Console.WriteLine($"Each branch saw 3 orders; accepted={accept.Accepted}, rejected={accept.Rejected}, revenue={accept.Revenue}");
    }

    private sealed class Order
    {
        public int Quantity;
        public bool Valid;
        public int Total;
    }
    private sealed class ValidateHandler : IEventHandler<Order>
    {
        public int Count;
        public void OnEvent(Order data, long sequence, bool endOfBatch)
        {
            data.Valid = data.Quantity > 0;
            Count++;
        }
    }
    private sealed class PriceHandler : IEventHandler<Order>
    {
        public int Count;
        public void OnEvent(Order data, long sequence, bool endOfBatch)
        {
            data.Total = data.Quantity * 100;
            Count++;
        }
    }
    private sealed class AcceptHandler : IEventHandler<Order>
    {
        public int Accepted, Rejected, Revenue;
        public void OnEvent(Order data, long sequence, bool endOfBatch)
        {
            // Then waits for both upstream sequences; their writes are now visible.
            if (data.Valid) { Accepted++; Revenue += data.Total; }
            else Rejected++;
        }
    }
}
