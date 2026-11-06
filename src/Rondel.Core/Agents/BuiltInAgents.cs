using Rondel.Core.Agents.ClaudeCode;

namespace Rondel.Core.Agents;

/// <summary>Agent profiles Rondel ships with; they are added to settings.json when missing.</summary>
public static class BuiltInAgents
{
    public const string ClaudeCodeId = "claude-code";

    public static List<AgentProfile> Create() => [ClaudeCode()];

    public static AgentProfile? Find(string id) =>
        Create().FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));

    public static AgentProfile ClaudeCode() => new()
    {
        Id = ClaudeCodeId,
        Name = "Claude Code",
        Command = "claude",
        Capabilities = new(StringComparer.OrdinalIgnoreCase)
        {
            [nameof(AgentCapability.Open)] = [],
            [nameof(AgentCapability.Continue)] = ["--continue"],
            [nameof(AgentCapability.Resume)] = ["--resume"],
            [nameof(AgentCapability.ResumeById)] = ["--resume", "{id}"],
            [nameof(AgentCapability.Prompt)] = ["{prompt}"],
            [nameof(AgentCapability.Plan)] = ["--permission-mode", "plan"],
        },
        Sessions = ClaudeCodeSessionStore.Id,
        FileMention = "@{file}",
        // Hooks from this file run next to the user's own ones (https://code.claude.com/docs/en/hooks).
        ActivityArgs = ["--settings", "{hooks}"],
        // No transcript and no prompt history (https://code.claude.com/docs/en/env-vars).
        EphemeralEnvironment = new() { ["CLAUDE_CODE_SKIP_PROMPT_HISTORY"] = "1" },
        // Print mode, streamed as it is written (https://code.claude.com/docs/en/headless). Not --bare, which ignores
        // the subscription login. On the card Claude may only read in its folder and search the web: there is no one to
        // ask for more, and print mode denies whatever would ask. --tools only offers the web tools; --allowedTools lets
        // them run. Manual mode, not dontAsk: "Continue in terminal" resumes in the mode the session ran in, and there
        // the user can answer.
        AnswerArgs =
        [
            "-p", "--output-format", "stream-json", "--verbose", "--include-partial-messages",
            "--tools", "Read,WebSearch,WebFetch", "--allowedTools", "WebSearch,WebFetch", "--permission-mode", "default",
        ],
        ModelArgs = ["--model", "{model}"],
    };
}
