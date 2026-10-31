using System.Globalization;
using System.Text.Json.Serialization;
using Rondel.Core.Agents;
using Rondel.Core.Sessions;

namespace Rondel.Core.Menus;

/// <summary>What the menu asks for before running the action.</summary>
public enum ActionInput { None, FolderName, FolderNameAndPrompt }

/// <summary>Which folder an action works on.</summary>
public enum ActionSource
{
    /// <summary>The folder under the cursor (or the projects folder on the desktop).</summary>
    Context,

    /// <summary>
    /// The slice opens into the folders worked in most recently; choosing one runs the action there
    /// (with its latest session as "{id}").
    /// </summary>
    RecentProjects,
}

/// <summary>
/// One menu action. Menus reference actions by <see cref="Id"/>, so adding an action is a
/// settings.json edit, not a code change.
/// </summary>
public sealed class ActionDefinition
{
    public string Id { get; set; } = "";

    /// <summary>"@Key" = built-in translated text, anything else is shown as written.</summary>
    public string Label { get; set; } = "";

    /// <summary>Segoe Fluent Icons / Segoe MDL2 Assets code point in hex, e.g. "E8F4".</summary>
    public string Icon { get; set; } = "";

    public ActionInput Input { get; set; } = ActionInput.None;

    /// <summary>What the agent is asked to do.</summary>
    public AgentCapability Capability { get; set; } = AgentCapability.Open;

    /// <summary>Id of the agent to start; null or empty = the default agent.</summary>
    public string? Agent { get; set; }

    /// <summary>Arguments placed before the capability's own arguments, e.g. ["--permission-mode", "plan"].</summary>
    public List<string> ExtraArgs { get; set; } = [];

    public SessionHint Hint { get; set; } = SessionHint.None;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ActionSource Source { get; set; } = ActionSource.Context;

    public char Glyph =>
        int.TryParse(Icon, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code) && code is > 0 and <= 0xFFFF
            ? (char)code
            : ''; // CommandPrompt
}
