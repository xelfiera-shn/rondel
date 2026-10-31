using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Rondel.Core;
using Rondel.Core.Agents;
using Rondel.Core.Agents.ClaudeCode;
using Rondel.Core.Context;
using Rondel.Core.Flow;
using Rondel.Core.Launch;
using Rondel.Core.Localization;
using Rondel.Core.Logging;
using Rondel.Core.Menus;
using Rondel.Core.Sessions;
using Rondel.Core.Settings;
using Rondel.Windows.Detection;
using Rondel.Windows.Interop;
using Rondel.Windows.Launch;
using Rondel.Windows.UI;

namespace Rondel.Windows;

/// <summary>
/// Command-line diagnostics. They run instead of the tray app, never touch a running instance,
/// and write their result to a file (the app has no console):
/// <code>
/// --probe X Y out.txt                        classify a screen point like a trigger would (with the UIA trace)
/// --sessions FOLDER out.txt                  session hints the menu would show for FOLDER
/// --launch-script FOLDER COMMAND PROMPT.txt out.ps1   the PowerShell script a launch would run
/// --launch-now FOLDER COMMAND PROMPT.txt     launch exactly like the menu, with COMMAND instead of the configured one
/// --export-icon out.ico                      write the app icon (used for the exe)
/// --render-menu out.png [dark|light] [en|tr] [desktop|item] [idle] [deep] [edge] [transparent] [FOLDER|FILE]   draw the Explorer menu
///                                            for FOLDER (default: current folder), its folder-icon menu with "item", or the menu of FILE
/// --render-settings out.png [en|tr] [bottom] draw the settings window (in the current Windows theme), scrolled down with "bottom"
/// --render-new out.png [dark|light] [en|tr] [prompt] [empty] [transparent]   draw the New Project card with sample templates
///                                            (prompt: with the Prompt option on; empty: with no templates)
/// </code>
/// </summary>
internal static class DevTools
{
    public static bool TryRun(string[] args)
    {
        switch (args)
        {
            case ["--probe", var x, var y, var output]:
                Probe(new POINT(int.Parse(x), int.Parse(y)), output);
                return true;

            case ["--sessions", var directory, var output]:
                var info = new ClaudeCodeSessionStore().Lookup(directory);
                Write(output, info is null
                    ? "unknown"
                    : $"count={info.Count} last={info.LastActivityUtc:u} age={(info.LastActivityUtc is { } t ? RelativeTime.Describe(t) : "-")}"
                      + $" folder={ClaudeCodeSessionStore.Encode(directory)}");
                return true;

            case ["--launch-script", var directory, var command, var promptFile, var output]:
                Write(output, PowerShellScript.Build(PromptPlan(directory, command, promptFile)));
                return true;

            case ["--launch-now", var directory, var command, var promptFile]:
                var settings = new AppSettings();
                settings.Normalize();
                new WindowsTerminalLauncher().Launch(PromptPlan(directory, command, promptFile), settings);
                return true;

            case ["--export-icon", var output]:
                ExportIcon(output);
                return true;

            case ["--render-menu", var output, .. var options]:
                RenderMenu(output, options);
                return true;

            case ["--render-settings", var output, .. var options]:
                RenderSettings(output, options);
                return true;

            case ["--render-new", var output, .. var options]:
                RenderNewProject(output, options);
                return true;

            default:
                return false;
        }
    }

    /// <summary>What the New + Prompt action would launch, with COMMAND instead of the configured command.</summary>
    static LaunchPlan PromptPlan(string directory, string command, string promptFile)
    {
        var agent = BuiltInAgents.ClaudeCode();
        agent.Command = command;
        var action = new ActionDefinition { Capability = AgentCapability.Prompt };
        return LaunchPlanner.Create(agent, action, directory, File.ReadAllText(promptFile))!;
    }

    static void Probe(POINT point, string output)
    {
        Log.DebugEnabled = true;
        var settings = new AppSettings();
        settings.Normalize();
        nint root = Native.GetAncestor(Native.WindowFromPoint(point), Native.GA_ROOT);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        TriggerContext context;
        try
        {
            context = new ShellContextDetector(() => settings).Detect(point, root);
        }
        catch (Exception ex)
        {
            context = TriggerContext.None($"exception: {ex}");
        }

        var text = new StringBuilder()
            .AppendLine($"point={point} root=0x{root:X} '{Native.GetClassName(root)}' elapsed={watch.ElapsedMilliseconds} ms")
            .AppendLine($"kind={context.Kind}")
            .AppendLine($"dir={context.TargetDir}")
            .AppendLine($"name={context.DisplayName}")
            .AppendLine($"reason={context.Reason}");
        foreach (string line in context.Trace)
            text.AppendLine("  " + line);
        Write(output, text.ToString());
    }

    /// <summary>Multi-size .ico: classic 32-bit BMP frames, plus a PNG frame for 256 px (the usual layout).</summary>
    static void ExportIcon(string output)
    {
        int[] sizes = [16, 20, 24, 32, 40, 48, 64, 256];
        var frames = sizes.Select(size => size >= 256 ? PngFrame(size) : BmpFrame(size)).ToList();

        using var file = new BinaryWriter(File.Create(output));
        file.Write((ushort)0);            // reserved
        file.Write((ushort)1);            // type: icon
        file.Write((ushort)sizes.Length);
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            file.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); // width, 0 means 256
            file.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); // height
            file.Write((byte)0);          // palette size
            file.Write((byte)0);          // reserved
            file.Write((ushort)1);        // color planes
            file.Write((ushort)32);       // bits per pixel
            file.Write(frames[i].Length);
            file.Write(offset);
            offset += frames[i].Length;
        }
        foreach (byte[] frame in frames)
            file.Write(frame);
    }

    static byte[] PngFrame(int size)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(AppIconRenderer.Render(size)));
        using var png = new MemoryStream();
        encoder.Save(png);
        return png.ToArray();
    }

    /// <summary>BITMAPINFOHEADER + bottom-up BGRA rows + an all-zero AND mask (alpha does the work).</summary>
    static byte[] BmpFrame(int size)
    {
        var bitmap = new FormatConvertedBitmap(AppIconRenderer.Render(size), System.Windows.Media.PixelFormats.Bgra32, null, 0);
        int stride = size * 4;
        byte[] pixels = new byte[stride * size];
        bitmap.CopyPixels(pixels, stride, 0);
        int maskStride = (size + 31) / 32 * 4;

        using var frame = new MemoryStream();
        using var writer = new BinaryWriter(frame);
        writer.Write(40);                 // biSize
        writer.Write(size);               // biWidth
        writer.Write(size * 2);           // biHeight: color + mask
        writer.Write((ushort)1);          // biPlanes
        writer.Write((ushort)32);         // biBitCount
        writer.Write(0);                  // biCompression = BI_RGB
        writer.Write(pixels.Length + maskStride * size);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        for (int row = size - 1; row >= 0; row--)
            writer.Write(pixels, row * stride, stride);
        writer.Write(new byte[maskStride * size]);
        writer.Flush();
        return frame.ToArray();
    }

    /// <summary>
    /// Draws the Explorer menu for a folder (or the menu of a file) with its real session hints and recent sessions:
    /// the first expandable slice hovered and opened, or nothing hovered with "idle".
    /// </summary>
    static void RenderMenu(string output, string[] options)
    {
        bool dark = !options.Contains("light", StringComparer.OrdinalIgnoreCase);
        Loc.SetLanguage(options.Contains("tr", StringComparer.OrdinalIgnoreCase) ? "tr" : "en");
        string target = options.FirstOrDefault(o => o.Contains('\\') || o.Contains('/')) ?? Environment.CurrentDirectory;
        var settings = new AppSettings();
        settings.Normalize();
        var kind = options.Contains("desktop", StringComparer.OrdinalIgnoreCase) ? ContextKind.DesktopEmpty
            : options.Contains("item", StringComparer.OrdinalIgnoreCase) ? ContextKind.FolderItem
            : ContextKind.ExplorerEmpty;
        var context = File.Exists(target)
            ? new TriggerContext(ContextKind.FileItem, Path.GetDirectoryName(target), Path.GetFileName(target), "render-menu") { FilePath = target }
            : new TriggerContext(kind, target, Path.GetFileName(target), "render-menu");
        var model = MenuBuilder.Build(context, settings);
        var details = MenuDetails.Load(model);
        var children = details.Children.ToList();

        // Hover the first slice that opens and its newest item; with "deep", also open the first item that opens further.
        var path = new List<int>();
        int hover = children.FindIndex(c => c.Count > 0);
        if (!options.Contains("idle", StringComparer.OrdinalIgnoreCase) && hover >= 0)
        {
            path.AddRange([hover, 0]);
            int deep = children[hover].ToList().FindIndex(c => c.Children.Count > 0);
            if (options.Contains("deep", StringComparer.OrdinalIgnoreCase) && deep >= 0)
                path = [hover, deep, 0];
        }

        var window = new RadialMenuWindow();
        var background = options.Contains("transparent", StringComparer.OrdinalIgnoreCase) ? Colors.Transparent
            : dark ? Color.FromRgb(0x20, 0x24, 0x2C) : Color.FromRgb(0xD9, 0xDE, 0xE6);
        bool edge = options.Contains("edge", StringComparer.OrdinalIgnoreCase);
        var bitmap = window.RenderPreview(model, new ThemeColors(dark), details.Hints, children, path, background, edge);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(output);
        encoder.Save(file);
        window.Close();
    }

    static T? FindChild<T>(System.Windows.DependencyObject parent) where T : System.Windows.DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match || FindChild<T>(child) is { } nested && (match = nested) is not null)
                return match;
        }
        return null;
    }

    /// <summary>Draws the New Project card with made-up templates, for checking its design.</summary>
    static void RenderNewProject(string output, string[] options)
    {
        bool dark = !options.Contains("light", StringComparer.OrdinalIgnoreCase);
        bool tr = options.Contains("tr", StringComparer.OrdinalIgnoreCase);
        bool prompt = options.Contains("prompt", StringComparer.OrdinalIgnoreCase);
        Loc.SetLanguage(tr ? "tr" : "en");
        string[] templates = options.Contains("empty", StringComparer.OrdinalIgnoreCase) ? []
            : tr ? ["Claude talimatları", "Python", "Veri bilimi", "Web uygulaması"]
            : ["Claude instructions", "Data science", "Python", "Web app"];
        var initial = new NewProjectOptions { Prompt = prompt, Git = true, Templates = templates.Length > 1 ? [templates[0], templates[2]] : [] };
        // A made-up location: these pictures end up in the README.
        string parent = Path.Combine("C:" + Path.DirectorySeparatorChar, "Users", "dev", "Projects");
        var spec = new NewProjectSpec(Loc.Get("Action.NewProject"), '', parent, "Claude Code", templates, initial,
            CanPrompt: true, CanPlan: true, ValidateName: _ => null, Submit: _ => null)
        { TemplatesDirectory = parent };

        var window = new RadialMenuWindow();
        var background = options.Contains("transparent", StringComparer.OrdinalIgnoreCase) ? Colors.Transparent
            : dark ? Color.FromRgb(0x20, 0x24, 0x2C) : Color.FromRgb(0xD9, 0xDE, 0xE6);
        var bitmap = window.RenderNewProjectPreview(spec, new ThemeColors(dark), background, "weather-api",
            prompt ? (tr ? "Hava durumu için küçük bir REST API kur, testleriyle birlikte." : "Set up a small REST API for the weather, with tests.") : null);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(output))
            encoder.Save(file);
        window.Close();
    }

    /// <summary>
    /// Draws the settings window with the current settings. The host is created but not started:
    /// no mouse hook, no tray icon, and a running Rondel is left alone.
    /// </summary>
    static void RenderSettings(string output, string[] options)
    {
        bool dark = SystemTheme.Read().IsDark; // the window takes its theme at construction
        // A copy of the settings: loading migrates and saves them, and a running (possibly older) Rondel reads the real file.
        var copy = Directory.CreateTempSubdirectory("rondel-render-");
        string real = Path.Combine(AppInfo.SettingsDirectory, "settings.json");
        if (File.Exists(real))
            File.Copy(real, Path.Combine(copy.FullName, "settings.json"));
        try
        {
            RenderSettingsWindow(output, options, dark, copy.FullName);
        }
        finally
        {
            copy.Delete(recursive: true);
        }
    }

    static void RenderSettingsWindow(string output, string[] options, bool dark, string settingsDirectory)
    {
        using var host = new AppHost(settingsDirectory);
        Loc.SetLanguage(options.Contains("tr", StringComparer.OrdinalIgnoreCase) ? "tr" : "en");
        var window = new SettingsWindow(host)
        {
            WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            ShowActivated = false,
        };
        window.Show();
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        if (options.Contains("bottom", StringComparer.OrdinalIgnoreCase) && FindChild<System.Windows.Controls.ScrollViewer>(window) is { } scroller)
        {
            scroller.ScrollToEnd();
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        var content = (System.Windows.FrameworkElement)window.Content;
        var size = new System.Windows.Size(content.ActualWidth, content.ActualHeight);
        var backdrop = new DrawingVisual();
        using (var dc = backdrop.RenderOpen())
            dc.DrawRectangle(new SolidColorBrush(dark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3)), null, new System.Windows.Rect(size));
        var bitmap = new RenderTargetBitmap((int)(size.Width * 2), (int)(size.Height * 2), 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(backdrop);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(output))
            encoder.Save(file);
        window.Close();
    }

    static void Write(string path, string text) => File.WriteAllText(path, text, new UTF8Encoding(true));
}
