namespace Mixer.Services;

public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _show;
    private readonly EventWaitHandle _exit;
    private readonly Thread _thread;
    private bool _disposed;

    private SingleInstance(Mutex mutex, Action onShow)
    {
        _mutex = mutex;
        _show = new EventWaitHandle(false, EventResetMode.AutoReset, AppInfo.ShowEventName);
        _exit = new EventWaitHandle(false, EventResetMode.ManualReset);
        _thread = new Thread(() =>
        {
            var handles = new WaitHandle[] { _show, _exit };
            while (WaitHandle.WaitAny(handles) == 0)
            {
                try { onShow(); }
                catch { /* the UI thread reports its own failures */ }
            }
        })
        {
            IsBackground = true,
            Name = "Mixer.SingleInstance"
        };
        _thread.Start();
    }

    public static bool TryAcquire(Action onShow, out SingleInstance? instance)
    {
        var mutex = new Mutex(true, AppInfo.MutexName, out var created);
        if (!created)
        {
            SignalExisting();
            mutex.Dispose();
            instance = null;
            return false;
        }

        instance = new SingleInstance(mutex, onShow);
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _exit.Set(); } catch { /* already gone */ }
        try { _thread.Join(500); } catch { /* background thread */ }
        try { _show.Dispose(); } catch { /* ignore */ }
        try { _exit.Dispose(); } catch { /* ignore */ }
        try
        {
            _mutex.ReleaseMutex();
            _mutex.Dispose();
        }
        catch
        {
            // The OS releases the mutex when the process exits.
        }
    }

    private static void SignalExisting()
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            try
            {
                using var ev = EventWaitHandle.OpenExisting(AppInfo.ShowEventName);
                ev.Set();
                return;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Thread.Sleep(50);
            }
            catch
            {
                return;
            }
        }
    }
}
