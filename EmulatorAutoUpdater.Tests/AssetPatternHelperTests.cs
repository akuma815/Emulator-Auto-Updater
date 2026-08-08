using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using EmulatorAutoUpdater.Models;
using EmulatorAutoUpdater.Services;

namespace EmulatorAutoUpdater.Tests;

public class AssetPatternHelperTests
{
    [Theory]
    [InlineData("windows-x86_64.zip", "(?i).*(win|windows).*x86_64.*\\.zip$")]
    [InlineData("duckstation-windows-x86_64-release.zip", "(?i)duckstation.*(win|windows).*x86_64.*release.*\\.zip$")]
    [InlineData("Eden-Windows-5ec94b1971-amd64-clang-pgo.zip", "(?i)Eden.*(win|windows).*amd64.*clang.*pgo.*\\.zip$")]
    [InlineData("dolphin-master-5.0-21430-x64.7z", "(?i)dolphin.*master.*x64.*\\.7z$")]
    public void ConvertFilenameToAssetPattern_ShouldGenerateExpectedRegex(string input, string expectedPattern)
    {
        var result = AssetPatternHelper.ConvertFilenameToAssetPattern(input);
        Assert.Equal(expectedPattern, result);

        // Verify that the generated regex pattern actually matches the original input filename!
        var isMatched = Regex.IsMatch(input, result, RegexOptions.IgnoreCase);
        Assert.True(isMatched, $"Generated pattern '{result}' failed to match original input filename '{input}'");
    }

    [Fact]
    public void FindAssets_VariantPattern_ShouldDifferentiateBetweenClangPgoAndMsvc()
    {
        var service = new GitHubReleaseService();
        var release = new GitHubReleaseService.GitHubRelease
        {
            TagName = "5ec94b1971",
            Assets = new List<GitHubReleaseService.GitHubAsset>
            {
                new GitHubReleaseService.GitHubAsset { Name = "Eden-Windows-5ec94b1971-amd64-clang-pgo.zip", BrowserDownloadUrl = "http://example.com/clang-pgo" },
                new GitHubReleaseService.GitHubAsset { Name = "Eden-Windows-5ec94b1971-amd64-msvc.zip", BrowserDownloadUrl = "http://example.com/msvc" }
            }
        };

        var clangPgoPattern = AssetPatternHelper.ConvertFilenameToAssetPattern("Eden-Windows-5ec94b1971-amd64-clang-pgo.zip");
        var matchedClang = service.FindAssets(release, clangPgoPattern);

        Assert.Single(matchedClang);
        Assert.Equal("Eden-Windows-5ec94b1971-amd64-clang-pgo.zip", matchedClang.First().AssetName);
    }

    [Fact]
    public void FindAssets_NegativeLookahead_ShouldExcludeSymbolsAndSsl()
    {
        var service = new GitHubReleaseService();
        var release = new GitHubReleaseService.GitHubRelease
        {
            TagName = "v1.17.0",
            Assets = new List<GitHubReleaseService.GitHubAsset>
            {
                new GitHubReleaseService.GitHubAsset { Name = "duckstation-windows-x64-symbols.zip", BrowserDownloadUrl = "http://example.com/symbols" },
                new GitHubReleaseService.GitHubAsset { Name = "duckstation-windows-x64-release.zip", BrowserDownloadUrl = "http://example.com/release" }
            }
        };

        var pattern = "(?i)(?!.*symbol)duckstation.*(win|windows).*(x64).*\\.(zip)$";
        var matched = service.FindAssets(release, pattern);

        Assert.Single(matched);
        Assert.Equal("duckstation-windows-x64-release.zip", matched.First().AssetName);
    }

    [Fact]
    public void FriendlyExceptionHelper_ShouldFormatUserFriendlyErrorMessage()
    {
        var ex = new System.InvalidOperationException("지정된 바인딩 제약 조건과 일치하는 'EmulatorAutoUpdater.App' 형식에 대한 생성자 호출에서 예외가 throw되었습니다.");
        var formatted = FriendlyExceptionHelper.FormatUserFriendlyErrorMessage(ex, "하위 폴더 관리 창 열기");

        Assert.Contains("💡 [해결 가이드]", formatted);
        Assert.DoesNotContain("MarkupExtension", formatted);
    }

    [Theory]
    [InlineData("bios", false)]
    [InlineData("savestates", false)]
    [InlineData("cheats", false)]
    [InlineData("shaders", false)]
    [InlineData("SAK 64bit", false)]
    [InlineData("2.7.494", true)]
    [InlineData("2026-07-28_10-55", true)]
    [InlineData("v1.18.0", true)]
    [InlineData("1.3.340", true)]
    public void IsValidVersionSubfolder_ShouldFilterProtectedFolders(string folderName, bool expectedResult)
    {
        var result = SubfolderManagerWindow.IsValidVersionSubfolder(@"C:\FakeFolder\" + folderName, folderName, "1.3.340", "Ryujinx");
        Assert.Equal(expectedResult, result);
    }
}
