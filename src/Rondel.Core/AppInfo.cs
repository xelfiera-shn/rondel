namespace Rondel.Core;

public static class AppInfo
{
    /// <summary>Product name; also used for folder names, the single-instance mutex and the autostart entry.</summary>
    public const string Name = "Rondel";

    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>%APPDATA%\Rondel on Windows (settings).</summary>
    public static string RoamingDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Name);

    /// <summary>%LOCALAPPDATA%\Rondel on Windows (logs).</summary>
    public static string LocalDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name);
}
