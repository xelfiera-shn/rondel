using Rondel.Core.Agents.ClaudeCode;

namespace Rondel.Core.Sessions;

/// <summary>Session store implementations, by the id an agent profile names in its "Sessions" field.</summary>
public static class SessionStores
{
    static readonly ClaudeCodeSessionStore ClaudeCode = new();

    public static ISessionStore? Get(string? id) =>
        string.Equals(id, ClaudeCodeSessionStore.Id, StringComparison.OrdinalIgnoreCase) ? ClaudeCode : null;
}
