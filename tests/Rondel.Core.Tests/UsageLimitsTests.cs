using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rondel.Core.Activity;
using Rondel.Core.Agents.ClaudeCode;

namespace Rondel.Core.Tests;

/// <summary>
/// The usage limits on the agents card: what a status line's input gives, how reports of several sessions add up, the
/// tap that runs as a session's status line, and the status line Rondel puts in front of the user's own.
/// </summary>
public sealed class UsageLimitsTests : IDisposable
{
    static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    readonly string _directory = Path.Combine(Path.GetTempPath(), "rondel-tests-" + Guid.NewGuid().ToString("N"));

    public UsageLimitsTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    /// <summary>A status line's input as Claude Code documents it, with Turkish in the folder's name.</summary>
    static byte[] Input(double fiveHour = 23.5, double weekly = 41.2) => Encoding.UTF8.GetBytes(string.Format(CultureInfo.InvariantCulture,
        "{{\"session_id\":\"abc\",\"cwd\":\"C:\\\\Users\\\\Deniz\\\\Masaüstü\\\\çalışma\",\"model\":{{\"display_name\":\"Opus\"}},"
        + "\"rate_limits\":{{\"five_hour\":{{\"used_percentage\":{0},\"resets_at\":1794238200}},"
        + "\"seven_day\":{{\"used_percentage\":{1},\"resets_at\":1794639600}}}}}}",
        fiveHour, weekly));

    [Fact]
    public void AStatusLinesInputGivesBothWindowsAndOneWithoutLimitsGivesNone()
    {
        var limits = UsageLimits.FromStatusLine(Input(), Now);

        Assert.NotNull(limits);
        Assert.Equal(23.5, limits.FiveHour!.Percent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1794238200), limits.FiveHour.ResetsAt);
        Assert.Equal(41.2, limits.Weekly!.Percent);
        Assert.Equal(Now, limits.Reported);

        // Before the first reply, with an API key, or with a window Claude Code dropped once it reset.
        Assert.Null(UsageLimits.FromStatusLine("""{"session_id":"abc","model":{"display_name":"Opus"}}"""u8.ToArray(), Now));
        Assert.Null(UsageLimits.FromStatusLine("""{"rate_limits":{"five_hour":{"used_percentage":null,"resets_at":1}}}"""u8.ToArray(), Now));
        Assert.Null(UsageLimits.FromStatusLine("not json"u8.ToArray(), Now));
        var weeklyOnly = UsageLimits.FromStatusLine("""{"rate_limits":{"seven_day":{"used_percentage":5,"resets_at":1794639600}}}"""u8.ToArray(), Now);
        Assert.Null(weeklyOnly!.FiveHour);
        Assert.Equal(5, weeklyOnly.Weekly!.Percent);
    }

    [Fact]
    public void ReportsAddUpSoThatALateSessionCannotTakeTheFiguresBack()
    {
        var resets = Now.AddHours(2);
        var saved = new UsageLimits(new UsageWindow(40, resets), new UsageWindow(30, Now.AddDays(3)), Now.AddMinutes(-5));

        // A session that last had a reply earlier reports the same window with a smaller share: the larger one stays.
        var late = UsageLimits.Merge(saved, new UsageLimits(new UsageWindow(35, resets.AddSeconds(20)), null, Now));
        Assert.Equal(40, late.FiveHour!.Percent);
        Assert.Equal(30, late.Weekly!.Percent);
        Assert.Equal(Now, late.Reported);

        // A window that resets later is a new one, even with a smaller share.
        var next = UsageLimits.Merge(saved, new UsageLimits(new UsageWindow(2, resets.AddHours(5)), null, Now));
        Assert.Equal(2, next.FiveHour!.Percent);

        // And a report of the window before it changes nothing.
        var older = UsageLimits.Merge(next, new UsageLimits(new UsageWindow(90, resets), null, Now));
        Assert.Equal(2, older.FiveHour!.Percent);
    }

    [Fact]
    public void TheStoreKeepsWhatSessionsReportAcrossReads()
    {
        var store = new UsageStore(Path.Combine(_directory, "usage", UsageStore.FileName));
        Assert.Null(store.Read());

        store.Report(UsageLimits.FromStatusLine(Input(fiveHour: 10), Now)!);
        store.Report(UsageLimits.FromStatusLine(Input(fiveHour: 12), Now.AddMinutes(1))!);

        var kept = store.Read();
        Assert.Equal(12, kept!.FiveHour!.Percent);
        Assert.Equal(41.2, kept.Weekly!.Percent);
        Assert.Equal(Now.AddMinutes(1), kept.Reported);
        Assert.Equal([UsageStore.FileName], Directory.GetFiles(Path.GetDirectoryName(store.Path)!).Select(Path.GetFileName));
    }

    [Fact]
    public void TheTapHandsTheInputOnAsItIsInBashAsAsciiJsonInPowerShellAndKeepsTheLimits()
    {
        var store = new UsageStore(Path.Combine(_directory, UsageStore.FileName));
        byte[] input = Input();

        // Git Bash ("$PSHOME" is empty there): the very same bytes.
        using var bash = new MemoryStream();
        StatusLineTap.Run([StatusLineTap.Argument, "--ps=", StatusLineTap.PassArgument], new MemoryStream(input), bash, store, new FixedTime(Now));
        Assert.Equal(input, bash.ToArray());
        Assert.Equal(23.5, store.Read()!.FiveHour!.Percent);

        // Windows PowerShell would turn the Turkish letters into "?" between two programs: escaped, the JSON is the same.
        using var powerShell = new MemoryStream();
        StatusLineTap.Run([StatusLineTap.Argument, @"--ps=C:\Windows\System32\WindowsPowerShell\v1.0", StatusLineTap.PassArgument],
            new MemoryStream(input), powerShell, store);
        byte[] ascii = powerShell.ToArray();
        Assert.All(ascii, b => Assert.True(b < 0x80));
        using var document = JsonDocument.Parse(ascii);
        Assert.Equal(@"C:\Users\Deniz\Masaüstü\çalışma", document.RootElement.GetProperty("cwd").GetString());

        // Without a status line of the user's after it, it prints nothing.
        using var alone = new MemoryStream();
        StatusLineTap.Run([StatusLineTap.Argument, "--ps="], new MemoryStream(input), alone, store);
        Assert.Empty(alone.ToArray());
    }

    [Fact]
    public void TheExecutableRunsUnquotedFromBothShellsOrNotAtAll()
    {
        Assert.Equal("C:/Users/Deniz/AppData/Local/Programs/Rondel/Rondel.exe",
            StatusLineTap.ShellPath(@"C:\Users\Deniz\AppData\Local\Programs\Rondel\Rondel.exe"));
        Assert.Equal("C:/Users/DENIZK~1/Rondel/Rondel.exe",
            StatusLineTap.ShellPath(@"C:\Users\Deniz Kaya\Rondel\Rondel.exe", _ => @"C:\Users\DENIZK~1\Rondel\Rondel.exe"));
        Assert.Null(StatusLineTap.ShellPath(@"C:\Users\Deniz Kaya\Rondel\Rondel.exe", _ => null));
        Assert.Null(StatusLineTap.ShellPath(@"C:\Tools (x)\Rondel.exe"));
        Assert.Equal("C:/Rondel/Rondel.exe --status-line \"--ps=$PSHOME\"", StatusLineTap.Prefix("C:/Rondel/Rondel.exe"));
    }

    [Fact]
    public void RondelsStatusLineGoesInFrontOfTheUsersOwnAndKeepsItsOptions()
    {
        const string tap = "C:/Rondel/Rondel.exe --status-line \"--ps=$PSHOME\"";
        var alone = ClaudeCodeStatusLine.Compose(tap, null);
        Assert.Equal(["type", "command"], alone.Select(p => p.Key));
        Assert.Equal("command", alone["type"]!.GetValue<string>());
        Assert.Equal(tap, alone["command"]!.GetValue<string>());

        var user = new JsonObject { ["type"] = "command", ["command"] = "~/.claude/statusline.sh", ["padding"] = 2, ["refreshInterval"] = 5 };
        var line = ClaudeCodeStatusLine.Compose(tap, user);
        Assert.Equal(tap + " --pass | ~/.claude/statusline.sh", line["command"]!.GetValue<string>());
        Assert.Equal(2, line["padding"]!.GetValue<int>());
        Assert.Equal(5, line["refreshInterval"]!.GetValue<int>());
        Assert.Equal("~/.claude/statusline.sh", user["command"]!.GetValue<string>());
    }

    [Fact]
    public void TheUsersStatusLineComesFromTheProjectFirstThenTheUsersSettings()
    {
        string project = Path.Combine(_directory, "project"), config = Path.Combine(_directory, "config");
        Directory.CreateDirectory(Path.Combine(project, ".claude"));
        Directory.CreateDirectory(config);
        Assert.Null(ClaudeCodeStatusLine.UserStatusLine(project, config));

        File.WriteAllText(Path.Combine(config, "settings.json"), """{ "statusLine": { "type": "command", "command": "user" }, }""");
        Assert.Equal("user", ClaudeCodeStatusLine.UserStatusLine(project, config)!["command"]!.GetValue<string>());

        File.WriteAllText(Path.Combine(project, ".claude", "settings.json"), """{ "statusLine": { "type": "command", "command": "project" } }""");
        Assert.Equal("project", ClaudeCodeStatusLine.UserStatusLine(project, config)!["command"]!.GetValue<string>());

        // A project that sets a status line that is no command has none, whatever the user's settings say.
        File.WriteAllText(Path.Combine(project, ".claude", "settings.local.json"), """{ "statusLine": null }""");
        Assert.Null(ClaudeCodeStatusLine.UserStatusLine(project, config));
    }

    [Fact]
    public void AnAgentGetsTheHooksFileAloneOrOneWithTheStatusLineThatIsWrittenOnce()
    {
        string hooks = Path.Combine(_directory, HookServer.FileName);
        File.WriteAllText(hooks, HookServer.Build(51234, "0123456789abcdef0123456789abcdef"));
        var activity = new AgentActivity { HooksFile = hooks };
        string folder = Path.Combine(_directory, "work");
        Directory.CreateDirectory(folder);

        Assert.Equal(hooks, activity.HooksFileFor(folder));

        activity.UsageTap = "C:/Rondel/Rondel.exe --status-line \"--ps=$PSHOME\"";
        string? withStatusLine = activity.HooksFileFor(folder);
        Assert.NotNull(withStatusLine);
        Assert.NotEqual(hooks, withStatusLine);
        using (var document = JsonDocument.Parse(File.ReadAllText(withStatusLine)))
        {
            Assert.True(document.RootElement.GetProperty("hooks").TryGetProperty("Stop", out _));
            Assert.StartsWith("C:/Rondel/Rondel.exe --status-line", document.RootElement.GetProperty("statusLine").GetProperty("command").GetString());
        }
        Assert.Equal(withStatusLine, activity.HooksFileFor(folder));
        Assert.Equal(2, Directory.GetFiles(_directory).Length);
    }

    [Fact]
    public void AWindowThatResetSinceItWasReportedHasNoShare()
    {
        var window = new UsageWindow(81, Now.AddMinutes(30));
        Assert.Equal(81, UsageText.Share(window, Now));
        Assert.Null(UsageText.Share(window, Now.AddMinutes(30)));
        Assert.Equal("—", UsageText.Percent(window, Now.AddHours(1)));
        Assert.Null(UsageText.Share(null, Now));
    }

    sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
