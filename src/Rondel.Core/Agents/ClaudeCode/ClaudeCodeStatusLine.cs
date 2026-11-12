using System.Text.Json;
using System.Text.Json.Nodes;
using Rondel.Core.Activity;

namespace Rondel.Core.Agents.ClaudeCode;

/// <summary>
/// Claude Code's status line (https://code.claude.com/docs/en/statusline): the user's own, and Rondel's, which goes in
/// front of it in the sessions Rondel starts (<see cref="StatusLineTap"/>).
/// </summary>
public static class ClaudeCodeStatusLine
{
    static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>
    /// The status line Claude Code would show in <paramref name="directory"/>: from the project's local settings, its
    /// settings, or the user's, the first that sets one. Null when none does, or it is not a command.
    /// </summary>
    /// <param name="configDirectory">Claude Code's folder; ~/.claude (or CLAUDE_CONFIG_DIR) by default.</param>
    public static JsonObject? UserStatusLine(string directory, string? configDirectory = null)
    {
        string config = configDirectory ?? ClaudeCodeSessionStore.DefaultConfigDirectory();
        string[] files =
        [
            Path.Combine(directory, ".claude", "settings.local.json"),
            Path.Combine(directory, ".claude", "settings.json"),
            Path.Combine(config, "settings.json"),
        ];
        foreach (string file in files)
        {
            if (TrySet(file, out var line))
                return line;
        }
        return null;
    }

    /// <summary>
    /// Rondel's status line: <paramref name="tap"/> (<see cref="StatusLineTap.Prefix"/>), with the user's status line
    /// after it in a pipe and its other options (padding, refresh interval) kept.
    /// </summary>
    public static JsonObject Compose(string tap, JsonObject? user)
    {
        var line = user?.DeepClone().AsObject() ?? [];
        string? own = user?["command"] is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;
        line["type"] = "command";
        line["command"] = own is null ? tap : $"{tap} {StatusLineTap.PassArgument} | {own}";
        return line;
    }

    /// <summary>Whether a settings file sets a "statusLine"; <paramref name="line"/> is null when it is not a command.</summary>
    static bool TrySet(string file, out JsonObject? line)
    {
        line = null;
        try
        {
            if (!File.Exists(file))
                return false;
            if (JsonNode.Parse(File.ReadAllText(file), documentOptions: Lenient) is not JsonObject settings
                || !settings.TryGetPropertyValue("statusLine", out var node))
            {
                return false;
            }
            if (node is JsonObject set && set["type"] is JsonValue type && type.TryGetValue(out string? kind) && kind == "command"
                && set["command"] is JsonValue command && command.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text))
            {
                line = set.DeepClone().AsObject();
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }
}
