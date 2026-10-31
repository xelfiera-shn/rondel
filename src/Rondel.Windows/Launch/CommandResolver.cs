using Microsoft.Win32;

namespace Rondel.Windows.Launch;

/// <summary>
/// Finds executables the way a freshly opened shell would: PATH is re-read from the registry,
/// so tools installed after the app started (such as a freshly installed agent) are found without restarting it.
/// </summary>
internal static class CommandResolver
{
    /// <summary>Full path of <paramref name="command"/> on PATH (PATHEXT + .ps1), or null.</summary>
    public static string? Find(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;
        if (command.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            return File.Exists(command) ? Path.GetFullPath(command) : null;

        string[] extensions = Path.HasExtension(command) ? [""] : [.. PathExtensions(), ".PS1"];
        foreach (string directory in PathDirectories())
        {
            foreach (string extension in extensions)
            {
                try
                {
                    string candidate = Path.Combine(directory, command + extension);
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch (ArgumentException)
                {
                    // Malformed PATH entry.
                }
            }
        }
        return null;
    }

    public static string? FindWindowsTerminal() =>
        Find("wt.exe") ?? Existing(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\wt.exe"));

    public static string? FindPwsh() =>
        Find("pwsh.exe") ?? Existing(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"PowerShell\7\pwsh.exe"));

    public static string WindowsPowerShell { get; } =
        Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");

    /// <summary>Machine + user PATH as currently stored in the registry, then this process's PATH.</summary>
    public static string FreshPath()
    {
        string machine = ReadEnvironment(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment");
        string user = ReadEnvironment(Registry.CurrentUser, "Environment");
        string process = Environment.GetEnvironmentVariable("PATH") ?? "";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parts = $"{machine};{user};{process}"
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Environment.ExpandEnvironmentVariables)
            .Where(seen.Add);
        return string.Join(';', parts);
    }

    static IEnumerable<string> PathDirectories() =>
        FreshPath().Split(';', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim('"'));

    static IEnumerable<string> PathExtensions() =>
        (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.ToLowerInvariant()); // only affects how the found path is displayed

    static string ReadEnvironment(RegistryKey hive, string subKey)
    {
        try
        {
            using var key = hive.OpenSubKey(subKey);
            // Keep %VARS% unexpanded here; they are expanded against the current environment above.
            return key?.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return "";
        }
    }

    static string? Existing(string path) => File.Exists(path) ? path : null;
}
