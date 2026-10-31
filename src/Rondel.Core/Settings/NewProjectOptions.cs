namespace Rondel.Core.Settings;

/// <summary>
/// How a new project starts. Saved in settings.json as the choice made last time, so the New Project card
/// opens with it; editing it there sets the defaults.
/// </summary>
public sealed class NewProjectOptions
{
    /// <summary>Ask for a first prompt and start the agent with it.</summary>
    public bool Prompt { get; set; }

    /// <summary>Run "git init" in the new folder before the agent starts.</summary>
    public bool Git { get; set; }

    /// <summary>Start the agent in plan mode (when the agent has one).</summary>
    public bool Plan { get; set; }

    /// <summary>Templates copied into the new folder, by folder name; a later one overwrites files of an earlier one.</summary>
    public List<string> Templates { get; set; } = [];

    public NewProjectOptions Clone() => new() { Prompt = Prompt, Git = Git, Plan = Plan, Templates = [.. Templates] };
}

/// <summary>
/// Which options the New Project card offers. An option that is turned off is neither shown nor applied,
/// whatever was chosen last time; with all of them off the card asks only for the folder name.
/// </summary>
public sealed class NewProjectCardOptions
{
    public bool Prompt { get; set; } = true;

    public bool Git { get; set; } = true;

    public bool Plan { get; set; } = true;

    public bool Templates { get; set; } = true;
}
