using System;
using System.Linq;
using EmulatorAutoUpdater.Services;
using Xunit;

namespace EmulatorAutoUpdater.Tests;

public class OpenMsxNightlyParserTests
{
    private const string PageUrl = "https://openmsx.vampier.net/";

    // Trimmed copy of the real page layout (2026-11): newest row first, some rows lack Windows builds.
    private const string SampleHtml = """
        <table><thead><tr><th>Filename</th><th>Windows MSI</th><th>Windows Bin</th><th>Apple Silicon</th><th>Apple Intel</th><th>Build Log</th></tr></thead>
        <tbody>
        <tr class="latest">
            <td class="filename">
                openmsx-22.0-220-gac4fa06b8
                <span class="latest-label">LATEST</span>
            </td>
            <td><a class="download-link msi" href="downloads/openmsx-22.0-220-gac4fa06b8-windows-vc-x64-bin-msi.zip"><svg></svg><span>Download</span></a></td>
            <td><a class="download-link windows" href="downloads/openmsx-22.0-220-gac4fa06b8-windows-vc-x64-bin.zip"><svg></svg><span>Download</span></a></td>
            <td><a class="download-link silicon" href="downloads/openmsx-22.0-220-gac4fa06b8-mac-aarch64-bin.dmg"><svg></svg><span>Download</span></a></td>
            <td><a class="download-link intel" href="downloads/openmsx-22.0-220-gac4fa06b8-mac-x86_64-bin.dmg"><svg></svg><span>Download</span></a></td>
            <td><a class="log-link" href="https://github.com/openMSX/openMSX/actions/runs/43629740150" target="_blank" rel="noopener">View Log</a></td>
        </tr>
        <tr class="">
            <td class="filename">
                openmsx-22.0-218-g58e392ec3
            </td>
            <td><span class="missing">Unavailable</span></td>
            <td><span class="missing">Unavailable</span></td>
            <td><a class="download-link silicon" href="downloads/openmsx-22.0-218-g58e392ec3-mac-aarch64-bin.dmg"><svg></svg><span>Download</span></a></td>
            <td><a class="download-link intel" href="downloads/openmsx-22.0-218-g58e392ec3-mac-x86_64-bin.dmg"><svg></svg><span>Download</span></a></td>
            <td><a class="log-link" href="https://github.com/openMSX/openMSX/actions/runs/43625955155">View Log</a></td>
        </tr>
        <tr class="">
            <td class="filename">
                openmsx-22.0-216-gf3ee7fa18
            </td>
            <td><a class="download-link msi" href="downloads/openmsx-22.0-216-gf3ee7fa18-windows-vc-x64-bin-msi.zip"><svg></svg><span>Download</span></a></td>
            <td><a class="download-link windows" href="downloads/openmsx-22.0-216-gf3ee7fa18-windows-vc-x64-bin.zip"><svg></svg><span>Download</span></a></td>
            <td><span class="missing">Unavailable</span></td>
            <td><span class="missing">Unavailable</span></td>
            <td></td>
        </tr>
        </tbody></table>
        <div class="build-date">
            Latest build:
            2026-11-14T19:00:51Z    &nbsp; | &nbsp;
            Commit:
            ac4fa06b8b</div>
        """;

    [Theory]
    [InlineData("https://openmsx.vampier.net/", true)]
    [InlineData("https://openmsx.vampier.net", true)]
    [InlineData("https://OPENMSX.vampier.net/index.php", true)]
    [InlineData("https://openmsx.vampier.net/downloads/openmsx-22.0-220-gac4fa06b8-windows-vc-x64-bin.zip", false)]
    [InlineData("https://github.com/openMSX/openMSX", false)]
    [InlineData("", false)]
    public void IsOpenMsxNightlyUrl_DetectsNightlyPage(string url, bool expected)
    {
        Assert.Equal(expected, GitHubReleaseService.IsOpenMsxNightlyUrl(url));
    }

    [Fact]
    public void Parse_PicksNewestBuild_WithAllAssets_PortableZipFirst()
    {
        var release = GitHubReleaseService.ParseOpenMsxNightlyPage(SampleHtml, PageUrl, null);

        Assert.NotNull(release);
        Assert.Equal("22.0-220-gac4fa06b8", release!.TagName);
        Assert.Equal("openMSX Nightly Web", release.FetchSource);
        Assert.Equal(new DateTimeOffset(2026, 11, 14, 19, 0, 51, TimeSpan.Zero), release.PublishedAt);
        Assert.Equal(4, release.Assets.Count);
        Assert.Equal("openmsx-22.0-220-gac4fa06b8-windows-vc-x64-bin.zip", release.Assets[0].Name);
        Assert.Equal("openmsx-22.0-220-gac4fa06b8-windows-vc-x64-bin-msi.zip", release.Assets[1].Name);
        Assert.Equal(
            "https://openmsx.vampier.net/downloads/openmsx-22.0-220-gac4fa06b8-windows-vc-x64-bin.zip",
            release.Assets[0].BrowserDownloadUrl);
        Assert.Contains("Recent builds (3)", release.Body);
        Assert.Contains("openmsx-22.0-218-g58e392ec3 (no Windows build)", release.Body);
    }

    [Fact]
    public void Parse_SkipsBuildsWithoutMatchingAsset()
    {
        // Drop the newest row so the first row is the mac-only 218 build.
        var html = SampleHtml.Substring(SampleHtml.IndexOf("<tr class=\"\">", StringComparison.Ordinal));
        var release = GitHubReleaseService.ParseOpenMsxNightlyPage(html, PageUrl, @"(?i)windows-vc-x64-bin\.zip$");

        Assert.NotNull(release);
        Assert.Equal("22.0-216-gf3ee7fa18", release!.TagName);
        // Footer date belongs to a different (newer) commit, so it must not be attributed here.
        Assert.Equal(DateTimeOffset.MinValue, release.PublishedAt);

        var assets = new GitHubReleaseService().FindAssets(release, @"(?i)windows-vc-x64-bin\.zip$");
        var asset = Assert.Single(assets);
        Assert.Equal("openmsx-22.0-216-gf3ee7fa18-windows-vc-x64-bin.zip", asset.AssetName);
        Assert.Equal("22.0-216-gf3ee7fa18", asset.Version);
    }

    [Fact]
    public void Parse_ReturnsNullForUnrelatedHtml()
    {
        Assert.Null(GitHubReleaseService.ParseOpenMsxNightlyPage("<html><body>maintenance</body></html>", PageUrl, null));
    }
}
