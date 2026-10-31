using Rondel.Core.Settings;

namespace Rondel.Core.Flow;

/// <summary>What the New Project card offers and what happens with the answer.</summary>
/// <param name="ParentDir">Folder the new project folder is created in.</param>
/// <param name="AgentName">Shown in the prompt placeholder ("What should Claude Code do?").</param>
/// <param name="Templates">Template names, in the order they are offered and copied.</param>
/// <param name="Initial">The options the card opens with (the last choice).</param>
/// <param name="CanPrompt">The agent can start with a prompt.</param>
/// <param name="CanPlan">The agent has a plan mode.</param>
/// <param name="ValidateName">Localized error for a folder name, or null; called as the user types.</param>
/// <param name="Submit">Creates and launches; returns a localized error to show inline, or null when done.</param>
public sealed record NewProjectSpec(string Title, char Glyph, string ParentDir, string AgentName,
    IReadOnlyList<string> Templates, NewProjectOptions Initial, bool CanPrompt, bool CanPlan,
    Func<string, string?> ValidateName, Func<NewProjectRequest, string?> Submit)
{
    /// <summary>The card offers to create a git repository.</summary>
    public bool CanGit { get; init; } = true;

    /// <summary>The card has a templates section (also when there are no templates yet, to say where they go).</summary>
    public bool OffersTemplates { get; init; } = true;

    /// <summary>Where templates are added (one folder each); the card offers to open it.</summary>
    public string? TemplatesDirectory { get; init; }
}

/// <param name="Prompt">The first prompt, when <see cref="NewProjectOptions.Prompt"/> is on.</param>
public sealed record NewProjectRequest(string Name, NewProjectOptions Options, string? Prompt);
