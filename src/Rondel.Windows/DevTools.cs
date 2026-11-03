using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Rondel.Core;
using Rondel.Core.Activity;
using Rondel.Core.Agents;
using Rondel.Core.Agents.ClaudeCode;
using Rondel.Core.Context;
using Rondel.Core.Flow;
using Rondel.Core.Launch;
using Rondel.Core.Localization;
using Rondel.Core.Logging;
using Rondel.Core.Menus;
using Rondel.Core.Prompts;
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
/// --render-notice-icons out.png              the icons of notifications about agents, dark and light
///    (--render-menu … agents: made-up followed agents, with the Active Agents slice open)
/// --render-menu out.png [dark|light] [en|tr] [desktop|item] [idle] [deep] [edge] [transparent] [theme=ID] [accent=#RRGGBB[,#RRGGBB]]
///              [hover=N] [prompts] [FOLDER|FILE...]    draw the Explorer menu
///                                            for FOLDER (default: current folder), its folder-icon menu with "item", or the menu of FILE
///                                            (several FILEs: held in a selection of them); prompts: made-up saved prompts pinned
/// --render-settings out.png [en|tr] [bottom|scroll=DIP] [picker] [prompts]   draw the settings window (in the current Windows
///                                            theme), scrolled down; picker: with a custom accent and its color picker open;
///                                            prompts: with made-up saved prompts, scrolled to them
/// --render-ask out.png [dark|light] [en|tr] [empty] [picked] [transparent] [theme=ID] [accent=#RRGGBB[,#RRGGBB]]
///                                            draw the Ask card with made-up saved prompts (empty: none in the folder;
///                                            picked: one of them picked)
/// --render-update out.png [dark|light] [en|tr] [working|failed]   draw the update window for a made-up release
/// --render-sessions out.png [dark|light] [en|tr] [confirm] [forgood]   draw the Sessions window with made-up sessions,
///                                            some selected; confirm: with the question before deleting open
/// --render-welcome out.png [dark|light] [en|tr] [stage] [t=SECONDS] [frames=N]   draw the welcome window at a moment
///                                            of its picture (the still one by default); stage: the picture alone;
///                                            frames=N: N moments over one loop, as out-000.png, out-001.png, …
/// --render-card out.png [dark|light] [en|tr] [theme=ID] [accent=#RRGGBB[,#RRGGBB]]   draw the agents card with made-up
///                                            agents: open, collapsed, its quick settings and with no agent
/// --card-demo [dark|light] [en|tr] [desktop] [report.txt]   the live agents card with made-up agents, its options in
///                                            memory and its place in a temporary file; ends with Settings… on the card.
///                                            With report.txt it runs a short script instead and writes what it saw
/// --render-new out.png [dark|light] [en|tr] [prompt] [empty] [transparent] [theme=ID] [accent=#RRGGBB[,#RRGGBB]]
///                                            draw the New Project card with sample templates
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

            case ["--render-ask", var output, .. var options]:
                RenderAsk(output, options);
                return true;

            case ["--render-notice-icons", var output]:
                RenderNoticeIcons(output);
                return true;

            case ["--render-update", var output, .. var options]:
                RenderUpdate(output, options);
                return true;

            case ["--render-welcome", var output, .. var options]:
                RenderWelcome(output, options);
                return true;

            case ["--render-sessions", var output, .. var options]:
                RenderSessions(output, options);
                return true;

            case ["--render-card", var output, .. var options]:
                RenderCard(output, options);
                return true;

            case ["--card-demo", .. var options]:
                CardDemo(options);
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
            .AppendLine($"files={string.Join(" | ", context.Files)}")
            .AppendLine($"reason={context.Reason}");
        foreach (string line in context.Trace)
            text.AppendLine("  " + line);
        Write(output, text.ToString());
    }

    /// <summary>Multi-size .ico: classic 32-bit BMP frames, plus a PNG frame for 256 px (the usual layout).</summary>
    /// <summary>Followed agents in every state, waiting ones first (as <see cref="AgentActivity.Agents"/> orders them).</summary>
    static List<ActiveAgent> MadeUpAgents()
    {
        string projects = Path.Combine(Path.GetTempPath(), "Projects");
        var now = DateTime.UtcNow;
        ActiveAgent Agent(string name, AgentState state, int minutes) =>
            new(name, Path.Combine(projects, name), "Claude Code") { State = state, Since = now.AddMinutes(-minutes) };
        return
        [
            Agent("weather-api", AgentState.Waiting, 1) with { Tool = "Bash" },
            Agent("acme-dashboard", AgentState.Waiting, 3) with { Message = "Which database should I use?" },
            Agent("mobile-app", AgentState.Failed, 6),
            Agent("billing-service", AgentState.Ready, 4),
            Agent("docs-site", AgentState.Working, 2),
        ];
    }

    /// <summary>The notification icons at the sizes Windows shows them, on a dark and a light notification.</summary>
    static void RenderNoticeIcons(string output)
    {
        int[] sizes = [32, 48, 64];
        var kinds = Enum.GetValues<NoticeIcon>();
        int cell = 80, width = cell * kinds.Length * sizes.Length, height = cell * 2;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            foreach (var (row, background) in new[] { (0, Color.FromRgb(0x2B, 0x2B, 0x2B)), (1, Color.FromRgb(0xF9, 0xF9, 0xF9)) })
            {
                dc.DrawRectangle(new SolidColorBrush(background), null, new System.Windows.Rect(0, row * cell, width, cell));
                int column = 0;
                foreach (int size in sizes)
                {
                    foreach (var kind in kinds)
                    {
                        double x = column++ * cell + (cell - size) / 2.0, y = row * cell + (cell - size) / 2.0;
                        dc.DrawImage(NoticeIconRenderer.Render(kind, size), new System.Windows.Rect(x, y, size, size));
                    }
                }
            }
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(output);
        encoder.Save(file);
    }

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
        string[] paths = [.. options.Where(o => o.Contains('\\') || o.Contains('/'))];
        string target = paths.FirstOrDefault() ?? Environment.CurrentDirectory;
        var settings = new AppSettings();
        settings.Normalize();
        var kind = options.Contains("desktop", StringComparer.OrdinalIgnoreCase) ? ContextKind.DesktopEmpty
            : options.Contains("item", StringComparer.OrdinalIgnoreCase) ? ContextKind.FolderItem
            : ContextKind.ExplorerEmpty;
        var context = File.Exists(target)
            ? new TriggerContext(ContextKind.FileItem, Path.GetDirectoryName(target), Path.GetFileName(target), "render-menu")
            {
                FilePath = target,
                SelectedFiles = paths.Length > 1 ? paths : [],
            }
            : new TriggerContext(kind, target, Path.GetFileName(target), "render-menu");
        // "prompts": made-up saved prompts, three of them pinned to the Ask slices.
        using var prompts = options.Contains("prompts", StringComparer.OrdinalIgnoreCase) ? new MadeUpPrompts(Loc.Language == "tr") : null;
        if (prompts is not null)
            settings.Prompts = new SavedPromptsOptions { Enabled = true, Pinned = [.. prompts.Pinned] };
        var model = MenuBuilder.Build(context, settings);
        // "agents": made-up followed agents, with the Active Agents slice open.
        bool showAgents = options.Contains("agents", StringComparer.OrdinalIgnoreCase);
        var details = MenuDetails.Load(model, showAgents ? MadeUpAgents() : [], prompts?.Prompts);
        var children = details.Children.ToList();

        // Hover the first slice that opens and its newest item; with "deep", also open the first item that opens further.
        var path = new List<int>();
        int hover = showAgents ? model.Items.ToList().FindIndex(MenuBuilder.ListsActiveAgents) : children.FindIndex(c => c.Count > 0);
        if (!options.Contains("idle", StringComparer.OrdinalIgnoreCase) && hover >= 0)
        {
            path.AddRange([hover, 0]);
            int deep = children[hover].ToList().FindIndex(c => c.Children.Count > 0);
            if (options.Contains("deep", StringComparer.OrdinalIgnoreCase) && deep >= 0)
                path = [hover, deep, 0];
        }
        // "hover=N": just slice N under the pointer, nothing opened (for showing a theme's highlight).
        if (options.FirstOrDefault(o => o.StartsWith("hover=", StringComparison.OrdinalIgnoreCase)) is { } only && int.TryParse(only[6..], out int slice))
            path = [slice];

        var window = new RadialMenuWindow();
        var background = options.Contains("transparent", StringComparer.OrdinalIgnoreCase) ? Colors.Transparent
            : dark ? Color.FromRgb(0x20, 0x24, 0x2C) : Color.FromRgb(0xD9, 0xDE, 0xE6);
        bool edge = options.Contains("edge", StringComparer.OrdinalIgnoreCase);
        var bitmap = window.RenderPreview(model, PaletteFrom(options, dark), details.Hints, children, path, background, edge, details.States);
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

    /// <summary>"theme=ID" picks a built-in theme (Rondel's by default), "accent=#RRGGBB[,#RRGGBB]" replaces its accent.</summary>
    static RondelPalette PaletteFrom(string[] options, bool dark)
    {
        string? Value(string name) => options.FirstOrDefault(o => o.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))?[(name.Length + 1)..];
        string[] accent = (Value("accent") ?? "").Split(',');
        var appearance = new Rondel.Core.Themes.AppearanceOptions
        {
            Theme = Value("theme") ?? Rondel.Core.Themes.BuiltInThemes.RondelId,
            Mode = dark ? Rondel.Core.Themes.ColorMode.Dark : Rondel.Core.Themes.ColorMode.Light,
            AccentFrom = accent[0],
            AccentTo = accent.Length > 1 ? accent[1] : "",
        };
        return RondelPalette.For(appearance, new ThemeColors(dark));
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
        var bitmap = window.RenderNewProjectPreview(spec, PaletteFrom(options, dark), background, "weather-api",
            prompt ? (tr ? "Hava durumu için küçük bir REST API kur, testleriyle birlikte." : "Set up a small REST API for the weather, with tests.") : null);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(output))
            encoder.Save(file);
        window.Close();
    }

    /// <summary>Draws the Ask card with made-up saved prompts, for checking its design.</summary>
    static void RenderAsk(string output, string[] options)
    {
        bool dark = !options.Contains("light", StringComparer.OrdinalIgnoreCase);
        bool tr = options.Contains("tr", StringComparer.OrdinalIgnoreCase);
        Loc.SetLanguage(tr ? "tr" : "en");
        using var prompts = new MadeUpPrompts(tr, empty: options.Contains("empty", StringComparer.OrdinalIgnoreCase));
        // A made-up location: these pictures may end up in the README.
        string folder = Path.Combine("C:" + Path.DirectorySeparatorChar, "Users", "dev", "Projects", "weather-api");
        var spec = new InputSpec(Loc.Get("Action.AskHere"), '\uE8F2', folder, "Claude Code", AskPrompt: true, ValidateName: _ => null,
            Submit: (_, _) => null)
        {
            AskName = false,
            Location = folder,
            SavedPrompts = prompts.Prompts,
            PromptNames = prompts.Prompts.List(),
        };
        var window = new RadialMenuWindow();
        var background = options.Contains("transparent", StringComparer.OrdinalIgnoreCase) ? Colors.Transparent
            : dark ? Color.FromRgb(0x20, 0x24, 0x2C) : Color.FromRgb(0xD9, 0xDE, 0xE6);
        string? picked = options.Contains("picked", StringComparer.OrdinalIgnoreCase) ? prompts.Pinned[0] : null;
        var bitmap = window.RenderAskPreview(spec, PaletteFrom(options, dark), background, picked);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(output))
            encoder.Save(file);
        window.Close();
    }

    /// <summary>Saved prompts made up for the pictures, in a temporary folder that goes with this object.</summary>
    sealed class MadeUpPrompts : IDisposable
    {
        readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("rondel-prompts-");

        /// <param name="into">The folder to write them to (next to a copy of the settings), or null for a temporary one.</param>
        public MadeUpPrompts(bool tr, bool empty = false, string? into = null)
        {
            string directory = into ?? _folder.FullName;
            Directory.CreateDirectory(directory);
            Prompts = new SavedPrompts(directory);
            (string Name, string Text)[] prompts = tr
                ?
                [
                    ("Değişikliklerimi gözden geçir", "Commit edilmemiş değişiklikleri gözden geçir: hataları, belirsiz isimleri ve eksik testleri bul. En önemliden başlayarak listele."),
                    ("Test yaz", "Testi olmayan kodu bul ve en riskli kısımlar için testler yaz."),
                    ("Projeyi açıkla", "Bu projenin ne yaptığını, nasıl kurulduğunu ve nereden başlanacağını kısaca açıkla."),
                    ("Bağımlılıkları güncelle", "Eskimiş bağımlılıkları bul, güncelle ve testleri çalıştır."),
                    ("Son hatayı ayıkla", "Son hatanın nedenini bul ve düzelt."),
                    ("README yaz", "Bu proje için kısa ve açık bir README yaz."),
                ]
                :
                [
                    ("Review my changes", "Review the uncommitted changes: look for bugs, unclear names and missing tests. List what you find, most important first."),
                    ("Write tests", "Find the code that has no tests and write tests for the riskiest parts."),
                    ("Explain this project", "Explain briefly what this project does, how it is set up and where to start."),
                    ("Update dependencies", "Find outdated dependencies, update them and run the tests."),
                    ("Debug the last error", "Find the cause of the last error and fix it."),
                    ("Write the README", "Write a short, clear README for this project."),
                ];
            if (!empty)
            {
                foreach (var (name, text) in prompts)
                    File.WriteAllText(Path.Combine(directory, name + SavedPrompts.Extension), text);
            }
            Pinned = [.. prompts.Take(3).Select(p => p.Name)];
        }

        public SavedPrompts Prompts { get; }

        /// <summary>The first three, in the order they are pinned.</summary>
        public IReadOnlyList<string> Pinned { get; }

        public void Dispose() => _folder.Delete(recursive: true);
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
        // A made-up update check five minutes ago that found this version, so the picture shows the usual line.
        File.WriteAllText(Path.Combine(copy.FullName, "update-state.json"),
            $$"""{ "LastCheck": "{{DateTime.UtcNow.AddMinutes(-5):O}}", "Latest": "{{AppInfo.Version}}" }""");
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
        bool picker = options.Contains("picker", StringComparer.OrdinalIgnoreCase);
        if (picker)
        {
            host.Store.Update(s =>
            {
                s.Appearance.AccentFrom = "#7C3AED";
                s.Appearance.AccentTo = "#22D3EE";
            });
        }
        // "prompts": made-up saved prompts next to the copy of the settings, turned on with three of them pinned.
        bool savedPrompts = options.Contains("prompts", StringComparer.OrdinalIgnoreCase);
        using var prompts = savedPrompts ? new MadeUpPrompts(Loc.Language == "tr", into: host.Prompts.Directory) : null;
        if (prompts is not null)
            host.Store.Update(s => s.Prompts = new SavedPromptsOptions { Enabled = true, Pinned = [.. prompts.Pinned] });
        var window = new SettingsWindow(host)
        {
            WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            ShowActivated = false,
        };
        window.Show();
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        string? scroll = options.FirstOrDefault(o => o.StartsWith("scroll=", StringComparison.OrdinalIgnoreCase))?[7..];
        if ((scroll is not null || options.Contains("bottom", StringComparer.OrdinalIgnoreCase))
            && FindChild<System.Windows.Controls.ScrollViewer>(window) is { } scroller)
        {
            if (double.TryParse(scroll, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double offset))
                scroller.ScrollToVerticalOffset(offset);
            else
                scroller.ScrollToEnd();
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        if (savedPrompts && window.FindName("PromptsBox") is System.Windows.FrameworkElement section
            && FindChild<System.Windows.Controls.ScrollViewer>(window) is { } list)
        {
            list.ScrollToVerticalOffset(section.TransformToAncestor(list).Transform(default).Y + list.VerticalOffset - 120);
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        var content = (System.Windows.FrameworkElement)window.Content;
        // The picker is a window of its own: drawn over the settings where it opens, with its shadow.
        var overlay = new DrawingVisual();
        if (picker && window.FindName("CustomAccent") is System.Windows.FrameworkElement row
            && FindChild<System.Windows.Controls.ScrollViewer>(window) is { } rows)
        {
            rows.ScrollToVerticalOffset(row.TransformToAncestor(rows).Transform(default).Y + rows.VerticalOffset - 240);
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var popup = window.ShowAccentPicker();
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var card = (System.Windows.FrameworkElement)popup.Child;
            var target = (System.Windows.UIElement)popup.PlacementTarget;
            var at = target.TranslatePoint(new System.Windows.Point(popup.HorizontalOffset, target.RenderSize.Height + popup.VerticalOffset), content);
            var margin = card.Margin;
            var area = new System.Windows.Rect(-margin.Left, -margin.Top, card.ActualWidth + margin.Left + margin.Right, card.ActualHeight + margin.Top + margin.Bottom);
            using var dc = overlay.RenderOpen();
            dc.DrawRectangle(new VisualBrush(card) { Viewbox = area, ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.None }, null,
                new System.Windows.Rect(at, area.Size));
        }
        var size = new System.Windows.Size(content.ActualWidth, content.ActualHeight);
        var backdrop = new DrawingVisual();
        using (var dc = backdrop.RenderOpen())
            dc.DrawRectangle(new SolidColorBrush(dark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3)), null, new System.Windows.Rect(size));
        var bitmap = new RenderTargetBitmap((int)(size.Width * 2), (int)(size.Height * 2), 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(backdrop);
        bitmap.Render(content);
        bitmap.Render(overlay);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(output))
            encoder.Save(file);
        window.Close();
    }

    /// <summary>
    /// Draws the Sessions window with made-up projects and sessions (never the real ones: their titles are private),
    /// some of them selected, and with <c>confirm</c> the question before deleting.
    /// </summary>
    static void RenderSessions(string output, string[] options)
    {
        bool dark = !options.Contains("light", StringComparer.OrdinalIgnoreCase);
        bool tr = options.Contains("tr", StringComparer.OrdinalIgnoreCase);
        Loc.SetLanguage(tr ? "tr" : "en");
        var now = DateTime.UtcNow;
        const long Kb = 1024, Mb = Kb * 1024;
        string Folder(string name) => Path.Combine("C:" + Path.DirectorySeparatorChar, "Users", "dev", "Projects", name);
        StoredSession Session(int n, string en, string trTitle, long bytes, TimeSpan age, bool running = false) =>
            new($"{n:00000000}-0000-4000-8000-000000000000", tr ? trTitle : en, now - age, bytes, running);
        IReadOnlyList<StoredProject> projects =
        [
            new("C--Users-dev-Projects-acme-docs", Folder("acme-docs"), true, 14 * Kb,
            [
                Session(1, "Draw the architecture diagrams from the screenshots", "Ekran görüntülerinden mimari çizimlerini çıkar", 760 * Mb, TimeSpan.FromDays(2)),
                Session(2, "Fix the broken links in the guide", "Kılavuzdaki kırık bağlantıları düzelt", 18 * Mb, TimeSpan.FromDays(5)),
            ]),
            new("C--Users-dev-Projects-weather-api", Folder("weather-api"), true, 6 * Kb,
            [
                Session(3, "Add tests for the forecast endpoint", "Tahmin uç noktası için testler ekle", 114 * Mb, TimeSpan.FromMinutes(3), running: true),
                Session(4, "Set up a small REST API for the weather", "Hava durumu için küçük bir REST API kur", 9 * Mb + 600 * Kb, TimeSpan.FromDays(3)),
                Session(5, "Explain how the cache works", "Önbelleğin nasıl çalıştığını açıkla", 48 * Kb, TimeSpan.FromDays(12)),
            ]),
            new("C--Users-dev-Projects-game-prototype", Folder("game-prototype"), false, 32 * Kb,
            [
                Session(6, "Make the enemy AI smarter", "Düşman yapay zekâsını akıllandır", 104 * Mb, TimeSpan.FromDays(9)),
            ]),
        ];
        var window = new SessionsWindow(() => projects, (_, _) => new CleanupResult(0, 0, 0, []), (_, _) => { }, 30,
            new ThemeColors(dark), dark ? System.Windows.ThemeMode.Dark : System.Windows.ThemeMode.Light)
        {
            WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            ShowActivated = false,
        };
        window.Show();
        window.Show(projects);
        window.SelectForPreview([(0, 0), (0, 1), (2, 0)], [2]);
        if (options.Contains("confirm", StringComparer.OrdinalIgnoreCase))
            window.ShowConfirm(forGood: options.Contains("forgood", StringComparer.OrdinalIgnoreCase));
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        // The list is read again once the window has loaded: wait for that, and keep the selection made above.
        for (int i = 0; i < 20; i++)
        {
            Thread.Sleep(25);
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

    /// <summary>
    /// Draws the welcome window, at one moment of its picture or at <c>frames=N</c> moments over one loop, for checking the
    /// design and for making an animation of it.
    /// </summary>
    static void RenderWelcome(string output, string[] options)
    {
        bool dark = !options.Contains("light", StringComparer.OrdinalIgnoreCase);
        Loc.SetLanguage(options.Contains("tr", StringComparer.OrdinalIgnoreCase) ? "tr" : "en");
        string? Value(string name) => options.FirstOrDefault(o => o.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))?[(name.Length + 1)..];
        var appearance = new Rondel.Core.Themes.AppearanceOptions { Mode = dark ? Rondel.Core.Themes.ColorMode.Dark : Rondel.Core.Themes.ColorMode.Light };
        var window = new WelcomeWindow(appearance, new ThemeColors(dark), dark ? System.Windows.ThemeMode.Dark : System.Windows.ThemeMode.Light)
        {
            WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            ShowActivated = false,
        };
        window.Show();
        // "stage": only the picture, for the README's animation.
        var content = options.Contains("stage", StringComparer.OrdinalIgnoreCase)
            ? (System.Windows.FrameworkElement)window.FindName("Stage")
            : (System.Windows.FrameworkElement)window.Content;
        var background = new SolidColorBrush(dark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3));

        void Save(double seconds, string path)
        {
            window.ShowMoment(seconds);
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var size = new System.Windows.Size(content.ActualWidth, content.ActualHeight);
            var backdrop = new DrawingVisual();
            using (var dc = backdrop.RenderOpen())
                dc.DrawRectangle(background, null, new System.Windows.Rect(size));
            var bitmap = new RenderTargetBitmap((int)(size.Width * 2), (int)(size.Height * 2), 192, 192, PixelFormats.Pbgra32);
            bitmap.Render(backdrop);
            bitmap.Render(content);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(path);
            encoder.Save(file);
        }

        // Each moment is drawn where the picture would be then, not where a running clock has it.
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        window.StopPlaying();
        if (int.TryParse(Value("frames"), out int frames) && frames > 0)
        {
            string stem = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, Path.GetFileNameWithoutExtension(output));
            for (int i = 0; i < frames; i++)
                Save(i * WelcomeWindow.Period / frames, $"{stem}-{i:000}.png");
        }
        else
        {
            double moment = double.TryParse(Value("t"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double t)
                ? t
                : WelcomeWindow.StillMoment;
            Save(moment, output);
        }
        window.Close();
    }

    /// <summary>
    /// Draws the update window for a made-up 0.7.0, over 0.6.0: the offer, or with "working" the download under way,
    /// or with "failed" a failed download.
    /// </summary>
    static void RenderUpdate(string output, string[] options)
    {
        bool dark = !options.Contains("light", StringComparer.OrdinalIgnoreCase);
        Loc.SetLanguage(options.Contains("tr", StringComparer.OrdinalIgnoreCase) ? "tr" : "en");
        var release = new Rondel.Core.Updates.ReleaseInfo(new Rondel.Core.Updates.AppVersion(0, 7, 0), """
            ### Features

            - **windows:** Update Rondel from within the app, verified with its release key (#46)
            - **core:** Look for a newer release once a day and when the connection comes back (#45)
            - **menu:** Open the active agents from the desktop menu or a shortcut (#40)

            ### Bug fixes

            - **settings:** Keep reading settings.json written by a newer version (#34)
            """, new Uri("https://github.com/xelfiera-shn/rondel/releases/tag/v0.7.0"),
            new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero), []);
        var window = new UpdateWindow(release, new Rondel.Core.Updates.AppVersion(0, 6, 0), new ThemeColors(dark),
            dark ? System.Windows.ThemeMode.Dark : System.Windows.ThemeMode.Light)
        {
            WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            ShowActivated = false,
        };
        if (options.Contains("working", StringComparer.OrdinalIgnoreCase))
            window.ShowWorking(Loc.Format("Update.Downloading", 23.4.ToString("0.0", Loc.Culture), 63.9.ToString("0.0", Loc.Culture)), 23.4 / 63.9, canCancel: true);
        else if (options.Contains("failed", StringComparer.OrdinalIgnoreCase))
            window.ShowFailed(Loc.Get("Update.Failed.Network"), canRetry: true);
        window.Show();
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

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

    /// <summary>
    /// Draws the agents card with made-up agents in every state, side by side on one picture: open, collapsed to its
    /// badge, its quick settings, and open with no agent.
    /// </summary>
    static void RenderCard(string output, string[] options)
    {
        bool dark = !options.Contains("light", StringComparer.OrdinalIgnoreCase);
        Loc.SetLanguage(options.Contains("tr", StringComparer.OrdinalIgnoreCase) ? "tr" : "en");
        var palette = PaletteFrom(options, dark);
        var window = new AgentsCardWindow
        {
            WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
        };
        window.ApplyLook(palette);
        var agents = MadeUpAgents();

        BitmapSource Snapshot()
        {
            // The whole window: its content has a margin for the shadow, which a picture of the content alone would cut off.
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)(window.ActualWidth * 2), (int)(window.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
            bitmap.Render(window);
            return bitmap;
        }

        window.ShowAgents(agents);
        window.Show();
        var open = Snapshot();
        window.SetCollapsed(true);
        var badge = Snapshot();
        window.SetCollapsed(false);
        window.ShowAgents([]);
        var empty = Snapshot();

        var settings = new AgentsCardOptions { Placement = CardPlacement.OnTop, MuteNotifications = true };
        var panel = window.QuickSettings(settings, "Ctrl+Alt+K", _ => { }, () => { }, () => { });
        var holder = new System.Windows.Controls.Border { Padding = new System.Windows.Thickness(14), Child = panel };
        holder.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        holder.Arrange(new System.Windows.Rect(holder.DesiredSize));
        holder.UpdateLayout();
        var quick = new RenderTargetBitmap((int)(holder.ActualWidth * 2), (int)(holder.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
        quick.Render(holder);
        window.Close();

        // "open": the open card alone (the README's picture); otherwise the open card on the left, the quick settings, the
        // badge and the empty card stacked on the right.
        bool openOnly = options.Contains("open", StringComparer.OrdinalIgnoreCase);
        double gap = openOnly ? 0 : 24;
        double right = openOnly ? 0 : Math.Max(quick.PixelWidth, Math.Max(badge.PixelWidth, empty.PixelWidth)) + gap;
        int width = (int)(open.PixelWidth + right + 2 * gap);
        int height = (int)(openOnly ? open.PixelHeight : Math.Max(open.PixelHeight, quick.PixelHeight + badge.PixelHeight + empty.PixelHeight + 2 * gap) + 2 * gap);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(dark ? Color.FromRgb(0x20, 0x24, 0x2C) : Color.FromRgb(0xD9, 0xDE, 0xE6)), null, new System.Windows.Rect(0, 0, width, height));
            dc.DrawImage(open, new System.Windows.Rect(gap, gap, open.PixelWidth, open.PixelHeight));
            if (!openOnly)
            {
                double x = gap + open.PixelWidth + gap, y = gap;
                dc.DrawImage(quick, new System.Windows.Rect(x, y, quick.PixelWidth, quick.PixelHeight));
                y += quick.PixelHeight + gap;
                dc.DrawImage(badge, new System.Windows.Rect(x, y, badge.PixelWidth, badge.PixelHeight));
                y += badge.PixelHeight + gap;
                dc.DrawImage(empty, new System.Windows.Rect(x, y, empty.PixelWidth, empty.PixelHeight));
            }
        }
        var picture = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        picture.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(picture));
        using var file = File.Create(output);
        encoder.Save(file);
    }

    /// <summary>
    /// The live agents card with made-up agents, without the tray app: its options live in memory and its place in a
    /// temporary file, so the real settings are never touched. Settings… on the card ends it. With a report file it
    /// runs a short script (collapsed, pinned open, on the desktop, hidden) and writes the window's state at each step.
    /// </summary>
    static void CardDemo(string[] options)
    {
        bool dark = !options.Contains("light", StringComparer.OrdinalIgnoreCase);
        Loc.SetLanguage(options.Contains("tr", StringComparer.OrdinalIgnoreCase) ? "tr" : "en");
        string? report = options.FirstOrDefault(o => o.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));
        var card = new AgentsCardOptions
        {
            Show = true,
            Placement = options.Contains("desktop", StringComparer.OrdinalIgnoreCase) ? CardPlacement.OnDesktop : CardPlacement.OnTop,
        };
        var agents = MadeUpAgents();
        string state = Path.Combine(Path.GetTempPath(), "rondel-card-demo.json");
        var frame = new System.Windows.Threading.DispatcherFrame();
        AgentsCard? live = null;
        live = new AgentsCard(() => card, change =>
        {
            change(card);
            live!.Update(agents, enabled: true);
        }, report is null ? state : null);
        live.ApplyLook(PaletteFrom(options, dark));
        live.AgentClicked += id => Log.Info($"Card demo: agent {id} clicked");
        live.SettingsRequested += () => frame.Continue = false;
        // The demo has no tray icon and no shortcut: a card the user hides comes back after two seconds, as the tray
        // menu's "Show the agents card" would bring it back.
        live.HiddenByUser += () =>
        {
            if (report is not null)
                return;
            var back = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            back.Tick += (_, _) =>
            {
                back.Stop();
                live.ShowAgain();
            };
            back.Start();
        };
        live.HideShortcut = "Ctrl+Alt+K";
        live.Update(agents, enabled: true);

        if (report is not null)
        {
            var text = new StringBuilder();
            void Step(string name, Action action)
            {
                action();
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                text.AppendLine($"{name}: {live.Describe()}{(live.IsShown ? "; " + InFront() : "")}");
            }
            // A plain window over the card, activated: is the card still in front of it?
            string InFront()
            {
                RECT rect;
                unsafe
                {
                    Native.GetWindowRect(live.Handle, &rect);
                }
                var cover = new System.Windows.Window
                {
                    WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
                    Left = rect.Left - 20,
                    Top = rect.Top - 20,
                    Width = rect.Width + 40,
                    Height = rect.Height + 40,
                    Title = "cover",
                };
                cover.Show();
                cover.Activate();
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                var center = new POINT(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2);
                bool card = Native.GetAncestor(Native.WindowFromPoint(center), Native.GA_ROOT) == live.Handle;
                cover.Close();
                return card ? "card in front of an active window" : "card behind an active window";
            }
            Step("collapsed", () => { });
            Step("pinned open", () => { card.Pinned = true; live.Update(agents, enabled: true); });
            Step("on the desktop", () => { card.Placement = CardPlacement.OnDesktop; live.Update(agents, enabled: true); });
            Step("above other windows again", () => { card.Placement = CardPlacement.OnTop; live.Update(agents, enabled: true); });
            Step("on the desktop again", () => { card.Placement = CardPlacement.OnDesktop; live.Update(agents, enabled: true); });
            Step("no agents", () => live.Update([], enabled: true));
            Step("shortcut, no agents", live.Toggle);
            Step("shortcut again", live.Toggle);
            text.AppendLine($"tray item available: {live.CanShowAgain}");
            Step("tray: show the agents card", live.ShowAgain);
            text.AppendLine($"tray item available: {live.CanShowAgain}");
            Step("turned off", () => { card.Show = false; live.Update(agents, enabled: true); });
            Write(report, text.ToString());
            live.Dispose();
            return;
        }
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        live.Dispose();
    }

    static void Write(string path, string text) => File.WriteAllText(path, text, new UTF8Encoding(true));
}
