using System.Collections.Concurrent;
using Rondel.Core.Logging;

namespace Rondel.MacOS.Detection;

/// <summary>
/// One thread for the window list, Accessibility and Apple Events requests, which are synchronous and may wait on
/// another app, so neither the tap thread nor the main thread ever does.
/// </summary>
internal sealed class DetectionWorker : IDisposable
{
    readonly BlockingCollection<Action> _work = [];

    public DetectionWorker()
    {
        new Thread(() =>
        {
            foreach (var work in _work.GetConsumingEnumerable())
            {
                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    Log.Error("Unhandled exception on the detection thread", ex);
                }
            }
        })
        { IsBackground = true, Name = "ContextDetection" }.Start();
    }

    public void Post(Action work) => _work.TryAdd(work);

    public void Dispose() => _work.CompleteAdding();
}
