using System.Globalization;
using System.Text;
using Rondel.Core;
using Rondel.Core.Logging;
using Rondel.Core.Menus;
using Rondel.Core.Settings;
using Rondel.MacOS.Detection;
using Rondel.MacOS.Interop;

namespace Rondel.MacOS;

/// <summary>
/// Command-line diagnostics. They run instead of the menu bar app, never touch a running instance and write their
/// result to a file. Start them through open(1), so that macOS checks Rondel's own permissions, not the terminal's:
/// <c>open -n -W Rondel.app --args --probe 800 500 /tmp/probe.txt</c>.
/// </summary>
internal static class DevTools
{
    public static bool TryRun(string[] args)
    {
        switch (args)
        {
            case ["--probe", var x, var y, var output]:
                Probe(new CGPoint(double.Parse(x, CultureInfo.InvariantCulture), double.Parse(y, CultureInfo.InvariantCulture)), output);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Classifies a screen point (points, top-left origin) exactly like a gesture would, with the full trace.</summary>
    static void Probe(CGPoint point, string output)
    {
        Log.DebugEnabled = true; // collects the trace
        // The app gets AppKit through Avalonia; without it NSRunningApplication and NSWorkspace are missing.
        System.Runtime.InteropServices.NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
        var settings = ReadSettings();
        var report = new StringBuilder();
        report.AppendLine($"{AppInfo.Name} {AppInfo.Version} probe at {point}, macOS {Environment.OSVersion.Version}");
        report.AppendLine($"Accessibility permission: {Native.AXIsProcessTrusted()}");
        int finder = Native.AutomationPermission(Workspace.FinderId, ask: false);
        report.AppendLine($"Automation permission for Finder: {(finder == Native.AutomationAllowed ? "granted" : finder.ToString(CultureInfo.InvariantCulture))}");

        var context = new FinderDetector(() => settings).Detect(point);
        report.AppendLine($"Context: {context.Kind}");
        report.AppendLine($"Folder: {context.TargetDir}");
        if (context.FilePath is not null)
            report.AppendLine($"File: {context.FilePath}");
        report.AppendLine($"Name: {context.DisplayName}");
        report.AppendLine($"Reason: {context.Reason}");
        if (context.Kind != Core.Context.ContextKind.None)
            report.AppendLine($"Menu: {string.Join(", ", MenuBuilder.Build(context, settings).Items.Select(i => i.Action.Id))}");
        report.AppendLine("Trace:");
        foreach (string line in context.Trace)
            report.AppendLine("  " + line);
        File.WriteAllText(output, report.ToString());
    }

    /// <summary>The saved settings, read without watching or writing the file; the defaults when there are none.</summary>
    static AppSettings ReadSettings()
    {
        AppSettings? settings = null;
        string file = Path.Combine(AppInfo.SettingsDirectory, "settings.json");
        try
        {
            if (File.Exists(file))
                settings = SettingsJson.Deserialize(File.ReadAllText(file));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Log.Warn($"Reading {file} failed", ex);
        }
        settings ??= new AppSettings();
        settings.Normalize();
        return settings;
    }
}
