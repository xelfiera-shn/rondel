using System.Text.Json;

namespace Rondel.Core.Agents.ClaudeCode;

/// <summary>What one line of an answer's output says (see <see cref="ClaudeCodeAnswerStream"/>).</summary>
public abstract record AnswerEvent
{
    private AnswerEvent()
    {
    }

    /// <summary>The session the answer belongs to; it can be resumed by this id.</summary>
    public sealed record Started(string SessionId) : AnswerEvent;

    /// <summary>A piece of the answer, to append to what came before.</summary>
    public sealed record Text(string Piece) : AnswerEvent;

    /// <summary>A new message begins: its text is a new paragraph after the earlier one's.</summary>
    public sealed record NewMessage : AnswerEvent;

    /// <summary>Claude uses a tool (WebSearch, Read, …) before it answers on.</summary>
    public sealed record Tool(string Name) : AnswerEvent;

    /// <summary>A whole message's text, for output without partial messages.</summary>
    public sealed record Message(string Body) : AnswerEvent;

    /// <summary>The answer is complete, or failed with <paramref name="Error"/>.</summary>
    public sealed record Finished(string? Result, string? Error) : AnswerEvent;
}

/// <summary>
/// Reads Claude Code's print-mode output (-p --output-format stream-json --verbose --include-partial-messages), one JSON
/// event a line (https://code.claude.com/docs/en/headless). Lines of other kinds are left out.
/// </summary>
public static class ClaudeCodeAnswerStream
{
    /// <returns>What the line says, or null when it is nothing the card shows (or not an event at all).</returns>
    public static AnswerEvent? Read(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || !line.TrimStart().StartsWith('{'))
            return null;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            return Text(root, "type") switch
            {
                "system" when Text(root, "subtype") == "init" && Text(root, "session_id") is { Length: > 0 } session => new AnswerEvent.Started(session),
                "stream_event" when root.TryGetProperty("event", out var e) && e.ValueKind == JsonValueKind.Object => Streamed(e),
                "assistant" => Whole(root),
                "result" => Result(root),
                _ => null,
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    static AnswerEvent? Streamed(JsonElement e)
    {
        switch (Text(e, "type"))
        {
            case "message_start":
                return new AnswerEvent.NewMessage();
            case "content_block_delta" when e.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object
                && Text(delta, "type") == "text_delta" && Text(delta, "text") is { Length: > 0 } piece:
                return new AnswerEvent.Text(piece);
            case "content_block_start" when e.TryGetProperty("content_block", out var block) && block.ValueKind == JsonValueKind.Object
                && Text(block, "type") is "tool_use" or "server_tool_use" && Text(block, "name") is { Length: > 0 } tool:
                return new AnswerEvent.Tool(tool);
            default:
                return null;
        }
    }

    /// <summary>An assistant message as a whole: its text blocks joined.</summary>
    static AnswerEvent? Whole(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return null;
        string text = string.Concat(content.EnumerateArray()
            .Where(block => block.ValueKind == JsonValueKind.Object && Text(block, "type") == "text")
            .Select(block => Text(block, "text")));
        return text.Length > 0 ? new AnswerEvent.Message(text) : null;
    }

    static AnswerEvent Result(JsonElement root)
    {
        bool failed = (root.TryGetProperty("is_error", out var error) && error.ValueKind == JsonValueKind.True)
            || (Text(root, "subtype") is { } subtype && subtype != "success");
        string? result = Text(root, "result");
        return new AnswerEvent.Finished(failed ? null : result, failed ? result ?? Text(root, "subtype") ?? "error" : null);
    }

    static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
