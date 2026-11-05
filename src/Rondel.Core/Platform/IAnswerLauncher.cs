using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Rondel.Core.Launch;

namespace Rondel.Core.Platform;

/// <summary>Starts an agent without a terminal, for an answer Rondel shows on its own card (a Quick Conversation's first one).</summary>
public interface IAnswerLauncher
{
    /// <summary>
    /// Starts the command of <paramref name="plan"/> in its folder, with its environment and arguments, without a window,
    /// its standard input, output and error redirected as UTF-8.
    /// </summary>
    /// <returns>The process, or null when the command cannot be started that way (an alias of the user's shell, say):
    /// the caller then starts it in a terminal as before.</returns>
    IAnswerProcess? Start(LaunchPlan plan);
}

/// <summary>An agent answering one question: the question goes in, the answer comes out.</summary>
public interface IAnswerProcess
{
    TextWriter Input { get; }

    TextReader Output { get; }

    /// <summary>The end of what it wrote to its error output, for the message when it fails.</summary>
    string Errors { get; }

    /// <returns>The exit code, or null when it had not exited within <paramref name="timeout"/>.</returns>
    int? WaitForExit(TimeSpan timeout);

    /// <summary>Ends it and whatever it started.</summary>
    void Kill();
}

/// <summary>An <see cref="IAnswerProcess"/> that is a <see cref="System.Diagnostics.Process"/>, started with its streams redirected.</summary>
public sealed class AnswerProcess : IAnswerProcess
{
    const int MaxErrorChars = 2000;

    readonly Process _process;
    readonly StringBuilder _errors = new();

    public AnswerProcess(Process process)
    {
        _process = process;
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;
            lock (_errors)
            {
                _errors.AppendLine(e.Data);
                if (_errors.Length > MaxErrorChars)
                    _errors.Remove(0, _errors.Length - MaxErrorChars);
            }
        };
        process.BeginErrorReadLine();
    }

    /// <summary>The start info for <paramref name="plan"/> run as <paramref name="program"/> with <paramref name="arguments"/>.</summary>
    public static ProcessStartInfo StartInfo(LaunchPlan plan, string program, IEnumerable<string> arguments)
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var start = new ProcessStartInfo(program)
        {
            WorkingDirectory = plan.Directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = utf8,
            StandardOutputEncoding = utf8,
            StandardErrorEncoding = utf8,
        };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        foreach (var (name, value) in plan.Environment)
            start.Environment[name] = value;
        return start;
    }

    public TextWriter Input => _process.StandardInput;

    public TextReader Output => _process.StandardOutput;

    public string Errors
    {
        get
        {
            lock (_errors)
                return _errors.ToString();
        }
    }

    public int? WaitForExit(TimeSpan timeout) => _process.WaitForExit(timeout) ? _process.ExitCode : null;

    public void Kill()
    {
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already gone.
        }
    }
}
