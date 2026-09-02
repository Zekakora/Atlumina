namespace MyAlbum_App.Services;

/// <summary>Minimal async lock to serialize file writes from multiple settings changes.</summary>
public sealed class AsyncLock
{
    private readonly SemaphoreSlim _sem = new(1, 1);

    public async Task<IDisposable> LockAsync()
    {
        await _sem.WaitAsync();
        return new Releaser(_sem);
    }

    private sealed class Releaser : IDisposable
    {
        private readonly SemaphoreSlim _sem;
        public Releaser(SemaphoreSlim sem) => _sem = sem;
        public void Dispose() => _sem.Release();
    }
}
