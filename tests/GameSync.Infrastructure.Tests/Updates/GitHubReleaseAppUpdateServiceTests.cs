using System.Net;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using GameSync.Core.Abstractions.Storage;
using GameSync.Core.Abstractions.Updates;
using GameSync.Core.Options;
using GameSync.Core.Versioning;
using GameSync.Infrastructure.Updates;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace GameSync.Infrastructure.Tests.Updates;

public sealed class GitHubReleaseAppUpdateServiceTests : IDisposable
{
    private const string AssetUrl = "https://github.com/TCDev10/GameSync/releases/download/v99.0.0/GameSync-Setup-x64.exe";
    private const string ApiUrl = "https://api.github.com/repos/TCDev10/GameSync/releases/latest";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "GameSyncUpdateTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CheckForUpdates_NewerRelease_ReportsInstallerIntegrityData()
    {
        var payload = BuildInstaller(2048);
        var service = CreateService(payload, "v99.0.0", Sha256(payload), payload.Length, out _);

        var result = await service.CheckForUpdatesAsync();

        result.UpdateAvailable.Should().BeTrue();
        result.LatestVersion.Should().Be("99.0.0");
        result.CurrentVersion.Should().Be(AppVersion.Semantic);
        result.InstallerUri.Should().Be(AssetUrl);
        result.InstallerFileName.Should().Be("GameSync-Setup-x64.exe");
        result.InstallerSizeBytes.Should().Be(payload.Length);
        result.InstallerSha256.Should().Be(Sha256(payload));
    }

    [Fact]
    public async Task CheckForUpdates_OlderRelease_ReportsNoUpdate()
    {
        var payload = BuildInstaller(64);
        var service = CreateService(payload, "v0.0.1", Sha256(payload), payload.Length, out _);

        var result = await service.CheckForUpdatesAsync();

        result.UpdateAvailable.Should().BeFalse();
        result.Message.Should().Be("You are on the latest version.");
    }

    [Fact]
    public async Task Update_VerifiedPayload_StartsInstallerUnattended()
    {
        var payload = BuildInstaller(64 * 1024);
        var service = CreateService(payload, "v99.0.0", Sha256(payload), payload.Length, out var launcher);
        var reported = new List<int>();

        var result = await service.UpdateAsync(new Progress<int>(reported.Add));

        result.InstallerStarted.Should().BeTrue();
        result.Version.Should().Be("99.0.0");
        result.ShouldExitApplication.Should().BeTrue();
        result.ProcessId.Should().Be(4321);

        File.Exists(result.InstallerPath).Should().BeTrue();
        (await File.ReadAllBytesAsync(result.InstallerPath!)).Should().Equal(payload);
        Directory.GetFiles(Path.Combine(_root, "updates"), "*.part").Should().BeEmpty();

        await launcher.Received(1).StartAsync(
            result.InstallerPath!,
            Arg.Is<string>(a =>
                a.Contains("/SILENT") && a.Contains("/SUPPRESSMSGBOXES") && a.Contains("/CLOSEAPPLICATIONS")),
            Arg.Any<CancellationToken>());

        reported.Should().Contain(100);
    }

    [Fact]
    public async Task Update_DigestMismatch_DiscardsDownloadAndDoesNotInstall()
    {
        var payload = BuildInstaller(4096);
        var wrongDigest = Sha256(BuildInstaller(4096, fill: 0x42));
        var service = CreateService(payload, "v99.0.0", wrongDigest, payload.Length, out var launcher);

        var act = async () => await service.UpdateAsync();

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*SHA-256*");
        await launcher.DidNotReceiveWithAnyArgs().StartAsync(default!, default!);
        Directory.GetFiles(Path.Combine(_root, "updates")).Should().BeEmpty();
    }

    [Fact]
    public async Task Update_SizeMismatch_DoesNotInstall()
    {
        var payload = BuildInstaller(4096);
        var service = CreateService(payload, "v99.0.0", Sha256(payload), payload.Length + 1, out var launcher);

        var act = async () => await service.UpdateAsync();

        await act.Should().ThrowAsync<InvalidOperationException>();
        await launcher.DidNotReceiveWithAnyArgs().StartAsync(default!, default!);
    }

    [Fact]
    public async Task Update_PayloadIsNotAnExecutable_DoesNotInstall()
    {
        var payload = Encoding.UTF8.GetBytes("<html>404 not found</html>");
        var service = CreateService(payload, "v99.0.0", Sha256(payload), payload.Length, out var launcher);

        var act = async () => await service.UpdateAsync();

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*not a Windows executable*");
        await launcher.DidNotReceiveWithAnyArgs().StartAsync(default!, default!);
    }

    [Fact]
    public async Task Update_AssetHostedOutsideGitHub_IsRejected()
    {
        var payload = BuildInstaller(512);
        var service = CreateService(
            payload,
            "v99.0.0",
            Sha256(payload),
            payload.Length,
            out var launcher,
            assetUrl: "https://evil.example.com/GameSync-Setup-x64.exe");

        var act = async () => await service.UpdateAsync();

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*not an allowed download host*");
        await launcher.DidNotReceiveWithAnyArgs().StartAsync(default!, default!);
    }

    [Fact]
    public async Task Update_ReleaseWithoutInstaller_IsRejected()
    {
        var service = CreateService([], "v99.0.0", digest: null, size: 0, out var launcher, assetUrl: null);

        var act = async () => await service.UpdateAsync();

        await act.Should().ThrowAsync<InvalidOperationException>();
        await launcher.DidNotReceiveWithAnyArgs().StartAsync(default!, default!);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private GitHubReleaseAppUpdateService CreateService(
        byte[] payload,
        string tag,
        string? digest,
        long size,
        out IUpdateInstallerLauncher launcher,
        string? assetUrl = AssetUrl)
    {
        var releaseJson = BuildReleaseJson(tag, assetUrl, size, digest);
        var handler = new StubHttpHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Equals(ApiUrl, StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(releaseJson, Encoding.UTF8, "application/json")
                };
            }

            if (assetUrl is not null && url.Equals(assetUrl, StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(payload)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("GitHubApi").Returns(_ => new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("https://api.github.com/")
        });
        factory.CreateClient("GitHubDownload").Returns(_ => new HttpClient(handler, disposeHandler: false));

        launcher = Substitute.For<IUpdateInstallerLauncher>();
        launcher.StartAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(4321);

        return new GitHubReleaseAppUpdateService(
            factory,
            launcher,
            new TestPaths(_root),
            Options.Create(new GameSyncOptions()),
            NullLogger<GitHubReleaseAppUpdateService>.Instance);
    }

    private static string BuildReleaseJson(string tag, string? assetUrl, long size, string? digest)
    {
        var assets = assetUrl is null
            ? "[]"
            : $$"""
                [
                  {
                    "name": "GameSync-Setup-x64.exe",
                    "browser_download_url": "{{assetUrl}}",
                    "size": {{size}},
                    "digest": {{(digest is null ? "null" : $"\"sha256:{digest}\"")}}
                  }
                ]
                """;

        return $$"""
            {
              "tag_name": "{{tag}}",
              "html_url": "https://github.com/TCDev10/GameSync/releases/tag/{{tag}}",
              "assets": {{assets}}
            }
            """;
    }

    private static byte[] BuildInstaller(int length, byte fill = 0x90)
    {
        var bytes = new byte[length];
        Array.Fill(bytes, fill);
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        return bytes;
    }

    private static string Sha256(byte[] payload) =>
        Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

    private sealed class StubHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(_responder(request));
    }

    private sealed class TestPaths : ILocalAppDataPaths
    {
        public TestPaths(string root)
        {
            Root = root;
            MachineConfigurationFile = Path.Combine(root, "machine.json");
            RepositoriesDirectory = Path.Combine(root, "repositories");
            CacheDirectory = Path.Combine(root, "cache");
            LogsDirectory = Path.Combine(root, "logs");
            BackupsDirectory = Path.Combine(root, "backups");
            UpdatesDirectory = Path.Combine(root, "updates");
        }

        public string Root { get; }
        public string MachineConfigurationFile { get; }
        public string RepositoriesDirectory { get; }
        public string CacheDirectory { get; }
        public string LogsDirectory { get; }
        public string BackupsDirectory { get; }
        public string UpdatesDirectory { get; }

        public void EnsureCreated() => Directory.CreateDirectory(UpdatesDirectory);
    }
}
