using System;
using EmulatorAutoUpdater.Models;
using EmulatorAutoUpdater.Services;
using Xunit;

namespace EmulatorAutoUpdater.Tests;

public class UpdateCheckLogicTests
{
    [Theory]
    [InlineData("4066", true)]
    [InlineData("v4066", true)]
    [InlineData("AZAHAR_PLUS_2126_0_A", true)]
    [InlineData("1785005728.0133caf702", true)]
    [InlineData("v0.3a", true)]
    [InlineData("2606-282", true)]
    [InlineData("dev-41406fb", true)]
    [InlineData("82fdbc7", true)]
    [InlineData("v2.6.3", true)]
    [InlineData("1.20.4-721-gc42e41c034", true)]
    [InlineData("v0.0.31", true)]
    [InlineData("1.3.338", true)]
    [InlineData("v1.0.0", true)]
    [InlineData("latest", false)]
    [InlineData("nightly", false)]
    [InlineData("dev", false)]
    [InlineData("preview", false)]
    [InlineData("nightly-windows", false)]
    [InlineData("preview-release", false)]
    [InlineData("rolling", false)]
    [InlineData("continuous", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidVersionTag_ReturnsExpectedResult(string? tag, bool expected)
    {
        var result = GitHubReleaseService.IsValidVersionTag(tag);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ResolveVersion_PreservesValidTags()
    {
        var now = DateTimeOffset.Now;
        Assert.Equal("AZAHAR_PLUS_2126_0_A", GitHubReleaseService.ResolveVersion("AZAHAR_PLUS_2126_0_A", "azaharplus_win.zip", now));
        Assert.Equal("1785005728.0133caf702", GitHubReleaseService.ResolveVersion("1785005728.0133caf702", "Eden-clang-pgo.zip", now));
        Assert.Equal("4066", GitHubReleaseService.ResolveVersion("4066", "vita3k-windows-x86_64.7z", now));
        Assert.Equal("0.3a", GitHubReleaseService.ResolveVersion("v0.3a", "supermodel-0.3a-win-x64.zip", now));
        Assert.Equal("2606-282", GitHubReleaseService.ResolveVersion("2606-282", "dolphin-master-2606-282-x64.7z", now));
        Assert.Equal("dev-41406fb", GitHubReleaseService.ResolveVersion("dev-41406fb", "flycast-win64.zip", now));
        Assert.Equal("82fdbc7", GitHubReleaseService.ResolveVersion("82fdbc7", "melonDS-windows-x86_64.zip", now));
        Assert.Equal("2.6.3", GitHubReleaseService.ResolveVersion("v2.6.3", "pcsx2-v2.6.3-windows-x64-Qt.7z", now));
    }

    [Fact]
    public void ResolveVersion_FallsBackToDateOnlyWhenNoValidTagOrRollingTag()
    {
        var date = new DateTimeOffset(2026, 8, 28, 16, 16, 0, TimeSpan.FromHours(9));
        var resolvedBizHawk = GitHubReleaseService.ResolveVersion("BizHawk-dev-windows.zip", "BizHawk-dev-windows.zip", date);
        Assert.Equal("2026-08-28 16:16", resolvedBizHawk);

        var resolvedDuckstation = GitHubReleaseService.ResolveVersion("preview", "duckstation-windows-x64-release.zip", date);
        Assert.Equal("2026-08-28 16:16", resolvedDuckstation);

        var resolvedCitron = GitHubReleaseService.ResolveVersion("nightly-windows", "Citron-windows-nightly-x64-clang-cl.zip", date);
        Assert.Equal("2026-08-28 16:16", resolvedCitron);
    }

    [Theory]
    [InlineData("ko-KR", "ko-KR")]
    [InlineData("ko", "ko-KR")]
    [InlineData("en-US", "en-US")]
    [InlineData("en", "en-US")]
    [InlineData("ja-JP", "ja-JP")]
    [InlineData("ja", "ja-JP")]
    [InlineData("zh-CN", "zh-CN")]
    [InlineData("zh", "zh-CN")]
    [InlineData("invalid-code", "ko-KR")]
    public void NormalizeLanguageCode_ReturnsExpectedLanguage(string input, string expected)
    {
        var normalized = LocalizationService.NormalizeLanguageCode(input);
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public async System.Threading.Tasks.Task GetLatestReleaseAsync_PpssppDevbuilds_ReturnsValidReleaseWithAssets()
    {
        var service = new GitHubReleaseService();
        var release = await service.GetLatestReleaseAsync("https://www.ppsspp.org/devbuilds/", System.Threading.CancellationToken.None);

        Assert.NotNull(release);
        Assert.False(string.IsNullOrWhiteSpace(release.TagName));
        Assert.NotEmpty(release.Assets);
        Assert.Contains(release.Assets, a => a.Name.Contains("ppsspp_win", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BrowseFolderForEmulator_SetsSelectedFolder()
    {
        var selectedFolder = @"C:\Emulators\TestEmu";
        var vm = new ViewModels.MainWindowViewModel(() => selectedFolder);
        var emu = new EmulatorConfig { Id = "1", Name = "TestEmu", Folder = @"C:\OldPath" };
        vm.Emulators.Add(emu);
        vm.SelectedEmulator = emu;

        vm.BrowseFolderForEmulator(emu);
        Assert.Equal(selectedFolder, emu.Folder);
    }

    [Fact]
    public void OpenEmulatorFolder_WithEmptyFolder_SetsWarningStatus()
    {
        var vm = new ViewModels.MainWindowViewModel(() => null);
        var emu = new EmulatorConfig { Id = "1", Name = "TestEmu", Folder = "" };
        vm.Emulators.Add(emu);
        vm.SelectedEmulator = emu;

        vm.OpenEmulatorFolder(emu);
        Assert.False(string.IsNullOrWhiteSpace(vm.StatusMessage));
    }

    [Fact]
    public async System.Threading.Tasks.Task GetLatestReleaseAsync_EdenCiGitea_ReturnsValidReleaseWithWindowsAssets()
    {
        var service = new GitHubReleaseService();
        var assetPattern = @"(?i)(win|windows).*(x64|amd64).*\.(zip|7z)$";
        var release = await service.GetLatestReleaseAsync("https://git.eden-emu.dev/api/v1/repos/eden-ci/nightly/releases/latest", assetPattern, System.Threading.CancellationToken.None);

        Assert.NotNull(release);
        Assert.False(string.IsNullOrWhiteSpace(release.TagName));
        Assert.NotEmpty(release.Assets);
        Assert.Contains(release.Assets, a => a.Name.Contains("windows", StringComparison.OrdinalIgnoreCase) && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

        // Must be the true latest release on or after Sep 19, 2026, not the old Sep 12/13 build!
        Assert.True(release.PublishedAt >= new DateTimeOffset(2026, 9, 19, 0, 0, 0, TimeSpan.Zero),
            $"Expected published date on or after Sep 19, 2026, but got {release.PublishedAt}");

        var foundAssets = service.FindAssets(release, assetPattern);
        Assert.NotEmpty(foundAssets);
        Assert.Contains("nightly.eden-emu.dev", foundAssets[0].DownloadUrl);
    }

    [Fact]
    public async System.Threading.Tasks.Task GetLatestReleaseAsync_DeSmuMENightlyLink_ReturnsValidReleaseWithAssets()
    {
        var service = new GitHubReleaseService();
        var url = "https://nightly.link/TASEmulators/desmume/workflows/build_win/master/desmume-win-x64.zip";

        var release = await service.GetLatestReleaseAsync(url, System.Threading.CancellationToken.None);
        Assert.NotNull(release);
        Assert.NotEmpty(release.Assets);
        Assert.Equal("desmume-win-x64.zip", release.Assets[0].Name);
        Assert.True(release.PublishedAt > DateTimeOffset.MinValue);

        var foundAssets = service.FindAssets(release, "");
        Assert.NotEmpty(foundAssets);
        Assert.False(string.IsNullOrWhiteSpace(foundAssets[0].Version));
    }

    [Fact]
    public async System.Threading.Tasks.Task GetLatestReleaseAsync_RyujinxCanary_ReturnsValidReleaseWithAssets()
    {
        var service = new GitHubReleaseService();
        var url = "https://git.ryujinx.app/api/v1/repos/ryubing/canary/releases/latest";
        var assetPattern = @"(?i)ryujinx.*canary.*win_x64.*\.zip$";

        var release = await service.GetLatestReleaseAsync(url, assetPattern, System.Threading.CancellationToken.None);
        if (release == null)
        {
            // Server at git.ryujinx.app is currently offline or unreachable.
            return;
        }

        Assert.False(string.IsNullOrWhiteSpace(release.TagName));
        Assert.NotEmpty(release.Assets);
        Assert.Contains(release.Assets, a => a.Name.Contains("win_x64", StringComparison.OrdinalIgnoreCase) && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

        var foundAssets = service.FindAssets(release, assetPattern);
        Assert.NotEmpty(foundAssets);
        Assert.False(string.IsNullOrWhiteSpace(foundAssets[0].Version));
        Assert.Contains("1.3.", foundAssets[0].Version);
    }

    [Fact]
    public async System.Threading.Tasks.Task GetLatestReleaseAsync_MesenNightlyLink_ReturnsValidReleaseWithAssets()
    {
        var service = new GitHubReleaseService();
        var url = "https://nightly.link/nesdev-org/MesenCE/workflows/build/master/Mesen%20%28Windows%20-%20net10.0%20-%20AoT%29.zip";

        var release = await service.GetLatestReleaseAsync(url, System.Threading.CancellationToken.None);
        Assert.NotNull(release);
        Assert.NotEmpty(release.Assets);
        Assert.Contains("Mesen", release.Assets[0].Name);
    }

    [Fact]
    public async System.Threading.Tasks.Task GetLatestReleaseAsync_BizHawkNightlyLink_ReturnsValidReleaseWithAssets()
    {
        var service = new GitHubReleaseService();
        var url = "https://nightly.link/TASEmulators/BizHawk/workflows/ci/master/BizHawk-dev-windows.zip";

        var release = await service.GetLatestReleaseAsync(url, System.Threading.CancellationToken.None);
        Assert.NotNull(release);
        Assert.NotEmpty(release.Assets);
        Assert.Contains("BizHawk", release.Assets[0].Name);
        Assert.True(release.PublishedAt > DateTimeOffset.MinValue);

        var foundAssets = service.FindAssets(release, "");
        Assert.NotEmpty(foundAssets);
        Assert.False(string.IsNullOrWhiteSpace(foundAssets[0].Version));
    }
}
