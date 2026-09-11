using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace TiaMcp.Openness;

/// <summary>
/// Runs a single dedicated STA thread that every Openness/COM call is marshaled onto.
/// Openness requires calls to originate from an STA thread; MCP tool handlers run on
/// the thread pool by default, so this is the bridge between the two.
/// </summary>
public sealed class StaDispatcher : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;

    public StaDispatcher()
    {
        _thread = new Thread(RunLoop)
        {
            IsBackground = true,
            Name = "TiaMcp-STA",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private void RunLoop()
    {
        foreach (var action in _queue.GetConsumingEnumerable())
        {
            action();
        }
    }

    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() =>
        {
            try
            {
                tcs.SetResult(func());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }

    public Task InvokeAsync(Action action)
    {
        return InvokeAsync<object?>(() =>
        {
            action();
            return null;
        });
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(5));
        _queue.Dispose();
    }
}
