using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Rondel.Core.Updates;

namespace Rondel.Core.Tests;

/// <summary>Finding a newer release, and taking its file only when Rondel's release key vouches for it.</summary>
public sealed class UpdateTests : IDisposable
{
    const string Base = "https://example.test/";

    readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("rondel-update-tests-");
    readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    readonly FakeHandler _web = new();
    readonly HttpClient _http;
    readonly FixedTime _time = new(new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero));

    public UpdateTests() => _http = new HttpClient(_web);

    public void Dispose()
    {
        _http.Dispose();
        _key.Dispose();
        _folder.Delete(recursive: true);
    }

    string PublicKey => _key.ExportSubjectPublicKeyInfoPem();

    static string LatestJson(string tag, bool prerelease = false, string notes = "") => $$"""
        {
          "tag_name": "{{tag}}", "draft": false, "prerelease": {{(prerelease ? "true" : "false")}},
          "html_url": "https://github.com/xelfiera-shn/rondel/releases/tag/{{tag}}",
          "published_at": "2026-11-12T10:00:00Z", "body": {{System.Text.Json.JsonSerializer.Serialize(notes)}},
          "assets": [
            { "name": "Rondel-0.7.0-win-x64.exe", "size": 5, "browser_download_url": "{{Base}}Rondel-0.7.0-win-x64.exe" },
            { "name": "SHA256SUMS.txt", "size": 100, "browser_download_url": "{{Base}}SHA256SUMS.txt" },
            { "name": "SHA256SUMS.txt.sig", "size": 72, "browser_download_url": "{{Base}}SHA256SUMS.txt.sig" },
            { "name": "insecure.exe", "size": 5, "browser_download_url": "http://example.test/insecure.exe" },
            { "name": "local.exe", "size": 5, "browser_download_url": "http://127.0.0.1:8765/local.exe" }
          ]
        }
        """;

    /// <summary>A published 0.7.0 whose checksums are signed with the test key (or with <paramref name="signer"/>).</summary>
    ReleaseInfo Publish(byte[] exe, byte[]? listed = null, ECDsa? signer = null)
    {
        string sums = $"{Convert.ToHexStringLower(SHA256.HashData(listed ?? exe))}  Rondel-0.7.0-win-x64.exe\n";
        byte[] sumsBytes = Encoding.UTF8.GetBytes(sums);
        _web.Files[Base + "Rondel-0.7.0-win-x64.exe"] = exe;
        _web.Files[Base + "SHA256SUMS.txt"] = sumsBytes;
        _web.Files[Base + "SHA256SUMS.txt.sig"] = (signer ?? _key).SignData(sumsBytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return ReleaseInfo.Parse(Encoding.UTF8.GetBytes(LatestJson("v0.7.0")))!;
    }

    [Theory]
    [InlineData("0.6.0", true)]
    [InlineData("v1.12.3", true)]
    [InlineData("0.7.0-beta.1", false)]
    [InlineData("0.7", false)]
    [InlineData("", false)]
    public void OnlyPlainVersionsAreVersions(string text, bool valid) => Assert.Equal(valid, AppVersion.TryParse(text, out _));

    [Fact]
    public void VersionsCompareByNumberAndTheDevelopmentBuildIsZero()
    {
        Assert.True(new AppVersion(0, 10, 0) > new AppVersion(0, 9, 3));
        Assert.True(new AppVersion(1, 0, 0) > new AppVersion(0, 99, 99));
        Assert.True(default(AppVersion).IsDevelopment);
        Assert.Equal("0.6.0", new AppVersion(0, 6, 0).ToString());
    }

    [Fact]
    public void TheLatestReleaseIsReadWithItsHttpsAndLocalFilesOnly()
    {
        var release = ReleaseInfo.Parse(Encoding.UTF8.GetBytes(LatestJson("v0.7.0")))!;

        Assert.Equal(new AppVersion(0, 7, 0), release.Version);
        Assert.Equal(["Rondel-0.7.0-win-x64.exe", "SHA256SUMS.txt", "SHA256SUMS.txt.sig", "local.exe"], release.Assets.Select(a => a.Name));
        Assert.Equal(new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero), release.Published);
        Assert.Equal("Rondel-0.7.0-win-arm64.exe", ReleaseInfo.WindowsExecutable(release.Version, Architecture.Arm64));
        Assert.Null(ReleaseInfo.Parse(Encoding.UTF8.GetBytes(LatestJson("v0.7.0", prerelease: true))));
        Assert.Null(ReleaseInfo.Parse(Encoding.UTF8.GetBytes(LatestJson("nightly"))));
        Assert.Null(ReleaseInfo.Parse("not json"u8.ToArray()));
    }

    [Fact]
    public void ReleaseNotesLoseTheirScopesAndPullRequestNumbers()
    {
        var sections = ReleaseNotes.Parse("""
            ### Features

            - **core:** Follow the agents Rondel starts through their hooks (#37)
            - **menu:** Open the active agents from the desktop menu or a shortcut (#40)

            ### Bug fixes

            - Keep reading `settings.json` written by a newer version (#34)
            """);

        Assert.Equal([Localization.Loc.Get("Update.Notes.Features"), Localization.Loc.Get("Update.Notes.Fixes")], sections.Select(s => s.Heading));
        Assert.Equal(["Follow the agents Rondel starts through their hooks", "Open the active agents from the desktop menu or a shortcut"], sections[0].Lines);
        Assert.Equal(["Keep reading settings.json written by a newer version"], sections[1].Lines);
    }

    [Fact]
    public void ChecksumLinesAreReadAsSha256sumWritesThem()
    {
        string hash = new('a', 64);
        var sums = ReleaseVerifier.ParseChecksums($"{hash}  Rondel-0.7.0-win-x64.exe\r\n{hash.ToUpperInvariant()} *Rondel-0.7.0-win-arm64.exe\nnot a line\n");

        Assert.Equal(hash, sums["Rondel-0.7.0-win-x64.exe"]);
        Assert.Equal(hash, sums["Rondel-0.7.0-win-arm64.exe"]);
        Assert.Equal(2, sums.Count);
    }

    [Fact]
    public void TheBuiltInReleaseKeyIsAPublicP256Key()
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(ReleaseVerifier.PublicKey);
        Assert.Equal(256, key.KeySize);
        Assert.DoesNotContain("PRIVATE", ReleaseVerifier.PublicKey, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFileSignedWithTheReleaseKeyIsDownloaded()
    {
        byte[] exe = "MZnew"u8.ToArray();
        var release = Publish(exe);
        var progress = new List<DownloadProgress>();

        string path = await new UpdateDownloader(_http, PublicKey).DownloadAsync(release, "Rondel-0.7.0-win-x64.exe", _folder.FullName,
            new SyncProgress(progress.Add), TestContext.Current.CancellationToken);

        Assert.Equal(exe, File.ReadAllBytes(path));
        Assert.Equal(new DownloadProgress(5, 5), progress[^1]);
        Assert.Equal([path], Directory.GetFiles(_folder.FullName));
    }

    [Fact]
    public async Task ChecksumsSignedWithAnotherKeyAreRefusedBeforeTheFileIsDownloaded()
    {
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var release = Publish("MZbad"u8.ToArray(), signer: stranger);

        var error = await Assert.ThrowsAsync<UpdateException>(() =>
            new UpdateDownloader(_http, PublicKey).DownloadAsync(release, "Rondel-0.7.0-win-x64.exe", _folder.FullName,
                cancel: TestContext.Current.CancellationToken));

        Assert.Equal(UpdateFailure.NotVerified, error.Failure);
        Assert.DoesNotContain(Base + "Rondel-0.7.0-win-x64.exe", _web.Requested);
        Assert.Empty(Directory.GetFiles(_folder.FullName));
    }

    [Fact]
    public async Task AFileThatDoesNotMatchItsChecksumIsDeleted()
    {
        var release = Publish("MZswapped"u8.ToArray(), listed: "MZnew"u8.ToArray());

        var error = await Assert.ThrowsAsync<UpdateException>(() =>
            new UpdateDownloader(_http, PublicKey).DownloadAsync(release, "Rondel-0.7.0-win-x64.exe", _folder.FullName,
                cancel: TestContext.Current.CancellationToken));

        Assert.Equal(UpdateFailure.NotVerified, error.Failure);
        Assert.Empty(Directory.GetFiles(_folder.FullName));
    }

    UpdateService Service(string current = "0.6.0", bool enabled = true)
    {
        AppVersion.TryParse(current, out var version);
        return new UpdateService(_http, version, () => enabled, Path.Combine(_folder.FullName, "state.json"), new Uri(Base + "latest"), _time);
    }

    [Fact]
    public async Task ANewerReleaseIsAnnouncedOnceADayUntilItIsSkipped()
    {
        _web.Files[Base + "latest"] = Encoding.UTF8.GetBytes(LatestJson("v0.7.0"));
        var announced = new List<AppVersion>();
        using var service = Service();
        service.Found += release => announced.Add(release.Version);

        Assert.Equal(UpdateStatus.Available, await service.CheckAsync(manual: false));
        Assert.Equal(new AppVersion(0, 7, 0), service.Latest!.Version);
        await service.CheckAsync(manual: false);
        Assert.Single(announced); // not again the same day

        _time.Now = _time.Now.AddDays(1);
        using (var restarted = Service())
        {
            restarted.Found += release => announced.Add(release.Version);
            await restarted.CheckAsync(manual: false);
            Assert.Equal(2, announced.Count); // a day later, also after a restart

            restarted.Skip(restarted.Latest!);
        }
        _time.Now = _time.Now.AddDays(2);
        using var later = Service();
        later.Found += release => announced.Add(release.Version);
        Assert.Equal(UpdateStatus.Available, await later.CheckAsync(manual: false));
        Assert.Equal(2, announced.Count);
        Assert.True(later.IsSkipped(later.Latest!));
    }

    [Fact]
    public async Task TheSameOrAnOlderReleaseIsUpToDateAndAFailedCheckIsNotRemembered()
    {
        _web.Files[Base + "latest"] = Encoding.UTF8.GetBytes(LatestJson("v0.7.0"));
        using var service = Service("0.7.0");
        Assert.Equal(UpdateStatus.UpToDate, await service.CheckAsync(manual: true));
        Assert.Null(service.Latest);
        Assert.Equal(_time.Now.UtcDateTime, service.LastCheckUtc);
        using (var restarted = Service("0.7.0"))
            Assert.Equal(UpdateStatus.UpToDate, restarted.Status); // until its first check, what the last one found
        using (var older = Service("0.6.0"))
            Assert.Equal(UpdateStatus.NotChecked, older.Status);

        _web.Files.Clear(); // GitHub answers 404 for everything: a release that is not there is no error...
        Assert.Equal(UpdateStatus.UpToDate, await service.CheckAsync(manual: true));
        _web.Fail = true; // ...but no connection is
        _time.Now = _time.Now.AddHours(3);
        Assert.Equal(UpdateStatus.Failed, await service.CheckAsync(manual: true));
        Assert.Equal(_time.Now.AddHours(-3).UtcDateTime, service.LastCheckUtc);
    }

    [Fact]
    public async Task ACheckTheUserAskedForAnnouncesNothing()
    {
        _web.Files[Base + "latest"] = Encoding.UTF8.GetBytes(LatestJson("v0.7.0"));
        using var service = Service();
        bool announced = false;
        service.Found += _ => announced = true;

        Assert.Equal(UpdateStatus.Available, await service.CheckAsync(manual: true));
        Assert.False(announced);
    }

    sealed class FakeHandler : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Files { get; } = [];

        public List<string> Requested { get; } = [];

        public bool Fail { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri!.ToString();
            Requested.Add(url);
            if (Fail)
                throw new HttpRequestException("no connection");
            return Task.FromResult(Files.TryGetValue(url, out byte[]? body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Reports at once, on the reporting thread (Progress&lt;T&gt; would post them).</summary>
    sealed class SyncProgress(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => report(value);
    }
}
