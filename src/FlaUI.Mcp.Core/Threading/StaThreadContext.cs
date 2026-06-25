using System.Collections.Concurrent;

namespace FlaUI.Mcp.Core.Threading;

/// <summary>A single dedicated STA thread that serially executes queued work.</summary>
public sealed class StaThreadContext : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;

    public StaThreadContext(string name)
    {
        _thread = new Thread(Run) { Name = name, IsBackground = true };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private void Run()
    {
        foreach (var work in _queue.GetConsumingEnumerable())
            work();
    }

    public Task<T> RunAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    public Task RunAsync(Action action) => RunAsync(() => { action(); return true; });

    public void Dispose()
    {
        _queue.CompleteAdding();
        if (_thread.IsAlive) _thread.Join(TimeSpan.FromSeconds(5));
        _queue.Dispose();
    }
}
