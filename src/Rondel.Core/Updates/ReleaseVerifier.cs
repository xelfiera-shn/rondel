using System.Security.Cryptography;

namespace Rondel.Core.Updates;

/// <summary>
/// Checks that a release's files are the ones Rondel's release workflow published. The workflow signs
/// SHA256SUMS.txt with Rondel's release key (ECDSA P-256 with SHA-256, as <c>openssl dgst -sha256 -sign</c> writes
/// it); the private key exists only as a secret of that workflow, and its public half is built into the app. A file
/// is taken only when the signature is valid and the file matches its checksum there.
/// </summary>
public static class ReleaseVerifier
{
    /// <summary>The public half of Rondel's release key (Updates/release-key.pem).</summary>
    public static string PublicKey { get; } = ReadPublicKey();

    static string ReadPublicKey()
    {
        using var stream = typeof(ReleaseVerifier).Assembly.GetManifestResourceStream("Rondel.ReleaseKey.pem")
            ?? throw new InvalidOperationException("The release key is missing from the build");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Whether <paramref name="signature"/> is a valid signature of <paramref name="data"/> by the key.</summary>
    public static bool IsSigned(byte[] data, byte[] signature, string publicKeyPem)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(publicKeyPem);
            return key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// File name → SHA-256 (lower-case hex) from "&lt;hash&gt;  &lt;name&gt;" lines, as sha256sum writes them; a '*'
    /// before the name (binary mode) is allowed. Lines that are not such a line are skipped.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParseChecksums(string text)
    {
        var sums = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            int space = line.IndexOf(' ', StringComparison.Ordinal);
            if (space != 64 || !line[..64].All(Uri.IsHexDigit))
                continue;
            string name = line[(space + 1)..].TrimStart(' ', '*');
            if (name.Length > 0)
                sums[name] = line[..64].ToLowerInvariant();
        }
        return sums;
    }
}
