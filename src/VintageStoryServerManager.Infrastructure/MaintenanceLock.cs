namespace VintageStoryServerManager.Infrastructure;

/// <summary>
/// Serialises operations that change files in the data volume (mod changes, backups,
/// restores, config edits, game installs) so they never overlap.
/// </summary>
public sealed class MaintenanceLock
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        return new Releaser(_semaphore);
    }

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                semaphore.Release();
            }
        }
    }
}
