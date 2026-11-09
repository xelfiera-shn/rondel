using System.Text;
using System.Text.Json;

namespace Rondel.Core.Activity;

/// <summary>
/// usage.json: the latest usage limits any session reported. The status line of each session Rondel starts adds what
/// it sees (<see cref="StatusLineTap"/>), also while Rondel itself is not running; the agents card reads it.
/// </summary>
public sealed class UsageStore(string path)
{
    public const string FileName = "usage.json";

    /// <summary>How long a report waits for another one being written at the same moment before it gives up.</summary>
    static readonly TimeSpan LockWait = TimeSpan.FromMilliseconds(300);

    /// <summary>usage.json in <see cref="AppInfo.LocalDirectory"/>.</summary>
    public static UsageStore Default { get; } = new(System.IO.Path.Combine(AppInfo.LocalDirectory, FileName));

    public string Path => path;

    /// <summary>What is kept; null when nothing was reported yet, or the file cannot be read.</summary>
    public UsageLimits? Read()
    {
        try
        {
            // Shared for writing and replacing: a session's report must never wait for the card reading it.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Reported", out var reported)
                || !reported.TryGetDateTimeOffset(out var at))
            {
                return null;
            }
            return new UsageLimits(Window(root, "FiveHour"), Window(root, "Weekly"), at);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Adds a session's report to what is kept. Several sessions may report at once; when another report holds the file
    /// for too long, this one is dropped, as the next reply brings the figures again.
    /// </summary>
    public void Report(UsageLimits fresh)
    {
        string directory = System.IO.Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        using var gate = Lock();
        if (gate is null)
            return;
        var merged = UsageLimits.Merge(Read(), fresh);
        string temp = path + "." + Environment.ProcessId + ".tmp";
        File.WriteAllText(temp, Serialize(merged), new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    FileStream? Lock()
    {
        string file = path + ".lock";
        var until = DateTime.UtcNow + LockWait;
        while (true)
        {
            try
            {
                return new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException) when (DateTime.UtcNow < until)
            {
                Thread.Sleep(15);
            }
            catch (IOException)
            {
                return null;
            }
        }
    }

    internal static string Serialize(UsageLimits limits)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            Write(json, "FiveHour", limits.FiveHour);
            Write(json, "Weekly", limits.Weekly);
            json.WriteString("Reported", limits.Reported.ToUniversalTime());
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    static void Write(Utf8JsonWriter json, string name, UsageWindow? window)
    {
        if (window is null)
            return;
        json.WriteStartObject(name);
        json.WriteNumber("Percent", window.Percent);
        json.WriteString("ResetsAt", window.ResetsAt.ToUniversalTime());
        json.WriteEndObject();
    }

    static UsageWindow? Window(JsonElement root, string name) =>
        root.TryGetProperty(name, out var window) && window.ValueKind == JsonValueKind.Object
        && window.TryGetProperty("Percent", out var percent) && percent.ValueKind == JsonValueKind.Number
        && window.TryGetProperty("ResetsAt", out var resets) && resets.TryGetDateTimeOffset(out var at)
            ? new UsageWindow(Math.Clamp(percent.GetDouble(), 0, 100), at)
            : null;
}
