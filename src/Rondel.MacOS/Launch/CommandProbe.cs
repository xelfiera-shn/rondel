using System.Diagnostics;
using Rondel.Core.Launch;
using Rondel.Core.Logging;

namespace Rondel.MacOS.Launch;

/// <summary>
/// Finds an agent's command the way the terminal will: in the user's login and interactive shell, so the
/// <c>PATH</c> from <c>~/.zprofile</c> and aliases from <c>~/.zshrc</c> count. Apps started from Finder do not
/// inherit the terminal's <c>PATH</c>, so looking in Rondel's own would be wrong.
/// </summary>
internal static class CommandProbe
{
    /// <summary>What <c>command -v</c> printed (a path, or an alias' definition), or null when it is not found.</summary>
    public static async Task<string?> FindAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        string shell = PosixShellScript.ChooseShell(MacTerminalLauncher.LoginShell());
        var start = new ProcessStartInfo(shell)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in new[] { "-l", "-i", "-c", "command -v -- " + Quote(name) })
            start.ArgumentList.Add(argument);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Process? process = null;
        try
        {
            process = Process.Start(start);
            if (process is null)
                return null;
            process.StandardInput.Close();
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            string output = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await error.ConfigureAwait(false);
            // A profile may print its own lines first; command -v prints the last one.
            string? found = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
            return process.ExitCode == 0 && !string.IsNullOrEmpty(found) ? found : null;
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
            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            Log.Warn($"Looking for '{name}' in {shell} failed", ex);
            return null;
        }
        finally
        {
            process?.Dispose();
        }
    }

    static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
