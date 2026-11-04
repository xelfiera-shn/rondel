using Rondel.Core.Agents.ClaudeCode;
using Rondel.Core.Flow;
using Rondel.Core.Logging;
using Rondel.MacOS.Interop;

namespace Rondel.MacOS.Launch;

/// <summary>
/// Quick Conversation: what Claude Code keeps of a session there is deleted for good once it is over. Rondel does not
/// follow agents on macOS yet (C-24), so it never hears that one ended, as Windows does: it sweeps at its start, before
/// each new Quick Conversation, and from the first one on every two minutes. A session Claude Code still runs, or one
/// written to in the last minute, is kept until a later sweep.
/// </summary>
internal sealed class QuickSweeper : IDisposable
{
    static readonly TimeSpan Every = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Where Claude Code keeps its temporary folders on macOS (a session's scratchpad among them): /tmp/claude-&lt;uid&gt;,
    /// or under CLAUDE_CODE_TMPDIR. The core knows only the Windows one.
    /// </summary>
    static string TempDirectory()
    {
        string? configured = Environment.GetEnvironmentVariable("CLAUDE_CODE_TMPDIR");
        return Path.Combine(string.IsNullOrWhiteSpace(configured) ? "/tmp" : configured.Trim(), $"claude-{Native.UserId()}");
    }

    readonly string _directory;
    readonly object _lock = new();
    Timer? _timer;

    public QuickSweeper(MenuFlow flow)
    {
        _directory = flow.QuickDirectory;
        flow.QuickConversationStarting += () =>
        {
            Sweep();
            _timer ??= new Timer(_ => Sweep(), null, Every, Every);
        };
    }

    /// <summary>Sweeps what Quick Conversations left while Rondel was not running.</summary>
    public void Start() => Sweep();

    /// <summary>Deletes, off the main thread, what Quick Conversations that no longer run left behind.</summary>
    void Sweep() => Task.Run(() =>
    {
        lock (_lock)
        {
            try
            {
                new ClaudeCodeStorage(tempDirectory: TempDirectory()).Sweep(_directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Log.Warn("Sweeping the Quick Conversation folder failed", ex);
            }
        }
    });

    public void Dispose() => _timer?.Dispose();
}
