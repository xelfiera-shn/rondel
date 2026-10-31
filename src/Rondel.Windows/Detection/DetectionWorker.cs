using System.Windows.Threading;
using Rondel.Core.Logging;

namespace Rondel.Windows.Detection;

/// <summary>
/// Dedicated STA thread with a message pump for UI Automation and Shell COM calls, so neither
/// the hook thread nor the UI thread ever waits on Explorer.
/// </summary>
internal sealed class DetectionWorker : IDisposable
{
    readonly Dispatcher _dispatcher;

    public DetectionWorker()
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.UnhandledException += (_, e) =>
            {
                Log.Error("Unhandled exception on the detection thread", e.Exception);
                e.Handled = true;
            };
            ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "ContextDetection",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        _dispatcher = dispatcher!;
    }

    public void Post(Action work) => _dispatcher.BeginInvoke(work);

    public void Dispose() => _dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
}
