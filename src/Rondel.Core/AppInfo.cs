namespace Rondel.Core;

public static class AppInfo
{
    /// <summary>Product name; also used for folder names, the single-instance mutex and the autostart entry.</summary>
    public const string Name = "Rondel";

    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>
    /// Folder of settings.json: %APPDATA%\Rondel on Windows, ~/Library/Application Support/Rondel on macOS,
    /// ~/.config/Rondel on Linux.
    /// </summary>
    public static string SettingsDirectory { get; } = OperatingSystem.IsMacOS()
        ? Path.Combine(Home, "Library", "Application Support", Name)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Name);

    /// <summary>
    /// Log folder: %LOCALAPPDATA%\Rondel\logs on Windows, ~/Library/Logs/Rondel on macOS,
    /// ~/.local/share/Rondel/logs on Linux.
    /// </summary>
    public static string LogDirectory { get; } = OperatingSystem.IsMacOS()
        ? Path.Combine(Home, "Library", "Logs", Name)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name, "logs");

    static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}
