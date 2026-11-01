namespace Rondel.Core.Settings;

/// <summary>Looking for newer releases of Rondel.</summary>
public sealed class UpdatesOptions
{
    /// <summary>
    /// Look for a newer release once a day, and when the connection comes back; Rondel asks before it updates.
    /// "Check now" in Settings works either way.
    /// </summary>
    public bool Check { get; set; } = true;
}
