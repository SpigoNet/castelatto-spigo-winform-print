namespace ZplPrintManager.Core;

// Two operations may run concurrently. Maintenance closes admission and drains both slots.
public sealed class ProcessingGate
{
    private readonly SemaphoreSlim admission = new(1, 1);
    private readonly SemaphoreSlim slots = new(2, 2);
    public async Task<IDisposable> EnterOperationAsync(CancellationToken token)
    {
        await admission.WaitAsync(token);
        try { await slots.WaitAsync(token); return new Lease(slots); }
        finally { admission.Release(); }
    }
    public async Task WaitAsync(CancellationToken token)
    {
        await admission.WaitAsync(token); int acquired = 0;
        try { await slots.WaitAsync(token); acquired++; await slots.WaitAsync(token); }
        catch { if (acquired > 0) slots.Release(acquired); admission.Release(); throw; }
    }
    public void Release() { slots.Release(2); admission.Release(); }
    private sealed class Lease(SemaphoreSlim semaphore) : IDisposable
    {
        private int disposed;
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) semaphore.Release(); }
    }
}
