namespace AutoJev.Local;

// One active lease, with cancelable waiters in acceptance order. Waiting holds
// no parsed request bodies; the HTTP transport applies backpressure to uploads.
internal sealed class FifoGate(int capacity)
{
    private readonly object sync = new();
    private readonly LinkedList<Waiter> waiting = new();
    private bool active;
    private bool maintenance;

    public (int Active, int Waiting) Snapshot { get { lock (sync) return (active && !maintenance ? 1 : 0, waiting.Count); } }

    public Task<Lease> EnterAsync(CancellationToken cancellation)
    {
        Waiter waiter;
        lock (sync)
        {
            if (cancellation.IsCancellationRequested) return Task.FromCanceled<Lease>(cancellation);
            if (!active) { active = true; return Task.FromResult(new Lease(this)); }
            if (waiting.Count >= capacity) return Task.FromException<Lease>(new QueueFullException());
            waiter = new Waiter();
            waiter.Node = waiting.AddLast(waiter);
        }
        return WaitAsync(waiter, cancellation);
    }

    private async Task<Lease> WaitAsync(Waiter waiter, CancellationToken cancellation)
    {
        using var registration = cancellation.Register(() =>
        {
            lock (sync)
            {
                if (waiter.Node!.List is null) return; // Already handed to its caller.
                waiting.Remove(waiter.Node);
                waiter.Completion.TrySetCanceled(cancellation);
            }
        });
        return await waiter.Completion.Task.ConfigureAwait(false);
    }

    public Lease? TryEnterIdle()
    {
        lock (sync)
        {
            if (active) return null;
            active = true; maintenance = true;
            return new Lease(this);
        }
    }

    private void Release()
    {
        lock (sync)
        {
            maintenance = false;
            if (waiting.First is { } first)
            {
                waiting.RemoveFirst();
                first.Value.Completion.SetResult(new Lease(this));
            }
            else active = false;
        }
    }

    private sealed class Waiter
    {
        public readonly TaskCompletionSource<Lease> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public LinkedListNode<Waiter>? Node;
    }

    internal sealed class QueueFullException : Exception { }
    internal sealed class Lease(FifoGate owner) : IDisposable
    {
        private FifoGate? owner = owner;
        public void Dispose() => Interlocked.Exchange(ref owner, null)?.Release();
    }
}
