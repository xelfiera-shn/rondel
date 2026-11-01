using System.Diagnostics;
using System.Globalization;
using Rondel.Core.Launch;
using Rondel.Core.Logging;

namespace Rondel.MacOS.Launch;

/// <summary>What looking for a command in the login shell found out.</summary>
/// <param name="Found">What <c>command -v</c> printed (a path, or an alias' definition), when it found the command.</param>
/// <param name="Known">False when the shell gave no answer: it took too long (a slow profile) or never ran the check
/// (a <c>~/.zshrc</c> that starts tmux or another shell). The terminal may still find the command then.</param>
internal sealed record CommandCheck(string? Found, bool Known);

/// <summary>
/// Finds an agent's command the way the terminal will: in the user's login and interactive shell, so the
/// <c>PATH</c> from <c>~/.zprofile</c> and aliases from <c>~/.zshrc</c> count. Apps started from Finder do not
/// inherit the terminal's <c>PATH</c>, so looking in Rondel's own would be wrong.
/// </summary>
internal static class CommandProbe
{
    /// <summary>
    /// Printed after <c>command -v</c> with its exit status. Only its presence tells "not found" from "the shell never
    /// got that far", since a shell that fails in its profile also exits with an error.
    /// </summary>
    const string Marker = "rondel-command-check:";

    static readonly CommandCheck Unknown = new(null, Known: false);

    public static async Task<CommandCheck> CheckAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return new CommandCheck(null, Known: true);
        string shell = PosixShellScript.ChooseShell(MacTerminalLauncher.LoginShell());
        var start = new ProcessStartInfo(shell)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        string script = $"command -v -- {Quote(name)}; printf '\\n{Marker}%s\\n' \"$?\"";
        foreach (string argument in new[] { "-l", "-i", "-c", script })
            start.ArgumentList.Add(argument);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Process? process = null;
        try
        {
            process = Process.Start(start);
            if (process is null)
                return Unknown;
            process.StandardInput.Close();
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            string output = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await error.ConfigureAwait(false);
            return Parse(output, name, shell);
        }
        catch (OperationCanceledException)
        {
            Log.Warn($"Looking for '{name}' in {shell} took too long");
            try
            {
                process?.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // it ended meanwhile
            }
            return Unknown;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            Log.Warn($"Looking for '{name}' in {shell} failed", ex);
            return Unknown;
        }
        finally
        {
            process?.Dispose();
        }
    }

    /// <summary>A profile may print its own lines first; command -v prints the last one before the marker.</summary>
    static CommandCheck Parse(string output, string name, string shell)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int marker = Array.FindLastIndex(lines, line => line.StartsWith(Marker, StringComparison.Ordinal));
        if (marker < 0)
        {
            Log.Warn($"{shell} ended before it looked for '{name}'");
            return Unknown;
        }
        bool found = int.TryParse(lines[marker][Marker.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out int status)
            && status == 0 && marker > 0;
        return new CommandCheck(found ? lines[marker - 1] : null, Known: true);
    }

    static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
