using System.Threading.Channels;
using MyAlbum.Core.Data;

namespace MyAlbum.Core.Services;

/// <summary>
/// Watches registered folders with FileSystemWatcher and updates the index as files
/// appear, change, or disappear. Events are serialized through a channel so the
/// database is never touched concurrently.
/// </summary>
public sealed class FolderWatcherService : IDisposable
{
    private readonly LibraryService _library;
    private readonly PhotoDatabase _db;
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<WatchEvent> _queue = Channel.CreateUnbounded<WatchEvent>();
    private readonly Dictionary<string, DateTime> _lastHandled = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;

    /// <summary>Raised on the worker thread after the index changed (files added / removed / renamed).
    /// Subscribers should debounce / marshal to their own UI thread.</summary>
    public event Action? LibraryChanged;

    public FolderWatcherService(LibraryService library, PhotoDatabase db)
    {
        _library = library;
        _db = db;
        _worker = Task.Run(ProcessQueueAsync);
    }

    public void WatchFolder(string folder)
    {
        if (!Directory.Exists(folder) || _watchers.ContainsKey(folder))
        {
            return;
        }

        var watcher = new FileSystemWatcher(folder)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
            EnableRaisingEvents = true,
        };
        watcher.Created += (_, e) => Enqueue(EventKind.Created, e.FullPath);
        watcher.Changed += (_, e) => Enqueue(EventKind.Changed, e.FullPath);
        watcher.Deleted += (_, e) => Enqueue(EventKind.Deleted, e.FullPath);
        watcher.Renamed += (_, e) => Enqueue(EventKind.Renamed, e.OldFullPath, e.FullPath);

        _watchers[folder] = watcher;
    }

    public IReadOnlyList<string> WatchedFolders => _watchers.Keys.ToList();

    public void UnwatchFolder(string folder)
    {
        if (_watchers.TryGetValue(folder, out var watcher))
        {
            watcher.Dispose();
            _watchers.Remove(folder);
        }
    }

    private void Enqueue(EventKind kind, string path, string? newPath = null)
    {
        // Directories: keep only "Created" so a newly added subfolder can be watched and
        // scanned (FileSystemWatcher with IncludeSubdirectories does NOT watch subfolders
        // created after the watcher was installed).
        if (Directory.Exists(path))
        {
            if (kind == EventKind.Created)
            {
                _queue.Writer.TryWrite(new WatchEvent(kind, path, newPath));
            }
            return;
        }
        if (!LibraryService.IsSupportedFile(path))
        {
            return;
        }
        _queue.Writer.TryWrite(new WatchEvent(kind, path, newPath));
    }

    private async Task ProcessQueueAsync()
    {
        await foreach (var evt in _queue.Reader.ReadAllAsync(_cts.Token))
        {
            try
            {
                await HandleAsync(evt);
                NotifyChanged();
            }
            catch
            {
                // watcher failures must never crash the app
            }
        }
    }

    private long _lastChangeTicks;
    private int _notifyScheduled;

    /// <summary>Debounce rapid change bursts (e.g. copying 1000 files at once) into one signal:
    /// wait for a quiet 1.5s gap after the last change, then raise <see cref="LibraryChanged"/>.</summary>
    private void NotifyChanged()
    {
        Interlocked.Exchange(ref _lastChangeTicks, DateTime.UtcNow.Ticks);
        if (Interlocked.Exchange(ref _notifyScheduled, 1) == 0)
        {
            _ = NotifyWhenQuietAsync();
        }
    }

    private async Task NotifyWhenQuietAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(500, _cts.Token);
                if (_cts.IsCancellationRequested)
                {
                    return;
                }
                long last = Volatile.Read(ref _lastChangeTicks);
                if (DateTime.UtcNow.Ticks - last >= TimeSpan.FromMilliseconds(1500).Ticks)
                {
                    break;
                }
            }
            LibraryChanged?.Invoke();
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        finally
        {
            Interlocked.Exchange(ref _notifyScheduled, 0);
        }
    }

    private async Task HandleAsync(WatchEvent evt)
    {
        switch (evt.Kind)
        {
            case EventKind.Deleted:
                await _db.MarkMissingAsync(evt.Path, true);
                break;

            case EventKind.Renamed:
                await _db.MarkMissingAsync(evt.Path, true);
                if (evt.NewPath is not null && LibraryService.IsSupportedFile(evt.NewPath))
                {
                    await _library.IndexFileAsync(evt.NewPath);
                }
                break;

            case EventKind.Created:
                // A newly created subfolder: watch it (so photos added later sync) and scan
                // it immediately (so photos already copied into it get indexed right away).
                if (Directory.Exists(evt.Path))
                {
                    WatchFolder(evt.Path);
                    await _library.ScanFolderAsync(evt.Path);
                    break;
                }
                if (Debounce(evt.Path))
                {
                    await _library.IndexFileAsync(evt.Path);
                }
                break;

            case EventKind.Changed:
                // Debounce rapid Changed events that follow a Create.
                if (Debounce(evt.Path))
                {
                    await _library.IndexFileAsync(evt.Path);
                }
                break;
        }
    }

    private bool Debounce(string path)
    {
        var now = DateTime.UtcNow;
        _gate.Wait();
        try
        {
            if (_lastHandled.TryGetValue(path, out var last) && (now - last).TotalMilliseconds < 400)
            {
                return false;
            }
            _lastHandled[path] = now;
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _queue.Writer.TryComplete();
        foreach (var watcher in _watchers.Values)
        {
            watcher.Dispose();
        }
        _watchers.Clear();
        try
        {
            _worker.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // ignore
        }
    }

    private readonly record struct WatchEvent(EventKind Kind, string Path, string? NewPath);

    private enum EventKind
    {
        Created,
        Changed,
        Deleted,
        Renamed,
    }
}
