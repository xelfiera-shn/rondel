using System.Text.Json;

namespace Rondel.Core.Activity;

/// <summary>
/// One event an agent's hook reported, reduced to what following it needs. Claude Code sends these as JSON
/// (https://code.claude.com/docs/en/hooks); other fields, and fields of an unexpected type, are ignored.
/// </summary>
/// <param name="Name">The event, e.g. "Stop" or "PermissionRequest".</param>
public sealed record HookEvent(string Name)
{
    public string? SessionId { get; init; }

    public string? Cwd { get; init; }

    public string? ToolName { get; init; }

    /// <summary>For Notification: permission_prompt, idle_prompt, elicitation_dialog, …</summary>
    public string? NotificationType { get; init; }

    /// <summary>A notification's text, or an error's.</summary>
    public string? Message { get; init; }

    /// <summary>For Stop: the agent's reply.</summary>
    public string? LastAssistantMessage { get; init; }

    /// <summary>For SessionEnd: clear, resume, logout, prompt_input_exit, other.</summary>
    public string? Reason { get; init; }

    /// <summary>For the AskUserQuestion tool: its first question.</summary>
    public string? Question { get; init; }

    /// <returns>The event, or null when <paramref name="json"/> is not a hook event.</returns>
    public static HookEvent? Parse(ReadOnlyMemory<byte> json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Text(root, "hook_event_name") is not { Length: > 0 } name)
                return null;
            return new HookEvent(name)
            {
                SessionId = Text(root, "session_id"),
                Cwd = Text(root, "cwd"),
                ToolName = Text(root, "tool_name"),
                NotificationType = Text(root, "notification_type"),
                Message = Text(root, "message") ?? Text(root, "error"),
                LastAssistantMessage = Text(root, "last_assistant_message"),
                Reason = Text(root, "reason"),
                Question = root.TryGetProperty("tool_input", out var input) ? FirstQuestion(input) : null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>AskUserQuestion's input: { "questions": [ { "question": "…", … } ] }.</summary>
    static string? FirstQuestion(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("questions", out var questions)
            || questions.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var question in questions.EnumerateArray())
        {
            if (question.ValueKind == JsonValueKind.Object && Text(question, "question") is { Length: > 0 } text)
                return text;
        }
        return null;
    }
}
