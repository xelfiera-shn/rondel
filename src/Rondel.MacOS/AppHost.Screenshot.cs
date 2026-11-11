using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Threading;
using Rondel.Core.Localization;
using Rondel.Core.Logging;
using Rondel.MacOS.Interop;

namespace Rondel.MacOS;

/// <summary>
/// Ask About a Screenshot: its shortcut or the menu bar opens macOS's own picker (<c>screencapture -i</c>, as ⌘⇧4):
/// drag over an area, or press Space and click a window; Esc cancels. The picture is read into memory and its file
/// deleted at once (it never goes to the clipboard), and the Quick Conversation card asks about it.
/// </summary>
internal sealed partial class AppHost
{
    Process? _capture;

    /// <summary>Asks for the Screen Recording permission and opens its page in System Settings.</summary>
    public void AllowScreenRecording()
    {
        Native.CGRequestScreenCaptureAccess();
        Workspace.Open(Workspace.ScreenRecordingSettings);
    }

    /// <summary>Opens the picker; pressed again while picking, cancels it.</summary>
    void AskAboutScreenshot()
    {
        if (_capture is { } running)
        {
            try
            {
                running.Kill();
            }
            catch (InvalidOperationException)
            {
                // It has just ended.
            }
            return;
        }
        if (Store.Current.Paused)
            return;
        // Without the permission macOS would hand over the desktop picture without any window on it.
        if (!Native.CGPreflightScreenCaptureAccess())
        {
            Log.Warn("Ask About a Screenshot needs the Screen Recording permission");
            AllowScreenRecording();
            return;
        }
        // Rondel's own windows are not in the picture: the menu (or a card) closes, the agents card steps aside.
        _menu?.CloseMenu(restoreFocus: true);
        _card?.HideForCapture();
        string file = Path.Combine(Path.GetTempPath(), $"rondel-screenshot-{Guid.NewGuid():N}.png");
        // -i: the picker, -o: a window without its shadow.
        var start = new ProcessStartInfo("/usr/sbin/screencapture") { UseShellExecute = false, ArgumentList = { "-i", "-o", file } };
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("screencapture did not start");
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            Log.Warn("Starting screencapture failed", ex);
            CardAfterCapture();
            Warn(Loc.Format("Screenshot.Failed", ex.Message));
            return;
        }
        _capture = process;
        Log.Info("Picking a part of the screen");
        _ = Task.Run(async () =>
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            byte[]? png = TakePicture(file);
            Dispatcher.UIThread.Post(() =>
            {
                _capture = null;
                process.Dispose();
                CardAfterCapture();
                if (png is null)
                {
                    Log.Info("Ask About a Screenshot cancelled");
                    return;
                }
                _flow.AskAboutScreenshot(png);
            });
        });
    }

    /// <summary>Brings the agents card back after the picker, and the menu bar's "Show the agents card" with it.</summary>
    void CardAfterCapture()
    {
        _card?.ShowAfterCapture();
        UpdateCardMenu();
    }

    /// <summary>
    /// The picked picture, or null when nothing was picked (Esc, or ⌃ held, which sends it to the clipboard instead);
    /// its file is deleted either way.
    /// </summary>
    static byte[]? TakePicture(string file)
    {
        try
        {
            if (!File.Exists(file))
                return null;
            byte[] png = File.ReadAllBytes(file);
            return png.Length > 0 ? png : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Reading the screenshot '{file}' failed", ex);
            return null;
        }
        finally
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Deleting the screenshot '{file}' failed", ex);
            }
        }
    }
}
