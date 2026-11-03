using Avalonia.Threading;
using Rondel.Core;
using Rondel.Core.Localization;
using Rondel.Core.Logging;
using Rondel.Core.Settings;
using Rondel.Core.Updates;
using Rondel.MacOS.Interop;
using Rondel.MacOS.UI;

namespace Rondel.MacOS.Updates;

/// <summary>
/// Looking for newer releases, asking the user, and updating Rondel.app in place: the counterpart of the Windows
/// AppHost.Updates. The checks are the core's (<see cref="UpdateService"/>); a release is announced with a
/// notification, offered in the menu bar menu and installed by <see cref="MacSelfUpdate"/>.
/// </summary>
internal sealed class MacUpdates : IDisposable
{
    /// <summary>~/Library/Caches/Rondel/updates: the download until it is installed.</summary>
    static readonly string Downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Caches", "Rondel", "updates");

    /// <summary>
    /// Developer aid: RONDEL_UPDATE_FEED names another "latest release" address, such as a local test feed. What it
    /// offers is still taken only when it carries the release key's signature.
    /// </summary>
    static Uri? TestFeed => Uri.TryCreate(Environment.GetEnvironmentVariable("RONDEL_UPDATE_FEED"), UriKind.Absolute, out var feed) ? feed : null;

    /// <summary>No overall time limit: the update checks set their own, and a download is given up when it stalls.</summary>
    readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    readonly Action _quit;
    UpdateWindow? _window;
    CancellationTokenSource? _cancel;

    /// <param name="quit">Ends Rondel once the update script waits for it.</param>
    public MacUpdates(SettingsStore store, Action quit)
    {
        _quit = quit;
        // A release that changes only the Windows app is not announced here.
        Service = new UpdateService(_http, AppVersion.Current, () => store.Current.Updates.Check,
            Path.Combine(AppInfo.SettingsDirectory, "update-state.json"), TestFeed,
            concerns: release => !ReleaseNotes.OnlyScoped(release.Notes, "windows"));
    }

    public UpdateService Service { get; }

    /// <summary>The release to offer in the menu bar menu: newer, about this app, and not skipped.</summary>
    public ReleaseInfo? Offered =>
        Service.Latest is { } release && Service.Concerns(release) && !Service.IsSkipped(release) ? release : null;

    /// <summary>The status or the offered release changed (on the UI thread).</summary>
    public event Action? Changed;

    public void Start()
    {
        RemoveDownloads();
        Service.Found += release => Dispatcher.UIThread.Post(() => Announce(release));
        Service.StatusChanged += () => Dispatcher.UIThread.Post(() => Changed?.Invoke());
        Service.Start();
    }

    /// <summary>A notification, and the update window when Rondel may not notify.</summary>
    void Announce(ReleaseInfo release)
    {
        Changed?.Invoke();
        Notifications.Show(Loc.Format("Update.Notice.Title", release.Version), Loc.Get("Update.Notice.Text"),
            clicked: () => OpenWindow(release), denied: () => OpenWindow(release));
    }

    /// <summary>Settings → Check now: a newer release opens the update window.</summary>
    public async Task CheckNowAsync()
    {
        if (await Service.CheckAsync(manual: true) == UpdateStatus.Available && Service.Latest is { } release)
            OpenWindow(release);
        Changed?.Invoke();
    }

    /// <summary>Started by an update: says so, and a click shows what is new.</summary>
    public static void ShowUpdated(AppVersion from)
    {
        var current = AppVersion.Current;
        Log.Info($"Updated from {from} to {current}");
        string page = $"https://github.com/xelfiera-shn/rondel/releases/tag/v{current}";
        Notifications.Show(Loc.Format("Update.Done.Title", current), Loc.Get("Update.Done.Text"), clicked: () => Workspace.Open(page));
    }

    public void OpenWindow(ReleaseInfo release)
    {
        if (_window is { } open && open.Release.Version == release.Version)
        {
            open.Activate();
            return;
        }
        _window?.Close();
        var window = new UpdateWindow(release, Service.Current);
        window.UpdateRequested += () => _ = UpdateAsync(window);
        window.CancelRequested += () => _cancel?.Cancel();
        window.SkipRequested += () =>
        {
            Service.Skip(release);
            Changed?.Invoke();
        };
        window.ReleasePageRequested += () => Workspace.Open(release.Page.ToString());
        window.Closed += (_, _) =>
        {
            if (_window == window)
                _window = null;
        };
        _window = window;
        window.Show();
        window.Activate();
    }

    /// <summary>Downloads and verifies the release's zip, and hands it to the update script; the window shows each step.</summary>
    async Task UpdateAsync(UpdateWindow window)
    {
        var release = window.Release;
        if (MacSelfUpdate.Bundle() is not { } bundle || !MacSelfUpdate.CanReplace(bundle))
        {
            string where = MacSelfUpdate.Bundle() is { } app ? Path.GetDirectoryName(app)! : AppContext.BaseDirectory;
            Log.Warn($"Rondel cannot replace itself in '{where}': updating from the release page instead");
            window.ShowFailed(Loc.Format("Update.NotWritable", where), canRetry: false);
            return;
        }

        string asset = MacSelfUpdate.ZipName(release.Version);
        using var cancel = new CancellationTokenSource();
        _cancel = cancel;
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
            file = await new UpdateDownloader(_http, ReleaseVerifier.PublicKey).DownloadAsync(release, asset, Downloads, progress, cancel.Token);
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
            _cancel = null;
        }

        window.ShowWorking(Loc.Get("Update.Restarting"), null, canCancel: false);
        Log.Info($"Installing {release.Version} over {Service.Current}");
        if (await Task.Run(() => MacSelfUpdate.Install(bundle, file, Service.Current)))
        {
            _quit();
            return;
        }
        window.ShowFailed(Loc.Format("Update.Failed.Install", Service.Current), canRetry: false);
    }

    /// <summary>
    /// What an earlier update left (its zip, the unpacked app, the script): nothing is installing while Rondel starts.
    /// </summary>
    static void RemoveDownloads()
    {
        try
        {
            if (Directory.Exists(Downloads))
                Directory.Delete(Downloads, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Removing '{Downloads}' failed", ex);
        }
    }

    static string Downloading(long done, long total) =>
        Loc.Format("Update.Downloading", Megabytes(done), Megabytes(total));

    static string Megabytes(long bytes) => (bytes / 1048576.0).ToString("0.0", Loc.Culture);

    public void Dispose()
    {
        _cancel?.Cancel();
        Service.Dispose();
        _http.Dispose();
    }
}
