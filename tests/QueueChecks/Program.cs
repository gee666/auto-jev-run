using AutoJev.Local;

void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
async Task Canceled(Task<FifoGate.Lease> task)
{
    try { using var lease = await task.WaitAsync(TimeSpan.FromSeconds(2)); throw new Exception("Expected cancellation"); }
    catch (OperationCanceledException) { }
}

var gate = new FifoGate(3);
var first = await gate.EnterAsync(default);
using var cancel = new CancellationTokenSource();
var a = gate.EnterAsync(default);
var b = gate.EnterAsync(cancel.Token);
var c = gate.EnterAsync(default);
Check(gate.Snapshot == (1, 3), "Queue counts");
try { using var rejected = await gate.EnterAsync(default); throw new Exception("Expected overflow"); }
catch (FifoGate.QueueFullException) { }
Check(gate.TryEnterIdle() is null, "Unload must not bypass active work");
cancel.Cancel(); await Canceled(b);
Check(gate.Snapshot == (1, 2), "Cancellation frees capacity immediately");
var d = gate.EnterAsync(default);
first.Dispose(); first.Dispose();
using (var lease = await a.WaitAsync(TimeSpan.FromSeconds(2)))
    Check(!c.IsCompleted && !d.IsCompleted, "FIFO and idempotent release");
using (var lease = await c.WaitAsync(TimeSpan.FromSeconds(2)))
    Check(!d.IsCompleted, "Survivors retain FIFO order");
(await d).Dispose();
Check(gate.Snapshot == (0, 0), "Fully drained");
using (var idle = gate.TryEnterIdle())
{
    Check(idle is not null && gate.Snapshot == (0, 0), "Maintenance isn't a request");
    using var timeout = new CancellationTokenSource(20);
    await Canceled(gate.EnterAsync(timeout.Token));
}
Check(gate.Snapshot == (0, 0), "Timeout leaves no stale waiter");

for (var i = 0; i < 500; i++)
{
    var race = new FifoGate(1);
    var held = await race.EnterAsync(default);
    using var token = new CancellationTokenSource();
    var queued = race.EnterAsync(token.Token);
    await Task.WhenAll(Task.Run(held.Dispose), Task.Run(token.Cancel));
    try { (await queued.WaitAsync(TimeSpan.FromSeconds(2))).Dispose(); }
    catch (OperationCanceledException) { }
    Check(race.Snapshot == (0, 0), "Cancellation/release race leaked a lease");
}
var shutdown = new FifoGate(10);
using (var held = await shutdown.EnterAsync(default))
using (var stop = new CancellationTokenSource())
{
    var tasks = Enumerable.Range(0, 10).Select(_ => shutdown.EnterAsync(stop.Token)).ToArray();
    stop.Cancel();
    foreach (var task in tasks) await Canceled(task);
    Check(shutdown.Snapshot == (1, 0), "Shutdown drains all waiting requests");
}
Console.WriteLine("PASS: FIFO, overflow, cancellation, timeout, idle exclusion, shutdown and 500 release/cancel races.");
