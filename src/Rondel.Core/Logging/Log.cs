using System.Globalization;
using System.Text;
using System.Threading.Channels;

namespace Rondel.Core.Logging;

/// <summary>
/// Minimal rolling file logger. Callers only enqueue a line (safe from the mouse hook thread);
/// a background task does the file I/O. One file per day, rolled at 5 MB, 20 files kept.
/// </summary>
public static class Log
{
    const long MaxFileBytes = 5 * 1024 * 1024;
    const int MaxFiles = 20;

    static readonly Channel<string> s_lines = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true });

    static Task? s_writer;

    public static string Directory { get; } = Path.Combine(AppInfo.LocalDirectory, "logs");

    /// <summary>Debug mode: per-trigger UI Automation chain, detected context and resolved folder.</summary>
    public static bool DebugEnabled { get; set; }

    public static void Start()
    {
        System.IO.Directory.CreateDirectory(Directory);
        DeleteOldFiles();
        s_writer = Task.Run(WriteLoopAsync);
    }

    public static void Stop()
    {
        s_lines.Writer.TryComplete();
        s_writer?.Wait(TimeSpan.FromSeconds(2));
    }

    public static void Info(string message) => Write("INF", message);

    public static void Warn(string message, Exception? ex = null) => Write("WRN", ex is null ? message : $"{message}: {ex}");

    public static void Error(string message, Exception? ex = null) => Write("ERR", ex is null ? message : $"{message}: {ex}");

    public static void Debug(string message)
    {
        if (DebugEnabled)
            Write("DBG", message);
    }

    static void Write(string level, string message)
    {
        string line = string.Create(CultureInfo.InvariantCulture,
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} [{Environment.CurrentManagedThreadId,2}] {message}");
        s_lines.Writer.TryWrite(line);
    }

    static async Task WriteLoopAsync()
    {
        StreamWriter? writer = null;
        DateTime day = default;
        int part = 0;
        try
        {
            while (await s_lines.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                try
                {
                    if (writer is null || DateTime.Today != day || writer.BaseStream.Length > MaxFileBytes)
                    {
                        part = DateTime.Today != day ? 0 : part + 1;
                        day = DateTime.Today;
                        writer?.Dispose();
                        writer = OpenWriter(day, ref part);
                    }
                    while (s_lines.Reader.TryRead(out string? line))
                        writer.WriteLine(line);
                    writer.Flush();
                }
                catch (IOException)
                {
                    // Logging must never take the app down; try again with a fresh file next time.
                    writer?.Dispose();
                    writer = null;
                }
            }
        }
        finally
        {
            writer?.Dispose();
        }
    }

    static StreamWriter OpenWriter(DateTime day, ref int part)
    {
        while (true)
        {
            string path = Path.Combine(Directory,
                part == 0 ? $"rondel-{day:yyyyMMdd}.log" : $"rondel-{day:yyyyMMdd}-{part}.log");
            var info = new FileInfo(path);
            if (info.Exists && info.Length > MaxFileBytes)
            {
                part++;
                continue;
            }
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            return new StreamWriter(stream, new UTF8Encoding(false));
        }
    }

    static void DeleteOldFiles()
    {
        try
        {
            var files = new DirectoryInfo(Directory).GetFiles("rondel-*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(MaxFiles);
            foreach (var file in files)
                file.Delete();
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
