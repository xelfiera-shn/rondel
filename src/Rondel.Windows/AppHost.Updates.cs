using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using Rondel.Core;
using Rondel.Core.Localization;
using Rondel.Core.Logging;
using Rondel.Core.Updates;
using Rondel.Windows.UI;
using Rondel.Windows.Updates;

namespace Rondel.Windows;

/// <summary>Looking for newer releases, asking the user, and updating Rondel in place.</summary>
internal sealed partial class AppHost
{
    /// <summary>%LOCALAPPDATA%\Rondel\updates: the checks' state and the download until it is installed.</summary>
    static string UpdatesDirectory => Path.Combine(Path.GetDirectoryName(AppInfo.LogDirectory)!, "updates");

    /// <summary>
    /// Developer aid: RONDEL_UPDATE_FEED names another "latest release" address, such as a local test feed. What it
    /// offers is still taken only when it carries the release key's signature.
    /// </summary>
    static Uri? TestFeed => Uri.TryCreate(Environment.GetEnvironmentVariable("RONDEL_UPDATE_FEED"), UriKind.Absolute, out var feed) ? feed : null;

    /// <summary>No overall time limit: the update checks set their own, and a download is given up when it stalls.</summary>
    readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };

    readonly UpdateService _updates;
    UpdateWindow? _updateWindow;
    CancellationTokenSource? _updateCancel;

    public UpdateService Updates => _updates;

    void StartUpdates()
    {
        if (Environment.ProcessPath is { } exe)
            SelfUpdate.RemoveOld(exe);
        _updates.Found += release => _ui.InvokeAsync(() => AnnounceUpdate(release));
        _updates.StatusChanged += () => _ui.InvokeAsync(() => _settingsWindow?.ShowUpdateStatus());
        _updates.Start();
    }

    void StopUpdates()
    {
        _updateCancel?.Cancel();
        _updates.Dispose();
        _http.Dispose();
    }

    void AnnounceUpdate(ReleaseInfo release) =>
        ShowBalloon(Loc.Format("Update.Notice.Title", release.Version), Loc.Get("Update.Notice.Text"), warning: false,
            icon: NoticeIcon.Update, onClick: () => OpenUpdateWindow(release));

    /// <summary>Settings → Check now: a newer release opens the update window.</summary>
    public async Task CheckForUpdatesAsync()
    {
        if (await _updates.CheckAsync(manual: true) == UpdateStatus.Available && _updates.Latest is { } release)
            OpenUpdateWindow(release);
    }

    /// <summary>Started by an update: says so, and a click shows what is new.</summary>
    public void ShowUpdated(AppVersion from)
    {
        var current = AppVersion.Current;
        Log.Info($"Updated from {from} to {current}");
        string page = $"https://github.com/xelfiera-shn/rondel/releases/tag/v{current}";
        ShowBalloon(Loc.Format("Update.Done.Title", current), Loc.Get("Update.Done.Text"), warning: false, icon: NoticeIcon.Done,
            onClick: () => ShellOpen(page));
    }

    public void OpenUpdateWindow(ReleaseInfo release)
    {
        if (_updateWindow is { } open && open.Release.Version == release.Version)
        {
            if (open.WindowState == WindowState.Minimized)
                open.WindowState = WindowState.Normal;
            open.Activate();
            return;
        }
        _updateWindow?.Close();
        var window = new UpdateWindow(release, _updates.Current, SystemTheme.Read());
        window.UpdateRequested += () => _ = UpdateAsync(window);
        window.CancelRequested += () => _updateCancel?.Cancel();
        window.SkipRequested += () => _updates.Skip(release);
        window.ReleasePageRequested += () => ShellOpen(release.Page.ToString());
        window.Closed += (_, _) =>
        {
            if (_updateWindow == window)
                _updateWindow = null;
        };
        _updateWindow = window;
        window.Show();
        window.Activate();
    }

    /// <summary>Downloads and verifies the release's exe, puts it in place and restarts into it; the window shows each step.</summary>
    async Task UpdateAsync(UpdateWindow window)
    {
        var release = window.Release;
        string? exe = Environment.ProcessPath;
        if (exe is null || !SelfUpdate.CanReplace(exe))
        {
            Log.Warn($"Rondel cannot replace '{exe}': updating from the release page instead");
            window.ShowFailed(Loc.Format("Update.NotWritable", Path.GetDirectoryName(exe) ?? ""), canRetry: false);
            return;
        }

        string asset = ReleaseInfo.WindowsExecutable(release.Version, RuntimeInformation.ProcessArchitecture);
        using var cancel = new CancellationTokenSource();
        _updateCancel = cancel;
        bool downloading = true; // progress reports are posted, and may arrive after the download ended
        var progress = new Progress<DownloadProgress>(p =>
        {
            if (downloading)
                window.ShowWorking(Downloading(p.Done, p.Total), p.Total > 0 ? (double)p.Done / p.Total : null, canCancel: true);
        });
        window.ShowWorking(Downloading(0, release.Asset(asset)?.Size ?? 0), 0, canCancel: true);
        string file;
        try
        {
            Log.Info($"Downloading {asset}");
            file = await new UpdateDownloader(_http, ReleaseVerifier.PublicKey).DownloadAsync(release, asset, UpdatesDirectory, progress, cancel.Token);
        }
        catch (OperationCanceledException)
        {
            Log.Info($"Update to {release.Version} cancelled");
            window.ShowOffer();
            return;
        }
        catch (UpdateException ex)
        {
            Log.Warn($"Update to {release.Version} failed: {ex.Message}", ex.InnerException);
            window.ShowFailed(Loc.Get(ex.Failure switch
            {
                UpdateFailure.Network => "Update.Failed.Network",
                UpdateFailure.Disk => "Update.Failed.Disk",
                UpdateFailure.Missing => "Update.Failed.Missing",
                _ => "Update.Failed.NotVerified",
            }), canRetry: ex.Failure is UpdateFailure.Network or UpdateFailure.Disk);
            return;
        }
        finally
        {
            downloading = false;
            _updateCancel = null;
        }

        window.ShowWorking(Loc.Get("Update.Restarting"), null, canCancel: false);
        Log.Info($"Installing {release.Version} over {_updates.Current}");
        if (await Task.Run(() => SelfUpdate.Install(exe, file, _updates.Current)))
        {
            Application.Current.Shutdown();
            return;
        }
        window.ShowFailed(Loc.Format("Update.Failed.Install", _updates.Current), canRetry: false);
    }

    static string Downloading(long done, long total) =>
        Loc.Format("Update.Downloading", Megabytes(done), Megabytes(total));

    static string Megabytes(long bytes) => (bytes / 1048576.0).ToString("0.0", Loc.Culture);
}
