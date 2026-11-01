using System.Windows;
using System.Windows.Threading;
using Rondel.Core.Logging;
using Rondel.Windows.Updates;

namespace Rondel.Windows;

public partial class App : Application
{
    const string MutexName = @"Local\Rondel.SingleInstance";
    const string ActivateEventName = @"Local\Rondel.Activate";

    Mutex? _mutex;
    EventWaitHandle? _activateEvent;
    RegisteredWaitHandle? _activateWait;
    AppHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (DevTools.TryRun(e.Args))
        {
            Shutdown();
            return;
        }

        // Started by an update: tell the previous version that this one runs, and wait for it to exit and make room.
        var updatedFrom = SelfUpdate.AfterUpdate(e.Args);

        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool firstInstance);
        if (!firstInstance)
        {
            // Ask the running copy to show its settings, then leave.
            try
            {
                using var activate = EventWaitHandle.OpenExisting(ActivateEventName);
                activate.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
            }
            _mutex.Dispose();
            _mutex = null;
            Shutdown();
            return;
        }

        Log.Start();
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("Unhandled exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        _host = new AppHost();
        _host.Start();
        if (updatedFrom is { } from)
            _host.ShowUpdated(from);
        if (e.Args.Contains("--show-menu", StringComparer.OrdinalIgnoreCase))
        {
            int inputAt = Array.FindIndex(e.Args, a => a.Equals("input", StringComparison.OrdinalIgnoreCase));
            string? text = inputAt >= 0 && inputAt + 1 < e.Args.Length ? e.Args[inputAt + 1] : null;
            Dispatcher.BeginInvoke(() => _host.ShowMenuAtCursor(inputAt >= 0, text), DispatcherPriority.ApplicationIdle);
        }

        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        _activateWait = ThreadPool.RegisterWaitForSingleObject(_activateEvent,
            (_, _) => Dispatcher.BeginInvoke(() => _host?.OnSecondInstance()), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // A tray utility should survive a bug in one action; log it and keep running.
        Log.Error("Unhandled UI exception", e.Exception);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activateWait?.Unregister(null);
        _activateEvent?.Dispose();
        _host?.Dispose();
        if (_mutex is not null)
        {
            _mutex.ReleaseMutex();
            _mutex.Dispose();
        }
        Log.Info("Exited");
        Log.Stop();
        base.OnExit(e);
    }
}
