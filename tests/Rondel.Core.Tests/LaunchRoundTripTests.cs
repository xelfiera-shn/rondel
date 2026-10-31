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
        var plan = new LaunchPlan(Path.GetTempPath(), "Echo", $"\"{echo}\"", Arguments);
        string script = PowerShellScript.Build(plan);
        var start = new ProcessStartInfo(shellPath!, "-NoLogo -NoProfile -NonInteractive -EncodedCommand "
            + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };

        using var process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd();
        string errors = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(60_000), "The shell did not exit.");

        string json = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(line => line.StartsWith('[')) ?? throw new Xunit.Sdk.XunitException($"No output.\n{output}\n{errors}");
        Assert.Equal(Arguments, JsonSerializer.Deserialize<string[]>(json));
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
