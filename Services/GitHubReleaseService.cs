using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using EmulatorAutoUpdater.Models;

namespace EmulatorAutoUpdater.Services;

public sealed class GitHubReleaseService
{
    private static readonly HttpClient HttpClient = new()
    {
        DefaultRequestVersion = new Version(2, 0)
    };

    public Task<GitHubRelease?> GetLatestReleaseAsync(string repository, CancellationToken cancellationToken)
        => GetLatestReleaseAsync(repository, null, cancellationToken);

    public async Task<GitHubRelease?> GetLatestReleaseAsync(string repository, string? assetPattern, CancellationToken cancellationToken)
    {
        if (IsFlycastBuildsUrl(repository))
        {
            return await GetLatestFlycastDevReleaseAsync(cancellationToken);
        }

        if (IsMelonDsNightliesUrl(repository))
        {
            return await GetLatestMelonDsNightlyReleaseAsync(repository, cancellationToken);
        }

        if (IsDolphinDownloadPageUrl(repository))
        {
            return await GetLatestDolphinDevelopmentReleaseAsync(repository, cancellationToken);
        }

        if (IsDirectDownloadUrl(repository))
        {
            return await GetLatestDirectDownloadReleaseAsync(repository, cancellationToken);
        }

        if (IsPpssppDevbuildsUrl(repository))
        {
            return await GetLatestPpssppDevbuildReleaseAsync(repository, cancellationToken);
        }

        if (IsDolphinBuildsApiUrl(repository))
        {
            return await GetLatestDolphinBuildsReleaseAsync(repository, cancellationToken);
        }

        if (IsOrphisBuildbotUrl(repository))
        {
            return await GetLatestOrphisBuildbotReleaseAsync(repository, cancellationToken);
        }

        var info = ParseRepositoryInfo(repository);
        if (info == null)
        {
            return null;
        }

        return info.Provider switch
        {
            RepositoryProvider.GitHub => await GetLatestGitHubReleaseAsync(info, assetPattern, cancellationToken),
            RepositoryProvider.Gitea => await GetLatestGiteaReleaseAsync(info, cancellationToken),
            _ => null
        };
    }

    private static bool IsMelonDsNightliesUrl(string repository)
    {
        if (string.IsNullOrWhiteSpace(repository) ||
            !Uri.TryCreate(repository.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        return string.Equals(uri.Host, "melonds.kuribo64.net", StringComparison.OrdinalIgnoreCase)
               && uri.AbsolutePath.Equals("/nightlies.php", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<GitHubRelease?> GetLatestMelonDsNightlyReleaseAsync(
        string repositoryUrl,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, repositoryUrl);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) EmulatorAutoUpdater/1.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

        using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        var latestCommitMatch = Regex.Match(
            html,
            @"<span\s+class=[""']entrytitle[""']>\s*Commit\s+(?<commit>[0-9a-f]+)\s*</span>(?<section>.*?)(?=<span\s+class=[""']entrytitle[""']>|\z)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        if (!latestCommitMatch.Success)
        {
            return null;
        }

        var commit = latestCommitMatch.Groups["commit"].Value;
        var section = latestCommitMatch.Groups["section"].Value;
        var windowsBuildMatch = Regex.Match(
            section,
            @"<a\s+[^>]*href=[""'](?<href>[^""']*melonDS-windows-x86_64\.zip)[""'][^>]*>",
            RegexOptions.IgnoreCase);

        if (!windowsBuildMatch.Success)
        {
            return null;
        }

        var relativeUrl = WebUtility.HtmlDecode(windowsBuildMatch.Groups["href"].Value);
        var downloadUrl = new Uri(new Uri(repositoryUrl), relativeUrl).ToString();
        var assetName = Path.GetFileName(new Uri(downloadUrl).LocalPath);
        var publishedAt = ParseMelonDsNightlyDate(section);

        return new GitHubRelease
        {
            TagName = commit,
            Name = $"melonDS Nightly {commit}",
            Body = $"Latest melonDS nightly Windows x86_64 build for commit {commit}.",
            PublishedAt = publishedAt,
            FetchSource = "melonDS Web",
            Assets = new List<GitHubAsset>
            {
                new()
                {
                    Name = assetName,
                    BrowserDownloadUrl = downloadUrl
                }
            }
        };
    }

    private static DateTimeOffset ParseMelonDsNightlyDate(string section)
    {
        var dateMatch = Regex.Match(
            section,
            @"<span\s+class=[""']entrydate[""']>\s*(?<date>[A-Za-z]+\s+\d{1,2}(?:st|nd|rd|th)\s+\d{4})",
            RegexOptions.IgnoreCase);

        if (!dateMatch.Success)
        {
            return DateTimeOffset.MinValue;
        }

        var normalizedDate = Regex.Replace(
            dateMatch.Groups["date"].Value,
            @"(?<=\d)(st|nd|rd|th)",
            string.Empty,
            RegexOptions.IgnoreCase);

        return DateTimeOffset.TryParse(
            normalizedDate,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var publishedAt)
            ? publishedAt
            : DateTimeOffset.MinValue;
    }

    private static bool IsFlycastBuildsUrl(string repository)
    {
        if (string.IsNullOrWhiteSpace(repository) ||
            !Uri.TryCreate(repository.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        return string.Equals(uri.Host, "flyinghead.github.io", StringComparison.OrdinalIgnoreCase)
               && uri.AbsolutePath.TrimEnd('/').Equals("/flycast-builds", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<GitHubRelease?> GetLatestFlycastDevReleaseAsync(CancellationToken cancellationToken)
    {
        const string bucketBaseUrl = "https://flycast-builds.s3.fr-par.scw.cloud/";
        const string windowsDevPrefix = "win/heads/dev";
        var listUrl = $"{bucketBaseUrl}?prefix={Uri.EscapeDataString(windowsDevPrefix)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, listUrl);
        request.Headers.UserAgent.ParseAdd("EmulatorAutoUpdater/1.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));

        using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);

        var latestBuild = document
            .Descendants()
            .Where(element => element.Name.LocalName == "Contents")
            .Select(element => new
            {
                Key = element.Elements().FirstOrDefault(child => child.Name.LocalName == "Key")?.Value,
                LastModifiedText = element.Elements().FirstOrDefault(child => child.Name.LocalName == "LastModified")?.Value
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Key)
                           && item.Key.StartsWith(windowsDevPrefix + "-", StringComparison.OrdinalIgnoreCase)
                           && item.Key.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                           && DateTimeOffset.TryParse(item.LastModifiedText, out _))
            .Select(item => new
            {
                Key = item.Key!,
                LastModified = DateTimeOffset.Parse(item.LastModifiedText!)
            })
            .OrderByDescending(item => item.LastModified)
            .FirstOrDefault();

        if (latestBuild == null)
        {
            return null;
        }

        var commitMatch = Regex.Match(latestBuild.Key, @"^win/heads/dev-(?<commit>[0-9a-f]+)/", RegexOptions.IgnoreCase);
        var commit = commitMatch.Success ? commitMatch.Groups["commit"].Value : string.Empty;
        var shortCommit = commit.Length > 7 ? commit[..7] : commit;
        var assetName = Path.GetFileName(latestBuild.Key);
        var downloadUrl = bucketBaseUrl + string.Join("/", latestBuild.Key.Split('/').Select(Uri.EscapeDataString));

        return new GitHubRelease
        {
            TagName = string.IsNullOrWhiteSpace(shortCommit) ? "dev" : $"dev-{shortCommit}",
            Name = "Flycast DEV Windows x64",
            Body = $"Flycast DEV Windows build{(string.IsNullOrWhiteSpace(commit) ? string.Empty : $" (commit {commit})")}",
            PublishedAt = latestBuild.LastModified,
            FetchSource = "Flycast Web",
            Assets = new List<GitHubAsset>
            {
                new()
                {
                    Name = assetName,
                    BrowserDownloadUrl = downloadUrl
                }
            }
        };
    }

    private static bool IsPpssppDevbuildsUrl(string repository)
    {
        if (string.IsNullOrWhiteSpace(repository))
        {
            return false;
        }

        if (!Uri.TryCreate(repository.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        return (string.Equals(uri.Host, "www.ppsspp.org", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(uri.Host, "ppsspp.org", StringComparison.OrdinalIgnoreCase))
               && uri.AbsolutePath.StartsWith("/devbuilds", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDolphinDownloadPageUrl(string repository)
    {
        if (string.IsNullOrWhiteSpace(repository))
        {
            return false;
        }

        var trimmed = repository.Trim();
        return trimmed.Contains("dolphin-emu.org", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains("r.jina.ai", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<GitHubRelease?> GetLatestDolphinDevelopmentReleaseAsync(
        string repositoryUrl,
        CancellationToken cancellationToken)
    {
        var html = await GetDolphinDownloadPageHtmlAsync(repositoryUrl, cancellationToken);
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var latestWindowsBuild = Regex.Match(
            html,
            @"(?<url>https?://dl\.dolphin-emu\.org/builds/[^\s""'<>()\]]+/dolphin-master-(?<version>[^/\s""'<>()\]]+)-x64\.7z)",
            RegexOptions.IgnoreCase);

        if (!latestWindowsBuild.Success)
        {
            return null;
        }

        var version = WebUtility.HtmlDecode(latestWindowsBuild.Groups["version"].Value).Trim();
        var downloadUrl = WebUtility.HtmlDecode(latestWindowsBuild.Groups["url"].Value);
        var assetName = Path.GetFileName(new Uri(downloadUrl).LocalPath);
        var publishedAt = ParseDolphinBuildDate(html, latestWindowsBuild.Index);

        return new GitHubRelease
        {
            TagName = version,
            Name = $"Dolphin Development {version}",
            Body = $"Latest Dolphin Development Version Windows x64 build ({version}).",
            PublishedAt = publishedAt,
            FetchSource = "Dolphin Web",
            Assets = new List<GitHubAsset>
            {
                new()
                {
                    Name = assetName,
                    BrowserDownloadUrl = downloadUrl
                }
            }
        };
    }

    private static DateTimeOffset ParseDolphinBuildDate(string content, int buildLinkIndex)
    {
        var precedingContent = content[..buildLinkIndex];
        var dateMatches = Regex.Matches(
            precedingContent,
            @"title=[""'](?<date>\d{4}-\d{2}-\d{2}T[^""']+)[""']",
            RegexOptions.IgnoreCase);

        if (dateMatches.Count > 0 &&
            DateTimeOffset.TryParse(
                dateMatches[^1].Groups["date"].Value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var publishedAt))
        {
            return publishedAt;
        }

        return DateTimeOffset.MinValue;
    }

    private async Task<string?> GetDolphinDownloadPageHtmlAsync(
        string repositoryUrl,
        CancellationToken cancellationToken)
    {
        var target = repositoryUrl.Trim();
        var jinaUrl = target.StartsWith("https://r.jina.ai/", StringComparison.OrdinalIgnoreCase)
            ? target
            : "https://r.jina.ai/http://dolphin-emu.org/download/";

        const string directUrl = "https://dolphin-emu.org/download/";

        var urls = new[]
        {
            jinaUrl,
            target,
            directUrl,
            DolphinProxyFormats[0] + WebUtility.UrlEncode(directUrl),
            DolphinProxyFormats[1] + directUrl,
            DolphinProxyFormats[2] + WebUtility.UrlEncode(directUrl)
        };

        foreach (var url in urls)
        {
            using var perUrlCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            perUrlCts.CancelAfter(TimeSpan.FromSeconds(6));

            using var request = new HttpRequestMessage(HttpMethod.Get, url)
            {
                Version = new Version(1, 1)
            };
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) EmulatorAutoUpdater/1.0");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

            try
            {
                using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, perUrlCts.Token);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                var html = await response.Content.ReadAsStringAsync(perUrlCts.Token);
                if (Regex.IsMatch(
                        html,
                        @"https?://dl\.dolphin-emu\.org/builds/[^\s""'<>()\]]+/dolphin-master-[^/\s""'<>()\]]+-x64\.7z",
                        RegexOptions.IgnoreCase) &&
                    !html.Contains("Bunny Shield", StringComparison.OrdinalIgnoreCase))
                {
                    return html;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Try the next page source.
            }
        }

        return null;
    }

    private static bool IsDolphinBuildsApiUrl(string repository)
    {
        if (string.IsNullOrWhiteSpace(repository))
        {
            return false;
        }

        if (!Uri.TryCreate(repository.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Host.Contains("dolphin-emu.org", StringComparison.OrdinalIgnoreCase)
               && uri.AbsolutePath.Contains("/download/list/json", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOrphisBuildbotUrl(string repository)
    {
        if (string.IsNullOrWhiteSpace(repository))
        {
            return false;
        }

        if (!Uri.TryCreate(repository.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        return string.Equals(uri.Host, "buildbot.orphis.net", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<GitHubRelease?> GetLatestOrphisBuildbotReleaseAsync(string repositoryUrl, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, repositoryUrl);
        request.Headers.UserAgent.ParseAdd("EmulatorAutoUpdater/1.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

        using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        var assets = await CollectBuildbotAssetsAsync(repositoryUrl, html, cancellationToken);
        if (assets.Count == 0)
        {
            return null;
        }

        var latestAsset = assets.OrderByDescending(asset => asset.Name, StringComparer.OrdinalIgnoreCase).First();
        return new GitHubRelease
        {
            TagName = latestAsset.Name,
            Name = latestAsset.Name,
            Body = string.Empty,
            PublishedAt = ParseHtmlPublishedDate(html),
            Assets = assets.ToList()
        };
    }

    private async Task<GitHubRelease?> GetLatestPpssppDevbuildReleaseAsync(string repositoryUrl, CancellationToken cancellationToken)
    {
        const string historyJsonUrl = "https://builds.ppsspp.org/meta/history-20.json";

        using var request = new HttpRequestMessage(HttpMethod.Get, historyJsonUrl);
        request.Headers.UserAgent.ParseAdd("EmulatorAutoUpdater/1.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var history = await JsonSerializer.DeserializeAsync<List<PpssppHistoryEntry>>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (history == null || history.Count == 0)
        {
            return null;
        }

        var latest = history.FirstOrDefault(entry => entry.Builds != null && entry.Builds.Count > 0)
                     ?? history.First();

        var assets = CreatePpssppAssets(latest);
        if (assets.Count == 0)
        {
            return null;
        }

        return new GitHubRelease
        {
            TagName = latest.Description ?? latest.HashShort ?? string.Empty,
            Name = latest.Description ?? latest.HashShort ?? string.Empty,
            Body = latest.Message ?? string.Empty,
            PublishedAt = ParseDateTimeOffset(latest.Date),
            FetchSource = "PPSSPP Devbuild Web",
            Assets = assets.ToList()
        };
    }

    private static IReadOnlyList<GitHubAsset> CreatePpssppAssets(PpssppHistoryEntry entry)
    {
        var assets = new List<GitHubAsset>();
        if (entry.Builds == null)
        {
            return assets;
        }

        foreach (var platformFiles in entry.Builds)
        {
            if (platformFiles.Value == null)
            {
                continue;
            }

            foreach (var fileName in platformFiles.Value)
            {
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    continue;
                }

                assets.Add(new GitHubAsset
                {
                    Name = fileName,
                    BrowserDownloadUrl = $"https://builds.ppsspp.org/builds/{entry.Description}/{fileName}"
                });
            }
        }

        return assets;
    }

    private static DateTimeOffset ParseDateTimeOffset(string? date)
    {
        if (string.IsNullOrWhiteSpace(date))
        {
            return DateTimeOffset.MinValue;
        }

        return DateTimeOffset.TryParse(date, out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;
    }

    private sealed record PpssppHistoryEntry(
        [property: JsonPropertyName("hash")] string? Hash,
        [property: JsonPropertyName("hash_short")] string? HashShort,
        [property: JsonPropertyName("message")] string? Message,
        [property: JsonPropertyName("tag")] string? Tag,
        [property: JsonPropertyName("revs_since_tag")] int? RevsSinceTag,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("date")] string? Date,
        [property: JsonPropertyName("builds")] Dictionary<string, string[]?>? Builds
    );

    public static bool IsDirectDownloadUrl(string repository)
    {
        if (string.IsNullOrWhiteSpace(repository))
        {
            return false;
        }

        if (!Uri.TryCreate(repository.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        var path = uri.AbsolutePath.ToLowerInvariant();
        if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return uri.Host.Contains("desmume", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<GitHubRelease?> GetLatestDirectDownloadReleaseAsync(string repositoryUrl, CancellationToken cancellationToken)
    {
        using var response = await GetDirectDownloadResponseAsync(repositoryUrl, cancellationToken);
        if (response == null)
        {
            return null;
        }

        var downloadUrl = response.RequestMessage?.RequestUri?.ToString() ?? repositoryUrl;
        var assetName = GetAssetNameFromResponse(response) ?? Path.GetFileName(new Uri(downloadUrl).LocalPath);
        if (string.IsNullOrWhiteSpace(assetName))
        {
            assetName = "download.zip";
        }

        var publishedAt = response.Content.Headers.LastModified ??
                          DateTimeOffset.MinValue;

        if (repositoryUrl.StartsWith("https://nightly.link", StringComparison.OrdinalIgnoreCase))
        {
            var commitDate = await FetchNightlyLinkCommitDateAsync(repositoryUrl, cancellationToken);
            if (commitDate.HasValue)
            {
                publishedAt = commitDate.Value;
            }
        }

        var asset = new GitHubAsset
        {
            Name = assetName,
            BrowserDownloadUrl = downloadUrl
        };

        return new GitHubRelease
        {
            TagName = asset.Name,
            Name = asset.Name,
            Body = string.Empty,
            PublishedAt = publishedAt,
            Assets = new List<GitHubAsset> { asset }
        };
    }

    private async Task<HttpResponseMessage?> GetDirectDownloadResponseAsync(string repositoryUrl, CancellationToken cancellationToken)
    {
        HttpResponseMessage? response = null;
        try
        {
            var headRequest = new HttpRequestMessage(HttpMethod.Head, repositoryUrl);
            headRequest.Headers.UserAgent.ParseAdd("EmulatorAutoUpdater/1.0");
            headRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

            response = await HttpClient.SendAsync(headRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            if (response.StatusCode != System.Net.HttpStatusCode.MethodNotAllowed &&
                response.StatusCode != System.Net.HttpStatusCode.NotImplemented &&
                response.StatusCode != System.Net.HttpStatusCode.NotFound &&
                response.StatusCode != System.Net.HttpStatusCode.Forbidden)
            {
                response.Dispose();
                return null;
            }

            response.Dispose();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            response?.Dispose();
            throw;
        }
        catch
        {
            response?.Dispose();
        }

        try
        {
            var getRequest = new HttpRequestMessage(HttpMethod.Get, repositoryUrl);
            getRequest.Headers.UserAgent.ParseAdd("EmulatorAutoUpdater/1.0");
            getRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

            var getResponse = await HttpClient.SendAsync(getRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!getResponse.IsSuccessStatusCode)
            {
                getResponse.Dispose();
                return null;
            }

            return getResponse;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static string? GetAssetNameFromResponse(HttpResponseMessage response)
    {
        if (response.Content.Headers.ContentDisposition?.FileNameStar != null)
        {
            return response.Content.Headers.ContentDisposition.FileNameStar.Trim('"');
        }

        if (response.Content.Headers.ContentDisposition?.FileName != null)
        {
            return response.Content.Headers.ContentDisposition.FileName.Trim('"');
        }

        return null;
    }

    private static IReadOnlyList<GitHubAsset> ParsePpssppDevbuildWindowsAssets(string html)
    {
        var buildSections = Regex.Matches(html, "<h2[^>]*>(?<version>[^<]+)</h2>(?<section>.*?)(?=<h2|$)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        foreach (Match sectionMatch in buildSections)
        {
            var sectionHtml = sectionMatch.Groups["section"].Value;
            if (!Regex.IsMatch(sectionHtml, "Windows:", RegexOptions.IgnoreCase))
            {
                continue;
            }

            var links = ExtractHrefValues(sectionHtml);
            var windowsAssets = new List<GitHubAsset>();
            foreach (var href in links)
            {
                var name = Path.GetFileName(href);
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                if (Regex.IsMatch(name, "(?i)(ppsspp_win_|PPSSPPSetup_|PPSSPPWindowsARM64_|ppsspp_uwp_).*\\.(zip|exe)$"))
                {
                    windowsAssets.Add(new GitHubAsset
                    {
                        Name = name,
                        BrowserDownloadUrl = href
                    });
                }
            }

            if (windowsAssets.Count > 0)
            {
                return windowsAssets;
            }
        }

        return Array.Empty<GitHubAsset>();
    }

    private async Task<IReadOnlyList<GitHubAsset>> CollectBuildbotAssetsAsync(string baseUrl, string html, CancellationToken cancellationToken, int depth = 0)
    {
        var assets = ParseBuildbotAssetLinks(baseUrl, html);
        if (assets.Count > 0 || depth >= 2)
        {
            return assets;
        }

        var directories = ParseBuildbotDirectoryLinks(baseUrl, html);
        foreach (var directoryUrl in directories)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, directoryUrl);
                request.Headers.UserAgent.ParseAdd("EmulatorAutoUpdater/1.0");
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

                using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();

                var nestedHtml = await response.Content.ReadAsStringAsync(cancellationToken);
                var nestedAssets = await CollectBuildbotAssetsAsync(directoryUrl, nestedHtml, cancellationToken, depth + 1);
                if (nestedAssets.Count > 0)
                {
                    assets = assets.Concat(nestedAssets).ToList();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // ignore invalid directories
            }
        }

        return assets;
    }

    private static IReadOnlyList<GitHubAsset> ParseBuildbotAssetLinks(string baseUrl, string html)
    {
        var links = ExtractHrefValues(html);
        var assets = new List<GitHubAsset>();
        foreach (var href in links)
        {
            if (string.IsNullOrWhiteSpace(href) || href == "../")
            {
                continue;
            }

            if (!IsBuildbotFileLink(href))
            {
                continue;
            }

            if (Uri.TryCreate(href, UriKind.Absolute, out var absoluteUri))
            {
                assets.Add(new GitHubAsset { Name = Path.GetFileName(absoluteUri.LocalPath), BrowserDownloadUrl = absoluteUri.ToString() });
            }
            else if (Uri.TryCreate(new Uri(baseUrl), href, out var resolvedUri))
            {
                assets.Add(new GitHubAsset { Name = Path.GetFileName(resolvedUri.LocalPath), BrowserDownloadUrl = resolvedUri.ToString() });
            }
        }

        return assets;
    }

    private static readonly string[] DolphinProxyFormats =
    {
        "https://api.allorigins.win/raw?url=",
        "https://thingproxy.freeboard.io/fetch/",
        "https://api.codetabs.com/v1/proxy?quest="
    };

    private async Task<GitHubRelease?> GetLatestDolphinBuildsReleaseAsync(string apiUrl, CancellationToken cancellationToken)
    {
        using var response = await GetDolphinApiResponseAsync(apiUrl, cancellationToken);
        if (response == null)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, default, cancellationToken);
        var buildItems = ExtractDolphinBuildItems(document.RootElement);

        if (buildItems.Count == 0)
        {
            return null;
        }

        var assets = new List<GitHubAsset>();
        foreach (var item in buildItems)
        {
            var assetUrl = item.Url ?? item.DownloadUrl ?? item.File ?? item.AssetUrl;
            if (string.IsNullOrWhiteSpace(assetUrl))
            {
                continue;
            }

            if (Uri.TryCreate(assetUrl, UriKind.Absolute, out var uri))
            {
                var assetName = Path.GetFileName(uri.LocalPath);
                if (!string.IsNullOrWhiteSpace(assetName) && !assets.Any(a => a.BrowserDownloadUrl == assetUrl))
                {
                    assets.Add(new GitHubAsset
                    {
                        Name = assetName,
                        BrowserDownloadUrl = assetUrl
                    });
                }
            }
        }

        if (assets.Count == 0)
        {
            return null;
        }

        var masterBuild = buildItems
            .Where(item => string.Equals(item.Branch, "master", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.TimeCreated)
            .FirstOrDefault() ?? buildItems.OrderByDescending(item => item.TimeCreated).First();

        var masterAssetUrl = masterBuild.Url ?? masterBuild.DownloadUrl ?? masterBuild.File ?? masterBuild.AssetUrl;
        var latestVersion = masterBuild.Name ?? (masterAssetUrl != null ? Path.GetFileName(new Uri(masterAssetUrl).LocalPath) : assets.First().Name);

        return new GitHubRelease
        {
            TagName = latestVersion,
            Name = $"Dolphin Development {latestVersion}",
            Body = masterBuild.Description ?? "Dolphin Development Build",
            PublishedAt = masterBuild.TimeCreated != default ? masterBuild.TimeCreated : DateTimeOffset.MinValue,
            Assets = assets
        };
    }

    private async Task<HttpResponseMessage?> GetDolphinApiResponseAsync(string apiUrl, CancellationToken cancellationToken)
    {
        var response = await SendDolphinRequestAsync(apiUrl, cancellationToken);
        if (response != null)
        {
            return response;
        }

        foreach (var proxyFormat in DolphinProxyFormats)
        {
            var proxyUrl = $"{proxyFormat}{WebUtility.UrlEncode(apiUrl)}";
            response = await SendDolphinRequestAsync(proxyUrl, cancellationToken);
            if (response != null)
            {
                return response;
            }
        }

        return null;
    }

    private async Task<HttpResponseMessage?> SendDolphinRequestAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url)
        {
            Version = new Version(1, 1)
        };

        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/123.0.0.0 Safari/537.36");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue("en-US"));
        request.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue("en", 0.9));
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("deflate"));
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));
        request.Headers.TryAddWithoutValidation("Referer", "https://dolphin-emu.org/");
        request.Headers.TryAddWithoutValidation("Origin", "https://dolphin-emu.org");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");

        try
        {
            var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                response.Dispose();
                return null;
            }

            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static List<DolphinBuildItem> ExtractDolphinBuildItems(JsonElement element)
    {
        var items = new List<DolphinBuildItem>();
        CollectDolphinBuildItems(element, items);
        return items;
    }

    private static void CollectDolphinBuildItems(JsonElement element, List<DolphinBuildItem> items)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                CollectDolphinBuildItems(child, items);
            }

            return;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var branch = GetString(element, "branch");
        var url = GetString(element, "url");
        var downloadUrl = GetString(element, "download_url");
        var file = GetString(element, "file");
        var assetUrl = GetString(element, "asset_url") ?? GetString(element, "downloadUrl");
        var name = GetString(element, "name") ?? GetString(element, "filename") ?? GetString(element, "file_name");
        var description = GetString(element, "description") ?? GetString(element, "notes");
        var createdAt = ParseDateTimeOffset(GetString(element, "created_at") ?? GetString(element, "created") ?? GetString(element, "date") ?? GetString(element, "time"));

        if (!string.IsNullOrWhiteSpace(branch) || !string.IsNullOrWhiteSpace(url) || !string.IsNullOrWhiteSpace(downloadUrl) || !string.IsNullOrWhiteSpace(file) || !string.IsNullOrWhiteSpace(assetUrl))
        {
            items.Add(new DolphinBuildItem(name, branch, url, downloadUrl, file, assetUrl, description, createdAt));
        }

        foreach (var property in element.EnumerateObject())
        {
            CollectDolphinBuildItems(property.Value, items);
        }
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private sealed record DolphinBuildItem(
        string? Name,
        string? Branch,
        string? Url,
        string? DownloadUrl,
        string? File,
        string? AssetUrl,
        string? Description,
        DateTimeOffset TimeCreated
    );

    private static IReadOnlyList<string> ParseBuildbotDirectoryLinks(string baseUrl, string html)
    {
        var links = ExtractHrefValues(html);
        var directories = new List<string>();
        foreach (var href in links)
        {
            if (string.IsNullOrWhiteSpace(href) || href == "../")
            {
                continue;
            }

            if (IsBuildbotFileLink(href))
            {
                continue;
            }

            if (href.EndsWith("/", StringComparison.Ordinal))
            {
                if (Uri.TryCreate(href, UriKind.Absolute, out var absoluteUri))
                {
                    directories.Add(absoluteUri.ToString());
                }
                else if (Uri.TryCreate(new Uri(baseUrl), href, out var resolvedUri))
                {
                    directories.Add(resolvedUri.ToString());
                }
            }
        }

        return directories;
    }

    private static IReadOnlyList<string> ExtractHrefValues(string html)
    {
        var hrefs = new List<string>();
        var matches = Regex.Matches(html, "<a\\s+[^>]*href=[\"']?([^\"' >]+)[\"']?", RegexOptions.IgnoreCase);
        foreach (Match match in matches)
        {
            if (match.Groups.Count > 1)
            {
                hrefs.Add(match.Groups[1].Value);
            }
        }

        return hrefs;
    }

    private static bool IsBuildbotFileLink(string href)
    {
        return Regex.IsMatch(href, "\\.(zip|7z|tar\\.gz|tgz|exe)$", RegexOptions.IgnoreCase);
    }

    public IReadOnlyList<BuildAsset> FindAssets(GitHubRelease release, string assetPattern)
    {
        if (release == null)
        {
            return Array.Empty<BuildAsset>();
        }

        var version = CleanReleaseVersion(release.TagName ?? release.Name ?? string.Empty);

        if (!string.IsNullOrWhiteSpace(assetPattern))
        {
            var assets = MatchAssets(release.Assets, assetPattern);
            return assets.Select(asset => CreateBuildAsset(asset, version, release.PublishedAt)).ToList();
        }

        return release.Assets.Select(asset => CreateBuildAsset(asset, version, release.PublishedAt)).ToList();
    }

    private static IReadOnlyList<GitHubAsset> MatchAssets(IReadOnlyList<GitHubAsset> assets, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return assets;
        }

        var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
        return assets.Where(asset => regex.IsMatch(asset.Name)).ToList();
    }

    private static async Task<DateTimeOffset?> FetchNightlyLinkCommitDateAsync(string nightlyUrl, CancellationToken cancellationToken)
    {
        try
        {
            if (!Uri.TryCreate(nightlyUrl, UriKind.Absolute, out var uri))
            {
                return null;
            }

            var parts = uri.AbsolutePath.Trim('/').Split('/');
            if (parts.Length < 5)
            {
                return null;
            }

            var owner = parts[0];
            var repo = parts[1];
            var branch = parts[4];

            var apiUrl = $"https://api.github.com/repos/{owner}/{repo}/commits/{branch}";
            using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
            request.Headers.UserAgent.ParseAdd("EmulatorAutoUpdater/1.0");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = doc.RootElement;

            if (root.TryGetProperty("commit", out var commitObj) &&
                commitObj.TryGetProperty("committer", out var committerObj) &&
                committerObj.TryGetProperty("date", out var dateProp) &&
                dateProp.ValueKind == JsonValueKind.String)
            {
                return DateTimeOffset.Parse(dateProp.GetString()!);
            }
        }
        catch
        {
        }

        return null;
    }

    private static BuildAsset CreateBuildAsset(GitHubAsset asset, string version, DateTimeOffset publishedAt)
    {
        var localPublishedAt = publishedAt > DateTimeOffset.MinValue ? publishedAt.ToLocalTime() : DateTimeOffset.MinValue;
        var resolvedVersion = ResolveVersion(version, asset.Name, localPublishedAt);
        return new BuildAsset
        {
            Version = resolvedVersion,
            PublishedAt = localPublishedAt,
            AssetName = asset.Name,
            DownloadUrl = asset.BrowserDownloadUrl
        };
    }

    public static string ResolveVersion(string? rawVersion, string assetName, DateTimeOffset publishedAt)
    {
        var cleanRaw = rawVersion?.Trim().TrimStart('v', 'V') ?? string.Empty;
        var nameWithoutExt = Path.GetFileNameWithoutExtension(assetName);

        if (string.Equals(cleanRaw, assetName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(cleanRaw, nameWithoutExt, StringComparison.OrdinalIgnoreCase))
        {
            cleanRaw = string.Empty;
        }

        if (!IsValidVersionTag(cleanRaw))
        {
            var extracted = ExtractRealVersionNumber(assetName);
            if (!string.IsNullOrWhiteSpace(extracted))
            {
                cleanRaw = extracted;
            }
        }

        if (string.IsNullOrWhiteSpace(cleanRaw) || !IsValidVersionTag(cleanRaw))
        {
            return publishedAt > DateTimeOffset.MinValue
                ? publishedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                : string.Empty;
        }

        return cleanRaw;
    }

    public static bool IsValidVersionTag(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return false;

        var clean = v.Trim().TrimStart('v', 'V');
        if (string.IsNullOrWhiteSpace(clean)) return false;

        var lower = clean.ToLowerInvariant();
        if (lower is "latest" or "nightly" or "dev" or "canary" or "builds" or "main" or "master" or "release" or "unknown" or "x64" or "win64" or "x86" or "amd64" or "arm64" or "windows")
        {
            return false;
        }

        return Regex.IsMatch(clean, @"[0-9a-fA-F]");
    }

    private static string? ExtractRealVersionNumber(string assetName)
    {
        var match = Regex.Match(assetName, @"\b\d+[\.-]\d+([\.-]\d+)?(-\d+)?\b");
        if (match.Success)
        {
            return match.Value;
        }

        var buildNumberMatch = Regex.Match(assetName, @"-(\d+-\d+)-x64", RegexOptions.IgnoreCase);
        if (buildNumberMatch.Success)
        {
            return buildNumberMatch.Groups[1].Value;
        }

        return null;
    }

    private enum RepositoryProvider
    {
        GitHub,
        Gitea,
        Unknown
    }

    private sealed record RepositoryInfo(RepositoryProvider Provider, string Host, string Owner, string Repo, string ApiBaseUrl);

    private static RepositoryInfo? ParseRepositoryInfo(string repository)
    {
        if (string.IsNullOrWhiteSpace(repository))
        {
            return null;
        }

        repository = repository.Trim();
        if (Uri.TryCreate(repository, UriKind.Absolute, out var uri))
        {
            var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (IsGitHubHost(uri.Host))
            {
                if (segments.Length >= 4 && string.Equals(segments[0], "repos", StringComparison.OrdinalIgnoreCase))
                {
                    return new RepositoryInfo(RepositoryProvider.GitHub, uri.Host, segments[1], segments[2], "https://api.github.com");
                }

                if (segments.Length >= 2)
                {
                    return new RepositoryInfo(RepositoryProvider.GitHub, uri.Host, segments[0], segments[1], "https://api.github.com");
                }

                return null;
            }

            if (segments.Length >= 5 && string.Equals(segments[0], "api", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(segments[1], "v1", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(segments[2], "repos", StringComparison.OrdinalIgnoreCase))
            {
                return new RepositoryInfo(RepositoryProvider.Gitea, uri.Host, segments[3], segments[4], $"{uri.Scheme}://{uri.Host}/api/v1");
            }

            if (segments.Length >= 2)
            {
                return new RepositoryInfo(RepositoryProvider.Gitea, uri.Host, segments[0], segments[1], $"{uri.Scheme}://{uri.Host}/api/v1");
            }

            return null;
        }

        var parts = repository.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2)
        {
            return new RepositoryInfo(RepositoryProvider.GitHub, "github.com", parts[0], parts[1], "https://api.github.com");
        }

        return null;
    }

    private static bool IsGitHubHost(string host)
    {
        return string.Equals(host, "github.com", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(host, "api.github.com", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<GitHubRelease?> GetLatestGitHubReleaseAsync(RepositoryInfo info, string? assetPattern, CancellationToken cancellationToken)
    {
        var candidates = new List<GitHubReleaseResponse>();

        // 1. Query /repos/{owner}/{repo}/releases/latest (Official Latest Stable Release)
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{info.ApiBaseUrl}/repos/{info.Owner}/{info.Repo}/releases/latest");
            request.Headers.UserAgent.ParseAdd("EmulatorAutoUpdater/1.0");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                await using var latestStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var apiRelease = await JsonSerializer.DeserializeAsync<GitHubReleaseResponse>(latestStream, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                }, cancellationToken);

                if (apiRelease != null)
                {
                    candidates.Add(apiRelease);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Fallback to /releases list
        }

        // 2. Query /repos/{owner}/{repo}/releases list (Includes Prereleases / Nightly Builds like PCSX2 v2.7.x)
        try
        {
            using var listRequest = new HttpRequestMessage(HttpMethod.Get, $"{info.ApiBaseUrl}/repos/{info.Owner}/{info.Repo}/releases");
            listRequest.Headers.UserAgent.ParseAdd("EmulatorAutoUpdater/1.0");
            listRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var listResponse = await HttpClient.SendAsync(listRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (listResponse.IsSuccessStatusCode)
            {
                await using var stream = await listResponse.Content.ReadAsStreamAsync(cancellationToken);
                var releases = await JsonSerializer.DeserializeAsync<List<GitHubReleaseResponse>>(stream, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                }, cancellationToken);

                if (releases != null && releases.Count > 0)
                {
                    candidates.AddRange(releases);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Fallback to HTML scraper
        }

        // 3. Filter candidates by assetPattern (if specified) and select the release with the LATEST PublishedAt date!
        if (candidates.Count > 0)
        {
            Regex? regex = null;
            if (!string.IsNullOrWhiteSpace(assetPattern))
            {
                try
                {
                    regex = new Regex(assetPattern, RegexOptions.IgnoreCase);
                }
                catch { }
            }

            GitHubReleaseResponse? bestRelease = null;
            if (regex != null)
            {
                bestRelease = candidates
                    .Where(r => r.Assets != null && r.Assets.Any(a => regex.IsMatch(a.Name)))
                    .OrderByDescending(r => r.PublishedAt)
                    .FirstOrDefault();
            }

            bestRelease ??= candidates
                .Where(r => r.Assets != null && r.Assets.Count > 0)
                .OrderByDescending(r => r.PublishedAt)
                .FirstOrDefault() ?? candidates.OrderByDescending(r => r.PublishedAt).First();

            return new GitHubRelease
            {
                TagName = bestRelease.TagName ?? bestRelease.Name,
                Name = bestRelease.Name,
                Body = bestRelease.Body ?? string.Empty,
                PublishedAt = bestRelease.PublishedAt,
                FetchSource = "GitHub REST API",
                Assets = bestRelease.Assets?.Select(asset => new GitHubAsset
                {
                    Name = asset.Name,
                    BrowserDownloadUrl = asset.BrowserDownloadUrl
                }).ToList() ?? new List<GitHubAsset>()
            };
        }

        // 4. Fallback to HTML Web Scraper (Bypasses GitHub 60 req/hr API Rate Limits 100%)
        return await GetLatestGitHubReleaseFromHtmlAsync(info, assetPattern, cancellationToken);
    }

    private async Task<GitHubRelease?> GetLatestGitHubReleaseFromHtmlAsync(RepositoryInfo info, string? assetPattern, CancellationToken cancellationToken)
    {
        // 1. Try GET https://github.com/{owner}/{repo}/releases/latest with HTTP redirect
        try
        {
            var latestUrl = $"https://github.com/{info.Owner}/{info.Repo}/releases/latest";
            using var latestRequest = new HttpRequestMessage(HttpMethod.Get, latestUrl);
            latestRequest.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) EmulatorAutoUpdater/1.0");
            latestRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

            using var latestResponse = await HttpClient.SendAsync(latestRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (latestResponse.IsSuccessStatusCode)
            {
                var finalUrl = latestResponse.RequestMessage?.RequestUri?.ToString() ?? string.Empty;
                var tagMatch = Regex.Match(finalUrl, @"/tag/(?<tag>[^""'/\s?#]+)", RegexOptions.IgnoreCase);

                string? tagName = tagMatch.Success ? WebUtility.HtmlDecode(tagMatch.Groups["tag"].Value) : null;
                var html = await latestResponse.Content.ReadAsStringAsync(cancellationToken);

                if (string.IsNullOrWhiteSpace(tagName))
                {
                    var htmlTagMatch = Regex.Match(html, $@"{info.Owner}/{info.Repo}/releases/tag/(?<tag>[^""'/\s?#]+)", RegexOptions.IgnoreCase);
                    if (htmlTagMatch.Success)
                    {
                        tagName = WebUtility.HtmlDecode(htmlTagMatch.Groups["tag"].Value);
                    }
                }

                if (!string.IsNullOrWhiteSpace(tagName))
                {
                    var assets = await FetchExpandedAssetsForTagAsync(info.Owner, info.Repo, tagName, html, cancellationToken);

                    // Check if latest release contains assets matching assetPattern
                    bool isMatched = true;
                    if (!string.IsNullOrWhiteSpace(assetPattern))
                    {
                        try
                        {
                            var regex = new Regex(assetPattern, RegexOptions.IgnoreCase);
                            isMatched = assets.Any(a => regex.IsMatch(a.Name));
                        }
                        catch { }
                    }

                    if (isMatched)
                    {
                        var parsedDate = ParseHtmlPublishedDate(html);
                        return new GitHubRelease
                        {
                            TagName = tagName,
                            Name = $"Release {tagName}",
                            Body = $"GitHub Release {tagName} (웹 파싱 - latest 리다이렉트).",
                            PublishedAt = parsedDate,
                            FetchSource = "웹 파싱 (latest 리다이렉트)",
                            Assets = assets
                        };
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Fallback to releases page
        }

        // 2. Fallback: Parse recent tags on https://github.com/{owner}/{repo}/releases page to find tag matching assetPattern
        var htmlUrl = $"https://github.com/{info.Owner}/{info.Repo}/releases";
        using var request = new HttpRequestMessage(HttpMethod.Get, htmlUrl);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) EmulatorAutoUpdater/1.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

        try
        {
            using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            var tagMatches = Regex.Matches(html, $@"{info.Owner}/{info.Repo}/releases/tag/(?<tag>[^""'/\s?#]+)", RegexOptions.IgnoreCase);
            if (tagMatches.Count == 0)
            {
                return null;
            }

            var seenTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            GitHubRelease? fallbackRelease = null;

            Regex? patternRegex = null;
            if (!string.IsNullOrWhiteSpace(assetPattern))
            {
                try { patternRegex = new Regex(assetPattern, RegexOptions.IgnoreCase); } catch { }
            }

            var pagePublishedDate = ParseHtmlPublishedDate(html);

            foreach (Match m in tagMatches)
            {
                var tagName = WebUtility.HtmlDecode(m.Groups["tag"].Value);
                if (!seenTags.Add(tagName)) continue;

                var assets = await FetchExpandedAssetsForTagAsync(info.Owner, info.Repo, tagName, html, cancellationToken);
                var candidateRelease = new GitHubRelease
                {
                    TagName = tagName,
                    Name = $"Release {tagName}",
                    Body = $"GitHub Release {tagName} (웹 파싱 - 태그 순회).",
                    PublishedAt = pagePublishedDate,
                    FetchSource = "웹 파싱 (태그 순회)",
                    Assets = assets
                };

                fallbackRelease ??= candidateRelease;

                if (patternRegex != null && assets.Any(a => patternRegex.IsMatch(a.Name)))
                {
                    return candidateRelease;
                }
            }

            return fallbackRelease;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static DateTimeOffset ParseHtmlPublishedDate(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return DateTimeOffset.MinValue;
        }

        var match = Regex.Match(html, @"<(relative-time|time)[^>]+datetime=[""'](?<date>[^""']+)[""']", RegexOptions.IgnoreCase);
        if (match.Success && DateTimeOffset.TryParse(match.Groups["date"].Value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var dto))
        {
            return dto;
        }

        return DateTimeOffset.MinValue;
    }

    private async Task<List<GitHubAsset>> FetchExpandedAssetsForTagAsync(string owner, string repo, string tagName, string htmlPage, CancellationToken cancellationToken)
    {
        var assets = new List<GitHubAsset>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Fetch lazy-loaded expanded_assets
        try
        {
            var expandedUrl = $"https://github.com/{owner}/{repo}/releases/expanded_assets/{tagName}";
            using var expandedRequest = new HttpRequestMessage(HttpMethod.Get, expandedUrl);
            expandedRequest.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) EmulatorAutoUpdater/1.0");
            expandedRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

            using var expandedResponse = await HttpClient.SendAsync(expandedRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (expandedResponse.IsSuccessStatusCode)
            {
                var expandedHtml = await expandedResponse.Content.ReadAsStringAsync(cancellationToken);
                var expandedMatches = Regex.Matches(expandedHtml, @"href=[""'](?<url>/[^""']+/releases/download/[^""']+)[""']", RegexOptions.IgnoreCase);

                foreach (Match m in expandedMatches)
                {
                    var fullUrl = "https://github.com" + m.Groups["url"].Value;
                    if (seenUrls.Add(fullUrl))
                    {
                        var filename = Path.GetFileName(new Uri(fullUrl).LocalPath);
                        assets.Add(new GitHubAsset
                        {
                            Name = filename,
                            BrowserDownloadUrl = fullUrl
                        });
                    }
                }
            }
        }
        catch
        {
            // Fallback to direct page matches
        }

        // 2. Direct page matches if expanded_assets returned empty
        if (assets.Count == 0 && !string.IsNullOrWhiteSpace(htmlPage))
        {
            var directMatches = Regex.Matches(htmlPage, @"href=[""'](?<url>/[^""']+/releases/download/[^""']+)[""']", RegexOptions.IgnoreCase);
            foreach (Match m in directMatches)
            {
                var fullUrl = "https://github.com" + m.Groups["url"].Value;
                if (seenUrls.Add(fullUrl))
                {
                    var filename = Path.GetFileName(new Uri(fullUrl).LocalPath);
                    assets.Add(new GitHubAsset
                    {
                        Name = filename,
                        BrowserDownloadUrl = fullUrl
                    });
                }
            }
        }

        return assets;
    }

    private async Task<GitHubRelease?> GetLatestGiteaReleaseAsync(RepositoryInfo info, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{info.ApiBaseUrl}/repos/{info.Owner}/{info.Repo}/releases/latest");
        request.Headers.UserAgent.ParseAdd("EmulatorAutoUpdater/1.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;

        var tagName = root.GetProperty("tag_name").GetString() ?? root.GetProperty("name").GetString() ?? string.Empty;
        var name = root.GetProperty("name").GetString() ?? tagName;
        var body = root.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() ?? string.Empty :
                   root.TryGetProperty("description", out var descProp) ? descProp.GetString() ?? string.Empty : string.Empty;
        var publishedAt = root.TryGetProperty("published_at", out var publishedAtProp) && publishedAtProp.ValueKind == JsonValueKind.String
            ? DateTimeOffset.Parse(publishedAtProp.GetString()!)
            : (root.TryGetProperty("created_at", out var createdAtProp) && createdAtProp.ValueKind == JsonValueKind.String
                ? DateTimeOffset.Parse(createdAtProp.GetString()!)
                : DateTimeOffset.MinValue);

        var assets = new List<GitHubAsset>();
        if (root.TryGetProperty("assets", out var assetsElement) && assetsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var assetElement in assetsElement.EnumerateArray())
            {
                var assetName = assetElement.GetProperty("name").GetString() ?? string.Empty;
                var browserUrl = assetElement.TryGetProperty("browser_download_url", out var browserProp) ? browserProp.GetString() : null;
                var downloadUrl = assetElement.TryGetProperty("download_url", out var downloadProp) ? downloadProp.GetString() : null;
                var assetUrl = browserUrl ?? downloadUrl;
                if (string.IsNullOrWhiteSpace(assetUrl))
                {
                    continue;
                }

                assets.Add(new GitHubAsset
                {
                    Name = assetName,
                    BrowserDownloadUrl = assetUrl!
                });
            }
        }

        return new GitHubRelease
        {
            TagName = tagName,
            Name = name,
            Body = body,
            PublishedAt = publishedAt,
            FetchSource = "Gitea API",
            Assets = assets
        };
    }

    private static string CleanReleaseVersion(string releaseTag)
    {
        if (string.IsNullOrWhiteSpace(releaseTag))
        {
            return string.Empty;
        }

        return releaseTag.Trim().TrimStart('v', 'V');
    }

    private sealed record GitHubReleaseResponse(
        [property: JsonPropertyName("tag_name")] string? TagName,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("published_at")] DateTimeOffset PublishedAt,
        [property: JsonPropertyName("assets")] List<GitHubAssetResponse> Assets
    );

    private sealed record GitHubAssetResponse(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl
    );

    public sealed class GitHubRelease
    {
        public string? TagName { get; init; }
        public string? Name { get; init; }
        public string Body { get; init; } = string.Empty;
        public DateTimeOffset PublishedAt { get; init; }
        public List<GitHubAsset> Assets { get; init; } = new();
        public string FetchSource { get; init; } = "GitHub REST API";
    }

    public sealed class GitHubAsset
    {
        public string Name { get; init; } = string.Empty;
        public string BrowserDownloadUrl { get; init; } = string.Empty;
    }
}
