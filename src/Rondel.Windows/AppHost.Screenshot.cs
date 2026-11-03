using System.Windows.Media.Imaging;
using Rondel.Core;
using Rondel.Core.Localization;
using Rondel.Core.Logging;
using Rondel.Windows.Interop;
using Rondel.Windows.UI;

namespace Rondel.Windows;

/// <summary>
/// Ask About a Screenshot: its shortcut or the tray menu freezes the screen, the user picks a part of it, and the
/// Quick Conversation card asks about it. The picture stays in memory (never on the clipboard) until the conversation
/// starts.
/// </summary>
internal sealed partial class AppHost
{
    ScreenshotWindow? _screenshotWindow;

    /// <summary>Why the Ask About a Screenshot shortcut is not active, or null.</summary>
    public string? ScreenshotHotkeyError { get; private set; }

    void ApplyScreenshotHotkey(string text)
    {
        ScreenshotHotkeyError = null;
        if (!HotkeyBinding.TryParse(text, out var binding))
        {
            _tray.ClearScreenshotHotkey();
            if (!string.IsNullOrWhiteSpace(text))
                Log.Warn($"Ask About a Screenshot shortcut '{text}' could not be parsed");
            return;
        }
        if (!_tray.SetScreenshotHotkey(binding.NativeModifiers, binding.VirtualKey))
            ScreenshotHotkeyError = Loc.Get("Settings.Hotkey.Failed");
        else
            Log.Info($"Ask About a Screenshot shortcut {binding} registered");
    }

    /// <summary>Freezes the screen for picking a part of it; pressed again while picking, cancels.</summary>
    void AskAboutScreenshot()
    {
        if (_screenshotWindow is { } open)
        {
            open.Close();
            return;
        }
        if (Store.Current.Paused)
            return;
        // Rondel's own windows are not in the picture: the menu (or a card) closes, the agents card steps aside.
        _menu.CloseMenu(restoreFocus: true);
        bool cardWasShown = _card?.IsShown == true;
        if (cardWasShown)
            _card!.HideForCapture();
        // Give Windows a frame to draw what was under them before the screen is copied.
        _ui.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            Task.Delay(120).ContinueWith(_ => _ui.BeginInvoke(() => Capture(cardWasShown)), TaskScheduler.Default);
        });
    }

    void Capture(bool restoreCard)
    {
        BitmapSource pixels;
        RECT bounds;
        try
        {
            (pixels, bounds) = ScreenCapture.VirtualScreen();
        }
        catch (InvalidOperationException ex)
        {
            Log.Warn("Capturing the screen failed", ex);
            if (restoreCard)
                _card?.ShowAfterCapture();
            ShowBalloon(AppInfo.Name, Loc.Format("Screenshot.Failed", ex.Message), warning: true);
            return;
        }
        var palette = RondelPalette.For(Store.Current.Appearance, SystemTheme.Read());
        var window = new ScreenshotWindow(pixels, bounds, ScreenCapture.Monitors(), ScreenCapture.Windows(), palette, picked =>
        {
            _screenshotWindow = null;
            if (restoreCard)
                _card?.ShowAfterCapture();
            if (picked is null)
            {
                Log.Info("Ask About a Screenshot cancelled");
                return;
            }
            _flow.AskAboutScreenshot(Png(picked));
        });
        window.Closed += (_, _) => _screenshotWindow = null;
        _screenshotWindow = window;
        Log.Info($"Picking a part of the screen {bounds}");
        window.Show();
    }

    static byte[] Png(BitmapSource picture)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(picture));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
