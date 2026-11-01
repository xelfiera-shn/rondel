using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Media;
using Rondel.Core;
using Rondel.Core.Agents;
using Rondel.Core.Context;
using Rondel.Core.Flow;
using Rondel.Core.Launch;
using Rondel.Core.Localization;
using Rondel.Core.Logging;
using Rondel.Core.Menus;
using Rondel.Core.Settings;
using Rondel.MacOS.Detection;
using Rondel.MacOS.Interop;
using Rondel.MacOS.Launch;
using Rondel.MacOS.UI;

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
            case ["--launch-script", var directory, var command, var promptFile, var output]:
                File.WriteAllText(output, PosixShellScript.Build(PromptPlan(directory, command, promptFile), PosixShellScript.ChooseShell(null)));
                return true;
            case ["--launch-now", var directory, var command, var promptFile]:
                new MacTerminalLauncher().Launch(PromptPlan(directory, command, promptFile), ReadSettings());
                return true;
            case ["--render-new", var output, .. var options]:
                RenderNewProject(output, options);
                return true;
            case ["--render-menu", var output, .. var options]:
                RenderMenu(output, options);
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

    /// <summary>A Prompt launch of <paramref name="command"/> in <paramref name="directory"/>, with the prompt read from a file.</summary>
    static LaunchPlan PromptPlan(string directory, string command, string promptFile)
    {
        var agent = BuiltInAgents.ClaudeCode();
        agent.Command = command;
        var action = new ActionDefinition { Capability = AgentCapability.Prompt };
        return LaunchPlanner.Create(agent, action, directory, File.ReadAllText(promptFile))!;
    }

    /// <summary>
    /// Draws the menu into a PNG without showing it, for checking the design:
    /// <c>--render-menu out.png [dark|light] [en|tr] [desktop|item] [theme=ID] [hover=N] [FOLDER|FILE]</c>. The Finder-window
    /// menu for a folder by default (the desktop menu with <c>desktop</c>, the folder's own menu with <c>item</c>, a file's
    /// menu when given a file), with its real session hints.
    /// </summary>
    static void RenderMenu(string output, string[] options)
    {
        var settings = ReadSettings();
        bool? dark = options.Contains("dark") ? true : options.Contains("light") ? false : null;
        Loc.SetLanguage(options.FirstOrDefault(o => o is "en" or "tr") ?? settings.Language);
        if (options.FirstOrDefault(o => o.StartsWith("theme=", StringComparison.Ordinal)) is { } theme)
            settings.Appearance.Theme = theme["theme=".Length..];
        int hover = options.FirstOrDefault(o => o.StartsWith("hover=", StringComparison.Ordinal)) is { } h
            ? int.Parse(h["hover=".Length..], CultureInfo.InvariantCulture) : -1;
        string path = options.LastOrDefault(o => o.StartsWith('/')) ?? settings.ResolveProjectsRoot();
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        var context = options.Contains("desktop") ? new TriggerContext(ContextKind.DesktopEmpty, settings.ResolveProjectsRoot(), name, "render")
            : File.Exists(path) ? new TriggerContext(ContextKind.FileItem, Path.GetDirectoryName(path), name, "render") { FilePath = path }
            : new TriggerContext(options.Contains("item") ? ContextKind.FolderItem : ContextKind.ExplorerEmpty, path, name, "render");

        AppBuilder.Configure<App>().UsePlatformDetect()
            .With(new FontManagerOptions { DefaultFamilyName = ".AppleSystemUIFont" })
            .SetupWithoutStarting();
        bool isDark = dark ?? Application.Current?.PlatformSettings?.GetColorValues().ThemeVariant == Avalonia.Platform.PlatformThemeVariant.Dark;
        var model = MenuBuilder.Build(context, settings);
        var hints = MenuDetails.Needed(model) ? MenuDetails.Load(model).Hints : [];
        var window = new RadialMenuWindow(MenuPalette.For(settings.Appearance, isDark));
        var bitmap = window.RenderPreview(model, hints, hover, isDark ? Color.FromRgb(0x1E, 0x1E, 0x1E) : Color.FromRgb(0xEC, 0xEC, 0xEC));
        bitmap.Save(output, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    /// <summary>
    /// Draws the New Project card with made-up templates into a PNG: <c>--render-new out.png [dark|light] [en|tr] [prompt] [empty]</c>
    /// (<c>prompt</c>: with Prompt on and a prompt typed; <c>empty</c>: without templates).
    /// </summary>
    static void RenderNewProject(string output, string[] options)
    {
        var settings = ReadSettings();
        Loc.SetLanguage(options.FirstOrDefault(o => o is "en" or "tr") ?? settings.Language);
        AppBuilder.Configure<App>().UsePlatformDetect()
            .With(new FontManagerOptions { DefaultFamilyName = ".AppleSystemUIFont" })
            .SetupWithoutStarting();
        bool isDark = options.Contains("dark") || (!options.Contains("light")
            && Application.Current?.PlatformSettings?.GetColorValues().ThemeVariant == Avalonia.Platform.PlatformThemeVariant.Dark);
        bool prompt = options.Contains("prompt");
        string parent = settings.ResolveProjectsRoot();
        var spec = new NewProjectSpec(Loc.Get("Action.NewProject"), '\uE8F4', parent, "Claude Code",
            options.Contains("empty") ? [] : ["dotnet-api", "react-app", "docs"],
            new NewProjectOptions { Prompt = prompt, Git = true, Templates = ["react-app"] },
            CanPrompt: true, CanPlan: true, ValidateName: name => MacFolderNameRules.Validate(parent, name), Submit: _ => null)
        {
            TemplatesDirectory = Path.Combine(AppInfo.SettingsDirectory, "Templates"),
        };
        var window = new RadialMenuWindow(MenuPalette.For(settings.Appearance, isDark));
        var bitmap = window.RenderNewProjectPreview(spec, "hava-durumu-api", prompt ? "Bir hava durumu API'si kur: şehir adıyla sorgulansın." : null,
            isDark ? Color.FromRgb(0x1E, 0x1E, 0x1E) : Color.FromRgb(0xEC, 0xEC, 0xEC));
        bitmap.Save(output, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
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
