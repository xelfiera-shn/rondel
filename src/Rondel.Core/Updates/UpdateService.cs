using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json.Nodes;
using Rondel.Core.Logging;

namespace Rondel.Core.Updates;

public enum UpdateStatus
{
    /// <summary>No check yet since Rondel started.</summary>
    NotChecked,

    Checking,

    UpToDate,

    /// <summary>A newer release is out (<see cref="UpdateService.Latest"/>).</summary>
    Available,

    /// <summary>The last check could not reach GitHub; the next one tries again.</summary>
    Failed,

    /// <summary>A local or CI test build (0.0.0), which never looks for updates.</summary>
    Development,
}

/// <summary>
/// Looks for a newer release of Rondel: about a minute after it starts, then once a day, and soon after the
/// connection comes back when a check is due. It asks GitHub for the latest release and sends nothing about the
/// user. Asking the user and updating are the platform's part (<see cref="Found"/>).
/// </summary>
public sealed class UpdateService : IDisposable
{
    public static readonly Uri LatestRelease = new("https://api.github.com/repos/xelfiera-shn/rondel/releases/latest");

    static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(1);
    static readonly TimeSpan Tick = TimeSpan.FromHours(1);
    static readonly TimeSpan AfterConnection = TimeSpan.FromSeconds(10);
    static readonly TimeSpan CheckEvery = TimeSpan.FromDays(1);
    static readonly TimeSpan RemindAfter = TimeSpan.FromDays(1);
    static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    readonly HttpClient _http;
    readonly Func<bool> _enabled;
    readonly string _stateFile;
    readonly Uri _feed;
    readonly TimeProvider _time;
    readonly Lock _gate = new();
    ITimer? _timer;
    UpdateState _state;
    Task<UpdateStatus>? _check;

    /// <param name="enabled">Whether automatic checks are on (Settings); a check asked for by the user always runs.</param>
    /// <param name="stateFile">Where the last check and the skipped version are kept.</param>
    /// <param name="feed">GitHub's "latest release" address; another one only in tests.</param>
    public UpdateService(HttpClient http, AppVersion current, Func<bool> enabled, string stateFile, Uri? feed = null, TimeProvider? time = null)
    {
        _http = http;
        Current = current;
        _enabled = enabled;
        _stateFile = stateFile;
        _feed = feed ?? LatestRelease;
        _time = time ?? TimeProvider.System;
        _state = UpdateState.Read(stateFile);
        // Until the first check, what the last one found: up to date when nothing newer than this build was out.
        Status = current.IsDevelopment ? UpdateStatus.Development
            : _state.LastCheckUtc is not null && AppVersion.TryParse(_state.Latest, out var latest) && latest <= current ? UpdateStatus.UpToDate
            : UpdateStatus.NotChecked;
    }

    public AppVersion Current { get; }

    public UpdateStatus Status { get; private set; }

    /// <summary>The newest release when it is newer than this build.</summary>
    public ReleaseInfo? Latest { get; private set; }

    /// <summary>The last check that reached GitHub.</summary>
    public DateTime? LastCheckUtc => _state.LastCheckUtc;

    /// <summary>
    /// An automatic check found a newer release to tell the user about: not one they skipped, and not one they were
    /// told about within the last day. Raised on a thread-pool thread.
    /// </summary>
    public event Action<ReleaseInfo>? Found;

    /// <summary><see cref="Status"/> changed. Raised on a thread-pool thread.</summary>
    public event Action? StatusChanged;

    public void Start()
    {
        if (Current.IsDevelopment)
            return;
        _timer = _time.CreateTimer(_ => OnTick(), null, FirstCheck, Tick);
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
    }

    /// <summary>Whether the user chose to skip this release (it is still offered by a check they ask for).</summary>
    public bool IsSkipped(ReleaseInfo release) => _state.Skipped == release.Version.ToString();

    public void Skip(ReleaseInfo release)
    {
        lock (_gate)
            _state = _state with { Skipped = release.Version.ToString() };
        Save();
        Log.Info($"Update {release.Version} skipped");
    }

    /// <summary>
    /// Asks GitHub now; a check already running is joined. An automatic check raises <see cref="Found"/>; a check the
    /// user asked for (<paramref name="manual"/>) leaves showing the release to the caller.
    /// </summary>
    public Task<UpdateStatus> CheckAsync(bool manual)
    {
        lock (_gate)
        {
            if (_check is { IsCompleted: false } running)
                return running;
            return _check = Task.Run(() => RunCheckAsync(manual));
        }
    }

    void OnNetworkChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        // The connection came back: look soon if a check is due (one may have failed while it was gone).
        if (e.IsAvailable)
            _timer?.Change(AfterConnection, Tick);
    }

    void OnTick()
    {
        if (_enabled() && IsDue)
            _ = CheckAsync(manual: false);
    }

    bool IsDue => _state.LastCheckUtc is not { } last || Now - last >= CheckEvery;

    DateTime Now => _time.GetUtcNow().UtcDateTime;

    async Task<UpdateStatus> RunCheckAsync(bool manual)
    {
        SetStatus(UpdateStatus.Checking);
        ReleaseInfo? release;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _feed);
            request.Headers.UserAgent.ParseAdd($"Rondel/{(Current.IsDevelopment ? "dev" : Current.ToString())}");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var timeout = new CancellationTokenSource(RequestTimeout);
            using var response = await _http.SendAsync(request, timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                release = null; // nothing released yet
            }
            else
            {
                response.EnsureSuccessStatusCode();
                release = ReleaseInfo.Parse(await response.Content.ReadAsByteArrayAsync(timeout.Token))
                    ?? throw new InvalidDataException("the answer is not a release");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidDataException)
        {
            Log.Info($"Update check failed: {ex.Message}");
            SetStatus(UpdateStatus.Failed);
            return UpdateStatus.Failed;
        }

        bool tell;
        lock (_gate)
        {
            _state = _state with { LastCheckUtc = Now, Latest = release?.Version.ToString() };
            Latest = release is not null && release.Version > Current ? release : null;
            tell = Latest is { } newer && !manual && !IsSkipped(newer)
                && !(_state.Told == newer.Version.ToString() && _state.ToldUtc is { } told && Now - told < RemindAfter);
            if (tell)
                _state = _state with { Told = Latest!.Version.ToString(), ToldUtc = Now };
        }
        Save();
        var status = Latest is null ? UpdateStatus.UpToDate : UpdateStatus.Available;
        Log.Info(Latest is null ? $"Update check: {Current} is the latest" : $"Update check: {Latest.Version} is available{(tell ? "" : " (not announced now)")}");
        SetStatus(status);
        if (tell)
            Found?.Invoke(Latest!);
        return status;
    }

    void SetStatus(UpdateStatus status)
    {
        Status = status;
        StatusChanged?.Invoke();
    }

    void Save()
    {
        UpdateState state;
        lock (_gate)
            state = _state;
        state.Write(_stateFile);
    }

    public void Dispose()
    {
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        _timer?.Dispose();
    }
}

/// <summary>What the update checks remember between runs (a small JSON file, not in settings.json).</summary>
/// <param name="Latest">The newest release the last check found.</param>
/// <param name="Skipped">The version the user chose to skip.</param>
/// <param name="Told">The version last announced, at <paramref name="ToldUtc"/>.</param>
sealed record UpdateState(DateTime? LastCheckUtc, string? Latest, string? Skipped, string? Told, DateTime? ToldUtc)
{
    public static UpdateState Read(string path)
    {
        try
        {
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject json)
                return new UpdateState(Date(json, "LastCheck"), Text(json, "Latest"), Text(json, "Skipped"), Text(json, "Told"), Date(json, "ToldAt"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Log.Warn($"Reading {path} failed", ex);
        }
        return new UpdateState(null, null, null, null, null);
    }

    public void Write(string path)
    {
        var json = new JsonObject
        {
            ["LastCheck"] = LastCheckUtc?.ToString("O", CultureInfo.InvariantCulture),
            ["Latest"] = Latest,
            ["Skipped"] = Skipped,
            ["Told"] = Told,
            ["ToldAt"] = ToldUtc?.ToString("O", CultureInfo.InvariantCulture),
        };
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json.ToJsonString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Writing {path} failed", ex);
        }
    }

    static string? Text(JsonObject json, string name) =>
        json[name] is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrEmpty(text) ? text : null;

    static DateTime? Date(JsonObject json, string name) =>
        DateTime.TryParse(Text(json, name), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date)
            ? date.ToUniversalTime() : null;
}
