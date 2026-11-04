using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Rondel.Core.Logging;
using Rondel.Core.Updates;

namespace Rondel.MacOS.Updates;

/// <summary>
/// Puts a newer Rondel.app in place of the running one. The release's zip is unpacked next to the download, the new
/// app must pass codesign and carry the same signature as the running one (so macOS keeps its permissions), and a
/// small script waits for Rondel to exit, swaps the two bundles and opens the new one. Files Rondel downloads itself
/// carry no quarantine, so macOS opens the new version without asking.
/// </summary>
internal static partial class MacSelfUpdate
{
    /// <summary>Tells the new version which one it replaced (see <see cref="AfterUpdate"/>).</summary>
    public const string AfterUpdateSwitch = "--updated-from";

    /// <summary>The running Rondel.app, or null when Rondel does not run from an app bundle.</summary>
    public static string? Bundle()
    {
        // …/Rondel.app/Contents/MacOS/Rondel
        string? macOS = Path.GetDirectoryName(Environment.ProcessPath);
        string? bundle = Path.GetDirectoryName(Path.GetDirectoryName(macOS));
        return bundle is not null && bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ? bundle : null;
    }

    /// <summary>
    /// Whether Rondel may replace <paramref name="bundle"/>: not a copy macOS runs from a read-only place (an app
    /// opened from Downloads without moving it), and a folder the user may write in.
    /// </summary>
    public static bool CanReplace(string bundle)
    {
        const int W_OK = 2;
        string parent = Path.GetDirectoryName(bundle)!;
        return !bundle.Contains("/AppTranslocation/", StringComparison.Ordinal) && access(parent, W_OK) == 0 && access(bundle, W_OK) == 0;
    }

    /// <summary>The release's zip for this processor, as the release workflow names it: "Rondel-0.15.0-osx-arm64.zip".</summary>
    public static string ZipName(AppVersion version) =>
        $"Rondel-{version}-osx-{(RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64")}.zip";

    /// <summary>
    /// Unpacks <paramref name="zip"/>, checks the new app and starts the script that swaps it in once Rondel exits.
    /// Call it off the UI thread; on true the caller quits Rondel.
    /// </summary>
    public static bool Install(string bundle, string zip, AppVersion from)
    {
        string staging = Path.Combine(Path.GetDirectoryName(zip)!, "staging");
        try
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
            Directory.CreateDirectory(staging);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Preparing '{staging}' failed", ex);
            return false;
        }
        if (Run("/usr/bin/ditto", "-x", "-k", zip, staging) != 0)
            return false;
        string newBundle = Path.Combine(staging, "Rondel.app");
        if (!Directory.Exists(newBundle))
        {
            Log.Error($"{Path.GetFileName(zip)} holds no Rondel.app");
            return false;
        }
        // The same signer as the running app: then macOS keeps the Accessibility and Automation permissions.
        if (Run("/usr/bin/codesign", "--verify", "--strict", "--deep", newBundle) != 0)
            return false;
        if (DesignatedRequirement(bundle) is not { } requirement || Run("/usr/bin/codesign", "--verify", "-R=" + requirement, newBundle) != 0)
        {
            Log.Error("The new Rondel.app is not signed like the running one");
            return false;
        }

        string script = Path.Combine(staging, "install.sh");
        File.WriteAllText(script, $"""
            #!/bin/bash
            # Written by Rondel's update: wait for it to exit, swap the bundles, open the new one.
            while kill -0 {Environment.ProcessId} 2>/dev/null; do sleep 0.2; done
            old={Quote(bundle)}
            new={Quote(newBundle)}
            aside={Quote(Path.Combine(staging, "Rondel.old.app"))}
            if mv "$old" "$aside" && mv "$new" "$old"; then
                rm -rf "$aside"
                open "$old" --args {AfterUpdateSwitch} {from}
            else
                [ -d "$old" ] || mv "$aside" "$old"
                open "$old"
            fi
            """.Replace("\r\n", "\n", StringComparison.Ordinal));
        try
        {
            var start = new ProcessStartInfo("/bin/bash") { UseShellExecute = false };
            start.ArgumentList.Add(script);
            Process.Start(start)?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error("Starting the update script failed", ex);
            return false;
        }
        Log.Info($"Installing {Path.GetFileName(zip)} over {from} once Rondel exits");
        return true;
    }

    /// <summary>In the new version: the version it replaced, when an update started it.</summary>
    public static AppVersion? AfterUpdate(IReadOnlyList<string> args)
    {
        int at = args.ToList().IndexOf(AfterUpdateSwitch);
        return at >= 0 && at + 1 < args.Count && AppVersion.TryParse(args[at + 1], out var from) ? from : null;
    }

    /// <summary>The app's designated requirement as codesign prints it (after "designated => ").</summary>
    static string? DesignatedRequirement(string bundle)
    {
        var start = new ProcessStartInfo("/usr/bin/codesign") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "-d", "-r-", bundle })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start);
        if (process is null)
            return null;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        const string Prefix = "designated => ";
        string? line = output.Split('\n').FirstOrDefault(l => l.StartsWith(Prefix, StringComparison.Ordinal));
        return line?[Prefix.Length..].Trim();
    }

    static int Run(string program, params string[] arguments)
    {
        var start = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardError = true };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start);
        if (process is null)
            return -1;
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            Log.Warn($"{Path.GetFileName(program)} {string.Join(' ', arguments)} failed ({process.ExitCode.ToString(CultureInfo.InvariantCulture)}): {error.Trim()}");
        return process.ExitCode;
    }

    static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    [LibraryImport("/usr/lib/libSystem.B.dylib", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int access(string path, int mode);
}
