namespace Rondel.Core.Platform;

/// <summary>Starting Rondel together with the user's session.</summary>
public interface IAutostart
{
    bool IsEnabled { get; }

    void Set(bool enabled);
}
