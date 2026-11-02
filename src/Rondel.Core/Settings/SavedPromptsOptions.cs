using Rondel.Core.Prompts;

namespace Rondel.Core.Settings;

/// <summary>Saved prompts (<see cref="SavedPrompts"/>) in the Ask slices; off, Ask asks only for a prompt to type.</summary>
public sealed class SavedPromptsOptions
{
    /// <summary>The Ask slices open into the pinned prompts, and the prompt card lists every saved prompt.</summary>
    public bool Enabled { get; set; }

    /// <summary>The prompts on the Ask slices' arc, by name, in this order; at most <see cref="SavedPrompts.MaxPinned"/>.</summary>
    public List<string> Pinned { get; set; } = [];
}
