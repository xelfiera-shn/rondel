using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Rondel.Core.Launch;
using Rondel.Core.Localization;

namespace Rondel.Core.Tests;

public class PosixShellScriptTests
{
    [Fact]
    public void QuoteEndsAndRestartsTheLiteralAroundSingleQuotes()
    {
        Assert.Equal(@"'it'\''s $HOME `id` $(x) ‘y’'", PosixShellScript.Quote("it's $HOME `id` $(x) ‘y’"));
    }

    [Theory]
    [InlineData("claude", true)]
    [InlineData("cc", true)]
    [InlineData("~/.local/bin/claude", true)]
    [InlineData("/opt/homebrew/bin/claude-code", true)]
    [InlineData("my agent", false)]
    [InlineData("a;b", false)]
    [InlineData("$HOME/x", false)]
    [InlineData("a~b", false)]
    [InlineData("", false)]
    public void OnlyPlainWordsAreLeftUnquoted(string name, bool plain)
    {
        Assert.Equal(plain, PosixShellScript.IsPlainWord(name));
    }

    [Theory]
    [InlineData("/bin/zsh", "/bin/zsh")]
    [InlineData("/opt/homebrew/bin/bash", "/opt/homebrew/bin/bash")]
    [InlineData("/opt/homebrew/bin/fish", PosixShellScript.FallbackShell)]
    [InlineData("zsh", PosixShellScript.FallbackShell)]
    [InlineData(null, PosixShellScript.FallbackShell)]
    public void FishAndUnknownShellsFallBackToZsh(string? loginShell, string expected)
    {
        Assert.Equal(expected, PosixShellScript.ChooseShell(loginShell));
    }

    [Fact]
    public void BuildKeepsEveryValueInsideSingleQuotedLiterals()
    {
        var plan = new LaunchPlan("/Users/x/O'Brien $x", "Claude Code", "claude --model opus", ["say \"hi\"; rm `$HOME", "ikinci satır\nson"]);

        string script = PosixShellScript.Build(plan, "/bin/zsh");

        Assert.StartsWith("#!/bin/zsh\nrm -f -- \"$0\"\ncd -- '/Users/x/O'\\''Brien $x' 2>/dev/null || ", script);
        Assert.Contains("then claude --model opus \"$@\";", script);
        Assert.EndsWith(" rondel 'say \"hi\"; rm `$HOME' 'ikinci satır\nson'\n", script);
        Assert.DoesNotContain("\r", script);
    }

    [Fact]
    public void BuildQuotesACommandThatIsNotAPlainWord()
    {
        var plan = new LaunchPlan(Path.GetTempPath(), "Agent", "\"/Applications/My Agent/agent\" --fast", []);

        Assert.Contains(@"then '\''/Applications/My Agent/agent'\'' --fast ""$@"";", PosixShellScript.Build(plan, "/bin/zsh"));
    }

    [Fact]
    public void BuildCreatesTheRepositoryOnlyWhenAsked()
    {
        var plan = new LaunchPlan(Path.GetTempPath(), "Claude Code", "claude", []);

        Assert.DoesNotContain("git init", PosixShellScript.Build(plan, "/bin/zsh"));
        Assert.Contains("then git init;", PosixShellScript.Build(plan with { InitGit = true }, "/bin/zsh"));
    }

    [Fact]
    public void BuildPutsTheFollowedSessionsIdIntoTheEnvironment()
    {
        var plan = new LaunchPlan(Path.GetTempPath(), "Claude Code", "claude", []);

        Assert.DoesNotContain("RONDEL_SESSION", PosixShellScript.Build(plan, "/bin/zsh"));
        Assert.Contains("\nexport RONDEL_SESSION='0123456789ab'\n", PosixShellScript.Build(plan with { ActivityId = "0123456789ab" }, "/bin/zsh"));
    }

    // ---- the script run for real, the way Terminal.app runs the .command file ----

    static readonly string[] Arguments =
    [
        "O'Brien said \"hi\"; $HOME `id` & | < > %PATH% ‘curly’ $(touch rondel-pwned)",
        "ikinci satır\nson satır: ğüşiöç İı",
        @"ends with a backslash\",
        "--continue",
        "",
    ];

    static string ArgEcho => Path.Combine(AppContext.BaseDirectory, "ArgEcho");

    [Theory]
    [InlineData("/bin/zsh")]
    [InlineData("/bin/bash")]
    public void EveryArgumentArrivesUnchanged(string shell)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The macOS launch script runs on macOS and Linux.");
        Assert.SkipUnless(File.Exists(shell), $"{shell} is not installed.");
        using var temp = new TempFolder();
        string folder = temp.Create("Proje 'tırnak' $x; ğİ");

        var (output, errors, script) = Run(new LaunchPlan(folder, "Echo", $"\"{ArgEcho}\"", Arguments), shell, temp);

        Assert.Equal(Arguments, EchoedArguments(output, errors));
        Assert.Equal(folder, EchoedFolder(output));
        Assert.StartsWith("\e]0;Echo · Proje 'tırnak' $x; ğİ\a", output);
        Assert.False(File.Exists(script), "The .command file deletes itself.");
        Assert.False(File.Exists(Path.Combine(folder, "rondel-pwned")), "$(…) in an argument must not run.");
    }

    [Fact]
    public void AnAliasFromTheShellsRcFileIsTheCommand()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The macOS launch script runs on macOS and Linux.");
        Assert.SkipUnless(File.Exists("/bin/zsh"), "zsh is not installed.");
        using var temp = new TempFolder();
        File.WriteAllText(Path.Combine(temp.Path, ".zshrc"), $"alias rondelecho={PosixShellScript.Quote($"{PosixShellScript.Quote(ArgEcho)} --from-alias")}\n");

        var (output, errors, _) = Run(new LaunchPlan(temp.Create("work"), "Echo", "rondelecho", ["x y"]), "/bin/zsh", temp);

        Assert.Equal(["--from-alias", "x y"], EchoedArguments(output, errors));
    }

    [Fact]
    public void AMissingCommandIsReportedAndTheShellStays()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The macOS launch script runs on macOS and Linux.");
        Assert.SkipUnless(File.Exists("/bin/zsh"), "zsh is not installed.");
        using var temp = new TempFolder();

        var (_, errors, _) = Run(new LaunchPlan(temp.Create("work"), "Echo", "rondel-no-such-agent", []), "/bin/zsh", temp);

        Assert.Contains(Loc.Format("Launch.NotFound", "rondel-no-such-agent"), errors);
    }

    [Fact]
    public void ANewProjectGetsItsRepositoryFirst()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The macOS launch script runs on macOS and Linux.");
        Assert.SkipUnless(File.Exists("/bin/zsh"), "zsh is not installed.");
        Assert.SkipUnless((Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Any(d => File.Exists(Path.Combine(d, "git"))), "git is not installed.");
        using var temp = new TempFolder();
        string folder = temp.Create("new project");

        var (output, errors, _) = Run(new LaunchPlan(folder, "Echo", $"\"{ArgEcho}\"", ["go"]) { InitGit = true }, "/bin/zsh", temp);

        Assert.True(Directory.Exists(Path.Combine(folder, ".git")), $"No repository.\n{output}\n{errors}");
        Assert.Equal(["go"], EchoedArguments(output, errors));
    }

    /// <summary>Writes the script to a .command file and runs it with zsh, as Terminal.app does; stdin is closed, so the last shell exits.</summary>
    static (string Output, string Errors, string Script) Run(LaunchPlan plan, string shell, TempFolder temp)
    {
        string script = Path.Combine(temp.Path, "launch.command");
        File.WriteAllText(script, PosixShellScript.Build(plan, shell), new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var start = new ProcessStartInfo("/bin/zsh", [script])
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        start.Environment["ZDOTDIR"] = temp.Path; // zsh reads its rc files from here, not from the user's home
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var errors = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(60_000), "The script did not exit.");
        return (output, errors.Result, script);
    }

    static string[] EchoedArguments(string output, string errors) =>
        JsonSerializer.Deserialize<string[]>(output.Split('\n').LastOrDefault(line => line.StartsWith('['))
            ?? throw new Xunit.Sdk.XunitException($"No output.\n{output}\n{errors}"))!;

    /// <remarks>The window title (an escape sequence without a line break) comes right before it on the same line.</remarks>
    static string EchoedFolder(string output)
    {
        string line = output.Split('\n').Single(line => line.Contains("cwd \"", StringComparison.Ordinal));
        return JsonSerializer.Deserialize<string>(line[(line.IndexOf("cwd \"", StringComparison.Ordinal) + 4)..])!;
    }

    sealed class TempFolder : IDisposable
    {
        // The real path: /var on macOS is a link to /private/var, and the shell reports the real one.
        public string Path { get; } = RealPath(Directory.CreateDirectory(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "rondel-tests-" + Guid.NewGuid().ToString("N"))).FullName);

        public string Create(string name) => Directory.CreateDirectory(System.IO.Path.Combine(Path, name)).FullName;

        public void Dispose() => Directory.Delete(Path, recursive: true);

        static string RealPath(string path)
        {
            nint resolved = realpath(path, 0);
            if (resolved == 0)
                return path;
            string result = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(resolved)!;
            free(resolved);
            return result;
        }

        [System.Runtime.InteropServices.DllImport("libc")]
        static extern nint realpath(string path, nint resolved);

        [System.Runtime.InteropServices.DllImport("libc")]
        static extern void free(nint pointer);
    }
}
