using System.Text.Json;
using Rondel.Core.Logging;

namespace Rondel.Core.Settings;

/// <summary>
/// Loads and saves settings.json and watches it, so edits made by hand or from the settings window
/// apply without a restart. <see cref="Current"/> is an immutable snapshot that is swapped as a whole;
/// other threads may read it at any time.
/// </summary>
public sealed class SettingsStore : IDisposable
{
    readonly SynchronizationContext _context;
    readonly Timer _debounce;
    FileSystemWatcher? _watcher;
    volatile AppSettings _current = new();
    string? _lastWrittenJson;

    /// <param name="context">Where <see cref="Changed"/> and <see cref="LoadFailed"/> are raised (the UI thread).</param>
    /// <param name="directory">Folder of settings.json; <see cref="AppInfo.SettingsDirectory"/> by default.</param>
    public SettingsStore(SynchronizationContext context, string? directory = null)
    {
        _context = context;
        DirectoryPath = directory ?? AppInfo.SettingsDirectory;
        FilePath = Path.Combine(DirectoryPath, "settings.json");
        _debounce = new Timer(_ => _context.Post(_ => ReloadFromDisk(), null));
    }

    public string DirectoryPath { get; }

    public string FilePath { get; }

    public AppSettings Current => _current;

    /// <summary>Message of the last failed parse; null when the file on disk is valid.</summary>
    public string? LoadError { get; private set; }

    /// <summary>Raised on the UI thread. The flag is true when the change came from the file (not from <see cref="Update"/>).</summary>
    public event Action<AppSettings, bool>? Changed;

    public event Action<string>? LoadFailed;

    public void Load()
    {
        Directory.CreateDirectory(DirectoryPath);
        if (!File.Exists(FilePath))
        {
            _current = Normalized(new AppSettings());
            Save();
            Log.Info($"Created default settings at {FilePath}");
        }
        else if (TryRead(out var settings, out string? json, out string? error))
        {
            _current = settings;
            _lastWrittenJson = json;
            if (settings.WasMigrated)
            {
                Save();
                Log.Info($"Upgraded settings.json to schema {AppSettings.CurrentSchemaVersion}");
            }
        }
        else
        {
            _current = Normalized(new AppSettings());
            LoadError = error;
        }
        StartWatching();
    }

    /// <summary>Applies a change made in the app (settings window, tray) and persists it.</summary>
    public void Update(Action<AppSettings> change)
    {
        var next = _current.Clone();
        change(next);
        next.Normalize();
        _current = next;
        Save();
        Changed?.Invoke(next, false);
    }

    void Save()
    {
        try
        {
            if (LoadError is not null && File.Exists(FilePath))
            {
                // Don't silently destroy a hand-edited file that failed to parse.
                string backup = FilePath + $".invalid-{DateTime.Now:yyyyMMdd-HHmmss}.bak";
                File.Copy(FilePath, backup, overwrite: true);
                Log.Warn($"Backed up unreadable settings to {backup}");
                LoadError = null;
            }
            string json = SettingsJson.Serialize(_current);
            _lastWrittenJson = json;
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Saving settings failed", ex);
        }
    }

    bool TryRead(out AppSettings settings, out string? json, out string? error)
    {
        settings = null!;
        json = null;
        try
        {
            json = File.ReadAllText(FilePath);
            settings = SettingsJson.Deserialize(json)
                ?? throw new JsonException("The file is empty.");
            settings.Normalize();
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            error = ex.Message;
            Log.Error($"Reading {FilePath} failed", ex);
            return false;
        }
    }

    void StartWatching()
    {
        try
        {
            _watcher = new FileSystemWatcher(DirectoryPath, "settings.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            _watcher.Changed += (_, _) => RestartDebounce();
            _watcher.Created += (_, _) => RestartDebounce();
            _watcher.Renamed += (_, _) => RestartDebounce();
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException)
        {
            Log.Warn("Watching settings.json failed; external edits need a restart", ex);
        }
    }

    /// <summary>Editors save in several steps; reload once things have been quiet for a moment.</summary>
    void RestartDebounce() => _debounce.Change(350, Timeout.Infinite);

    void ReloadFromDisk()
    {
        string? json;
        try
        {
            json = File.ReadAllText(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // Mid-write or deleted; a later event will bring us back.
        }
        if (json == _lastWrittenJson)
            return;

        if (TryRead(out var settings, out json, out string? error))
        {
            _current = settings;
            _lastWrittenJson = json;
            LoadError = null;
            Log.Info("settings.json changed on disk, applied");
            Changed?.Invoke(settings, true);
        }
        else
        {
            LoadError = error;
            _lastWrittenJson = json;
            LoadFailed?.Invoke(error ?? "");
        }
    }

    static AppSettings Normalized(AppSettings settings)
    {
        settings.Normalize();
        return settings;
    }

    public void Dispose()
    {
        _debounce.Dispose();
        _watcher?.Dispose();
    }
}
