using Rondel.Core.Launch;
using Rondel.Core.Settings;

namespace Rondel.Core.Platform;

/// <summary>Starts a <see cref="LaunchPlan"/> in a terminal window.</summary>
public interface ITerminalLauncher
{
    /// <exception cref="LaunchException">The plan cannot be started (the message is localized).</exception>
    void Launch(LaunchPlan plan, AppSettings settings);
}

public sealed class LaunchException(string message) : Exception(message);
