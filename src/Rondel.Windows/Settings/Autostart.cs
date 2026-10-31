using Microsoft.Win32;
using Rondel.Core;
using Rondel.Core.Logging;
using Rondel.Core.Platform;

namespace Rondel.Windows.Settings;

/// <summary>"Start with Windows" via HKCU\...\Run - the only registry value the app writes.</summary>
internal sealed class Autostart : IAutostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    static string ExePath => Environment.ProcessPath ?? "";

    public bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(AppInfo.Name) is string value
                    && value.Contains(ExePath, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                return false;
            }
        }
    }

    public void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
                key.SetValue(AppInfo.Name, $"\"{ExePath}\"");
            else
                key.DeleteValue(AppInfo.Name, throwOnMissingValue: false);
            Log.Info($"Start with Windows {(enabled ? "enabled" : "disabled")}");
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Error("Changing the Run registry value failed", ex);
        }
    }
}
