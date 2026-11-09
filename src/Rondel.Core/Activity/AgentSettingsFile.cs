using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rondel.Core.Logging;

namespace Rondel.Core.Activity;

/// <summary>
/// The hooks file with a status line added (agent-settings-&lt;hash&gt;.json, next to it), for a session that reports
/// the usage limits. The user's status line can differ by project, so there is one file for each status line;
/// a file is named after its content, so the same one serves every session that needs it and is written only once.
/// </summary>
public static class AgentSettingsFile
{
    const string Prefix = "agent-settings-";

    /// <summary>A file no session started from for this long is deleted (<see cref="Prune"/>).</summary>
    static readonly TimeSpan Unused = TimeSpan.FromDays(30);

    /// <summary>The settings file for a session: <paramref name="hooksFile"/>'s hooks and <paramref name="statusLine"/>.</summary>
    /// <returns>Its path, or null when it cannot be written (logged); the session then starts with the hooks file alone.</returns>
    public static string? With(string hooksFile, JsonObject statusLine)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(hooksFile)) is not JsonObject settings)
                return null;
            settings["statusLine"] = statusLine.DeepClone();
            // Relaxed escaping leaves the command's quotes as they are, so it reads as typed; it is the same JSON.
            string json = settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..12];
            string path = Path.Combine(Path.GetDirectoryName(hooksFile)!, Prefix + hash + ".json");
            if (File.Exists(path))
            {
                // In use again: Prune leaves it alone for another month.
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                return path;
            }
            string temp = path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warn($"Writing the agents' settings with a status line next to {hooksFile} failed", ex);
            return null;
        }
    }

    /// <summary>Deletes the files in <paramref name="directory"/> that no session started from in a month.</summary>
    public static void Prune(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
                return;
            foreach (var file in new DirectoryInfo(directory).EnumerateFiles(Prefix + "*.json"))
            {
                if (DateTime.UtcNow - file.LastWriteTimeUtc > Unused)
                    file.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Deleting old agent settings in {directory} failed", ex);
        }
    }
}
