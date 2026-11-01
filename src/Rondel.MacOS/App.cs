using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Rondel.Core.Logging;

namespace Rondel.MacOS;

internal sealed class App : Application
{
    AppHost? _host;

    // macOS shows this name for the app (otherwise "Avalonia Application").
    public override void Initialize()
    {
        Name = Core.AppInfo.Name;
        // Templates for the few standard controls Rondel uses (the text boxes of the input cards); colors come from the menu palette.
        Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // A menu bar app should survive a bug in one action: log it and keep running.
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Log.Error("Unhandled UI exception", e.Exception);
            e.Handled = true;
        };
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
        {
            _host = new AppHost();
            _host.QuitRequested += () => lifetime.Shutdown();
            lifetime.Exit += (_, _) =>
            {
                _host.Dispose();
                Log.Info("Exited");
                Log.Stop();
            };
            _host.Start();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
