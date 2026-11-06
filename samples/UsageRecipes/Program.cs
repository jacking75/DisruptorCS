using UsageRecipes;

Console.OutputEncoding = System.Text.Encoding.UTF8;
var recipes = new (string Name, string Description, Func<Task> Run)[]
{
    ("publish", "Publish safely with scopes, translators, and batches", Sync(Publication.Run)),
    ("pipeline", "Validate and price in parallel, then accept an order", Sync(OrderPipeline.Run)),
    ("multi-producer", "Merge concurrent connections and verify per-producer order", MultiProducer.Run),
    ("batch", "Aggregate telemetry using bounded spans", Sync(BatchAggregation.Run)),
    ("value", "Transform struct events in place", Sync(ValueEvents.Run)),
    ("backpressure", "Observe drop, rejection, cancellation, and recovery", Sync(Backpressure.Run)),
    ("poller", "Process a fixed number of commands per frame", Sync(FramePolling.Run)),
    ("workers", "Distribute independent jobs and join their results", Sync(WorkerJobs.Run)),
    ("async-batch", "Await a simulated batch sink without releasing slots early", AsyncSink.Run),
    ("pooled-payload", "Return rented packet buffers after all readers finish", Sync(PooledPayload.Run)),
    ("errors", "Copy rejected events and continue after invalid input", Sync(ErrorHandling.Run)),
    ("snapshots", "Expose owned snapshots through await foreach", SnapshotStream.Run),
    ("journal", "Record before applying state, then verify and replay", Sync(JournalReplay.Run)),
};

if (args.Length == 0 || (args.Length == 1 && args[0] is "--list" or "--help"))
{
    Console.WriteLine("Usage: dotnet run --project samples/UsageRecipes -c Release -- <name|all|--list>");
    foreach (var recipe in recipes)
        Console.WriteLine($"  {recipe.Name,-18} {recipe.Description}");
    return 0;
}

if (args.Length != 1 || (args[0] != "all" && !recipes.Any(r => r.Name == args[0])))
{
    Console.Error.WriteLine("Unknown recipe or extra arguments. Use --list to see the available names.");
    return 2;
}

try
{
    foreach (var recipe in recipes.Where(r => args[0] == "all" || r.Name == args[0]))
    {
        Console.WriteLine($"\n--- {recipe.Name}: {recipe.Description} ---");
        await recipe.Run();
        Console.WriteLine("PASS");
    }
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: {ex}");
    return 1;
}

static Func<Task> Sync(Action action) => () => { action(); return Task.CompletedTask; };
