using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Rondel.Core.Launch;

namespace Rondel.Core.Tests;

/// <summary>
/// Runs the launch script the way the Windows launcher does (-EncodedCommand) with a command that
/// echoes its arguments, and checks that every argument arrives exactly as planned. Windows PowerShell
/// and pwsh 7.3+ pass quotes to native programs differently; the script has to cope with both.
/// </summary>
public class LaunchRoundTripTests
{
    static readonly string[] Arguments =
    [
        "O'Brien said \"hi\"; $HOME `n & | < > %PATH% ‘curly’",
        "ikinci satır\nson satır: ğüşiöç İı",
        @"ends with a backslash\",
        @"C:\Program Files\quoted ""dir""\",
    ];

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public void EveryArgumentArrivesUnchanged(string shell)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The launch script runs on Windows.");
        string? shellPath = FindShell(shell);
        Assert.SkipWhen(shellPath is null, $"{shell} is not installed.");

        string echo = Path.Combine(AppContext.BaseDirectory, "ArgEcho.exe");
        Assert.Equal(Arguments, Run(shellPath!, $"\"{echo}\"", Arguments));
    }

    /// <summary>What cmd.exe would take for its own: an npm install runs the agent through a .cmd file.</summary>
    static readonly string[] BatchArguments =
    [
        "R&D",
        "a&echo.INJECTED",
        "a|b",
        "x>y<z",
        "^caret",
        "(paren)",
        "two words & more",
        @"ends with a backslash\",
        "",
    ];

    [Theory]
    [InlineData("powershell", null)]
    [InlineData("pwsh", null)]
    [InlineData("pwsh", "Standard")] // set in a profile: the script passes the batch file's arguments as they are anyway
    public void ABatchFileGetsTheArgumentsAsTheyAreAndRunsNothingElse(string shell, string? passing)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The launch script runs on Windows.");
        string? shellPath = FindShell(shell);
        Assert.SkipWhen(shellPath is null, $"{shell} is not installed.");

        // As npm's shim does: the program, then %*.
        var folder = Directory.CreateTempSubdirectory("rondel-tests-");
        try
        {
            string shim = Path.Combine(folder.FullName, "agent.cmd");
            File.WriteAllText(shim, $"@\"{Path.Combine(AppContext.BaseDirectory, "ArgEcho.exe")}\" %*\r\n");
            string? before = passing is null ? null : $"$PSNativeCommandArgumentPassing = '{passing}'";
            string after = "Write-Output \"MODE=$PSNativeCommandArgumentPassing\"";

            Assert.Equal(BatchArguments, Run(shellPath!, $"\"{shim}\"", BatchArguments, folder.FullName, before, after, out string output));
            if (passing is not null)
                Assert.Contains($"MODE={passing}", output); // put back for the window, which stays open
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    static string[]? Run(string shellPath, string command, string[] arguments) =>
        Run(shellPath, command, arguments, Path.GetTempPath(), null, null, out _);

    /// <summary>Runs the launch script with -EncodedCommand and returns the arguments the echo saw.</summary>
    static string[]? Run(string shellPath, string command, string[] arguments, string directory, string? before, string? after,
        out string output)
    {
        var plan = new LaunchPlan(directory, "Echo", command, arguments);
        string script = string.Join(Environment.NewLine, new[] { before, PowerShellScript.Build(plan), after }.Where(part => part is not null));
        var start = new ProcessStartInfo(shellPath, "-NoLogo -NoProfile -NonInteractive -EncodedCommand "
            + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };

        using var process = Process.Start(start)!;
        output = process.StandardOutput.ReadToEnd();
        string errors = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(60_000), "The shell did not exit.");

        // A second command that ran (echo.INJECTED) prints a line of its own.
        Assert.DoesNotContain(output.Split('\n'), line => line.Trim() == "INJECTED");
        string json = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(line => line.StartsWith('[')) ?? throw new Xunit.Sdk.XunitException($"No output.\n{output}\n{errors}");
        return JsonSerializer.Deserialize<string[]>(json);
    }

    static string? FindShell(string name)
    {
        string[] candidates = name == "powershell"
            ? [Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe")]
            : [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"PowerShell\7\pwsh.exe"),
                .. (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
                    .Select(directory => Path.Combine(directory.Trim('"'), "pwsh.exe")),
            ];
        return candidates.FirstOrDefault(File.Exists);
    }
}
