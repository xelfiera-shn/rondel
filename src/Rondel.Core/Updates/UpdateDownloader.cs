using System.Security.Cryptography;
using System.Text;

namespace Rondel.Core.Updates;

/// <summary>Why an update could not be downloaded.</summary>
public enum UpdateFailure
{
    /// <summary>No connection, or GitHub did not answer as expected.</summary>
    Network,

    /// <summary>The release has no file for this computer, or no signed checksums.</summary>
    Missing,

    /// <summary>The checksums' signature is not Rondel's, or the file does not match its checksum.</summary>
    NotVerified,

    /// <summary>The file could not be written.</summary>
    Disk,
}

public sealed class UpdateException(UpdateFailure failure, string message, Exception? inner = null) : Exception(message, inner)
{
    public UpdateFailure Failure { get; } = failure;
}

/// <summary>Bytes downloaded so far, out of the file's size (0 when unknown).</summary>
public readonly record struct DownloadProgress(long Done, long Total);

/// <summary>
/// Downloads a release's file and verifies it (<see cref="ReleaseVerifier"/>) before anyone can run it: first the
/// signed checksums, then the file itself, hashed while it arrives. Only a verified file is left in the folder.
/// </summary>
public sealed class UpdateDownloader(HttpClient http, string publicKey)
{
    /// <summary>The checksums and their signature are small; anything larger is not what the workflow wrote.</summary>
    const int SmallFileLimit = 64 * 1024;

    /// <summary>A download that receives nothing for this long is given up (a dropped connection may never fail on its own).</summary>
    static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);

    /// <returns>The verified file, in <paramref name="directory"/>.</returns>
    /// <exception cref="UpdateException">The file could not be downloaded or verified.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancel"/> was cancelled.</exception>
    public async Task<string> DownloadAsync(ReleaseInfo release, string assetName, string directory,
        IProgress<DownloadProgress>? progress = null, CancellationToken cancel = default)
    {
        var asset = release.Asset(assetName) ?? throw new UpdateException(UpdateFailure.Missing, $"{release.Version} has no {assetName}");
        if (release.Asset(ReleaseInfo.ChecksumsName) is not { } sums || release.Asset(ReleaseInfo.SignatureName) is not { } signature)
            throw new UpdateException(UpdateFailure.Missing, $"{release.Version} has no signed checksums");

        string target = Path.Combine(directory, assetName);
        string partial = target + ".partial";
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        stall.CancelAfter(StallTimeout);
        try
        {
            byte[] sumsBytes = await SmallFileAsync(sums.Url, stall.Token);
            byte[] signatureBytes = await SmallFileAsync(signature.Url, stall.Token);
            if (!ReleaseVerifier.IsSigned(sumsBytes, signatureBytes, publicKey))
                throw new UpdateException(UpdateFailure.NotVerified, $"the checksums of {release.Version} are not signed with Rondel's release key");
            if (!ReleaseVerifier.ParseChecksums(Encoding.UTF8.GetString(sumsBytes)).TryGetValue(assetName, out string? expected))
                throw new UpdateException(UpdateFailure.Missing, $"the checksums of {release.Version} do not list {assetName}");

            Directory.CreateDirectory(directory);
            string actual;
            using (var response = await http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, stall.Token))
            {
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? asset.Size;
                await using var input = await response.Content.ReadAsStreamAsync(stall.Token);
                await using var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, stall.Token)) > 0)
                {
                    stall.CancelAfter(StallTimeout);
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), stall.Token);
                    done += read;
                    progress?.Report(new DownloadProgress(done, total));
                }
                actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            }
            if (actual != expected)
                throw new UpdateException(UpdateFailure.NotVerified, $"{assetName} does not match its checksum");
            File.Move(partial, target, overwrite: true);
            return target;
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateException(UpdateFailure.Network, $"downloading {assetName} failed: {ex.Message}", ex);
        }
        catch (OperationCanceledException ex) when (!cancel.IsCancellationRequested)
        {
            throw new UpdateException(UpdateFailure.Network, $"downloading {assetName} stalled", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new UpdateException(UpdateFailure.Disk, $"writing {target} failed: {ex.Message}", ex);
        }
        finally
        {
            TryDelete(partial);
        }
    }

    async Task<byte[]> SmallFileAsync(Uri url, CancellationToken cancel)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel);
        response.EnsureSuccessStatusCode();
        byte[] bytes = response.Content.Headers.ContentLength <= SmallFileLimit || response.Content.Headers.ContentLength is null
            ? await response.Content.ReadAsByteArrayAsync(cancel)
            : [];
        return bytes.Length is > 0 and <= SmallFileLimit ? bytes
            : throw new UpdateException(UpdateFailure.NotVerified, $"{url} is not a checksum file");
    }

    static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
