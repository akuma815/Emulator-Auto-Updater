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
    public void ResolveVersion_FallsBackToDateOnlyWhenNoValidTag()
    {
        var date = new DateTimeOffset(2026, 7, 26, 10, 16, 0, TimeSpan.FromHours(9));
        var resolved = GitHubReleaseService.ResolveVersion("BizHawk-dev-windows.zip", "BizHawk-dev-windows.zip", date);
        Assert.Equal("2026-07-26 10:16", resolved);
    }
}
