using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
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
using Rondel.Core.Settings;
using Rondel.Core.Updates;
using Rondel.MacOS.Detection;
using Rondel.MacOS.Interop;
using Rondel.MacOS.Launch;
using Rondel.MacOS.UI;
using Rondel.MacOS.Updates;

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
            case ["--render-ask", var output, .. var options]:
                RenderAsk(output, options);
                return true;
            case ["--render-new", var output, .. var options]:
                RenderNewProject(output, options);
                return true;
            case ["--render-menu", var output, .. var options]:
                RenderMenu(output, options);
                return true;
            case ["--export-icon", var output, .. var options]:
                AppBuilder.Configure<App>().UsePlatformDetect().SetupWithoutStarting();
                using (var file = File.Create(output))
                    MenuBar.RenderIcon(paused: options.Contains("paused")).Save(file);
                return true;
            case ["--export-app-icon", var output]:
                ExportAppIcon(output);
                return true;
            case ["--render-settings", var output, .. var options]:
                RenderSettings(output, options);
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
            case ["--render-update", var output, .. var options]:
                RenderUpdate(output, options);
                return true;
            case ["--render-answer", var output, .. var options]:
                RenderAnswer(output, options);
                return true;
            case ["--install-update", var bundle, var zip, var from]:
                // Swaps a locally built zip into a bundle once this process exits, as an update does.
                if (!AppVersion.TryParse(from, out var version))
                    throw new ArgumentException($"'{from}' is not a version");
                if (!MacSelfUpdate.Install(Path.GetFullPath(bundle), Path.GetFullPath(zip), version))
                    throw new InvalidOperationException("The update was not installed; see the log");
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
        if (context.SelectedFiles.Count > 1)
            report.AppendLine($"Files: {string.Join(" | ", context.SelectedFiles)}");
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
    /// <c>--render-menu out.png [dark|light] [en|tr] [desktop|item] [theme=ID] [hover=N] [open|deep] [edge] [prompts] [FOLDER|FILE...]</c>.
    /// The Finder-window menu for a folder by default (the desktop menu with <c>desktop</c>, the folder's own menu with
    /// <c>item</c>, a file's menu when given a file, held in a selection of them when given several), with its real
    /// session hints and items. <c>open</c> hovers the first
    /// slice that opens and its newest item, <c>deep</c> also the first of its items that opens further; <c>edge</c> draws
    /// it as if opened at the left edge of the screen; <c>agents</c> opens the Active Agents slice into made-up agents in
    /// every state; <c>prompts</c> pins made-up saved prompts to the Ask slices.
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
        string[] paths = [.. options.Where(o => o.StartsWith('/'))];
        string path = paths.FirstOrDefault() ?? settings.ResolveProjectsRoot();
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        var context = options.Contains("desktop") ? new TriggerContext(ContextKind.DesktopEmpty, settings.ResolveProjectsRoot(), name, "render")
            : File.Exists(path) ? new TriggerContext(ContextKind.FileItem, Path.GetDirectoryName(path), name, "render")
            {
                FilePath = path,
                SelectedFiles = paths.Length > 1 ? paths : [],
            }
            : new TriggerContext(options.Contains("item") ? ContextKind.FolderItem : ContextKind.ExplorerEmpty, path, name, "render");

        AppBuilder.Configure<App>().UsePlatformDetect()
            .With(new FontManagerOptions { DefaultFamilyName = ".AppleSystemUIFont" })
            .SetupWithoutStarting();
        bool isDark = dark ?? Application.Current?.PlatformSettings?.GetColorValues().ThemeVariant == Avalonia.Platform.PlatformThemeVariant.Dark;
        var prompts = options.Contains("prompts") ? MadeUpPrompts() : null;
        if (prompts is not null)
        {
            settings.Prompts.Enabled = true;
            settings.Prompts.Pinned = [.. prompts.List().Take(3)];
        }
        var model = MenuBuilder.Build(context, settings);
        bool showAgents = options.Contains("agents");
        var details = MenuDetails.Needed(model) ? MenuDetails.Load(model, showAgents ? MadeUpAgents() : null, prompts) : null;
        var hints = details?.Hints ?? [];
        var children = details?.Children ?? [.. model.Items.Select(_ => (IReadOnlyList<MenuItemModel>)[])];
        var hoverPath = new List<int>();
        if (hover >= 0)
        {
            hoverPath.Add(hover);
        }
        else if (showAgents && model.Items.ToList().FindIndex(MenuBuilder.ListsActiveAgents) is >= 0 and var agentsSlice)
        {
            hoverPath = [agentsSlice, 0];
        }
        else if (options.Contains("open") || options.Contains("deep"))
        {
            int slice = children.ToList().FindIndex(c => c.Count > 0);
            int deep = slice < 0 ? -1 : children[slice].ToList().FindIndex(c => c.Children.Count > 0);
            hoverPath = slice < 0 ? [] : options.Contains("deep") && deep >= 0 ? [slice, deep, 0] : [slice, 0];
        }
        var window = new RadialMenuWindow(MenuPalette.For(settings.Appearance, isDark));
        var bitmap = window.RenderPreview(model, hints, children, hoverPath, isDark ? Color.FromRgb(0x1E, 0x1E, 0x1E) : Color.FromRgb(0xEC, 0xEC, 0xEC),
            atLeftEdge: options.Contains("edge"), states: details?.States);
        bitmap.Save(output, new PngBitmapEncoderOptions());
    }

    /// <summary>Made-up saved prompts in a folder of their own, for the pictures.</summary>
    static SavedPrompts MadeUpPrompts()
    {
        string folder = Directory.CreateTempSubdirectory("rondel-prompts-").FullName;
        bool tr = Loc.Language == "tr";
        string[] names = tr
            ? ["Kodu gözden geçir", "Testleri yaz", "Hatayı bul", "Yeni başlayana anlat", "Commit mesajı öner"]
            : ["Review the code", "Write the tests", "Find the bug", "Explain like I'm new", "Suggest a commit message"];
        foreach (string name in names)
            File.WriteAllText(Path.Combine(folder, name + SavedPrompts.Extension), tr ? $"{name}: kısa ve net ol." : $"{name}: keep it short and clear.");
        return new SavedPrompts(folder);
    }

    /// <summary>
    /// Draws the Ask card with made-up saved prompts into a PNG: <c>--render-ask out.png [dark|light] [en|tr] [empty] [picked]</c>
    /// (<c>empty</c>: none in the folder; <c>picked</c>: one of them picked into the box).
    /// </summary>
    static void RenderAsk(string output, string[] options)
    {
        var settings = ReadSettings();
        Loc.SetLanguage(options.FirstOrDefault(o => o is "en" or "tr") ?? settings.Language);
        AppBuilder.Configure<App>().UsePlatformDetect()
            .With(new FontManagerOptions { DefaultFamilyName = ".AppleSystemUIFont" })
            .SetupWithoutStarting();
        bool isDark = options.Contains("dark") || (!options.Contains("light")
            && Application.Current?.PlatformSettings?.GetColorValues().ThemeVariant == Avalonia.Platform.PlatformThemeVariant.Dark);
        var prompts = options.Contains("empty") ? new SavedPrompts(Directory.CreateTempSubdirectory("rondel-prompts-").FullName) : MadeUpPrompts();
        string folder = Path.Combine(settings.ResolveProjectsRoot(), "weather-api");
        var spec = new InputSpec(Loc.Get("Action.AskHere"), '\uE8F2', folder, "Claude Code", AskPrompt: true, ValidateName: _ => null, Submit: (_, _) => null)
        {
            AskName = false,
            Location = folder,
            SavedPrompts = prompts,
            PromptNames = prompts.List(),
        };
        var window = new RadialMenuWindow(MenuPalette.For(settings.Appearance, isDark));
        var bitmap = window.RenderAskPreview(spec, options.Contains("picked") ? spec.PromptNames.FirstOrDefault() : null,
            isDark ? Color.FromRgb(0x1E, 0x1E, 0x1E) : Color.FromRgb(0xEC, 0xEC, 0xEC));
        bitmap.Save(output, new PngBitmapEncoderOptions());
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
        bitmap.Save(output, new PngBitmapEncoderOptions());
    }

    /// <summary>
    /// Draws the agents card with made-up agents into a PNG at 2x:
    /// <c>--render-card out.png [dark|light] [en|tr] [collapsed] [options] [empty]</c> (<c>options</c>: its own options open).
    /// </summary>
    static void RenderCard(string output, string[] options)
    {
        AppBuilder.Configure<App>().UsePlatformDetect()
            .With(new FontManagerOptions { DefaultFamilyName = ".AppleSystemUIFont" })
            .SetupWithoutStarting();
        bool dark = !options.Contains("light");
        Loc.SetLanguage(options.Contains("tr") ? "tr" : "en");
        var settings = new AppSettings();
        settings.Normalize();
        var window = new AgentsCardWindow(MenuPalette.For(settings.Appearance, dark));
        window.ShowAgents(options.Contains("empty") ? [] : MadeUpAgents());
        window.SetOptions(settings.ActiveAgents.Card);
        var background = dark ? Color.FromRgb(0x20, 0x24, 0x2C) : Color.FromRgb(0xD9, 0xDE, 0xE6);
        using var bitmap = window.RenderPreview(background, collapsed: options.Contains("collapsed"), options: options.Contains("options"));
        bitmap.Save(output, new PngBitmapEncoderOptions());
        window.Close();
    }

    /// <summary>Made-up agents in every state, for the pictures.</summary>
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

    /// <summary>
    /// Draws the Sessions window with made-up projects and sessions, some selected, into a PNG at 2x:
    /// <c>--render-sessions out.png [dark|light] [en|tr] [confirm] [forgood]</c>, with <c>confirm</c> the question before
    /// deleting (<c>forgood</c>: with Delete permanently ticked). The real sessions are never drawn: their titles are the user's.
    /// </summary>
    static void RenderSessions(string output, string[] options)
    {
        AppBuilder.Configure<App>().UsePlatformDetect()
            .With(new FontManagerOptions { DefaultFamilyName = ".AppleSystemUIFont" })
            .SetupWithoutStarting();
        bool tr = options.Contains("tr");
        Loc.SetLanguage(tr ? "tr" : "en");
        var now = DateTime.UtcNow;
        const long Kb = 1024, Mb = Kb * 1024;
        static string Folder(string name) => Path.Combine("/Users", "dev", "Projects", name);
        StoredSession Session(int n, string en, string trTitle, long bytes, TimeSpan age, bool running = false) =>
            new($"{n:00000000}-0000-4000-8000-000000000000", tr ? trTitle : en, now - age, bytes, running);
        IReadOnlyList<StoredProject> projects =
        [
            new("-Users-dev-Projects-acme-docs", Folder("acme-docs"), true, 14 * Kb,
            [
                Session(1, "Draw the architecture diagrams from the screenshots", "Ekran görüntülerinden mimari çizimlerini çıkar", 760 * Mb, TimeSpan.FromDays(2)),
                Session(2, "Fix the broken links in the guide", "Kılavuzdaki kırık bağlantıları düzelt", 18 * Mb, TimeSpan.FromDays(5)),
            ]),
            new("-Users-dev-Projects-weather-api", Folder("weather-api"), true, 6 * Kb,
            [
                Session(3, "Add tests for the forecast endpoint", "Tahmin uç noktası için testler ekle", 114 * Mb, TimeSpan.FromMinutes(3), running: true),
                Session(4, "Set up a small REST API for the weather", "Hava durumu için küçük bir REST API kur", 9 * Mb + 600 * Kb, TimeSpan.FromDays(3)),
                Session(5, "Explain how the cache works", "Önbelleğin nasıl çalıştığını açıkla", 48 * Kb, TimeSpan.FromDays(12)),
            ]),
            new("-Users-dev-Projects-game-prototype", Folder("game-prototype"), false, 32 * Kb,
            [
                Session(6, "Make the enemy AI smarter", "Düşman yapay zekâsını akıllandır", 104 * Mb, TimeSpan.FromDays(9)),
            ]),
        ];
        var window = new SessionsWindow(() => projects, (_, _) => new CleanupResult(0, 0, 0, []), (_, _) => { }, 30)
        {
            RequestedThemeVariant = options.Contains("light") ? Avalonia.Styling.ThemeVariant.Light : Avalonia.Styling.ThemeVariant.Dark,
        };
        window.Show(projects);
        window.SelectForPreview([(0, 0), (0, 1), (2, 0)], [2]);
        if (options.Contains("confirm"))
            window.ShowConfirm(forGood: options.Contains("forgood"));
        const double Width = 780, Height = 720;
        var page = (Control)window.Content!;
        window.Content = null;
        var root = new Avalonia.Controls.Panel { Width = Width, Height = Height, Background = window.Background, Children = { page } };
        window.Content = root;
        for (int pass = 0; pass < 3; pass++)
        {
            Dispatcher.UIThread.RunJobs();
            window.Measure(new Size(Width, Height));
            window.Arrange(new Rect(0, 0, Width, Height));
        }
        var bitmap = new RenderTargetBitmap(new PixelSize((int)(Width * 2), (int)(Height * 2)), new Vector(192, 192));
        bitmap.Render(root);
        bitmap.Save(output, new PngBitmapEncoderOptions());
        window.Close();
    }

    /// <summary>
    /// Draws a Quick Conversation's card with a made-up answer, for checking its design:
    /// <c>--render-answer out.png [dark|light] [en|tr] [starting|search|writing|done|stopped|failed]</c>.
    /// </summary>
    static void RenderAnswer(string output, string[] options)
    {
        AppBuilder.Configure<App>().UsePlatformDetect()
            .With(new FontManagerOptions { DefaultFamilyName = ".AppleSystemUIFont" })
            .SetupWithoutStarting();
        bool dark = !options.Contains("light");
        bool tr = options.Contains("tr");
        Loc.SetLanguage(tr ? "tr" : "en");
        string state = new[] { "starting", "search", "writing", "stopped", "failed" }.FirstOrDefault(options.Contains) ?? "done";
        // Made up, like everything these pictures show.
        string question = tr ? "Terminal'de bir klasörün boyutunu nasıl bulurum?" : "How do I find the size of a folder in Terminal?";
        string answer = tr
            ? "Klasörün toplam boyutunu **du** ile okunur biçimde alın:\n\n```zsh\ndu -sh ~/Projects/weather-api\n```\n\n- `-s` yalnızca toplamı yazar, alt klasörleri tek tek saymaz.\n- `-h` boyutu `K`, `M` ya da `G` ile gösterir.\n\n| Seçenek | Ne yapar |\n|---|---|\n| `-d 1` | Bir seviye aşağıdaki klasörleri de yazar |\n| `-c` | Sonuna genel toplamı ekler |"
            : "Get the folder's total size in a *readable* form with **du**:\n\n```zsh\ndu -sh ~/Projects/weather-api\n```\n\n- `-s` prints only the total, not every subfolder.\n- `-h` shows the size with `K`, `M` or `G`.\n\n| Option | What it does |\n|---|---|\n| `-d 1` | Also lists the folders one level down |\n| `-c` | Adds a grand total at the end |";
        string partial = answer[..(answer.IndexOf("```", StringComparison.Ordinal) + 20)];
        var view = state switch
        {
            "starting" => new AnswerView(question, "", AnswerState.Starting, null, null, false),
            "search" => new AnswerView(question, "", AnswerState.Starting, "WebSearch", null, false),
            "writing" => new AnswerView(question, partial, AnswerState.Writing, null, null, false),
            "stopped" => new AnswerView(question, partial, AnswerState.Stopped, null, null, true),
            "failed" => new AnswerView(question, "", AnswerState.Failed, null, "Not logged in · Please run /login", false),
            _ => new AnswerView(question, answer, AnswerState.Done, null, null, true),
        };
        var settings = new AppSettings();
        settings.Normalize();
        var header = new AnswerHeader(Loc.Get("Action.Quick"), '\uE945', Loc.Get("Quick.Location"), null, "Claude Code");
        var window = new AnswerCardWindow(null, header, MenuPalette.For(settings.Appearance, dark));
        window.Show(view);
        var background = dark ? Color.FromRgb(0x20, 0x24, 0x2C) : Color.FromRgb(0xD9, 0xDE, 0xE6);
        using var bitmap = window.RenderPreview(background);
        bitmap.Save(output, new PngBitmapEncoderOptions());
        window.Close();
    }

    /// <summary>
    /// Draws the update window for a made-up 0.15.0 over 0.14.0 into a PNG at 2x:
    /// <c>--render-update out.png [dark|light] [en|tr] [working|failed]</c>. The notes mix lines for both apps; the
    /// Windows ones are left out, as in the app.
    /// </summary>
    static void RenderUpdate(string output, string[] options)
    {
        AppBuilder.Configure<App>().UsePlatformDetect()
            .With(new FontManagerOptions { DefaultFamilyName = ".AppleSystemUIFont" })
            .SetupWithoutStarting();
        Loc.SetLanguage(options.FirstOrDefault(o => o is "en" or "tr") ?? "en");
        var release = new ReleaseInfo(new AppVersion(0, 15, 0), """
            ### Features

            - **windows:** Ask about a screenshot in a Quick Conversation (#78)
            - **core:** Look for newer releases and verify them with the release key (#46)

            ### macOS

            - **macos:** Update Rondel from within the app (#90)
            - **macos:** Open in one click after downloading, notarized by Apple (#89)
            """, new Uri("https://github.com/xelfiera-shn/rondel/releases/tag/v0.15.0"), new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero), []);
        var window = new UpdateWindow(release, new AppVersion(0, 14, 0))
        {
            RequestedThemeVariant = options.Contains("light") ? Avalonia.Styling.ThemeVariant.Light : Avalonia.Styling.ThemeVariant.Dark,
        };
        if (options.Contains("working"))
            window.ShowWorking(Loc.Format("Update.Downloading", 6.2.ToString("0.0", Loc.Culture), 15.6.ToString("0.0", Loc.Culture)), 6.2 / 15.6, canCancel: true);
        else if (options.Contains("failed"))
            window.ShowFailed(Loc.Get("Update.Failed.Network"), canRetry: true);
        const double Width = 520, Height = 560;
        var page = (Control)window.Content!;
        window.Content = null;
        var root = new Avalonia.Controls.Panel { Width = Width, Height = Height, Background = window.Background, Children = { page } };
        window.Content = root;
        for (int pass = 0; pass < 3; pass++)
        {
            Dispatcher.UIThread.RunJobs();
            window.Measure(new Size(Width, Height));
            window.Arrange(new Rect(0, 0, Width, Height));
        }
        var bitmap = new RenderTargetBitmap(new PixelSize((int)(Width * 2), (int)(Height * 2)), new Vector(192, 192));
        bitmap.Render(root);
        bitmap.Save(output, new PngBitmapEncoderOptions());
        window.Close();
    }

    /// <summary>
    /// Writes the app icon as an .icns file (build/Rondel.icns, which publish-macos.sh puts into the bundle):
    /// <c>--export-app-icon out.icns</c>. Every size of Apple's iconset is drawn on its own, then iconutil packs them.
    /// </summary>
    static void ExportAppIcon(string output)
    {
        AppBuilder.Configure<App>().UsePlatformDetect().SetupWithoutStarting();
        string set = Path.Combine(Directory.CreateTempSubdirectory("rondel-icon-").FullName, "Rondel.iconset");
        Directory.CreateDirectory(set);
        foreach (int size in new[] { 16, 32, 128, 256, 512 })
        {
            AppIcon.RenderAppIcon(size).Save(Path.Combine(set, $"icon_{size}x{size}.png"), new PngBitmapEncoderOptions());
            AppIcon.RenderAppIcon(size * 2).Save(Path.Combine(set, $"icon_{size}x{size}@2x.png"), new PngBitmapEncoderOptions());
        }
        var start = new System.Diagnostics.ProcessStartInfo("/usr/bin/iconutil") { UseShellExecute = false };
        foreach (string argument in new[] { "-c", "icns", set, "-o", Path.GetFullPath(output) })
            start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        process.WaitForExit();
        Directory.Delete(Path.GetDirectoryName(set)!, recursive: true);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"iconutil failed ({process.ExitCode})");
    }

    /// <summary>
    /// Draws the welcome window at one moment of its picture into a PNG at 2x:
    /// <c>--render-welcome out.png [dark|light] [en|tr] [t=SECONDS]</c>, by default the still moment shown when Reduce
    /// Motion is on. It works on a copy of settings.json, so a running Rondel and the real file are not touched.
    /// </summary>
    static void RenderWelcome(string output, string[] options)
    {
        AppBuilder.Configure<App>().UsePlatformDetect()
            .With(new FontManagerOptions { DefaultFamilyName = ".AppleSystemUIFont" })
            .SetupWithoutStarting();
        string copy = Directory.CreateTempSubdirectory("rondel-settings-").FullName;
        string file = Path.Combine(AppInfo.SettingsDirectory, "settings.json");
        if (File.Exists(file))
            File.Copy(file, Path.Combine(copy, "settings.json"));
        var host = new AppHost(copy);
        if (options.FirstOrDefault(o => o is "en" or "tr") is { } language)
            Loc.SetLanguage(language);
        double moment = options.FirstOrDefault(o => o.StartsWith("t=", StringComparison.Ordinal)) is { } t
            ? double.Parse(t[2..], CultureInfo.InvariantCulture) : WelcomeWindow.StillMoment;
        var window = new WelcomeWindow(host)
        {
            RequestedThemeVariant = options.Contains("light") ? Avalonia.Styling.ThemeVariant.Light : Avalonia.Styling.ThemeVariant.Dark,
        };
        const double Width = 600, Height = 900;
        var page = (Control)window.Content!;
        window.Content = null;
        var root = new Avalonia.Controls.Panel { Width = Width, Height = Height, Background = window.Background, Children = { page } };
        window.Content = root;
        window.ShowMoment(moment);
        for (int pass = 0; pass < 3; pass++)
        {
            Dispatcher.UIThread.RunJobs();
            window.Measure(new Size(Width, Height));
            window.Arrange(new Rect(0, 0, Width, Height));
        }
        var bitmap = new RenderTargetBitmap(new PixelSize((int)(Width * 2), (int)(Height * 2)), new Vector(192, 192));
        bitmap.Render(root);
        bitmap.Save(output, new PngBitmapEncoderOptions());
        window.Close();
        host.Dispose();
        Directory.Delete(copy, recursive: true);
    }

    /// <summary>
    /// Draws the whole settings page, without scrolling, into a PNG at 2x:
    /// <c>--render-settings out.png [dark|light] [en|tr] [custom] [prompts]</c>, with <c>custom</c> a custom accent and its
    /// color dots, and <c>prompts</c> saved prompts turned on with made-up ones, two of them pinned. It works on a copy of settings.json, so a running Rondel and the real file are not touched.
    /// </summary>
    static void RenderSettings(string output, string[] options)
    {
        AppBuilder.Configure<App>().UsePlatformDetect()
            .With(new FontManagerOptions { DefaultFamilyName = ".AppleSystemUIFont" })
            .SetupWithoutStarting();
        string copy = Directory.CreateTempSubdirectory("rondel-settings-").FullName;
        string file = Path.Combine(AppInfo.SettingsDirectory, "settings.json");
        if (File.Exists(file))
            File.Copy(file, Path.Combine(copy, "settings.json"));
        if (options.Contains("prompts"))
        {
            Loc.SetLanguage(options.FirstOrDefault(o => o is "en" or "tr") ?? "en");
            var made = MadeUpPrompts();
            Directory.CreateDirectory(Path.Combine(copy, "Prompts"));
            foreach (string prompt in Directory.EnumerateFiles(made.Directory))
                File.Copy(prompt, Path.Combine(copy, "Prompts", Path.GetFileName(prompt)));
        }
        var host = new AppHost(copy);
        if (options.Contains("prompts"))
        {
            host.Store.Update(s =>
            {
                s.Prompts.Enabled = true;
                s.Prompts.Pinned = [.. host.Prompts.List().Skip(1).Take(2)];
            });
        }
        if (options.FirstOrDefault(o => o is "en" or "tr") is { } language)
            Loc.SetLanguage(language);
        if (options.Contains("custom"))
        {
            host.Store.Update(s =>
            {
                s.Appearance.AccentFrom = "#F97316";
                s.Appearance.AccentTo = "#DB2777";
            });
        }
        var window = new SettingsWindow(host)
        {
            RequestedThemeVariant = options.Contains("light") ? Avalonia.Styling.ThemeVariant.Light : Avalonia.Styling.ThemeVariant.Dark,
        };
        const double Width = 680, Height = 2000;
        // The page is drawn from a panel inside the window, so its controls get their templates.
        var page = (Control)window.Content!;
        window.Content = null;
        var root = new Avalonia.Controls.Panel { Width = Width, Height = Height, Background = window.Background, Children = { page } };
        window.Content = root;
        // Theme pictures are drawn while the window is idle, and text boxes settle over several layout passes.
        for (int pass = 0; pass < 4; pass++)
        {
            window.Width = Width;
            window.Height = Height;
            Dispatcher.UIThread.RunJobs();
            window.Measure(new Size(Width, Height));
            window.Arrange(new Rect(0, 0, Width, Height));
        }
        var bitmap = new RenderTargetBitmap(new PixelSize((int)(Width * 2), (int)(Height * 2)), new Vector(192, 192));
        bitmap.Render(root);
        bitmap.Save(output, new PngBitmapEncoderOptions());
        window.Close();
        host.Dispose();
        Directory.Delete(copy, recursive: true);
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
