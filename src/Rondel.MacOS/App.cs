using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
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
        // Templates for the standard controls (the input cards' text boxes, the settings page), in Rondel's teal
        // instead of the system accent; the input cards take their colors from the menu palette.
        Styles.Add(new FluentTheme
        {
            Palettes =
            {
                [ThemeVariant.Dark] = new ColorPaletteResources { Accent = Color.FromRgb(0x14, 0xB8, 0xA6) },
                [ThemeVariant.Light] = new ColorPaletteResources { Accent = Color.FromRgb(0x0D, 0x94, 0x88) },
            },
        });
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
