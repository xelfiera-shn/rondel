using System.Text;
using Rondel.Core.Agents.ClaudeCode;
using Rondel.Core.Logging;
using Rondel.Core.Platform;

namespace Rondel.Core.Flow;

public enum AnswerState
{
    /// <summary>The agent is starting, or thinking before its first word.</summary>
    Starting,

    /// <summary>The answer is being written.</summary>
    Writing,

    /// <summary>The answer is complete.</summary>
    Done,

    /// <summary>The user stopped it before it was complete.</summary>
    Stopped,

    /// <summary>The agent could not answer (<see cref="QuickAnswer.Error"/> says why).</summary>
    Failed,
}

/// <summary>
/// A Quick Conversation's first question, answered on its card without a terminal (<see cref="Agents.AgentProfile.AnswerArgs"/>).
/// The card shows <see cref="Text"/> as it grows and offers Copy, Close and Continue in terminal; nothing of it is kept
/// once the card closes, unless it continues there. Created by <see cref="MenuFlow"/>; <see cref="Changed"/> is raised on
/// the thread that created it (the UI thread).
/// </summary>
public sealed class QuickAnswer
{
    /// <summary>What is kept of the agent's error output, for the message when it fails.</summary>
    readonly SynchronizationContext? _context = SynchronizationContext.Current;
    readonly object _gate = new();
    readonly StringBuilder _text = new();
    readonly Action<QuickAnswer> _continue, _inTerminal, _closed;
    readonly bool _resumable;
    IAnswerProcess? _process;
    bool _paragraph, _messageHasText, _closedOnce;

    internal QuickAnswer(string question, bool resumable, Action<QuickAnswer> onContinue, Action<QuickAnswer> onTerminal,
        Action<QuickAnswer> onClosed)
    {
        Question = question;
        _resumable = resumable;
        _continue = onContinue;
        _inTerminal = onTerminal;
        _closed = onClosed;
    }

    /// <summary>What the user asked, as typed (without the file mentions in front of it).</summary>
    public string Question { get; }

    /// <summary>The answer so far, in Markdown.</summary>
    public string Text
    {
        get
        {
            lock (_gate)
                return _text.ToString();
        }
    }

    public AnswerState State { get; private set; }

    /// <summary>Why it failed (<see cref="AnswerState.Failed"/>), as the agent said it; null otherwise.</summary>
    public string? Error { get; private set; }

    /// <summary>The tool the agent uses right now ("WebSearch"), or null while it writes.</summary>
    public string? Tool { get; private set; }

    /// <summary>The agent's session, known once it started; "Continue in terminal" resumes it.</summary>
    public string? SessionId { get; private set; }

    /// <summary>The answer is over and can go on in a terminal.</summary>
    public bool CanContinue => _resumable && SessionId is not null && State is AnswerState.Done or AnswerState.Stopped;

    /// <summary>The agent is still at it (Esc stops it).</summary>
    public bool IsRunning => State is AnswerState.Starting or AnswerState.Writing;

    /// <summary>The card closed (or the conversation went on in a terminal); nothing more happens to it.</summary>
    public bool IsClosed { get; private set; }

    /// <summary>Something above changed; raised on the UI thread.</summary>
    public event Action? Changed;

    /// <summary>Feeds the question to the agent's process and reads its answer, off the UI thread.</summary>
    internal Task Run(IAnswerProcess process, string prompt)
    {
        _process = process;
        return Task.Run(() =>
        {
            int? exit = null;
            try
            {
                process.Input.Write(prompt);
                process.Input.Close();
                Read(process.Output);
                exit = process.WaitForExit(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
                Log.Warn("Reading the answer failed", ex);
            }
            Finish(exit, process.Errors);
        });
    }

    /// <summary>Reads the agent's output to its end; each line that says something changes the answer.</summary>
    internal void Read(TextReader output)
    {
        while (output.ReadLine() is { } line)
        {
            if (ClaudeCodeAnswerStream.Read(line) is { } e)
                Apply(e);
        }
    }

    internal void Apply(AnswerEvent e)
    {
        lock (_gate)
        {
            if (IsClosed || State is AnswerState.Stopped)
                return;
            switch (e)
            {
                case AnswerEvent.Started started:
                    SessionId = started.SessionId;
                    break;
                case AnswerEvent.NewMessage:
                    _paragraph = _text.Length > 0;
                    _messageHasText = false;
                    break;
                case AnswerEvent.Tool tool:
                    Tool = tool.Name;
                    break;
                case AnswerEvent.Text text:
                    Append(text.Piece);
                    break;
                case AnswerEvent.Message message when !_messageHasText:
                    Append(message.Body); // output without partial messages
                    break;
                case AnswerEvent.Finished { Error: { } error }:
                    State = AnswerState.Failed;
                    Error = error;
                    Tool = null;
                    break;
                case AnswerEvent.Finished finished:
                    if (_text.Length == 0 && !string.IsNullOrEmpty(finished.Result))
                        _text.Append(finished.Result);
                    State = AnswerState.Done;
                    Tool = null;
                    break;
                default:
                    return;
            }
        }
        Raise();
    }

    void Append(string piece)
    {
        if (_paragraph)
        {
            _text.Append(_text.ToString().EndsWith('\n') ? "\n" : "\n\n");
            _paragraph = false;
        }
        _text.Append(piece);
        _messageHasText = true;
        State = AnswerState.Writing;
        Tool = null;
    }

    /// <summary>The output ended: an answer that never said it was complete failed.</summary>
    void Finish(int? exitCode, string errors)
    {
        lock (_gate)
        {
            if (!IsRunning || IsClosed)
                return;
            State = AnswerState.Failed;
            errors = errors.Trim();
            Error = errors.Length > 0 ? errors : exitCode is { } code ? $"exit code {code}" : "no answer";
            Tool = null;
        }
        Log.Warn($"The answer ended without a result: {Error}");
        Raise();
    }

    /// <summary>Esc while it is written: the agent stops, what came so far stays on the card.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (!IsRunning)
                return;
            State = AnswerState.Stopped;
            Tool = null;
        }
        Kill();
        Raise();
    }

    /// <summary>The conversation goes on in a terminal, from where the answer is; the card closes.</summary>
    public void Continue()
    {
        if (!CanContinue || IsClosed)
            return;
        Kill();
        IsClosed = true;
        _closedOnce = true;
        _continue(this);
    }

    /// <summary>After a failure: a Quick Conversation in a terminal with the same question, as without the card's answer.</summary>
    public void OpenInTerminal()
    {
        if (IsClosed)
            return;
        Kill();
        IsClosed = true;
        _closedOnce = true;
        _inTerminal(this);
    }

    /// <summary>The card closes: the agent stops, and what it kept goes with the next sweep.</summary>
    public void Close()
    {
        if (_closedOnce)
            return;
        _closedOnce = true;
        IsClosed = true;
        Kill();
        _closed(this);
    }

    void Kill() => _process?.Kill();

    void Raise()
    {
        if (_context is null)
            Changed?.Invoke();
        else
            _context.Post(_ => Changed?.Invoke(), null);
    }
}
