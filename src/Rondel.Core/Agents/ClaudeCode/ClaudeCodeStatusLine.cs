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
        string? own = Command(user);
        line["type"] = "command";
        line["command"] = own is null ? tap : $"{tap} {StatusLineTap.PassArgument} | {own}";
        return line;
    }

    /// <summary>A status line's command, trimmed; null when it has none.</summary>
    internal static string? Command(JsonObject? line) =>
        line?["command"] is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;

    /// <summary>
    /// Whether <paramref name="command"/> is one statement, so that a pipe in front of it hands Claude Code's input to all
    /// of it. Not with a <c>;</c>, <c>&amp;&amp;</c>, <c>||</c>, a line break or a command sent to the background (<c>&amp;</c>)
    /// outside quotes and brackets: the pipe would reach only the first part, and the rest would show nothing. Brackets
    /// around it would not help, since PowerShell takes no group after a pipe. A leading <c>&amp;</c> is PowerShell's call
    /// operator, and <c>2&gt;&amp;1</c> a redirection. Unsure is no: the user's status line then stays as it is.
    /// </summary>
    public static bool IsOneStatement(string command)
    {
        string text = command.Trim();
        int depth = 0;
        char quote = '\0';
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quote != '\0')
            {
                if (c == '\\' && quote == '"')
                    i++; // an escaped character, such as \"
                else if (c == quote)
                    quote = '\0';
                continue;
            }
            char before = i > 0 ? text[i - 1] : '\0', after = i + 1 < text.Length ? text[i + 1] : '\0';
            switch (c)
            {
                case '\'' or '"':
                    quote = c;
                    break;
                case '\\':
                    i++;
                    break;
                case '(' or '{':
                    depth++;
                    break;
                case ')' or '}':
                    depth--;
                    break;
                case ';' or '\n' or '\r' when depth <= 0:
                    return false;
                case '|' when depth <= 0 && after == '|':
                    return false;
                case '&' when depth <= 0 && i > 0 && before is not ('>' or '<' or '|') && after != '>':
                    return false;
            }
        }
        return true;
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
