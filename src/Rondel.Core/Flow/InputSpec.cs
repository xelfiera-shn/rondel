using Rondel.Core.Prompts;

namespace Rondel.Core.Flow;

/// <summary>What the inline input card asks for and what happens with the answer.</summary>
/// <param name="Glyph">The chosen item's icon, shown on the card.</param>
/// <param name="ParentDir">Folder the new project folder is created in, or the agent starts in when no name is asked for.</param>
/// <param name="AgentName">Shown in the prompt placeholder ("What should Claude Code do?").</param>
/// <param name="AskPrompt">After the name, ask for a multi-line prompt.</param>
/// <param name="ValidateName">Localized error for a folder name, or null; called as the user types.</param>
/// <param name="Submit">Creates and launches; returns a localized error to show inline, or null when done.</param>
public sealed record InputSpec(string Title, char Glyph, string ParentDir, string AgentName, bool AskPrompt,
    Func<string, string?> ValidateName, Func<string, string?, string?> Submit)
{
    /// <summary>Ask for a folder name first. Without it, only the prompt is asked for, and the name passed to <see cref="Submit"/> is empty.</summary>
    public bool AskName { get; init; } = true;

    /// <summary>Shown under the title when no name is asked for: the folder, or the file the prompt is about.</summary>
    public string? Location { get; init; }

    /// <summary>A picture the prompt is about, shown on the card (Ask About a Screenshot): PNG bytes, or null.</summary>
    public byte[]? Image { get; init; }

    /// <summary>The prompt box's placeholder; "What should {agent} do?" when null.</summary>
    public string? Placeholder { get; init; }

    /// <summary>Where the saved prompts the card offers are read from; null when saved prompts are turned off.</summary>
    public SavedPrompts? SavedPrompts { get; init; }

    /// <summary>The saved prompts the card offers; picking one fills the prompt box with it, to edit before starting.</summary>
    public IReadOnlyList<string> PromptNames { get; init; } = [];
}
