using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Rondel.Core.Agents;
using Rondel.Core.Agents.ClaudeCode;
using Rondel.Core.Context;
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
/// --render-menu out.png [dark|light] [en|tr] [idle] [edge] [FOLDER]   draw the Explorer menu for FOLDER (default: current folder)
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
    /// Draws the Explorer menu for a folder with its real session hints and recent sessions: the first
    /// expandable slice hovered and opened, or nothing hovered with "idle".
    /// </summary>
    static void RenderMenu(string output, string[] options)
    {
        bool dark = !options.Contains("light", StringComparer.OrdinalIgnoreCase);
        Loc.SetLanguage(options.Contains("tr", StringComparer.OrdinalIgnoreCase) ? "tr" : "en");
        string folder = options.FirstOrDefault(o => o.Contains('\\') || o.Contains('/')) ?? Environment.CurrentDirectory;
        var settings = new AppSettings();
        settings.Normalize();
        var context = new TriggerContext(ContextKind.ExplorerEmpty, folder, Path.GetFileName(folder), "render-menu");
        var model = MenuBuilder.Build(context, settings);

        var hints = new List<(string?, bool)>();
        var children = new List<IReadOnlyList<MenuItemModel>>();
        foreach (var item in model.Items)
        {
            var store = SessionStores.Get(item.Agent.Sessions);
            hints.Add(SessionHints.Describe(item.Action.Hint, store?.Lookup(folder)));
            children.Add(store is not null && MenuBuilder.CanExpand(item)
                ? MenuBuilder.SessionItems(item, store.Recent(folder, MenuBuilder.MaxSessionItems))
                : []);
        }
        int hover = options.Contains("idle", StringComparer.OrdinalIgnoreCase) ? -1 : children.FindIndex(c => c.Count > 0);

        var window = new RadialMenuWindow();
        var background = dark ? Color.FromRgb(0x20, 0x24, 0x2C) : Color.FromRgb(0xD9, 0xDE, 0xE6);
        bool edge = options.Contains("edge", StringComparer.OrdinalIgnoreCase);
        var bitmap = window.RenderPreview(model, new ThemeColors(dark), hints, children, hover, hover >= 0 ? 0 : -1, background, edge);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(output);
        encoder.Save(file);
        window.Close();
    }

    static void Write(string path, string text) => File.WriteAllText(path, text, new UTF8Encoding(true));
}
