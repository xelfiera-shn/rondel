using System.Runtime.InteropServices;
using System.Text.Json;

namespace Rondel.Core.Updates;

/// <summary>A file of a release.</summary>
public sealed record ReleaseAsset(string Name, Uri Url, long Size);

/// <summary>A published release, as GitHub's API describes it.</summary>
/// <param name="Notes">The release notes the release workflow wrote (Markdown); see <see cref="ReleaseNotes"/>.</param>
/// <param name="Page">The release's page on GitHub.</param>
public sealed record ReleaseInfo(AppVersion Version, string Notes, Uri Page, DateTimeOffset? Published, IReadOnlyList<ReleaseAsset> Assets)
{
    /// <summary>The checksums of a release's files (as sha256sum writes them).</summary>
    public const string ChecksumsName = "SHA256SUMS.txt";

    /// <summary>The signature of <see cref="ChecksumsName"/> by Rondel's release key.</summary>
    public const string SignatureName = "SHA256SUMS.txt.sig";

    public ReleaseAsset? Asset(string name) =>
        Assets.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The Windows executable for a processor, as the release workflow names it: "Rondel-0.7.0-win-x64.exe".</summary>
    public static string WindowsExecutable(AppVersion version, Architecture architecture) =>
        $"Rondel-{version}-win-{(architecture == Architecture.Arm64 ? "arm64" : "x64")}.exe";

    /// <summary>
    /// Reads the answer to GitHub's "latest release" request. Null for a draft or a pre-release, a tag that is not a
    /// version, or anything that is not such an answer. Only https addresses are taken, and http ones on this computer
    /// (a local test feed); either way a file is used only when the release key vouches for it.
    /// </summary>
    public static ReleaseInfo? Parse(ReadOnlyMemory<byte> json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Bool(root, "draft") || Bool(root, "prerelease"))
                return null;
            if (!AppVersion.TryParse(Text(root, "tag_name"), out var version) || Https(Text(root, "html_url")) is not { } page)
                return null;

            DateTimeOffset? published = root.TryGetProperty("published_at", out var date) && date.ValueKind == JsonValueKind.String
                && date.TryGetDateTimeOffset(out var value) ? value : null;
            var assets = new List<ReleaseAsset>();
            if (root.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in list.EnumerateArray())
                {
                    if (asset.ValueKind != JsonValueKind.Object || Text(asset, "name") is not { Length: > 0 } name
                        || Https(Text(asset, "browser_download_url")) is not { } url)
                    {
                        continue;
                    }
                    long size = asset.TryGetProperty("size", out var bytes) && bytes.ValueKind == JsonValueKind.Number
                        && bytes.TryGetInt64(out long number) ? number : 0;
                    assets.Add(new ReleaseAsset(name, url, size));
                }
            }
            return new ReleaseInfo(version, Text(root, "body") ?? "", page, published, assets);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    static bool Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    static Uri? Https(string? text) =>
        Uri.TryCreate(text, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)) ? uri : null;
}
